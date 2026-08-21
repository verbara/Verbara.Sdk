using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process regression guard for ADR-0052 F3: a cancellation test hands the cancelled token to the
/// subject only. Handing it to <c>ToListAsync</c>/<c>ToArrayAsync</c>/<c>WithCancellation</c> as well
/// makes the assertion pass over a silent <c>yield break</c> exactly as it passes over a propagated
/// throw — which is how a truncating synthesizer shipped against ten green cancellation tests. The
/// defect it was written for is a matter of record, so §5.10 negative-tests it against those ten
/// rather than against a fixture. Shares <see cref="TestTreeSource"/> and the reporting shape with
/// <see cref="FakeServerCaptureGuardTests"/>.
/// </summary>
public sealed class CancellationProvenanceGuardTests
{
    // Conservative floors, both well below reality (~414 test files, ~200 enumeration sites).
    private const int MinimumScannedFiles = 250;
    private const int MinimumEnumerationSites = 80;

    [Fact]
    public void Guard_ShouldFindNoCancelledTokenHandedToEnumerator_InTestTree()
    {
        var repoRoot = Directory.GetParent(TestTreeSource.TestsRoot())!.FullName;

        var violations = new List<CancellationProvenanceViolation>();
        foreach (var file in TestTreeSource.EnumerateTestSources())
        {
            var source = File.ReadAllText(file);
            var relative = ToRelative(repoRoot, file);
            violations.AddRange(CancellationProvenanceScanner.Scan(source, relative));
        }

        violations.Should().BeEmpty(BuildFailureMessage(violations));
    }

    [Fact]
    public void Guard_ShouldScanManyFiles_WhenWalkingTestTree()
    {
        var count = TestTreeSource.EnumerateTestSources().Count();

        count.Should().BeGreaterThan(
            MinimumScannedFiles,
            "the guard must walk the real Tests/ tree; a near-zero count means the locator broke and " +
            "the provenance scan would be a false green");
    }

