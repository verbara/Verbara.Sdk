using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>One symbol of the deprecated set: its file under <c>src/</c> and its declaration key.</summary>
/// <param name="File">The file under <c>src/</c>, with forward slashes.</param>
/// <param name="Key">The type's name, or <c>Type.Member</c> for a member.</param>
internal sealed record MarkedSymbol(string File, string Key);

/// <summary>
/// Reads the tree for the deprecation id the SDK puts on the surface of dead Asterisk modules, and reports every way the
/// tree disagrees with the ruled set: a listed symbol without the mark, with it as an error or with another link; an
/// unlisted declaration that carries the id; a project-wide suppression of the id; a site-local suppression with no
/// restore or no reason; a migration guide without exactly one section for the id.
/// </summary>
/// <remarks>
/// A source scan, like the other guards of this project: it parses every non-generated <c>src/**/*.cs</c> with Roslyn
/// and reads attribute syntax, so it needs no reference to the packages it checks and no build output. Pragmas and
/// assembly attributes are read as syntax too, so a test that writes them inside a string literal is not one.
/// </remarks>
internal static class DeadModuleDeprecationScanner
{
    public const string DiagnosticId = "VSDK0005";

    public const string GuidePath = "docs/guides/obsolete-ami-events-removal-migration.md";

    public const string UrlFormat =
        "https://github.com/verbara/Verbara.Sdk/blob/main/" + GuidePath + "#vsdk0005";

    private static readonly string[] ObsoleteNames =
        ["Obsolete", "ObsoleteAttribute", "System.Obsolete", "System.ObsoleteAttribute", "global::System.Obsolete", "global::System.ObsoleteAttribute"];

    private static readonly string[] GeneratedSuffixes = [".g.cs", ".Designer.cs", ".generated.cs"];

    private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules"];

