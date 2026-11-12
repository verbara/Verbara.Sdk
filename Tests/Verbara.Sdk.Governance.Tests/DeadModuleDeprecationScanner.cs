using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>One symbol of the removed set: the package that declared it and its declaration key.</summary>
/// <param name="Package">The project folder under <c>src/</c> that declared it, for example <c>Verbara.Sdk.Ami</c>.</param>
/// <param name="Key">The type's name, or <c>Type.Member</c> for a member.</param>
internal sealed record RemovedSymbol(string Package, string Key);

/// <summary>
/// Reads the tree for the surface of dead Asterisk modules that 2.9.0 deprecated with one id and 2.10.0 removed, and
/// reports every way the tree brings it back: (a) a declaration of a removed type, or of the removed member, in the
/// package that declared it; (b) any use of the retired id, as an <c>[Obsolete]</c> mark, in a pragma, in an
/// assembly-wide suppression or in build configuration; (c) a migration guide that does not hold exactly one heading
/// for the id, which the 2.9.0 binaries' warning links open.
/// </summary>
/// <remarks>
/// A source scan, like the other guards of this project: it parses every non-generated <c>.cs</c> file under
/// <c>src/</c> and <c>Tests/</c> with Roslyn and reads declarations, attributes and pragmas as syntax, so it needs no
/// reference to the packages it checks and no build output, and a string literal that spells one is not one.
/// </remarks>
internal static class DeadModuleDeprecationScanner
{
    public const string DiagnosticId = "VSDK0005";

    public const string GuidePath = "docs/guides/obsolete-ami-events-removal-migration.md";

    /// <summary>The link every 2.9.0 mark carried; its fragment must keep resolving.</summary>
    public const string UrlFormat =
        "https://github.com/verbara/Verbara.Sdk/blob/main/" + GuidePath + "#vsdk0005";

    private static readonly string[] ObsoleteNames =
        ["Obsolete", "ObsoleteAttribute", "System.Obsolete", "System.ObsoleteAttribute", "global::System.Obsolete", "global::System.ObsoleteAttribute"];

    private static readonly string[] GeneratedSuffixes = [".g.cs", ".Designer.cs", ".generated.cs"];

    private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules"];

    private static readonly string[] ConfigurationSuffixes =
        [".csproj", ".props", ".targets", ".editorconfig", ".globalconfig", ".rsp"];

