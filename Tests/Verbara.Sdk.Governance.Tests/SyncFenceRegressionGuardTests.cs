using System.Text;
using System.Text.Json;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process regression guard: parses every test source file with Roslyn and fails the build if a
/// test file gains a NEW wall-clock synchronization barrier beyond the grandfathered baseline
/// (ADR-0004 net-new-only ratchet). Pre-existing barriers recorded in
/// <c>sync-fence-baseline.json</c> are tolerated; new ones must be removed or annotated with an
/// inline <c>// fence-allow:</c> marker. Includes liveness self-tests (the scan must actually walk a
/// large file set) and detector unit tests that pin both true positives and the prose/string
/// false-positive immunity.
/// </summary>
public sealed class SyncFenceRegressionGuardTests
{
    // Conservative floor: a floor well below the real test-file count defeats the "found zero
    // files -> false green" failure mode while tolerating churn.
    private const int MinimumScannedFiles = 250;

    private const string BaselineFileName = "sync-fence-baseline.json";

    [Fact]
    public void Guard_ShouldNotExceedBaseline_InTestTree()
    {
        var repoRoot = Directory.GetParent(TestTreeSource.TestsRoot())!.FullName;

        // Group unmarked violations by repo-relative path into the current counts.
        var currentCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in TestTreeSource.EnumerateTestSources())
        {
            var source = File.ReadAllText(file);
            var relative = ToRelative(repoRoot, file);
            var count = SyncFenceScanner.Scan(source, relative).Count;
            if (count > 0)
                currentCounts[relative] = count;
        }

        var baseline = LoadBaseline(Path.Combine(repoRoot, BaselineFileName));

        // A file fails only if its current unmarked-barrier count EXCEEDS its grandfathered
        // baseline (missing file => baseline 0). Files at or below baseline pass.
        var grown = new List<(string Path, int Baseline, int Current)>();
        foreach (var pair in currentCounts)
        {
            var allowed = baseline.TryGetValue(pair.Key, out var b) ? b : 0;
            if (pair.Value > allowed)
                grown.Add((pair.Key, allowed, pair.Value));
        }

        grown.Should().BeEmpty(BuildRatchetFailureMessage(grown));
    }

    [Fact]
    public void Guard_ShouldScanManyFiles_WhenWalkingTestTree()
    {
        var count = TestTreeSource.EnumerateTestSources().Count();

        count.Should().BeGreaterThan(
            MinimumScannedFiles,
            "the guard must walk the real test tree; a near-zero count means the locator broke and " +
            "the fence scan would be a false green");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenUnmarkedTaskDelay()
    {
        const string source = "class C { async System.Threading.Tasks.Task M() { await Task.Delay(5); } }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Task.Delay");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenUnmarkedThreadSleep()
    {
        const string source = "class C { void M() { Thread.Sleep(100); } }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Thread.Sleep");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenFullyQualifiedThreadSleep()
    {
        const string source = "class C { void M() { System.Threading.Thread.Sleep(1); } }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Thread.Sleep");
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTaskDelayInComment()
    {
        const string source = "class C { void M() { /* waited via Task.Delay earlier */ } }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTaskDelayInXmlDoc()
    {
        const string source =
            "/// <c>Task.Delay</c> guess\n" +
            "class C { void M() { } }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTaskDelayInStringLiteral()
    {
        const string source = "class C { void M() { var s = \"call Task.Delay( now\"; } }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenWellFormedMarkerOnSameLine()
    {
        const string source =
            "class C { async System.Threading.Tasks.Task M() {\n" +
            "    await Task.Delay(5); // fence-allow: SETTLE — wait for TTL\n" +
            "} }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenMarkerOnPrecedingLine()
    {
        const string source =
            "class C { async System.Threading.Tasks.Task M() {\n" +
            "    // fence-allow: SETTLE — wait for TTL\n" +
            "    await Task.Delay(5);\n" +
            "} }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenBareMarkerHasNoCategory()
    {
        const string source =
            "class C { async System.Threading.Tasks.Task M() {\n" +
            "    await Task.Delay(5); // fence-allow:\n" +
            "} }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Task.Delay");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenMarkerCategoryUnknown()
    {
        const string source =
            "class C { async System.Threading.Tasks.Task M() {\n" +
            "    await Task.Delay(5); // fence-allow: WHATEVER — y\n" +
            "} }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Task.Delay");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenMarkerHasNoReason()
    {
        const string source =
            "class C { async System.Threading.Tasks.Task M() {\n" +
            "    await Task.Delay(5); // fence-allow: SETTLE\n" +
            "} }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Task.Delay");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenStopwatchSpinLoop()
    {
        const string source =
            "class C { void M(System.Diagnostics.Stopwatch sw) {\n" +
            "    while (sw.Elapsed < System.TimeSpan.FromSeconds(1)) { }\n" +
            "} }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Stopwatch.spin");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenUnmarkedCallFollowsMarkerAcrossBlankLine()
    {
        const string source =
            "class C { async System.Threading.Tasks.Task M() {\n" +
            "    await Task.Delay(5); // fence-allow: SETTLE — legit\n" +
            "\n" +
            "    await Task.Delay(9);\n" +
            "} }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        // The marker excuses only the first call; the blank line breaks the immediately-preceding
        // adjacency, so the second (unmarked) call is flagged. Its line is the 4th physical line.
        violations.Should().ContainSingle().Which.Line.Should().Be(4);
    }

    [Fact]
    public void Scan_ShouldFlag_WhenMarkerTextIsInsideStringLiteral()
    {
        const string source =
            "class C { void M() {\n" +
            "    var s = \"// fence-allow: SETTLE — fake\"; Thread.Sleep(1);\n" +
            "} }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("Thread.Sleep");
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenElapsedLoopHasNoStopwatch()
    {
        const string source =
            "class C { void M(SomeFsm fsm) {\n" +
            "    while (!fsm.Elapsed) { }\n" +
            "} }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenStaticThreadImport()
    {
        const string source =
            "using static System.Threading.Thread;\n" +
            "class C { void M() { } }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("ThreadingAlias");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTaskAliasImport()
    {
        const string source =
            "using T = System.Threading.Tasks.Task;\n" +
            "class C { void M() { } }";

        var violations = SyncFenceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Api.Should().Be("ThreadingAlias");
    }

    private static string ToRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static Dictionary<string, int> LoadBaseline(string path)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return result;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("files", out var files)
            && files.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in files.EnumerateObject())
                result[property.Name] = property.Value.GetInt32();
        }

        return result;
    }

    private static string BuildRatchetFailureMessage(List<(string Path, int Baseline, int Current)> grown)
    {
        var sb = new StringBuilder();
        sb.Append(grown.Count)
            .AppendLine(" test file(s) gained NEW unmarked wall-clock synchronization barrier(s) beyond the grandfathered baseline:");
        foreach (var g in grown.OrderBy(g => g.Path, StringComparer.Ordinal))
        {
            sb.Append("  ").Append(g.Path)
                .Append("  (baseline=").Append(g.Baseline)
                .Append(", current=").Append(g.Current).AppendLine(")");
        }

        sb.AppendLine(
            "Remove the new barrier or annotate it with // fence-allow: CATEGORY — reason " +
            "(CATEGORY ∈ SIMULATED-WORK | GUARD-TIMEOUT | SETTLE | LOOP-DRIVER). " +
            "Never RAISE a count in sync-fence-baseline.json — only lower it as barriers are removed/annotated.");
        return sb.ToString();
    }
}
