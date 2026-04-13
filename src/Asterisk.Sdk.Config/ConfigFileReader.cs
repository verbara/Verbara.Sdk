using System.Text;

namespace Asterisk.Sdk.Config;

/// <summary>
/// Parses Asterisk configuration files (.conf format).
/// Supports:
///   - Sections: [section-name]
///   - Variables: key = value
///   - Comments: ; comment and // comment
///   - Directives: #include, #tryinclude, #exec
///   - Template inheritance: [section](template)
///   - Inline expansion of #include / #tryinclude with cycle detection
/// </summary>
public sealed class ConfigFileReader
{
    /// <summary>
    /// Parse an Asterisk .conf file from disk, expanding any <c>#include</c> /
    /// <c>#tryinclude</c> directives inline. Relative include paths are resolved
    /// against the directory of the file containing the directive. Cycles in the
    /// include graph are detected and reported as <see cref="ConfigParseException"/>.
    /// </summary>
    public static ConfigFile Parse(string filePath)
    {
        var canonical = Path.GetFullPath(filePath);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { canonical };
        var file = new ConfigFile { FileName = canonical };
        ParseFileInto(canonical, file, visited);
        return file;
    }

    /// <summary>
    /// Parse from a <see cref="TextReader"/>. <c>#include</c> / <c>#tryinclude</c>
    /// directives are recorded on <see cref="ConfigFile.Directives"/> but are NOT
    /// expanded — there is no anchor directory from which to resolve relative
    /// paths. Use the file-path overload when include expansion is required.
    /// </summary>
    public static ConfigFile Parse(TextReader reader, string? fileName = null)
    {
        var file = new ConfigFile { FileName = fileName ?? "unknown" };
        ParseReaderInto(reader, file, parentFilePath: null, visited: null);
        return file;
    }

    private static void ParseFileInto(string canonicalPath, ConfigFile file, HashSet<string> visited)
    {
        // Read with explicit UTF-8 (AOT-safe, no encoding sniffing).
        using var stream = new FileStream(canonicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        ParseReaderInto(reader, file, canonicalPath, visited);
    }

    private static void ParseReaderInto(
        TextReader reader,
        ConfigFile file,
        string? parentFilePath,
        HashSet<string>? visited)
    {
        ConfigCategory? currentCategory = file.Categories.Count > 0
            ? file.Categories[^1]
            : null;
        var lineNumber = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            line = line.Trim();

            // Skip empty lines and comments
            if (line.Length == 0 || line[0] == ';' || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            // Strip inline comments (respecting quoted values)
            var commentIdx = FindUnquotedSemicolon(line);
            if (commentIdx > 0)
                line = line[..commentIdx].TrimEnd();

            // Directives: #include, #tryinclude, #exec
            if (line[0] == '#')
            {
                var directive = ParseDirective(line, lineNumber);
                if (directive is not null)
                {
                    file.Directives.Add(directive);

                    if (directive is IncludeDirective inc && visited is not null && parentFilePath is not null)
                    {
                        ExpandInclude(inc, file, parentFilePath, visited, lineNumber);
                        // Inline expansion: subsequent bare variables in the parent
                        // attach to whatever the now-last category is (matches
                        // Asterisk's textual #include semantics).
                        currentCategory = file.Categories.Count > 0 ? file.Categories[^1] : null;
                    }
                }

                continue;
            }

            // Section header: [name] or [name](template)
            if (line[0] == '[')
            {
                currentCategory = ParseSection(line, lineNumber);
                file.Categories.Add(currentCategory);
                continue;
            }

            // Variable: key = value or key => value (append)
            if (currentCategory is not null)
            {
                var variable = ParseVariable(line, lineNumber);
                if (variable is not null)
                {
                    currentCategory.Variables[variable.Key] = variable.Value;
                    currentCategory.OrderedVariables.Add(variable);
                }
            }
        }
    }

    private static void ExpandInclude(
        IncludeDirective directive,
        ConfigFile file,
        string parentFilePath,
        HashSet<string> visited,
        int lineNumber)
    {
        var parentDir = Path.GetDirectoryName(parentFilePath) ?? string.Empty;
        var resolved = Path.IsPathRooted(directive.Path)
            ? directive.Path
            : Path.Combine(parentDir, directive.Path);
        var canonical = Path.GetFullPath(resolved);

        if (!File.Exists(canonical))
        {
            if (directive.IsTry)
                return; // swallow per Asterisk semantics

            throw new ConfigParseException(
                $"Include file not found: {canonical} (referenced from {parentFilePath} line {lineNumber})");
        }

        if (visited.Contains(canonical))
        {
            throw new ConfigParseException(
                $"Include cycle detected: {canonical} (referenced from {parentFilePath} line {lineNumber})");
        }

        // Branch the visited set so siblings (DAG) don't poison each other,
        // but the current chain does carry the parent.
        var nextVisited = new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase) { canonical };
        ParseFileInto(canonical, file, nextVisited);
    }