    private static readonly Regex XmlSuppression = new(
        @"<(?<el>NoWarn|WarningsNotAsErrors)\b[^>]*>[^<]*\bVSDK0005\b|\b(?<el>NoWarn|WarningsNotAsErrors)\s*=\s*""[^""]*\bVSDK0005\b",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static readonly Regex EditorConfigSuppression = new(
        @"^\s*dotnet_diagnostic\.VSDK0005\.severity\s*=", RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromSeconds(1));

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>The ruled set: 49 types and one member.</summary>
    public static IReadOnlyList<MarkedSymbol> RuledSet { get; } = BuildRuledSet();

    public static DeadModuleDeprecationReport Scan(string repoRoot, IReadOnlyList<MarkedSymbol> ruledSet)
    {
        var violations = new List<string>();
        var marks = new List<FoundMark>();
        var srcRoot = Path.Join(repoRoot, "src");
        var testsRoot = Path.Join(repoRoot, "Tests");

        var srcFiles = SourceFiles(srcRoot).ToList();
        if (srcFiles.Count == 0)
            violations.Add("found no source file under src/: the guard would pass on nothing");

        foreach (var file in srcFiles)
        {
            var tree = Parse(file);
            var relative = Relative(srcRoot, file);
            marks.AddRange(FindMarks(tree, relative));
            CheckAssemblySuppressions(tree, "src/" + relative, violations);
            CheckPragmas(tree, "src/" + relative, violations);
        }

        foreach (var file in SourceFiles(testsRoot))
        {
            var tree = Parse(file);
            var relative = "Tests/" + Relative(testsRoot, file);
            CheckAssemblySuppressions(tree, relative, violations);
            CheckPragmas(tree, relative, violations);
        }

        foreach (var symbol in ruledSet)
        {
            if (!File.Exists(Path.Join(srcRoot, symbol.File)))
            {
                violations.Add($"src/{symbol.File}: listed file missing (for {symbol.Key})");
                continue;
            }

            var found = marks.Where(m => m.File == symbol.File && m.Key == symbol.Key).ToList();
            if (found.Count == 0)
            {
                violations.Add($"src/{symbol.File}: {symbol.Key} carries no [Obsolete] with DiagnosticId = \"{DiagnosticId}\"");
                continue;
            }

            foreach (var mark in found)
            {
                if (mark.IsError)
                    violations.Add($"src/{symbol.File}:{mark.Line}: {symbol.Key} carries {DiagnosticId} as an error; it must be a warning");
                if (mark.UrlFormat != UrlFormat)
                    violations.Add($"src/{symbol.File}:{mark.Line}: {symbol.Key} has UrlFormat {Quote(mark.UrlFormat)}, not {Quote(UrlFormat)}");
            }
        }

        foreach (var mark in marks.Where(m => !ruledSet.Any(s => s.File == m.File && s.Key == m.Key)))
            violations.Add($"src/{mark.File}:{mark.Line}: {mark.Key} carries {DiagnosticId} but is not in the ruled set");

        CheckConfiguration(repoRoot, violations);
        CheckGuide(repoRoot, violations);

        return new DeadModuleDeprecationReport(srcFiles.Count, marks, violations);
    }

    private static IEnumerable<FoundMark> FindMarks(SyntaxTree tree, string relativeFile)
    {
        var obsoletes = tree.GetRoot().DescendantNodes().OfType<AttributeSyntax>()
            .Where(attribute => ObsoleteNames.Contains(attribute.Name.ToString(), StringComparer.Ordinal));
        foreach (var attribute in obsoletes)
        {
            var args = attribute.ArgumentList?.Arguments ?? default;
            string? diagnosticId = null;
            string? urlFormat = null;
            var isError = false;
            var position = 0;
            foreach (var arg in args)
            {
                var name = arg.NameEquals?.Name.Identifier.ValueText;
                var colon = arg.NameColon?.Name.Identifier.ValueText;
                if (name == "DiagnosticId")
                    diagnosticId = LiteralText(arg.Expression);
                else if (name == "UrlFormat")
                    urlFormat = LiteralText(arg.Expression);
                else if (name is null)
                {
                    var isErrorArg = colon == "error" || (colon is null && position == 1);
                    if (isErrorArg && arg.Expression is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.FalseLiteralExpression })
                        isError = true;
                    position++;
                }
            }

            if (diagnosticId != DiagnosticId)
                continue;

            var key = DeclarationKey(attribute);
            var line = attribute.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            yield return new FoundMark(relativeFile, key, line, isError, urlFormat);
        }
    }

