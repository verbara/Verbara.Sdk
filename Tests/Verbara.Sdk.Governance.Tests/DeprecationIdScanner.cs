using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// Holds every use of the SDK's own deprecation ids to <see cref="DeprecationIdRegister"/>. In one pass over
/// <c>src/</c> and <c>Tests/</c> it reports:
/// <list type="bullet">
/// <item>a mark or suppression the scan cannot read: an <c>[Obsolete]</c> whose <c>DiagnosticId</c> or <c>UrlFormat</c>,
/// or an assembly-wide or module-wide <c>SuppressMessage</c> whose category or check id, is not a single string literal;
/// a <c>using</c> alias of <c>ObsoleteAttribute</c> or <c>SuppressMessageAttribute</c>; an attribute that sets a
/// named <c>DiagnosticId</c> or <c>UrlFormat</c> under a name that is not one of the <c>[Obsolete]</c> spellings;</item>
/// <item>a mark under a <c>VSDK</c> id the register does not hold, and a <c>VSDK</c> mark on a target the scan cannot
/// key to <c>Type</c> or <c>Type.Member</c>;</item>
/// <item>a retired id used again: as a mark, in a pragma, in an assembly-wide or module-wide suppression, or in build
/// configuration; and a symbol of a retired id's removed set declared again in the package that declared it;</item>
/// <item>for a marked id: a shipped mark outside its member set, a listed member without its mark, a mark without the
/// registered <c>UrlFormat</c>, and a mark whose message does not name the register's removal release, or names a
/// second SDK release;</item>
/// <item>a guide that does not hold, outside fenced code, exactly one heading whose text is each registered id that
/// has the guide, retired or marked.</item>
/// </list>
/// </summary>
/// <remarks>
/// A syntax scan, like the other guards of this project: it needs no reference to the packages it checks and no build
/// output, and a string literal that spells an id is not a use of it. What it cannot read it reports rather than
/// skips, so an id behind a constant, an alias or an unkeyable target cannot pass unseen.
/// </remarks>
internal static class DeprecationIdScanner
{
    private const string IdPrefix = "VSDK";

    private static readonly string[] ObsoleteNames =
        ["Obsolete", "ObsoleteAttribute", "System.Obsolete", "System.ObsoleteAttribute", "global::System.Obsolete", "global::System.ObsoleteAttribute"];

    private static readonly string[] AliasedAttributes = ["ObsoleteAttribute", "SuppressMessageAttribute"];

    private static readonly string[] GeneratedSuffixes = [".g.cs", ".Designer.cs", ".generated.cs"];

    private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules"];

    private static readonly string[] ConfigurationSuffixes =
        [".csproj", ".props", ".targets", ".editorconfig", ".globalconfig", ".rsp"];

