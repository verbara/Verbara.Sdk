namespace Asterisk.NetAot.Config;

/// <summary>
/// Parses Asterisk configuration files (.conf format).
/// Supports:
///   - Sections: [section-name]
///   - Variables: key = value
///   - Comments: ; comment and // comment
///   - Directives: #include, #exec
///   - Template inheritance: [section](template)
/// </summary>
public sealed class ConfigFileReader
{
    /// <summary>Parse an Asterisk .conf file from disk.</summary>
    public static ConfigFile Parse(string filePath)
    {
        using var reader = new StreamReader(filePath);
        return Parse(reader, filePath);
    }

    /// <summary>Parse from a TextReader.</summary>
    public static ConfigFile Parse(TextReader reader, string? fileName = null)
    {
        var file = new ConfigFile { FileName = fileName ?? "unknown" };
        ConfigCategory? currentCategory = null;
        var lineNumber = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            line = line.Trim();

            // Skip empty lines and comments
            if (line.Length == 0 || line[0] == ';' || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            // Strip inline comments
            var commentIdx = line.IndexOf(';');
            if (commentIdx > 0)
                line = line[..commentIdx].TrimEnd();

            // Directives: #include, #exec
            if (line[0] == '#')
            {
                var directive = ParseDirective(line, lineNumber);
                if (directive is not null)
                {
                    file.Directives.Add(directive);
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

        return file;
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
        if (line.StartsWith("#include", StringComparison.OrdinalIgnoreCase))
        {
            var path = line[8..].Trim().Trim('"');
            return new IncludeDirective { Path = path, LineNumber = lineNumber };
        }

        if (line.StartsWith("#exec", StringComparison.OrdinalIgnoreCase))
        {
            var command = line[5..].Trim().Trim('"');
            return new ExecDirective { Command = command, LineNumber = lineNumber };
        }

        return null;
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

/// <summary>#include directive.</summary>
public sealed class IncludeDirective : ConfigDirective
{
    public string Path { get; init; } = string.Empty;
}

/// <summary>#exec directive.</summary>
public sealed class ExecDirective : ConfigDirective
{
    public string Command { get; init; } = string.Empty;
}

/// <summary>Parsing error.</summary>
public sealed class ConfigParseException(string message) : Exception(message);
