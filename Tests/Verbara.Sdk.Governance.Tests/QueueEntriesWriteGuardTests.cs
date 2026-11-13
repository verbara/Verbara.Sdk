using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for the queue table's write funnel: in <c>src/Verbara.Sdk.Live</c>, every write to a queue's
/// <c>Entries</c> goes through the queue manager's store and take helpers, which keep the count of held
/// <c>Uniqueid</c>s a <c>Hangup</c> reads before it walks the queues (<see cref="QueueEntriesWriteScanner"/>).
/// </summary>
public sealed class QueueEntriesWriteGuardTests
{
    private const string LivePackageRoot = "src/Verbara.Sdk.Live/";

    // Conservative floor: the store helper writes once and the take helper's two overloads remove once each.
    private const int MinimumHelperWrites = 3;

    // Conservative floor, below the package as this guard landed (21 source files).
    private const int MinimumScannedFiles = 15;

    [Fact]
    public void Guard_ShouldFindNoWriteToEntriesOutsideTheHelpers_InTheLivePackage()
    {
        var violations = LiveSources()
            .SelectMany(file => QueueEntriesWriteScanner.Scan(File.ReadAllText(file.FullPath), file.RelativePath))
            .ToList();

        violations.Should().BeEmpty(BuildFailureMessage(violations));
    }

    [Fact]
    public void Guard_ShouldCountTheHelpersWrites_WhenWalkingTheLivePackage()
    {
        var files = LiveSources().ToList();
        var helperWrites = files.Sum(file => QueueEntriesWriteScanner.CountHelperWrites(File.ReadAllText(file.FullPath)));

        files.Should().HaveCountGreaterThan(MinimumScannedFiles,
            "the guard must walk the Live package; a near-zero count means the locator broke");
        helperWrites.Should().BeGreaterThanOrEqualTo(MinimumHelperWrites,
            "the writes to Entries live in the two helpers; none found there means they were renamed or bypassed, and "
            + "the guard would be examining nothing");
    }

    [Fact]
    public void Scan_ShouldReportTheWrite_WhenACallerIsRemovedOutsideTheHelpers()
    {
        const string source =
            "namespace Fixture;\n" +
            "public sealed class QueueManager\n" +
            "{\n" +
            "    void OnCallerLeft(AsteriskQueue queue, string channel) => queue.Entries.TryRemove(channel, out _);\n" +
            "    void Join(AsteriskQueue queue, string channel, AsteriskQueueEntry entry) { queue.Entries[channel] = entry; }\n" +
            "}\n";

        var violations = QueueEntriesWriteScanner.Scan(source, "x.cs");

        violations.Should().Equal(
            new QueueEntriesWriteViolation("x.cs", 4, "queue.Entries.TryRemove(channel, out _)"),
            new QueueEntriesWriteViolation("x.cs", 5, "queue.Entries[channel]"));
    }

    [Fact]
    public void Scan_ShouldReportEveryWriteMethod_WhenCalledOnEntries()
    {
        const string source =
            "namespace Fixture;\n" +
            "public sealed class Consumer\n" +
            "{\n" +
            "    void M(AsteriskQueue q, AsteriskQueueEntry e)\n" +
            "    {\n" +
            "        q.Entries.TryAdd(\"a\", e); q.Entries.AddOrUpdate(\"a\", e, (_, _) => e); q.Entries.GetOrAdd(\"a\", e);\n" +
            "        q.Entries.TryUpdate(\"a\", e, e); q.Entries.Clear();\n" +
            "    }\n" +
            "}\n";

        QueueEntriesWriteScanner.Scan(source, "x.cs").Should().HaveCount(5);
    }

    [Fact]
    public void Scan_ShouldReportTheWrite_WhenItIsNullConditionalRemoveOrThroughACast()
    {
        const string source =
            "namespace Fixture;\n" +
            "public sealed class QueueManager\n" +
            "{\n" +
            "    void A(AsteriskQueue? q, string k) { q?.Entries.TryRemove(k, out _); }\n" +
            "    void B(AsteriskQueue q, string k) { q.Entries.Remove(k, out _); }\n" +
            "    void C(AsteriskQueue q, string k) { ((System.Collections.Generic.IDictionary<string, AsteriskQueueEntry>)q.Entries).Remove(k); }\n" +
            "    void D(AsteriskQueue q, string k) { q.Entries?.TryRemove(k, out _); }\n" +
            "    void E(AsteriskQueue? q, string k, AsteriskQueueEntry e) { q?.Entries[k] = e; }\n" +
            "    void F(AsteriskQueue q, string k) { _ = q.Entries?[k]; }\n" +
            "}\n";

        QueueEntriesWriteScanner.Scan(source, "x.cs").Select(violation => violation.Line).Should().Equal(4, 5, 6, 7, 8, 9);
    }

