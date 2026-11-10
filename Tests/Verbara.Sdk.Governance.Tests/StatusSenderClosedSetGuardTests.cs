using System.Text;
using System.Text.RegularExpressions;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for "the code that sends a channel <c>Status</c> is a closed set": every call site in <c>src/</c> of
/// <c>ReconcileChannelsAsync(</c>, <c>RequestChannelSnapshotAsync(</c> and <c>ReconcileChannelTableAsync(</c> —
/// declarations and comment lines excluded — is one of the sites listed here: the reconciliation sweep, the admission,
/// the admission's seam on the live server, the public reconciliation's channel half and the load's.
/// </summary>
/// <remarks>
/// <para>
/// A <c>Status</c> sent while a dropped event is still behind the event backlog lands in a buffer that cannot take its
/// answer and displaces live events. Each of the listed senders reads the gate that withholds it then; a new sender
/// would not, unless it is written to. The guard's value is the next sender: it fails until that sender is added here,
/// which is the moment to check it against the gate.
/// </para>
/// <para>
/// A site is named by its file, the member that encloses it and the method it calls, never by a line number, so an edit
/// that moves a sender keeps the guard green and one that adds a sender turns it red.
/// </para>
/// </remarks>
public sealed class StatusSenderClosedSetGuardTests
{
    private const string FailureReason = "every Status sender must respect the backlog gate";

    /// <summary>The senders as the guard landed: (file under <c>src/</c>, enclosing member, method called).</summary>
    private static readonly StatusSenderScanner.Site[] KnownSenders =
    [
        new("Verbara.Sdk.Hosting/ServerSweep.cs", "SweepAsync", "ReconcileChannelsAsync"),
        new("Verbara.Sdk.Sessions/Manager/CallSessionManager.Admission.cs", "AdmitExistingCallsCoreAsync", "RequestChannelSnapshotAsync"),
        new("Verbara.Sdk.Live/Server/VerbaraServer.cs", "RequestChannelSnapshotAsync", "ReconcileChannelsAsync"),
        new("Verbara.Sdk.Live/Server/VerbaraServer.cs", "ReconcileChannelsAsync", "ReconcileChannelTableAsync"),
        new("Verbara.Sdk.Live/Server/VerbaraServer.cs", "ReadStateAsync", "ReconcileChannelTableAsync"),
    ];

    [Fact]
    public void Guard_ShouldFindExactlyTheKnownStatusSenders_WhenScanningTheSrcTree()
    {
        var root = SrcTreeSource.SrcRoot();
        var found = SrcTreeSource.EnumerateSrcSources()
            .SelectMany(file => StatusSenderScanner.Scan(File.ReadAllText(file),
                Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')))
            .ToList();

        found.Select(hit => hit.Site).Should().BeEquivalentTo(KnownSenders, BuildFailureMessage(found));
    }

    [Fact]
    public void Scan_ShouldSkipTheDeclarationAndCommentLines_WhenTheyNameASender()
    {
        var source = string.Join('\n',
            "internal sealed class Sample",
            "{",
            "    /// <summary>Calls <see cref=\"ReconcileChannelsAsync(System.Threading.CancellationToken)\"/>.</summary>",
            "    public async ValueTask ReconcileChannelsAsync(CancellationToken cancellationToken = default)",
            "    {",
            "        // ReconcileChannelTableAsync( is named here only in a comment.",
            "        await ReconcileChannelTableAsync(cancellationToken);",
            "    }",
            "",
            "    internal ValueTask RequestChannelSnapshotAsync(CancellationToken cancellationToken = default) =>",
            "        ReconcileChannelsAsync(cancellationToken);",
            "}");

        StatusSenderScanner.Scan(source, "x.cs").Select(hit => hit.Site).Should().Equal(
            new StatusSenderScanner.Site("x.cs", "ReconcileChannelsAsync", "ReconcileChannelTableAsync"),
            new StatusSenderScanner.Site("x.cs", "RequestChannelSnapshotAsync", "ReconcileChannelsAsync"));
    }

    [Fact]
    public void Scan_ShouldReportANewSender_WhenAnotherMemberCallsTheReconciliation()
    {
        var source = string.Join('\n',
            "internal sealed class Healer",
            "{",
            "    private async Task HealAsync(VerbaraServer server, CancellationToken ct)",
            "    {",
            "        await server.ReconcileChannelsAsync(ct).ConfigureAwait(false);",
            "    }",
            "}");

        StatusSenderScanner.Scan(source, "Healer.cs").Select(hit => hit.Site).Should().Equal(
            new StatusSenderScanner.Site("Healer.cs", "HealAsync", "ReconcileChannelsAsync"));
    }

    private static string BuildFailureMessage(IReadOnlyList<StatusSenderScanner.Hit> found)
    {
        var message = new StringBuilder(FailureReason).Append(". Found:");
        foreach (var hit in found)
            message.Append(Environment.NewLine).Append("  ").Append(hit.Site).Append(" at line ").Append(hit.Line);
        return message.ToString();
    }
}

/// <summary>Finds the call sites of the three channel-snapshot senders in one C# source text.</summary>
internal static partial class StatusSenderScanner
{
    /// <summary>A call site: the file, the member that encloses it and the method it calls.</summary>
    internal sealed record Site(string File, string Member, string Calls);

    /// <summary>A call site and the 1-based line it was found on.</summary>
    internal sealed record Hit(Site Site, int Line);

    public static IEnumerable<Hit> Scan(string source, string relativePath)
    {
        var lines = source.Split('\n');
        string member = "";
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var declaration = MemberDeclaration().Match(line);
            if (declaration.Success)
                member = declaration.Groups["name"].Value;

            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                continue;

            var declared = declaration.Success ? declaration.Groups["name"].Value : null;
            foreach (var name in Sender().Matches(line)
                         .Select(call => call.Groups["name"].Value)
                         .Where(called => called != declared))
                yield return new Hit(new Site(relativePath, member, name), i + 1);
        }
    }

    [GeneratedRegex(@"\b(?<name>ReconcileChannelsAsync|RequestChannelSnapshotAsync|ReconcileChannelTableAsync)\(")]
    private static partial Regex Sender();

    // A method or local declaration: modifiers, a return type, then the name and its parameter list on the same line.
    [GeneratedRegex(@"^\s*(?:(?:public|private|internal|protected|static|async|override|sealed|virtual|partial|unsafe|new)\s+)+[\w<>\[\]?,.() ]+?\s+(?<name>\w+)\s*(?:<[^>]*>)?\(")]
    private static partial Regex MemberDeclaration();
}