    private static readonly Regex GuideHeading = new(
        @"^ {0,3}#{1,6}[ \t]+(?<text>.*?)[ \t]*#*[ \t]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex Fence = new(@"^ {0,3}(```|~~~)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex RemovalRelease = new(
        @"Removed in Verbara\.Sdk (?<release>\d+\.\d+\.\d+)\.", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex SdkRelease = new(@"Verbara\.Sdk \d+\.\d+\.\d+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    public static DeprecationIdReport Scan(string repoRoot, IReadOnlyList<DeprecationIdRow> register)
    {
        var scan = new ScanState(register);
        var srcRoot = Path.Join(repoRoot, "src");
        var testsRoot = Path.Join(repoRoot, "Tests");

        var srcFiles = SourceFiles(srcRoot).ToList();
        if (srcFiles.Count == 0)
            scan.Violations.Add("found no source file under src/: the guard would pass on nothing");

        foreach (var file in srcFiles)
        {
            var tree = Parse(file);
            var relative = Relative(srcRoot, file);
            var package = relative.Split('/')[0];
            CheckRemovedDeclarations(tree, package, "src/" + relative, scan);
            CheckSource(tree, "src/" + relative, package, scan);
        }

        foreach (var file in SourceFiles(testsRoot))
            CheckSource(Parse(file), "Tests/" + Relative(testsRoot, file), package: null, scan);

        CheckMembers(scan);
        CheckConfiguration(repoRoot, scan);
        CheckGuides(repoRoot, scan);

        return new DeprecationIdReport(srcFiles.Count, scan.MarksFound, scan.Violations);
    }

    /// <summary>A retired id's removed set: a symbol of it declared again in the package that declared it.</summary>
    private static void CheckRemovedDeclarations(SyntaxTree tree, string package, string relative, ScanState scan)
    {
        var inPackage = scan.Register
            .Where(r => r.State == DeprecationIdState.Retired)
            .SelectMany(r => r.RemovedSet.Where(s => s.Package == package).Select(s => (s.Key, r.Release)))
            .ToDictionary(p => p.Key, p => p.Release, StringComparer.Ordinal);
        if (inPackage.Count == 0)
            return;

        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            var key = node switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                DelegateDeclarationSyntax del => del.Identifier.ValueText,
                PropertyDeclarationSyntax or MethodDeclarationSyntax or FieldDeclarationSyntax or EventDeclarationSyntax
                    or EventFieldDeclarationSyntax => MemberKey(node),
                _ => null,
            };
            if (key is null)
                continue;

            foreach (var candidate in key.Split('|').Where(inPackage.ContainsKey))
                scan.Violations.Add($"{relative}:{Line(node)}: {candidate} is declared again; it was removed in {inPackage[candidate]}");
        }
    }

    private static void CheckSource(SyntaxTree tree, string relative, string? package, ScanState scan)
    {
        var root = tree.GetRoot();

        foreach (var alias in root.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(u => u.Alias is not null))
        {
            var target = alias.NamespaceOrType.ToString();
            if (AliasedAttributes.Any(a => target == a || target.EndsWith("." + a, StringComparison.Ordinal)))
            {
                scan.Violations.Add($"{relative}:{Line(alias)}: a using alias names {target}; the guard reads [Obsolete] and " +
                    "SuppressMessage only under their own names, so write the attribute by its name");
            }
        }

        foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            var name = attribute.Name.ToString();
            var args = attribute.ArgumentList?.Arguments ?? default;
            if (ObsoleteNames.Contains(name, StringComparer.Ordinal))
            {
                CheckMark(attribute, args, relative, package, scan);
                continue;
            }

            foreach (var named in args.Where(a => a.NameEquals?.Name.Identifier.ValueText is "DiagnosticId" or "UrlFormat"))
            {
                scan.Violations.Add($"{relative}:{Line(attribute)}: [{name}] sets {named.NameEquals!.Name.Identifier.ValueText} under a " +
                    "name the guard does not read as [Obsolete]; write [Obsolete] by its name");
            }

            if ((name.EndsWith("SuppressMessage", StringComparison.Ordinal) || name.EndsWith("SuppressMessageAttribute", StringComparison.Ordinal))
                && attribute.Parent is AttributeListSyntax { Target.Identifier.ValueText: "assembly" or "module" })
            {
                CheckSuppression(attribute, args, relative, scan);
            }
        }

        var pragmaCodes = root.DescendantTrivia(descendIntoTrivia: true)
            .Select(t => t.GetStructure()).OfType<PragmaWarningDirectiveTriviaSyntax>()
            .SelectMany(p => p.ErrorCodes.Select(c => (Pragma: p, Code: c.ToString().Trim())));
        foreach (var (pragma, code) in pragmaCodes.Where(p => scan.Retired.Contains(p.Code)))
            scan.Violations.Add($"{relative}:{Line(pragma)}: a #pragma warning names the retired id {code}");
    }

    private static void CheckMark(AttributeSyntax attribute, SeparatedSyntaxList<AttributeArgumentSyntax> args, string relative,
        string? package, ScanState scan)
    {
        var line = Line(attribute);
        var idArgument = Named(args, "DiagnosticId");
        var urlArgument = Named(args, "UrlFormat");

        foreach (var (argument, argumentName) in new[] { (idArgument, "DiagnosticId"), (urlArgument, "UrlFormat") })
        {
            if (argument is not null && LiteralText(argument.Expression) is null)
            {
                scan.Violations.Add($"{relative}:{line}: an [Obsolete] gives its {argumentName} as something other than a string " +
                    "literal; the guard reads only literals, so write it as one (DeprecationIdRegister holds the id)");
            }
        }

        var id = idArgument is null ? null : LiteralText(idArgument.Expression);
        if (id is null || !id.StartsWith(IdPrefix, StringComparison.Ordinal))
            return;

        var key = MarkKey(attribute);
        if (key is null)
            scan.Violations.Add($"{relative}:{line}: an [Obsolete] carrying {id} sits on a target the guard cannot key to Type or Type.Member");

        if (!scan.ById.TryGetValue(id, out var row))
        {
            scan.Violations.Add($"{relative}:{line}: an [Obsolete] carries {id}, which DeprecationIdRegister does not hold");
            return;
        }

        if (row.State == DeprecationIdState.Retired)
        {
            scan.Violations.Add($"{relative}:{line}: an [Obsolete] carries the retired id {id}");
            return;
        }

        var who = key?.Replace("|", ", ", StringComparison.Ordinal) ?? "the mark";
        if (package is not null)
        {
            scan.MarksFound++;
            foreach (var member in key?.Split('|') ?? [])
            {
                var symbol = new RemovedSymbol(package, member);
                if (row.Members.Contains(symbol))
                    scan.Found.Add((id, symbol));
                else
                    scan.Violations.Add($"{relative}:{line}: {member} carries {id} but is outside its registered set");
            }
        }

        CheckMessage(args, relative, line, who, row, scan);

        if (row.Link is not { } link)
            return;

        var url = urlArgument is null ? null : LiteralText(urlArgument.Expression);
        if (urlArgument is null)
        {
            scan.Violations.Add($"{relative}:{line}: {who} carries {id} with no UrlFormat; the registered link is {link}");
        }
        else if (url is not null && url != link)
        {
            var placeholder = url.Contains("{0}", StringComparison.Ordinal)
                ? " ({0} is written as the id in upper case, and the guide's anchor is lower case)"
                : "";
            scan.Violations.Add($"{relative}:{line}: {who} carries {id} with UrlFormat \"{url}\", not the registered link {link}{placeholder}");
        }
    }

    /// <summary>The message must be a literal naming <c>Removed in Verbara.Sdk X.Y.Z.</c> for the register's release, and no other SDK release.</summary>
    private static void CheckMessage(SeparatedSyntaxList<AttributeArgumentSyntax> args, string relative, int line, string who,
        DeprecationIdRow row, ScanState scan)
    {
        var expected = $"Removed in Verbara.Sdk {row.Release}.";
        var messageArgument = args.FirstOrDefault(a => a.NameEquals is null && (a.NameColon is null || a.NameColon.Name.Identifier.ValueText == "message"));
        if (messageArgument is null)
        {
            scan.Violations.Add($"{relative}:{line}: {who} carries {row.Id} with no message; it must name \"{expected}\"");
            return;
        }

        if (LiteralText(messageArgument.Expression) is not { } message)
        {
            scan.Violations.Add($"{relative}:{line}: {who} carries {row.Id} with a message that is not a string literal; it must name \"{expected}\"");
            return;
        }

        var removal = RemovalRelease.Match(message);
        if (!removal.Success)
            scan.Violations.Add($"{relative}:{line}: {who} carries {row.Id} with a message that names no removal release; the register expects \"{expected}\"");
        else if (removal.Groups["release"].Value != row.Release)
            scan.Violations.Add($"{relative}:{line}: {who} carries {row.Id} with a message naming removal in {removal.Groups["release"].Value}; the register expects \"{expected}\"");

        if (SdkRelease.Count(message) > 1)
            scan.Violations.Add($"{relative}:{line}: {who} carries {row.Id} with a message that names a second SDK release; it must name only \"{expected}\"");
    }

    private static void CheckSuppression(AttributeSyntax attribute, SeparatedSyntaxList<AttributeArgumentSyntax> args, string relative, ScanState scan)
    {
        var line = Line(attribute);
        var positional = args.Where(a => a.NameEquals is null && a.NameColon is null).ToList();
        var category = args.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "category") ?? positional.ElementAtOrDefault(0);
        var checkId = args.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "checkId") ?? positional.ElementAtOrDefault(1);

        foreach (var (argument, what) in new[] { (category, "category"), (checkId, "check id") })
        {
            if (argument is not null && LiteralText(argument.Expression) is null)
            {
                scan.Violations.Add($"{relative}:{line}: an assembly-wide SuppressMessage gives its {what} as something other than a " +
                    "string literal; the guard reads only literals, so write it as one");
            }
        }

        foreach (var retired in scan.Retired.Where(r => args.Any(a => LiteralText(a.Expression)?.Contains(r, StringComparison.Ordinal) == true)))
            scan.Violations.Add($"{relative}:{line}: an assembly-wide SuppressMessage names the retired id {retired}");
    }

    /// <summary>A marked id's listed member that carries no mark of it in its package.</summary>
    private static void CheckMembers(ScanState scan)
    {
        var listed = scan.Register.Where(r => r.State == DeprecationIdState.Marked).Sum(r => r.Members.Count);
        if (listed > 0 && scan.MarksFound == 0)
            scan.Violations.Add($"found none of the {listed} marks the register lists: the guard would pass on nothing");

        foreach (var row in scan.Register.Where(r => r.State == DeprecationIdState.Marked))
        {
            foreach (var member in row.Members.Where(m => !scan.Found.Contains((row.Id, m))))
                scan.Violations.Add($"src/{member.Package}: {member.Key} is registered under {row.Id} but carries no {row.Id} mark");
        }
    }

    /// <summary>A retired id named by any project, props, targets, editorconfig, globalconfig or response file.</summary>
    private static void CheckConfiguration(string repoRoot, ScanState scan)
    {
        foreach (var file in ConfigurationFiles(repoRoot))
        {
            var text = File.ReadAllText(file);
            foreach (var retired in scan.Retired.Order(StringComparer.Ordinal))
            {
                var at = text.IndexOf(retired, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    continue;

                var line = text[..at].Count(c => c == '\n') + 1;
                scan.Violations.Add($"{Relative(repoRoot, file)}:{line}: build configuration names the retired id {retired}");
            }
        }
    }

    /// <summary>Exactly one heading whose text is the id, at any level, outside fenced code, for every registered id with a guide.</summary>
    private static void CheckGuides(string repoRoot, ScanState scan)
    {
        foreach (var guide in scan.Register.Where(r => r.GuidePath is not null).GroupBy(r => r.GuidePath!, StringComparer.Ordinal))
        {
            var path = Path.Join(repoRoot, guide.Key);
            if (!File.Exists(path))
            {
                foreach (var row in guide)
                    scan.Violations.Add($"{guide.Key}: missing; the {row.Id} link points into it");
                continue;
            }

            var headings = new Dictionary<string, int>(StringComparer.Ordinal);
            var fenced = false;
            foreach (var line in File.ReadLines(path))
            {
                if (Fence.IsMatch(line))
                {
                    fenced = !fenced;
                    continue;
                }

                if (!fenced && GuideHeading.Match(line) is { Success: true } heading)
                    headings[heading.Groups["text"].Value] = headings.GetValueOrDefault(heading.Groups["text"].Value) + 1;
            }

            foreach (var row in guide)
            {
                var count = headings.GetValueOrDefault(row.Id);
                if (count != 1)
                    scan.Violations.Add($"{guide.Key}: holds {count} headings whose text is \"{row.Id}\", not exactly one");
            }
        }
    }

    /// <summary>GitHub's heading anchor: lower case, punctuation other than hyphens dropped, spaces as hyphens.</summary>
    public static string Slug(string heading)
    {
        var sb = new StringBuilder();
        foreach (var c in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
                sb.Append(c);
            else if (c == ' ')
                sb.Append('-');
        }

        return sb.ToString();
    }

    /// <summary>
    /// <c>Type</c> or <c>Type.Member</c> (several joined by <c>|</c>) for the declaration a mark sits on, or
    /// <see langword="null"/> when the scan cannot key it: a constructor, indexer, operator, accessor, enum member, plain
    /// parameter, or a target the attribute list redirects. A record's positional parameter marked through
    /// <c>property:</c> is keyed <c>Record.Parameter</c>, the property the compiler generates.
    /// </summary>
    private static string? MarkKey(AttributeSyntax attribute)
    {
        if (attribute.Parent is not AttributeListSyntax list)
            return null;

        var target = list.Target?.Identifier.ValueText;
        return list.Parent switch
        {
            ParameterSyntax { Parent: ParameterListSyntax { Parent: RecordDeclarationSyntax record } } parameter when target == "property"
                => record.Identifier.ValueText + "." + parameter.Identifier.ValueText,
            BaseTypeDeclarationSyntax type when target is null or "type" => type.Identifier.ValueText,
            DelegateDeclarationSyntax del when target is null or "type" => del.Identifier.ValueText,
            PropertyDeclarationSyntax property when target is null or "property" => MemberKey(property),
            MethodDeclarationSyntax method when target is null or "method" => MemberKey(method),
            FieldDeclarationSyntax field when target is null or "field" => MemberKey(field),
            EventDeclarationSyntax or EventFieldDeclarationSyntax when target is null or "event" => MemberKey(list.Parent),
            _ => null,
        };
    }

    /// <summary><c>Type.Member</c> for every name a member declaration declares, joined by <c>|</c>.</summary>
    private static string? MemberKey(SyntaxNode member)
    {
        var container = member.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
        if (container is null)
            return null;

        IEnumerable<string> names = member switch
        {
            PropertyDeclarationSyntax prop => [prop.Identifier.ValueText],
            MethodDeclarationSyntax method => [method.Identifier.ValueText],
            EventDeclarationSyntax evt => [evt.Identifier.ValueText],
            FieldDeclarationSyntax field => field.Declaration.Variables.Select(v => v.Identifier.ValueText),
            EventFieldDeclarationSyntax evt => evt.Declaration.Variables.Select(v => v.Identifier.ValueText),
            _ => [],
        };
        return string.Join("|", names.Select(n => container + "." + n));
    }

    private static AttributeArgumentSyntax? Named(SeparatedSyntaxList<AttributeArgumentSyntax> args, string name) =>
        args.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == name);

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !InSkippedDirectory(root, f) && !GeneratedSuffixes.Any(s => f.EndsWith(s, StringComparison.Ordinal)) && !LooksAutoGenerated(f))
                .Order(StringComparer.Ordinal)
            : [];

    private static IEnumerable<string> ConfigurationFiles(string repoRoot)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.None };
        var pending = new Stack<string>([repoRoot]);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(dir, "*", options))
            {
                var name = Path.GetFileName(file);
                if (ConfigurationSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal)))
                    yield return file;
            }

            foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
            {
                var name = Path.GetFileName(sub);
                // Dot-directories (.git, .worktrees, .github, .config) hold no build configuration of this tree.
                if (!name.StartsWith('.') && !SkippedDirectories.Contains(name, StringComparer.Ordinal))
                    pending.Push(sub);
            }
        }
    }

    private static bool InSkippedDirectory(string root, string file) =>
        Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)
            .SkipLast(1).Any(s => SkippedDirectories.Contains(s, StringComparer.Ordinal) || s.StartsWith('.'));

    private static bool LooksAutoGenerated(string path)
    {
        using var reader = new StreamReader(path);
        for (var i = 0; i < 3; i++)
        {
            var line = reader.ReadLine();
            if (line is null)
                break;
            if (line.Contains("<auto-generated", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static SyntaxTree Parse(string file) => CSharpSyntaxTree.ParseText(File.ReadAllText(file), ParseOptions, path: file);

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string? LiteralText(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal ? literal.Token.ValueText : null;

    private sealed class ScanState(IReadOnlyList<DeprecationIdRow> register)
    {
        public IReadOnlyList<DeprecationIdRow> Register { get; } = register;

        public Dictionary<string, DeprecationIdRow> ById { get; } = register.ToDictionary(r => r.Id, StringComparer.Ordinal);

        public HashSet<string> Retired { get; } =
            register.Where(r => r.State == DeprecationIdState.Retired).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        public HashSet<(string Id, RemovedSymbol Symbol)> Found { get; } = [];

        public List<string> Violations { get; } = [];

        public int MarksFound { get; set; }
    }
}

/// <summary>What one <see cref="DeprecationIdScanner.Scan"/> found.</summary>
internal sealed record DeprecationIdReport(int SourceFiles, int MarksFound, IReadOnlyList<string> Violations)
{
    public bool IsClean => Violations.Count == 0;

    public override string ToString() =>
        Violations.Count == 0 ? "(clean)" : string.Join(Environment.NewLine, Violations.Select(v => "  " + v));
}