    private static ConfigCategory ParseSection(string line, int lineNumber)
    {
        var closeBracket = line.IndexOf(']');
        if (closeBracket < 0)
            throw new ConfigParseException($"Missing closing bracket at line {lineNumber}: {line}");

        var name = line[1..closeBracket];
        string? template = null;

        // Check for template: [name](template)
        var parenStart = line.IndexOf('(', closeBracket);
        if (parenStart >= 0)
        {
            var parenEnd = line.IndexOf(')', parenStart);
            if (parenEnd > parenStart)
            {
                template = line[(parenStart + 1)..parenEnd];
            }
        }

        return new ConfigCategory { Name = name, Template = template };
    }

    private static ConfigVariable? ParseVariable(string line, int lineNumber)
    {
        // Try "key => value" (append) first, then "key = value"
        var arrowIdx = line.IndexOf("=>", StringComparison.Ordinal);
        if (arrowIdx > 0)
        {
            return new ConfigVariable
            {
                Key = line[..arrowIdx].Trim(),
                Value = line[(arrowIdx + 2)..].Trim(),
                IsAppend = true,
                LineNumber = lineNumber
            };
        }

        var equalIdx = line.IndexOf('=');
        if (equalIdx > 0)
        {
            return new ConfigVariable
            {
                Key = line[..equalIdx].Trim(),
                Value = line[(equalIdx + 1)..].Trim(),
                LineNumber = lineNumber
            };
        }

        return null;
    }

    private static ConfigDirective? ParseDirective(string line, int lineNumber)
    {
        // Order matters: #tryinclude must be matched before #include because of the prefix.
        if (line.StartsWith("#tryinclude", StringComparison.OrdinalIgnoreCase))
        {
            var path = ExtractIncludePath(line[11..]);
            return new IncludeDirective { Path = path, IsTry = true, LineNumber = lineNumber };
        }

        if (line.StartsWith("#include", StringComparison.OrdinalIgnoreCase))
        {
            var path = ExtractIncludePath(line[8..]);
            return new IncludeDirective { Path = path, LineNumber = lineNumber };
        }

        if (line.StartsWith("#exec", StringComparison.OrdinalIgnoreCase))
        {
            var command = line[5..].Trim().Trim('"');
            return new ExecDirective { Command = command, LineNumber = lineNumber };
        }

        return null;
    }

    /// <summary>
    /// Extract the include path from the operand of an <c>#include</c> /
    /// <c>#tryinclude</c> directive. Supports both <c>"path"</c> and
    /// <c>&lt;path&gt;</c> syntaxes; falls back to a bare token.
    /// </summary>
    private static string ExtractIncludePath(string operand)
    {
        var trimmed = operand.Trim();
        if (trimmed.Length >= 2)
        {
            if (trimmed[0] == '"' && trimmed[^1] == '"')
                return trimmed[1..^1];
            if (trimmed[0] == '<' && trimmed[^1] == '>')
                return trimmed[1..^1];
        }
        return trimmed.Trim('"');
    }

    private static int FindUnquotedSemicolon(ReadOnlySpan<char> line)
    {
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            switch (line[i])
            {
                case '"':
                    inQuote = !inQuote;
                    break;
                case ';' when !inQuote:
                    return i;
            }
        }
        return -1;
    }
}

/// <summary>Represents a parsed Asterisk configuration file.</summary>
public sealed class ConfigFile
{
    public string FileName { get; set; } = string.Empty;
    public List<ConfigCategory> Categories { get; } = [];
    public List<ConfigDirective> Directives { get; } = [];

    /// <summary>Get a category by name.</summary>
    public ConfigCategory? GetCategory(string name) =>
        Categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A configuration section [name].</summary>
public sealed class ConfigCategory
{
    public string Name { get; init; } = string.Empty;
    public string? Template { get; init; }
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ConfigVariable> OrderedVariables { get; } = [];
}

/// <summary>A key=value configuration variable.</summary>
public sealed class ConfigVariable
{
    public string Key { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public bool IsAppend { get; init; }
    public int LineNumber { get; init; }
}

/// <summary>Base for configuration directives.</summary>
public abstract class ConfigDirective
{
    public int LineNumber { get; init; }
}

/// <summary>#include or #tryinclude directive.</summary>
public sealed class IncludeDirective : ConfigDirective
{
    public string Path { get; init; } = string.Empty;

    /// <summary>True when this was a <c>#tryinclude</c> (missing target is non-fatal).</summary>
    public bool IsTry { get; init; }
}

/// <summary>#exec directive.</summary>
public sealed class ExecDirective : ConfigDirective
{
    public string Command { get; init; } = string.Empty;
}

/// <summary>Parsing error.</summary>
public sealed class ConfigParseException(string message) : Asterisk.Sdk.AsteriskException(message);