    private static readonly Regex GuideHeading = new(
        @"^ {0,3}#{1,6}[ \t]+(?<text>.*?)[ \t]*#*[ \t]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex Fence = new(@"^ {0,3}(```|~~~)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>The removed set: 49 types and one member.</summary>
    public static IReadOnlyList<RemovedSymbol> RemovedSet { get; } = BuildRemovedSet();

    public static DeadModuleDeprecationReport Scan(string repoRoot, IReadOnlyList<RemovedSymbol> removedSet)
    {
        var violations = new List<string>();
        var srcRoot = Path.Join(repoRoot, "src");
        var testsRoot = Path.Join(repoRoot, "Tests");

        var srcFiles = SourceFiles(srcRoot).ToList();
        if (srcFiles.Count == 0)
            violations.Add("found no source file under src/: the guard would pass on nothing");

        foreach (var file in srcFiles)
        {
            var tree = Parse(file);
            var relative = Relative(srcRoot, file);
            var package = relative.Split('/')[0];
            CheckRemovedDeclarations(tree, package, "src/" + relative, removedSet, violations);
            CheckRetiredId(tree, "src/" + relative, violations);
        }

        foreach (var file in SourceFiles(testsRoot))
            CheckRetiredId(Parse(file), "Tests/" + Relative(testsRoot, file), violations);

        CheckConfiguration(repoRoot, violations);
        CheckGuide(repoRoot, violations);

        return new DeadModuleDeprecationReport(srcFiles.Count, violations);
    }

    /// <summary>(a): a removed type, or the removed member, declared again in the package that declared it.</summary>
    private static void CheckRemovedDeclarations(SyntaxTree tree, string package, string relative,
        IReadOnlyList<RemovedSymbol> removedSet, List<string> violations)
    {
        var inPackage = removedSet.Where(s => s.Package == package).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
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

            foreach (var candidate in key.Split('|').Where(inPackage.Contains))
                violations.Add($"{relative}:{Line(node)}: {candidate} is declared again; it was removed in 2.10.0");
        }
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

    /// <summary>(b), in source: an <c>[Obsolete]</c> carrying the id, a pragma naming it, an assembly-wide suppression of it.</summary>
    private static void CheckRetiredId(SyntaxTree tree, string relative, List<string> violations)
    {
        var root = tree.GetRoot();
        foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            var name = attribute.Name.ToString();
            var args = attribute.ArgumentList?.Arguments ?? default;
            if (ObsoleteNames.Contains(name, StringComparer.Ordinal)
                && args.Any(a => a.NameEquals?.Name.Identifier.ValueText == "DiagnosticId" && LiteralText(a.Expression) == DiagnosticId))
            {
                violations.Add($"{relative}:{Line(attribute)}: an [Obsolete] carries the retired id {DiagnosticId}");
            }
            else if ((name.EndsWith("SuppressMessage", StringComparison.Ordinal) || name.EndsWith("SuppressMessageAttribute", StringComparison.Ordinal))
                && attribute.Parent is AttributeListSyntax { Target.Identifier.ValueText: "assembly" or "module" }
                && args.Any(a => LiteralText(a.Expression)?.Contains(DiagnosticId, StringComparison.Ordinal) == true))
            {
                violations.Add($"{relative}:{Line(attribute)}: an assembly-wide SuppressMessage names the retired id {DiagnosticId}");
            }
        }

        var namingPragmas = root.DescendantTrivia(descendIntoTrivia: true)
            .Select(t => t.GetStructure()).OfType<PragmaWarningDirectiveTriviaSyntax>()
            .Where(p => p.ErrorCodes.Any(c => string.Equals(c.ToString().Trim(), DiagnosticId, StringComparison.Ordinal)));
        foreach (var pragma in namingPragmas)
            violations.Add($"{relative}:{Line(pragma)}: a #pragma warning names the retired id {DiagnosticId}");
    }

    /// <summary>(b), in build configuration: any project, props, targets, editorconfig, globalconfig or response file naming the id.</summary>
    private static void CheckConfiguration(string repoRoot, List<string> violations)
    {
        foreach (var file in ConfigurationFiles(repoRoot))
        {
            var text = File.ReadAllText(file);
            var at = text.IndexOf(DiagnosticId, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                continue;

            var line = text[..at].Count(c => c == '\n') + 1;
            violations.Add($"{Relative(repoRoot, file)}:{line}: build configuration names the retired id {DiagnosticId}");
        }
    }

    /// <summary>(c): exactly one heading whose text is the id, at any level, outside fenced code.</summary>
    private static void CheckGuide(string repoRoot, List<string> violations)
    {
        var path = Path.Join(repoRoot, GuidePath);
        if (!File.Exists(path))
        {
            violations.Add($"{GuidePath}: missing; the {DiagnosticId} link of the 2.9.0 binaries points into it");
            return;
        }

        var headings = 0;
        var fenced = false;
        foreach (var line in File.ReadLines(path))
        {
            if (Fence.IsMatch(line))
            {
                fenced = !fenced;
                continue;
            }

            if (!fenced && GuideHeading.Match(line) is { Success: true } heading && heading.Groups["text"].Value == DiagnosticId)
                headings++;
        }

        if (headings != 1)
            violations.Add($"{GuidePath}: holds {headings} headings whose text is \"{DiagnosticId}\", not exactly one");

        var fragment = UrlFormat[(UrlFormat.IndexOf('#', StringComparison.Ordinal) + 1)..];
        if (fragment != Slug(DiagnosticId))
            violations.Add($"UrlFormat fragment \"{fragment}\" is not the slug \"{Slug(DiagnosticId)}\" of the \"{DiagnosticId}\" heading");
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

    private static List<RemovedSymbol> BuildRemovedSet()
    {
        const string Ami = "Verbara.Sdk.Ami";
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
            .. actions.Concat(responses).Concat(events).Select(n => new RemovedSymbol(Ami, n)),
            new(Ami, "MeetMeEventBase"),
            new(Ami, "MeetmeCompatibility"),
            new("Verbara.Sdk.Agi", "MeetmeCommand"),
            new("Verbara.Sdk.Activities", "MeetmeActivity.UseConfBridge"),
        ];
    }
}

/// <summary>What one <see cref="DeadModuleDeprecationScanner.Scan"/> found.</summary>
internal sealed record DeadModuleDeprecationReport(int SourceFiles, IReadOnlyList<string> Violations)
{
    public bool IsClean => Violations.Count == 0;

    public override string ToString() =>
        Violations.Count == 0 ? "(clean)" : string.Join(Environment.NewLine, Violations.Select(v => "  " + v));
}