    [Fact]
    public void Guard_ShouldRecogniseTheEnumerationIdiom_WhenWalkingTestTree()
    {
        // If the suites move to a different enumeration helper, this detector goes quiet without
        // failing anything — the file count would not move either.
        var sites = TestTreeSource.EnumerateTestSources()
            .Sum(file => CancellationProvenanceScanner.CountEnumerationSites(File.ReadAllText(file)));

        sites.Should().BeGreaterThan(
            MinimumEnumerationSites,
            "the detector must actually recognise the enumeration idiom this repo's async tests use; " +
            "finding almost none means the idiom moved and the guard now scans for a shape that no " +
            "longer exists");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenCancelledTokenIsPassedToToListAsync()
    {
        const string source =
            "class T {\n" +
            "    async System.Threading.Tasks.Task M() {\n" +
            "        using var cts = new System.Threading.CancellationTokenSource();\n" +
            "        cts.Cancel();\n" +
            "        await Subject(cts.Token).ToListAsync(cts.Token);\n" +
            "    }\n" +
            "}";

        var violations = CancellationProvenanceScanner.Scan(source, "x.cs");

        var violation = violations.Should().ContainSingle().Which;
        violation.Line.Should().Be(5);
        violation.Method.Should().Be("M");
        violation.Path.Should().Be("x.cs");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenCancelledTokenIsPassedToToArrayAsync()
    {
        const string source =
            "class T {\n" +
            "    async System.Threading.Tasks.Task M() {\n" +
            "        using var cts = new System.Threading.CancellationTokenSource();\n" +
            "        cts.Cancel();\n" +
            "        await Subject(cts.Token).ToArrayAsync(cts.Token);\n" +
            "    }\n" +
            "}";

        var violations = CancellationProvenanceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenCancelledTokenIsPassedToWithCancellation()
    {
        const string source =
            "class T {\n" +
            "    async System.Threading.Tasks.Task M() {\n" +
            "        using var cts = new System.Threading.CancellationTokenSource();\n" +
            "        cts.Cancel();\n" +
            "        await foreach (var x in Subject(cts.Token).WithCancellation(cts.Token)) { }\n" +
            "    }\n" +
            "}";

        var violations = CancellationProvenanceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenCancelHappensInsideANestedLambda()
    {
        // The cancel-on-first-frame trigger shape: the cancel is in a fire-and-forget lambda, the
        // enumeration is in the method body.
        const string source =
            "class T {\n" +
            "    async System.Threading.Tasks.Task M() {\n" +
            "        using var cts = new System.Threading.CancellationTokenSource();\n" +
            "        var trigger = System.Threading.Tasks.Task.Run(async () => await cts.CancelAsync());\n" +
            "        await Subject(cts.Token).ToListAsync(cts.Token);\n" +
            "        await trigger;\n" +
            "    }\n" +
            "}";

        var violations = CancellationProvenanceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Line.Should().Be(5);
    }

    [Fact]
    public void Scan_ShouldFlag_WhenCancelledTokenIsRenamedBeforeTheEnumeration()
    {
        const string source =
            "class T {\n" +
            "    async System.Threading.Tasks.Task M() {\n" +
            "        using var cts = new System.Threading.CancellationTokenSource();\n" +
            "        var token = cts.Token;\n" +
            "        cts.Cancel();\n" +
            "        await Subject(token).ToListAsync(token);\n" +
            "    }\n" +
            "}";

        var violations = CancellationProvenanceScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Line.Should().Be(6);
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenEnumerationTakesCancellationTokenNone()
    {
        // The shape all ten VoiceAi cancellation tests were converted to.
        const string source =
            "class T {\n" +
            "    async System.Threading.Tasks.Task M() {\n" +
            "        using var cts = new System.Threading.CancellationTokenSource();\n" +
            "        cts.Cancel();\n" +
            "        await Subject(cts.Token).ToListAsync(System.Threading.CancellationToken.None);\n" +
            "    }\n" +
            "}";

        var violations = CancellationProvenanceScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenEnumerationTakesNoArgument()
    {
        const string source =
            "class T {\n" +
            "    async System.Threading.Tasks.Task M() {\n" +
            "        using var cts = new System.Threading.CancellationTokenSource();\n" +
            "        cts.Cancel();\n" +
            "        await Subject(cts.Token).ToListAsync();\n" +
            "    }\n" +
            "}";

        var violations = CancellationProvenanceScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTheTokenIsNeverCancelled()
    {
        // This is the immunity that matters most. A token that only ever bounds a hang is the
        // legitimate case, and a detector that cannot tell it apart gets muted.
        const string source =
            "class T {\n" +
            "    async System.Threading.Tasks.Task M() {\n" +
            "        using var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(10));\n" +
            "        var frames = await Subject(cts.Token).ToListAsync(cts.Token);\n" +
            "    }\n" +
            "}";

        var violations = CancellationProvenanceScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenADifferentSourceIsCancelled()
    {
        // Two sources in one method: cancelling the server's lifetime token says nothing about the
        // token the enumeration received.
        const string source =
            "class T {\n" +
            "    async System.Threading.Tasks.Task M() {\n" +
            "        using var serverCts = new System.Threading.CancellationTokenSource();\n" +
            "        using var hangBound = new System.Threading.CancellationTokenSource();\n" +
            "        serverCts.Cancel();\n" +
            "        await Subject(hangBound.Token).ToListAsync(hangBound.Token);\n" +
            "    }\n" +
            "}";

        var violations = CancellationProvenanceScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenProvenanceShapeAppearsInPlainStringLiteral()
    {
        // The immunity that keeps THIS file's own C#-snippet fixtures from self-flagging.
        const string source =
            "class C { void M() { var s = \"cts.Cancel(); await S(cts.Token).ToListAsync(cts.Token);\"; } }";

        var violations = CancellationProvenanceScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    private static string ToRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string BuildFailureMessage(List<CancellationProvenanceViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count)
            .AppendLine(" cancellation test(s) hand the cancelled token to the enumerator as well as the subject:");
        foreach (var v in violations.OrderBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Line))
        {
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line)
                .Append("  [").Append(v.Method).Append("]  ").AppendLine(v.Detail);
        }

        sb.AppendLine(
            "The enumerator checks the token at every iteration boundary, so it supplies the " +
            "OperationCanceledException the assertion is looking for and the subject is never tested " +
            "(ADR-0052 F3). Hand the token to the subject and enumerate with CancellationToken.None.");
        return sb.ToString();
    }
}
