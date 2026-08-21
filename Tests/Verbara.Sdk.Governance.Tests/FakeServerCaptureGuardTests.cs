using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process regression guard: parses every test source file with Roslyn and fails the build if an
/// in-process fake server hands tests the live collection its own receive loop writes. Zero
/// tolerance — the repo carries no such member today (they were all converted to snapshots) and must
/// never gain one, because the defect is invisible on a quiet machine and shows up as a flaky count
/// or an enumerator throw under load. Includes a liveness self-test in two dimensions (files walked,
/// and fake-server types actually recognised) plus detector unit tests pinning both the true
/// positives and the immunity of the configuration collections that legitimately stay writable.
/// </summary>
public sealed class FakeServerCaptureGuardTests
{
    // Conservative floors, both well below reality (~414 test files, 10 fake-server types), so
    // churn is tolerated but a broken locator or a renamed-away detector cannot read as green.
    private const int MinimumScannedFiles = 250;
    private const int MinimumFakeServerTypes = 6;

    [Fact]
    public void Guard_ShouldFindNoLiveCaptureCollections_InTestTree()
    {
        var repoRoot = Directory.GetParent(TestTreeSource.TestsRoot())!.FullName;

        var violations = new List<FakeServerCaptureViolation>();
        foreach (var file in TestTreeSource.EnumerateTestSources())
        {
            var source = File.ReadAllText(file);
            var relative = ToRelative(repoRoot, file);
            violations.AddRange(FakeServerCaptureScanner.Scan(source, relative));
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
            "the capture scan would be a false green");
    }