    /// <summary>The type's name, or <c>Type.Member</c>, of the declaration an attribute is attached to.</summary>
    private static string DeclarationKey(AttributeSyntax attribute)
    {
        var target = attribute.Parent?.Parent;
        var member = target switch
        {
            BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
            DelegateDeclarationSyntax del => del.Identifier.ValueText,
            PropertyDeclarationSyntax prop => prop.Identifier.ValueText,
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            EventDeclarationSyntax evt => evt.Identifier.ValueText,
            FieldDeclarationSyntax field => string.Join(",", field.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            EventFieldDeclarationSyntax evt => string.Join(",", evt.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            EnumMemberDeclarationSyntax enumMember => enumMember.Identifier.ValueText,
            ConstructorDeclarationSyntax ctor => ctor.Identifier.ValueText + "()",
            _ => "<" + (target?.Kind().ToString() ?? "unknown") + ">",
        };

        if (target is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
            return member;

        var container = target?.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        return container is null ? member : container.Identifier.ValueText + "." + member;
    }

    private static void CheckAssemblySuppressions(SyntaxTree tree, string relative, List<string> violations)
    {
        var targetLists = tree.GetRoot().DescendantNodes().OfType<AttributeListSyntax>()
            .Where(list => list.Target?.Identifier.ValueText is "assembly" or "module");
        foreach (var list in targetLists)
        {
            var target = list.Target!.Identifier.ValueText;
            foreach (var attribute in list.Attributes)
            {
                var name = attribute.Name.ToString();
                if (!name.EndsWith("SuppressMessage", StringComparison.Ordinal) && !name.EndsWith("SuppressMessageAttribute", StringComparison.Ordinal))
                    continue;
                if (attribute.ArgumentList?.Arguments.Any(a => LiteralText(a.Expression)?.Contains(DiagnosticId, StringComparison.Ordinal) == true) == true)
                {
                    var line = attribute.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    violations.Add($"{relative}:{line}: [{target}: SuppressMessage] suppresses {DiagnosticId} project-wide");
                }
            }
        }
    }

    /// <summary>Every disable of the id has a reason and is restored, in the same file, before it is disabled again.</summary>
    private static void CheckPragmas(SyntaxTree tree, string relative, List<string> violations)
    {
        int? open = null;
        foreach (var pragma in tree.GetRoot().DescendantTrivia(descendIntoTrivia: true)
                     .Select(t => t.GetStructure()).OfType<PragmaWarningDirectiveTriviaSyntax>())
        {
            var ids = pragma.ErrorCodes.Select(c => c.ToString().Trim()).ToList();
            var line = pragma.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var disables = pragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword);
            if (!ids.Contains(DiagnosticId, StringComparer.Ordinal))
            {
                if (disables && ids.Count == 0)
                    violations.Add($"{relative}:{line}: a bare #pragma warning disable silences {DiagnosticId} with everything else");
                continue;
            }

            if (disables)
            {
                if (open is not null)
                    violations.Add($"{relative}:{open}: #pragma warning disable {DiagnosticId} is not restored before line {line} disables it again");
                open = line;
                if (!HasReason(pragma))
                    violations.Add($"{relative}:{line}: #pragma warning disable {DiagnosticId} carries no trailing reason comment");
            }
            else
            {
                if (open is null)
                    violations.Add($"{relative}:{line}: #pragma warning restore {DiagnosticId} restores nothing");
                open = null;
            }
        }

        if (open is not null)
            violations.Add($"{relative}:{open}: #pragma warning disable {DiagnosticId} has no matching restore in the same file");
    }

    private static bool HasReason(PragmaWarningDirectiveTriviaSyntax pragma)
    {
        var comment = pragma.EndOfDirectiveToken.LeadingTrivia
            .Concat(pragma.ErrorCodes.LastOrDefault()?.GetTrailingTrivia() ?? default)
            .FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia));
        return comment != default && comment.ToString().TrimStart('/').Trim().Length > 0;
    }

    private static void CheckConfiguration(string repoRoot, List<string> violations)
    {
        foreach (var file in ConfigurationFiles(repoRoot))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains(DiagnosticId, StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = Relative(repoRoot, file);
            var isEditorConfig = file.EndsWith(".editorconfig", StringComparison.Ordinal) || file.EndsWith(".globalconfig", StringComparison.Ordinal);
            var match = isEditorConfig ? EditorConfigSuppression.Match(text) : XmlSuppression.Match(text);
            if (match.Success)
            {
                var line = text[..match.Index].Count(c => c == '\n') + 1;
                violations.Add($"{relative}:{line}: suppresses {DiagnosticId} project-wide");
            }
        }
    }

