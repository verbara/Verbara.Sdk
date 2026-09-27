using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for ADR-0060 R4 (openspec change <c>a-decision-is-held-by-a-test-that-can-fail</c>,
/// spec requirement "The AudioSocket format cites its source and is captured once"): no C# source under
/// <c>Tests/</c> other than the shared wire fixture writes a run of at least eight bytes taken from any
/// of that fixture's hex-dump constants. Zero tolerance: the capture exists once today.
/// </summary>
/// <remarks>
/// <para>
/// A per-package copy of the capture is the defect returning, not a smaller version of the rule: two
/// copies drift apart the way the two parsers did, each toward what its own package already accepts
/// (ADR-0060 R4, and the <c>HeaderSize</c> mutation that turned 4 of 4 VoiceAi fixture entries red and
/// 0 of 5 ARI ones). The captured constants are read from the fixture file itself, so the guard follows
/// the capture as it grows (R5) without a second list of its bytes here.
/// </para>
/// <para>
/// The fixtures below use a stand-in capture of made-up bytes, never the real one, so this file can
/// never be reported by the guard it tests.
/// </para>
/// </remarks>
public sealed class AudioSocketCaptureGuardTests
{
    /// <summary>The single copy of the capture, relative to the repository root.</summary>
    private const string WireFixturePath = "Tests/Verbara.Sdk.TestInfrastructure.Wire/AudioSocketWireCapture.cs";

    // Conservative floors, well below the Tests/ tree as this guard landed: 479 C# files, and 2,220
    // literal byte runs of two bytes or more (hex dumps, byte arrays, argument lists). The second floor
    // keeps the guard honest: an extractor that stopped reading byte literals would walk every file and
    // compare nothing. Runs of eight or more are rare outside the fixture (six, in four files), so they
    // are no floor at all; the fixture self-scan below is what proves a copy of the capture is read.
    private const int MinimumScannedFiles = 250;
    private const int MinimumByteRuns = 1000;

    /// <summary>The fixture's constants long enough to hold a run of <see cref="AudioSocketCaptureScanner.MinimumRun"/>.</summary>
    private static readonly string[] CapturedFrameConstants =
    [
        "IdentificationFrameHex",
        "IdentificationFrameAsymmetricHex",
        "AudioFrameHex",
    ];

    /// <summary>
    /// A stand-in for the wire fixture: four hex dumps (one split across a '+', one mostly a single
    /// repeated byte, one too short to hold a run), a prose constant and a byte constant.
    /// </summary>
    private const string StandInCapture =
        "namespace Fixture;\n" +
        "public static class AudioSocketWireCapture\n" +
        "{\n" +
        "    public const string FirstFrameHex = \"a1 00 10 c4 d5 e6 f7 08 19 2a 3b 4c 5d 6e 7f 80 91 a2 b3\";\n" +
        "    public const string SecondFrameHex =\n" +
        "        \"b1 01 40 12 34 56 78 9a bc de f0 \" +\n" +
        "        \"0f ed cb a9 87 65 43 21\";\n" +
        "    public const string SilentFrameHex = \"d4 00 00 00 00 00 00 00 00 00 00 ee\";\n" +
        "    public const string ShortFrameHex = \"c3 00 01 39\";\n" +
        "    public const string Note = \"closes the connection instead of sending a frame\";\n" +
        "    public const byte TypeByte = 0xa1;\n" +
        "}\n";

    [Fact]
    public void Guard_ShouldFindNoCopyOfTheCapture_OutsideTheWireFixture()
    {
        var capture = ReadRealCapture();

        var violations = TestSources()
            .Where(file => file.RelativePath != WireFixturePath)
            .SelectMany(file => AudioSocketCaptureScanner.Scan(File.ReadAllText(file.FullPath), file.RelativePath, capture))
            .ToList();

        violations.Should().BeEmpty(BuildFailureMessage(violations, capture));
    }

    [Fact]
    public void Guard_ShouldReadTheCapturedFrames_FromTheWireFixture()
    {
        var capture = ReadRealCapture();

        capture.Select(c => c.Name).Should().Contain(
            CapturedFrameConstants,
            "the guard compares against the fixture's hex-dump constants; one it cannot read is one it " +
            "cannot find a copy of");
        capture.Single(c => c.Name == "AudioFrameHex").Bytes.Should().HaveCount(
            323,
            "the captured audio frame is a three-byte header and 320 bytes of payload; reading fewer means " +
            "the concatenated constant was not folded");
    }

    [Fact]
    public void Guard_ShouldReportTheWireFixture_WhenItIsScannedAsIfItWereACopy()
    {
        // The fixture is the one file that must hold the capture, so scanning it proves the extractor
        // recognises the capture in its own canonical form.
        var capture = ReadRealCapture();
        var fixture = File.ReadAllText(Path.Join(RepoRoot(), WireFixturePath));

        var violations = AudioSocketCaptureScanner.Scan(fixture, WireFixturePath, capture);

        violations.Select(v => v.Constant).Should().Contain(CapturedFrameConstants);
    }

    [Fact]
    public void Guard_ShouldScanManyFilesAndByteRuns_WhenWalkingTheTestTree()
    {
        var files = TestSources().ToList();
        var runs = files.Sum(file => AudioSocketCaptureScanner.ExtractByteRuns(File.ReadAllText(file.FullPath))
            .Count(run => run.Bytes.Length >= 2));

        files.Should().HaveCountGreaterThan(
            MinimumScannedFiles,
            "the guard must walk the real Tests/ tree; a near-zero count means the locator broke");
        runs.Should().BeGreaterThanOrEqualTo(
            MinimumByteRuns,
            "the tests write many literal byte runs; finding almost none means the extractor stopped " +
            "reading them and the guard would pass having compared nothing");
    }

    [Fact]
    public void ReadCapture_ShouldDecodeEveryHexDumpConstant_WhenTheFixtureSplitsOneAcrossLines()
    {
        var capture = AudioSocketCaptureScanner.ReadCapture(StandInCapture);

        capture.Select(c => c.Name).Should().Equal("FirstFrameHex", "SecondFrameHex", "SilentFrameHex", "ShortFrameHex");
        capture[1].Bytes.Should().Equal(
            0xb1, 0x01, 0x40, 0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0xde, 0xf0,
            0x0f, 0xed, 0xcb, 0xa9, 0x87, 0x65, 0x43, 0x21);
    }

    [Theory]
    [InlineData("var hex = \"c4 d5 e6 f7 08 19 2a 3b\";")]
    [InlineData("var hex = \"C4-D5-E6-F7-08-19-2A-3B\";")]
    [InlineData("var bytes = System.Convert.FromHexString(\"C4D5E6F708192A3B\");")]
    [InlineData("var bytes = new byte[] { 0xc4, 0xd5, 0xe6, 0xf7, 0x08, 0x19, 0x2a, 0x3b };")]
    [InlineData("byte[] bytes = [196, 213, 230, 247, 8, 25, 42, 59];")]
    [InlineData("var bytes = new[] { (byte)0xC4, (byte)0xD5, (byte)0xE6, (byte)0xF7, (byte)0x08, (byte)0x19, (byte)0x2A, (byte)0x3B };")]
    [InlineData("Feed(0xc4, 0xd5, 0xe6, 0xf7, 0x08, 0x19, 0x2a, 0x3b);")]
    [InlineData("var hex = \"a1 00 10 c4 d5 e6 f7 08 19 2a 3b 4c 5d 6e 7f 80 91 a2 b3\";")]
    public void Scan_ShouldReportTheCopy_WhenATestWritesEightCapturedBytes(string statement)
    {
        var source = TestFile(statement);

        var violations = AudioSocketCaptureScanner.Scan(source, "Tests/Fixture/CopyTests.cs", Capture());

        var violation = violations.Should().ContainSingle().Which;
        violation.Path.Should().Be("Tests/Fixture/CopyTests.cs");
        violation.Line.Should().Be(4);
        violation.Constant.Should().Be("FirstFrameHex");
        violation.Length.Should().BeGreaterThanOrEqualTo(AudioSocketCaptureScanner.MinimumRun);
    }

    [Fact]
    public void Scan_ShouldReportTheOffsetAndTheWholeSharedRun_WhenTheCopyIsLongerThanEight()
    {
        var source = TestFile("var hex = \"ff c4 d5 e6 f7 08 19 2a 3b 4c 5d ff\";");

        var violations = AudioSocketCaptureScanner.Scan(source, "x.cs", Capture());

        violations.Should().ContainSingle().Which.Should().Be(
            new AudioSocketCaptureViolation("x.cs", 4, "FirstFrameHex", 3, 10));
    }

    [Fact]
    public void Scan_ShouldReportTheCopy_WhenItStraddlesAConcatenation()
    {
        // The fixture writes its audio frame as 21 concatenated literals; a copy written the same way
        // must be read the same way, across the '+'.
        const string statement =
            "var hex =\n" +
            "        \"12 34 56 78 9a bc de f0 \" +\n" +
            "        \"0f ed\";";

        var violations = AudioSocketCaptureScanner.Scan(TestFile(statement), "x.cs", Capture());

        violations.Should().ContainSingle().Which.Should().Be(
            new AudioSocketCaptureViolation("x.cs", 5, "SecondFrameHex", 3, 10));
    }

    [Fact]
    public void Scan_ShouldReportEachCopy_WhenAFileHoldsTwo()
    {
        const string statement =
            "var a = \"c4 d5 e6 f7 08 19 2a 3b\";\n" +
            "        var b = new byte[] { 0xb1, 0x01, 0x40, 0x12, 0x34, 0x56, 0x78, 0x9a };";

        var violations = AudioSocketCaptureScanner.Scan(TestFile(statement), "x.cs", Capture());

        violations.Select(v => (v.Line, v.Constant)).Should().Equal((4, "FirstFrameHex"), (5, "SecondFrameHex"));
    }

    [Theory]
    [InlineData("var hex = \"c4 d5 e6 f7 08 19 2a\";")]
    [InlineData("var bytes = new byte[] { 0xc4, 0xd5, 0xe6, 0xf7, 0x08, 0x19, 0x2a };")]
    [InlineData("var bytes = new byte[] { 0xc4, 0xd5, 0xe6, 0xf7, x, 0x08, 0x19, 0x2a, 0x3b };")]
    [InlineData("byte[] bytes = [0xc4, 0xd5, 0xe6, 0xf7, .. rest, 0x08, 0x19, 0x2a, 0x3b];")]
    [InlineData("var bytes = new int[] { 0xc4, 0xd5, 0xe6, 0x1f7, 0x08, 0x19, 0x2a, 0x3b };")]
    public void Scan_ShouldPass_WhenNoUnbrokenRunOfEightCapturedBytesIsWritten(string statement)
    {
        var violations = AudioSocketCaptureScanner.Scan(TestFile(statement), "x.cs", Capture());

        violations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("var hex = \"00 00 00 00 00 00 00 00 00 00\";")]
    [InlineData("var frame = new byte[] { 0x10, 0x01, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };")]
    public void Scan_ShouldPass_WhenTheSharedRunIsOneRepeatedByte(string statement)
    {
        // Measured, not assumed: the one hit on the real tree without this rule was a hand-built audio
        // frame in AudioSocketProtocolTests whose ten zero bytes coincide with the capture's silence. A
        // run of one repeated value is what any zeroed buffer writes, so it says nothing about where
        // the bytes came from.
        var violations = AudioSocketCaptureScanner.Scan(TestFile(statement), "x.cs", Capture());

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldReportTheCopy_WhenTheSharedRunHoldsTwoValues()
    {
        var violations = AudioSocketCaptureScanner.Scan(
            TestFile("var hex = \"d4 00 00 00 00 00 00 00\";"), "x.cs", Capture());

        violations.Should().ContainSingle().Which.Constant.Should().Be("SilentFrameHex");
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheBytesAppearOnlyInCommentsOrXmlDocs()
    {
        // A comment cannot be fed to a parser, so it cannot become the second fixture R4 forbids.
        const string source =
            "namespace Fixture;\n" +
            "/// <remarks>The frame begins <c>c4 d5 e6 f7 08 19 2a 3b</c>.</remarks>\n" +
            "public sealed class CopyTests\n" +
            "{\n" +
            "    // c4 d5 e6 f7 08 19 2a 3b 4c 5d\n" +
            "    /* 0xc4, 0xd5, 0xe6, 0xf7, 0x08, 0x19, 0x2a, 0x3b */\n" +
            "}\n";

        var violations = AudioSocketCaptureScanner.Scan(source, "x.cs", Capture());

        violations.Should().BeEmpty();
        AudioSocketCaptureScanner.ExtractByteRuns(source).Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheBytesAreWrittenAsAGuid()
    {
        // A GUID's text is a UUID value a test compares against, not a dump of a frame. Its groups are
        // decoded as the separate runs they are written as, none of which reaches eight bytes.
        var violations = AudioSocketCaptureScanner.Scan(
            TestFile("var id = System.Guid.Parse(\"c4d5e6f7-0819-2a3b-4c5d-6e7f8091a2b3\");"), "x.cs", Capture());

        violations.Should().BeEmpty();
    }

    [Fact]
    public void ExtractByteRuns_ShouldReadStringsArraysAndArguments_WithTheLineEachStartsOn()
    {
        const string source =
            "class C {\n" +
            "    const string A = \"01 02 \" +\n" +
            "        \"03\";\n" +
            "    byte[] B = { 4, 5, 0x06 };\n" +
            "    void M() { Feed(7, 8); var s = $\"x {M} 0a0b\"; }\n" +
            "}\n";

        var runs = AudioSocketCaptureScanner.ExtractByteRuns(source);

        runs.Select(r => (r.Line, Convert.ToHexString(r.Bytes))).Should().BeEquivalentTo(
            [(2, "010203"), (4, "040506"), (5, "0708"), (5, "0A0B")]);
    }

    private static IReadOnlyList<CapturedConstant> Capture() => AudioSocketCaptureScanner.ReadCapture(StandInCapture);

    /// <summary>A test file whose <paramref name="statement"/> starts on line 4.</summary>
    private static string TestFile(string statement) =>
        "namespace Fixture;\n" +
        "public sealed class CopyTests\n" +
        "{\n" +
        "    void M(byte x, byte[] rest) { " + statement + " }\n" +
        "}\n";

    private static IReadOnlyList<CapturedConstant> ReadRealCapture()
    {
        var path = Path.Join(RepoRoot(), WireFixturePath);
        File.Exists(path).Should().BeTrue(
            "ADR-0060 R4 keeps the capture in {0}; if it moved, move this guard's path with it", WireFixturePath);

        return AudioSocketCaptureScanner.ReadCapture(File.ReadAllText(path));
    }

    private static string RepoRoot() => Directory.GetParent(TestTreeSource.TestsRoot())!.FullName;

    private static IEnumerable<(string FullPath, string RelativePath)> TestSources()
    {
        var repoRoot = RepoRoot();
        return TestTreeSource.EnumerateTestSources()
            .Select(file => (FullPath: file, RelativePath: Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/')));
    }

    private static string BuildFailureMessage(
        List<AudioSocketCaptureViolation> violations,
        IReadOnlyList<CapturedConstant> capture)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count)
            .Append(" copy(ies) of the captured AudioSocket bytes outside ").Append(WireFixturePath)
            .AppendLine(" (ADR-0060 R4):");
        foreach (var v in violations.OrderBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Line))
        {
            var bytes = capture.First(c => c.Name == v.Constant).Bytes.AsSpan(v.Offset, v.Length);
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line)
                .Append("  ").Append(v.Length).Append(" bytes of AudioSocketWireCapture.").Append(v.Constant)
                .Append(" from offset ").Append(v.Offset).Append(": ")
                .AppendLine(Convert.ToHexString(bytes).ToLowerInvariant());
        }

        sb.AppendLine(
            "The capture exists once, and every parser's tests reference Verbara.Sdk.TestInfrastructure.Wire. " +
            "A copy in a package's own tests drifts toward what that package's parser accepts, which is how " +
            "both parsers stayed green for six months against a header no Asterisk sends. Use " +
            "AudioSocketWireCapture's members instead of the bytes.");
        return sb.ToString();
    }
}
