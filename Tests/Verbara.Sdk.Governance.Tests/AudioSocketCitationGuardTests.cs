using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for ADR-0060 R1 (openspec change <c>a-decision-is-held-by-a-test-that-can-fail</c>,
/// spec requirement "The AudioSocket format cites its source and is captured once"): every source file
/// in <c>src/</c> that defines the AudioSocket frame format cites Asterisk's <c>res_audiosocket.h</c>
/// in a comment. Zero tolerance: the four format files cite it today.
/// </summary>
/// <remarks>
/// For six months both AudioSocket implementations read a header no Asterisk sends, and nothing in the
/// repository said where the format came from, so no reader could check it (ADR-0060 Context). The
/// citation is what turns an inherited number into one a reader can verify. The population is found
/// structurally, not from a list, so a third implementation is held to the rule the day it lands; the
/// liveness test names the four files ADR-0060 lists, so a detector that stopped finding them fails.
/// </remarks>
public sealed class AudioSocketCitationGuardTests
{
    // Conservative floor, well below the src tree as this guard landed (~865 files).
    private const int MinimumScannedFiles = 400;

    /// <summary>The four files ADR-0060's 2026-09-26 addendum lists as defining the format.</summary>
    private static readonly string[] Adr0060FormatFiles =
    [
        "src/Verbara.Sdk.Ari/Audio/AudioSocketProtocol.cs",
        "src/Verbara.Sdk.Ari/Audio/IAudioStream.cs",
        "src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketFrameType.cs",
        "src/Verbara.Sdk.VoiceAi.AudioSocket/Internal/AudioSocketFrameCodec.cs",
    ];

    [Fact]
    public void Guard_ShouldFindTheCitationInEveryFormatFile_InTheSrcTree()
    {
        var violations = SrcSources()
            .SelectMany(file => AudioSocketCitationScanner.Scan(File.ReadAllText(file.FullPath), file.RelativePath))
            .ToList();

        violations.Should().BeEmpty(BuildFailureMessage(violations));
    }

    [Fact]
    public void Guard_ShouldScanManyFiles_WhenWalkingTheSrcTree()
    {
        var count = SrcTreeSource.EnumerateSrcSources().Count();

        count.Should().BeGreaterThan(
            MinimumScannedFiles,
            "the guard must walk the real src tree; a near-zero count means the locator broke and the " +
            "citation check would be a false green");
    }

    [Fact]
    public void Guard_ShouldRecogniseTheFourFormatFiles_WhenWalkingTheSrcTree()
    {
        var formatFiles = SrcSources()
            .Where(file => AudioSocketCitationScanner.FindFormatDefinitions(File.ReadAllText(file.FullPath)).Count > 0)
            .Select(file => file.RelativePath)
            .ToList();

        formatFiles.Should().Contain(
            Adr0060FormatFiles,
            "each file ADR-0060 lists defines the AudioSocket format and must stay in the guard's scope; " +
            "a file missing here was renamed or no longer matches the detector, and the citation check " +
            "no longer examines it");
    }

    [Fact]
    public void Scan_ShouldNameTheEnum_WhenAFrameKindEnumDoesNotCiteTheHeader()
    {
        const string source =
            "namespace Fixture;\n" +
            "/// <summary>AudioSocket frame types.</summary>\n" +
            "public enum AudioFrameType : byte\n" +
            "{\n" +
            "    Hangup = 0x00,\n" +
            "    Uuid = 0x01,\n" +
            "    Audio = 0x10,\n" +
            "}\n";

        var violations = AudioSocketCitationScanner.Scan(source, "src/Fixture/AudioFrameType.cs");

        violations.Should().ContainSingle().Which.Should().Be(
            new AudioSocketCitationViolation("src/Fixture/AudioFrameType.cs", 3, "AudioFrameType"));
    }

    [Fact]
    public void Scan_ShouldNameTheCodec_WhenAnAudioSocketHeaderSizeDoesNotCiteTheHeader()
    {
        const string source =
            "namespace Fixture;\n" +
            "/// <summary>Frame format: [1 byte type][2 bytes length big-endian][payload]</summary>\n" +
            "internal static class AudioSocketWireCodec\n" +
            "{\n" +
            "    private const int HeaderSize = 3;\n" +
            "}\n";

        var violations = AudioSocketCitationScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Type.Should().Be("AudioSocketWireCodec");
    }

    [Theory]
    [InlineData("/// <remarks>The source is Asterisk's <c>res_audiosocket.h</c>.</remarks>")]
    [InlineData("// Values from res_audiosocket.h (enum ast_audiosocket_msg_kind).")]
    [InlineData("/* See res_audiosocket.h */")]
    public void Scan_ShouldPass_WhenTheFileCitesTheHeaderInAComment(string comment)
    {
        var source =
            "namespace Fixture;\n" +
            comment + "\n" +
            "public enum AudioFrameType : byte { Hangup = 0x00, Uuid = 0x01, Audio = 0x10 }\n";

        var violations = AudioSocketCitationScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
        AudioSocketCitationScanner.FindFormatDefinitions(source).Should().Equal("AudioFrameType");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheHeaderIsNamedOnlyInAString()
    {
        // A string is data the code carries, not a citation a reader is pointed to.
        const string source =
            "namespace Fixture;\n" +
            "internal static class AudioSocketProtocol\n" +
            "{\n" +
            "    public const int HeaderSize = 3;\n" +
            "    public const string Source = \"res_audiosocket.h\";\n" +
            "}\n";

        var violations = AudioSocketCitationScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Type.Should().Be("AudioSocketProtocol");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheCommentCitesTheModuleButNotTheHeader()
    {
        // res_audiosocket.so is the module a capture ran against, not the definition R1 names.
        const string source =
            "namespace Fixture;\n" +
            "/// <summary>Measured against the stock res_audiosocket.so.</summary>\n" +
            "public enum AudioSocketFrameType : byte { Hangup = 0x00, Uuid = 0x01, Audio = 0x10 }\n";

        var violations = AudioSocketCitationScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldReportEachFormatType_WhenOneFileDefinesTwo()
    {
        const string source =
            "namespace Fixture;\n" +
            "public enum AudioFrameType : byte { Hangup = 0x00, Uuid = 0x01, Audio = 0x10 }\n" +
            "internal static class AudioSocketProtocol { public const int HeaderSize = 3; }\n";

        var violations = AudioSocketCitationScanner.Scan(source, "x.cs");

        violations.Select(v => (v.Type, v.Line)).Should().Equal(("AudioFrameType", 2), ("AudioSocketProtocol", 3));
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTheTypeDoesNotDefineTheFormat()
    {
        // An enum missing a frame kind, a HeaderSize outside an AudioSocket type, and an AudioSocket type
        // without a header size are all something else.
        const string source =
            "namespace Fixture;\n" +
            "public enum StreamState { Hangup, Audio, Closed }\n" +
            "internal static class RtpPacket { public const int HeaderSize = 12; }\n" +
            "public sealed class AudioSocketServer { public int Port { get; } }\n";

        var violations = AudioSocketCitationScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
        AudioSocketCitationScanner.FindFormatDefinitions(source).Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldRecogniseAStructCodec_WhenItDeclaresTheHeaderSize()
    {
        const string source =
            "namespace Fixture;\n" +
            "internal readonly struct AudioSocketFrameHeader { internal const int HeaderSize = 3; }\n";

        AudioSocketCitationScanner.FindFormatDefinitions(source).Should().Equal("AudioSocketFrameHeader");
    }

    private static IEnumerable<(string FullPath, string RelativePath)> SrcSources()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;
        return SrcTreeSource.EnumerateSrcSources()
            .Select(file => (FullPath: file, RelativePath: ToRelative(repoRoot, file)));
    }

    private static string ToRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string BuildFailureMessage(List<AudioSocketCitationViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count)
            .AppendLine(" AudioSocket format definition(s) do not cite res_audiosocket.h (ADR-0060 R1):");
        foreach (var v in violations.OrderBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Line))
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line).Append("  ").AppendLine(v.Type);

        sb.AppendLine(
            "The AudioSocket format is Asterisk's, and its definition is enum ast_audiosocket_msg_kind in " +
            "res_audiosocket.h. Say so in a comment in the file that copies it, next to a pointer to the " +
            "capture in Verbara.Sdk.TestInfrastructure.Wire, so a reader can check the numbers instead of " +
            "inheriting them. Both implementations shipped a header no Asterisk sends for six months because " +
            "nothing named the source.");
        return sb.ToString();
    }
}
