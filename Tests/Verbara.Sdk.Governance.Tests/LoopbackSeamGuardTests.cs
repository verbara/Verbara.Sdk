using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process regression guard: parses every product AND test source file with Roslyn and fails the
/// build if a fake-server seam reintroduces the ambiguous <c>localhost</c> host token. Like the
/// reflection ban — and unlike the sync-fence ratchet — this guard is ZERO-TOLERANCE: the repo
/// carries no such seam today and must never gain one, because <c>localhost</c> resolves <c>::1</c>
/// before <c>127.0.0.1</c> while the in-process fakes bind IPv4-only listeners, letting two fake
/// servers share a port number and cross-wire (a WebSocket upgrade answered <c>200</c> instead of
/// <c>101</c>). Includes a liveness self-test (the scan must actually walk a large file set) and
/// detector unit tests that pin both the true positives and the immunity of the real product
/// defaults that legitimately name <c>localhost</c> with a fixed port (ARI 8088, OTLP 4317,
/// Toxiproxy 8474).
/// </summary>
public sealed class LoopbackSeamGuardTests
{
    // Conservative floor: a floor well below the real src+test file count (~1260) defeats the
    // "found zero files -> false green" failure mode while tolerating churn.
    private const int MinimumScannedFiles = 700;

    [Fact]
    public void Guard_ShouldFindNoLocalhostFakeServerSeams_InSrcAndTestTrees()
    {
        var repoRoot = Directory.GetParent(TestTreeSource.TestsRoot())!.FullName;

        var violations = new List<LoopbackSeamViolation>();
        foreach (var file in EnumerateScannedSources())
        {
            var source = File.ReadAllText(file);
            var relative = ToRelative(repoRoot, file);
            violations.AddRange(LoopbackSeamScanner.Scan(source, relative));
        }

        violations.Should().BeEmpty(BuildFailureMessage(violations));
    }

    [Fact]
    public void Guard_ShouldScanManyFiles_WhenWalkingSrcAndTestTrees()
    {
        var count = EnumerateScannedSources().Count();

        count.Should().BeGreaterThan(
            MinimumScannedFiles,
            "the guard must walk the real src/ + Tests/ trees; a near-zero count means the locator " +
            "broke and the loopback scan would be a false green");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenInterpolatedLocalhostPortInWebSocketUri()
    {
        const string source =
            "class C { System.Uri M(int p) => new System.Uri($\"ws://localhost:{p}/v1/listen\"); }";

        var violations = LoopbackSeamScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Rule.Should().Be("LoopbackInterpolation");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenInterpolatedLocalhostPortInBaseUriProperty()
    {
        const string source =
            "class C { public int Port { get; } public string BaseUri => $\"http://localhost:{Port}/generate\"; }";

        var violations = LoopbackSeamScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Rule.Should().Be("LoopbackInterpolation");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenHttpListenerPrefixUsesInterpolatedLocalhost()
    {
        const string source =
            "class C { void M(System.Net.HttpListener listener, int port) {\n" +
            "    listener.Prefixes.Add($\"http://localhost:{port}/\");\n" +
            "} }";

        var violations = LoopbackSeamScanner.Scan(source, "x.cs");

        // Both rules fire on this line — the interpolation shape AND the prefix registration.
        violations.Should().HaveCount(2);
        violations.Should().Contain(v => v.Rule == "LoopbackInterpolation");
        violations.Should().Contain(v => v.Rule == "HttpListenerPrefix");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenHttpListenerPrefixUsesLiteralLocalhost()
    {
        const string source =
            "class C { void M(System.Net.HttpListener listener) {\n" +
            "    listener.Prefixes.Add(\"http://localhost:5000/\");\n" +
            "} }";

        var violations = LoopbackSeamScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Rule.Should().Be("HttpListenerPrefix");
    }

    [Fact]
    public void Scan_ShouldReportOneBasedLine_WhenSeamIsNotOnFirstLine()
    {
        const string source =
            "class C {\n" +
            "    System.Uri M(int p) =>\n" +
            "        new System.Uri($\"ws://localhost:{p}/x\");\n" +
            "}";

        var violations = LoopbackSeamScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Line.Should().Be(3);
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenLoopbackLiteralInterpolatesPort()
    {
        const string source =
            "class C { System.Uri M(int p) => new System.Uri($\"ws://127.0.0.1:{p}/v1/listen\"); }";

        var violations = LoopbackSeamScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenHttpListenerPrefixUsesLoopbackLiteral()
    {
        const string source =
            "class C { void M(System.Net.HttpListener listener, int port) {\n" +
            "    listener.Prefixes.Add($\"http://127.0.0.1:{port}/\");\n" +
            "} }";

        var violations = LoopbackSeamScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenAriDefaultUsesFixedPort()
    {
        // Real product default (AriClientOptions.BaseUrl) — a fixed port, never a fake-server seam.
        const string source = "class C { public string BaseUrl { get; set; } = \"http://localhost:8088\"; }";

        var violations = LoopbackSeamScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenOtlpEndpointUsesFixedPort()
    {
        // Real OTel exporter endpoint — a fixed port, never a fake-server seam.
        const string source = "class C { void M() { var u = new System.Uri(\"http://localhost:4317\"); } }";

        var violations = LoopbackSeamScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenToxiproxyDefaultUsesFixedPort()
    {
        // Real Toxiproxy control API default — a fixed port, never a fake-server seam.
        const string source =
            "class C { const string Url = \"http://localhost:8474\"; }";

        var violations = LoopbackSeamScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenInterpolatedFixedPortFollowsLocalhost()
    {
        // A fixed port that merely sits next to an interpolation is NOT a fake-server seam.
        const string source =
            "class C { string M(string p) => $\"http://localhost:8088/ari/{p}\"; }";

        var violations = LoopbackSeamScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenLocalhostSeamInComment()
    {
        const string source =
            "class C { void M() { /* was $\"ws://localhost:{port}/x\" before the IPv4 fix */ } }";

        var violations = LoopbackSeamScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenLocalhostSeamInXmlDoc()
    {
        const string source =
            "/// Never write <c>ws://localhost:{port}</c>; the fakes are IPv4-only.\n" +
            "class C { void M() { } }";

        var violations = LoopbackSeamScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenLocalhostSeamInPlainStringLiteral()
    {
        // This is the immunity that keeps THIS file's own C#-snippet fixtures from self-flagging.
        const string source =
            "class C { void M() { var s = \"listener.Prefixes.Add($\\\"http://localhost:{port}/\\\");\"; } }";

        var violations = LoopbackSeamScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    private static IEnumerable<string> EnumerateScannedSources() =>
        SrcTreeSource.EnumerateSrcSources().Concat(TestTreeSource.EnumerateTestSources());

    private static string ToRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string BuildFailureMessage(List<LoopbackSeamViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count)
            .AppendLine(" fake-server seam(s) name the ambiguous host 'localhost':");
        foreach (var v in violations.OrderBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Line))
        {
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line)
                .Append("  [").Append(v.Rule).Append("]  ").AppendLine(v.Detail);
        }

        sb.AppendLine(
            "Replace the host token with the IPv4 literal '127.0.0.1' on BOTH the bind and the dial " +
            "side. 'localhost' resolves ::1 first; the in-process fakes bind IPv4-only listeners, so " +
            "a second server can take the same port number on ::1 with no EADDRINUSE and answer the " +
            "wrong client (WebSocket upgrade returns 200 instead of 101).");
        return sb.ToString();
    }
}