    [Fact]
    public void Scan_ShouldMissTheWrite_WhenItGoesThroughAnAlias()
    {
        // The documented limit: following a value needs types and flow, which a syntax scan does not have.
        const string source =
            "namespace Fixture;\n" +
            "public sealed class QueueManager\n" +
            "{\n" +
            "    void A(AsteriskQueue q, string k) { var e = q.Entries; e.TryRemove(k, out _); }\n" +
            "}\n";

        QueueEntriesWriteScanner.Scan(source, "x.cs").Should().BeEmpty("an alias is a known limit of the scan");
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheWritesSitInTheHelpers()
    {
        const string source =
            "namespace Fixture;\n" +
            "public sealed class QueueManager\n" +
            "{\n" +
            "    void StoreEntryLocked(AsteriskQueue queue, string key, AsteriskQueueEntry entry) { queue.Entries[key] = entry; }\n" +
            "    bool TakeEntryLocked(AsteriskQueue queue, string key, out AsteriskQueueEntry? entry) =>\n" +
            "        queue.Entries.TryRemove(key, out entry);\n" +
            "    bool Reads(AsteriskQueue queue, string key) => queue.Entries.TryGetValue(key, out _) && !queue.Entries.IsEmpty;\n" +
            "}\n";

        QueueEntriesWriteScanner.Scan(source, "x.cs").Should().BeEmpty("reads are not writes and the helpers are the funnel");
        QueueEntriesWriteScanner.CountHelperWrites(source).Should().Be(2);
    }

    [Fact]
    public void Scan_ShouldReportTheWrite_WhenAHelperHandsItToALambda()
    {
        const string source =
            "namespace Fixture;\n" +
            "public sealed class QueueManager\n" +
            "{\n" +
            "    void StoreEntryLocked(AsteriskQueue queue, string key, AsteriskQueueEntry entry)\n" +
            "    {\n" +
            "        System.Action later = () => queue.Entries.TryRemove(key, out _);\n" +
            "    }\n" +
            "}\n";

        QueueEntriesWriteScanner.Scan(source, "x.cs").Should().ContainSingle(
            "a lambda can run outside the queue's lock and after the count moved, so it is not the funnel");
    }

    [Fact]
    public void Scan_ShouldPass_WhenAnotherClassClearsItsOwnEntries()
    {
        // The server's batch of snapshot reports has a list of its own named Entries.
        const string source =
            "namespace Fixture;\n" +
            "internal sealed class SnapshotReports\n" +
            "{\n" +
            "    public System.Collections.Generic.List<int> Entries { get; } = new();\n" +
            "    public void Reset() => Entries.Clear();\n" +
            "}\n";

        QueueEntriesWriteScanner.Scan(source, "x.cs").Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldReportTheWrite_WhenTheQueueClearsItsOwnEntries()
    {
        const string source =
            "namespace Fixture;\n" +
            "public sealed class AsteriskQueue\n" +
            "{\n" +
            "    public void Reset() => Entries.Clear();\n" +
            "}\n";

        QueueEntriesWriteScanner.Scan(source, "x.cs").Should().ContainSingle();
    }

    private static IEnumerable<(string FullPath, string RelativePath)> LiveSources()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;
        return SrcTreeSource.EnumerateSrcSources()
            .Select(file => (FullPath: file, RelativePath: Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/')))
            .Where(file => file.RelativePath.StartsWith(LivePackageRoot, StringComparison.Ordinal));
    }

    private static string BuildFailureMessage(List<QueueEntriesWriteViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count).AppendLine(
            " write(s) to a queue's Entries outside QueueManager's StoreEntryLocked/TakeEntryLocked; they bypass the count "
            + "of held Uniqueids a Hangup reads before walking the queues:");
        foreach (var v in violations)
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line).Append("  ").AppendLine(v.Write);
        return sb.ToString();
    }
}