    private static void CheckGuide(string repoRoot, List<string> violations)
    {
        var path = Path.Join(repoRoot, GuidePath);
        if (!File.Exists(path))
        {
            violations.Add($"{GuidePath}: missing; the {DiagnosticId} link points into it");
            return;
        }

        var headings = File.ReadAllLines(path).Select((text, i) => (Text: text.TrimEnd(), Line: i + 1))
            .Where(h => h.Text == "## " + DiagnosticId).ToList();
        if (headings.Count != 1)
            violations.Add($"{GuidePath}: holds {headings.Count} \"## {DiagnosticId}\" headings, not exactly one");

        var fragment = UrlFormat[(UrlFormat.IndexOf('#', StringComparison.Ordinal) + 1)..];
        if (fragment != Slug(DiagnosticId))
            violations.Add($"UrlFormat fragment \"{fragment}\" is not the slug \"{Slug(DiagnosticId)}\" of the \"## {DiagnosticId}\" heading");
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
                if (name.EndsWith(".csproj", StringComparison.Ordinal) || name.EndsWith(".props", StringComparison.Ordinal)
                    || name.EndsWith(".targets", StringComparison.Ordinal) || name.EndsWith(".editorconfig", StringComparison.Ordinal)
                    || name.EndsWith(".globalconfig", StringComparison.Ordinal))
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

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string? LiteralText(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal ? literal.Token.ValueText : null;

    private static string Quote(string? value) => value is null ? "(none)" : "\"" + value + "\"";

    private static List<MarkedSymbol> BuildRuledSet()
    {
        const string Actions = "Verbara.Sdk.Ami/Actions/";
        string[] actions =
        [
            "SipPeersAction", "SipShowPeerAction", "SipShowRegistryAction", "SipNotifyAction",
            "MonitorAction", "StopMonitorAction", "ChangeMonitorAction", "PauseMonitorAction", "UnpauseMonitorAction",
            "MeetMeMuteAction", "MeetMeUnmuteAction",
            "SkypeAccountPropertyAction", "SkypeAddBuddyAction", "SkypeBuddiesAction", "SkypeBuddyAction",
            "SkypeChatSendAction", "SkypeLicenseListAction", "SkypeLicenseStatusAction", "SkypeRemoveBuddyAction",
            "ZapDialOffhookAction", "ZapDndOffAction", "ZapDndOnAction", "ZapHangupAction", "ZapRestartAction",
            "ZapShowChannelsAction", "ZapTransferAction",
            "AgentCallbackLoginAction", "SetCdrUserFieldAction",
        ];
        string[] responses = ["SipShowPeerResponse", "SkypeBuddyResponse", "SkypeLicenseStatusResponse"];
        string[] events =
        [
            "AgentCallbackLoginEvent", "AgentCallbackLogoffEvent", "QueueMemberPausedEvent", "MusicOnHoldEvent",
            "FaxReceivedEvent", "DtmfEvent", "TransferEvent", "ChannelReloadEvent", "ChannelUpdateEvent", "JabberEventEvent",
            "AgiExecEvent", "AsyncAgiEvent", "SendFaxStatusEvent", "BridgeExecEvent", "ModuleLoadReportEvent",
        ];

        return
        [
            .. actions.Select(n => new MarkedSymbol(Actions + n + ".cs", n)),
            .. responses.Select(n => new MarkedSymbol("Verbara.Sdk.Ami/Responses/" + n + ".cs", n)),
            .. events.Select(n => new MarkedSymbol("Verbara.Sdk.Ami/Events/" + n + ".cs", n)),
            new("Verbara.Sdk.Ami/Events/Base/MeetMeEventBase.cs", "MeetMeEventBase"),
            new("Verbara.Sdk.Ami/Internal/MeetmeCompatibility.cs", "MeetmeCompatibility"),
            new("Verbara.Sdk.Agi/Commands/MeetmeCommand.cs", "MeetmeCommand"),
            new("Verbara.Sdk.Activities/Activities/MeetmeActivity.cs", "MeetmeActivity.UseConfBridge"),
        ];
    }

}

/// <summary>One <c>[Obsolete]</c> carrying the deprecation id: its file under <c>src/</c>, declaration key and line.</summary>
internal sealed record FoundMark(string File, string Key, int Line, bool IsError, string? UrlFormat);

/// <summary>What one <see cref="DeadModuleDeprecationScanner.Scan"/> found.</summary>
internal sealed record DeadModuleDeprecationReport(int SourceFiles, IReadOnlyList<FoundMark> Marks, IReadOnlyList<string> Violations)
{
    public bool IsClean => Violations.Count == 0;

    public override string ToString() =>
        Violations.Count == 0 ? "(clean)" : string.Join(Environment.NewLine, Violations.Select(v => "  " + v));
}