    [Fact]
    public void Guard_ShouldRecogniseTheRepoFakeServers_WhenWalkingTestTree()
    {
        // Walking every file proves nothing if the detector recognises no fake server in any of
        // them — a rename would silence this guard without failing anything.
        var found = TestTreeSource.EnumerateTestSources()
            .SelectMany(file => FakeServerCaptureScanner.FindFakeServerTypes(File.ReadAllText(file)))
            .ToArray();

        found.Should().HaveCountGreaterThan(
            MinimumFakeServerTypes,
            "the detector must actually recognise this repo's in-process fake servers; finding none " +
            "means the naming convention moved and the guard now scans for a shape that no longer exists");
        found.Should().Contain("WebSocketTestServer", "the shared substrate is in scope too");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenFakeServerExposesMutableListItWrites()
    {
        const string source =
            "class DeepgramFakeServer {\n" +
            "    public System.Collections.Generic.List<string> ReceivedMessages { get; } = new();\n" +
            "    void Capture(string m) { ReceivedMessages.Add(m); }\n" +
            "}";

        var violations = FakeServerCaptureScanner.Scan(source, "x.cs");

        var violation = violations.Should().ContainSingle().Which;
        violation.Rule.Should().Be("MutableCapture");
        violation.Member.Should().Be("ReceivedMessages");
        violation.Path.Should().Be("x.cs");
    }

    [Fact]
    public void Scan_ShouldReportOneBasedLine_WhenCaptureIsNotOnFirstLine()
    {
        const string source =
            "class DeepgramFakeServer {\n" +
            "    private int _count;\n" +
            "    public System.Collections.Generic.List<string> ReceivedMessages { get; } = new();\n" +
            "    void Capture(string m) { ReceivedMessages.Add(m); }\n" +
            "}";

        var violations = FakeServerCaptureScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Line.Should().Be(3);
    }

    [Fact]
    public void Scan_ShouldFlag_WhenMutablePropertyAliasesTheLivePrivateField()
    {
        // Renaming the backing field and aliasing it through a property does not change what the
        // test gets. The rule follows the collection, not the identifier it is reached by.
        const string source =
            "class RealtimeFakeServer {\n" +
            "    private readonly System.Collections.Generic.List<string> _received = new();\n" +
            "    public System.Collections.Generic.List<string> ReceivedMessages => _received;\n" +
            "    void Capture(string m) { lock (_received) _received.Add(m); }\n" +
            "}";

        var violations = FakeServerCaptureScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Rule.Should().Be("MutableCapture");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenFakeServerExposesPrivateListBehindReadOnlyInterface()
    {
        // The same defect wearing an interface: nothing can be added through it, but the caller
        // still enumerates a list the receive loop appends to.
        const string source =
            "class LmntWsFakeServer {\n" +
            "    private readonly System.Collections.Generic.List<string> _received = new();\n" +
            "    public System.Collections.Generic.IReadOnlyList<string> ReceivedJsonMessages => _received;\n" +
            "    void Capture(string m) { _received.Add(m); }\n" +
            "}";

        var violations = FakeServerCaptureScanner.Scan(source, "x.cs");

        var violation = violations.Should().ContainSingle().Which;
        violation.Rule.Should().Be("LiveCaptureAlias");
        violation.Member.Should().Be("ReceivedJsonMessages");
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenSnapshotPropertyCopiesUnderLock()
    {
        // The shape every fake in this repo was converted to.
        const string source =
            "class LmntWsFakeServer {\n" +
            "    private readonly System.Collections.Generic.List<string> _received = new();\n" +
            "    public System.Collections.Generic.IReadOnlyList<string> ReceivedJsonMessages\n" +
            "    { get { lock (_received) return _received.ToArray(); } }\n" +
            "    void Capture(string m) { lock (_received) _received.Add(m); }\n" +
            "}";

        var violations = FakeServerCaptureScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenPublicListIsConfigurationTheServerOnlyReads()
    {
        // EventsToSend / AudioFramesToSend / ResultMessages: written by the test before Start(),
        // only enumerated inside the fake. Not a capture, so no synchronisation is warranted.
        const string source =
            "class RealtimeFakeServer {\n" +
            "    public System.Collections.Generic.List<string> EventsToSend { get; } = new();\n" +
            "    public System.Collections.Generic.List<byte[]> AudioFramesToSend { get; } = new();\n" +
            "    public System.Collections.Generic.List<string> ResultMessages { get; } = new();\n" +
            "    void Send() { foreach (var e in EventsToSend) { } foreach (var f in AudioFramesToSend) { } }\n" +
            "}";

        var violations = FakeServerCaptureScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenConfigurationListIsSeededInConstructor()
    {
        // Several fakes seed a realistic default payload in the constructor so a test that does not
        // care about the payload still exercises one. A constructor runs before any caller holds the
        // object, so nothing can be reading it concurrently — still configuration, not capture.
        const string source =
            "class LmntWsFakeServer {\n" +
            "    public System.Collections.Generic.List<byte[]> AudioFramesToSend { get; } = new();\n" +
            "    public LmntWsFakeServer() { AudioFramesToSend.AddRange(ReadRecordedTone()); }\n" +
            "    static byte[][] ReadRecordedTone() => System.Array.Empty<byte[]>();\n" +
            "    void Send() { foreach (var f in AudioFramesToSend) { } }\n" +
            "}";

        var violations = FakeServerCaptureScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenSeededListIsAlsoWrittenBySessionHandler()
    {
        // The constructor exemption is about seeding, not about the member: a list the session
        // handler also appends to is a capture no matter how it was initialised.
        const string source =
            "class LmntWsFakeServer {\n" +
            "    public System.Collections.Generic.List<string> Frames { get; } = new();\n" +
            "    public LmntWsFakeServer() { Frames.Add(\"seed\"); }\n" +
            "    void Capture(string m) { Frames.Add(m); }\n" +
            "}";

        var violations = FakeServerCaptureScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Rule.Should().Be("MutableCapture");
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenSnapshotArrayIsRepublishedWholesale()
    {
        // Assigning a fresh array is publishing a snapshot, not sharing a buffer — the fix, not the defect.
        const string source =
            "class RealtimeFakeServer {\n" +
            "    private readonly System.Collections.Generic.List<string> _received = new();\n" +
            "    private volatile string[] _snapshot = System.Array.Empty<string>();\n" +
            "    public System.Collections.Generic.IReadOnlyList<string> FramesCapturedWhenAnswering => _snapshot;\n" +
            "    void Freeze() { lock (_received) _snapshot = _received.ToArray(); }\n" +
            "    void Capture(string m) { lock (_received) _received.Add(m); }\n" +
            "}";

        var violations = FakeServerCaptureScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenPrivateListIsNeverExposed()
    {
        const string source =
            "class DeepgramFakeServer {\n" +
            "    private readonly System.Collections.Generic.List<string> _received = new();\n" +
            "    void Capture(string m) { _received.Add(m); }\n" +
            "}";

        var violations = FakeServerCaptureScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTypeIsNotAFakeServer()
    {
        // Production collections and ordinary test helpers are out of scope: this rule is about a
        // background receive loop racing an assertion, which only fake servers have.
        const string source =
            "class TranscriptAccumulator {\n" +
            "    public System.Collections.Generic.List<string> Received { get; } = new();\n" +
            "    void Capture(string m) { Received.Add(m); }\n" +
            "}";

        var violations = FakeServerCaptureScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenCaptureShapeAppearsInPlainStringLiteral()
    {
        // The immunity that keeps THIS file's own C#-snippet fixtures from self-flagging.
        const string source =
            "class C { void M() { var s = \"class XFakeServer { public List<string> R { get; } }\"; } }";

        var violations = FakeServerCaptureScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    private static string ToRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string BuildFailureMessage(List<FakeServerCaptureViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count)
            .AppendLine(" fake-server member(s) hand tests a collection the receive loop mutates:");
        foreach (var v in violations.OrderBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Line))
        {
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line)
                .Append("  [").Append(v.Rule).Append("]  ").AppendLine(v.Detail);
        }

        sb.AppendLine(
            "Keep the backing collection private and expose IReadOnlyList<T> whose getter returns a " +
            "copy taken under the same lock the receive loop writes under. Configuration collections " +
            "a test fills before Start() are exempt — this guard only reports members the fake itself writes.");
        return sb.ToString();
    }
}
