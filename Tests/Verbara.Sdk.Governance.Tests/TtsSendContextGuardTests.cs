using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for "a provider reply is held to its contract, and a request is not" (openspec
/// change <c>a-provider-reply-is-held-to-its-contract</c>): in <c>src/Verbara.Sdk.VoiceAi.Tts</c>,
/// <c>TtsJsonReadOptions</c> appears only inside the arguments of a <c>JsonSerializer.Deserialize*</c>
/// call, so no <c>Serialize*</c> call (no send) can use the strict options.
/// </summary>
/// <remarks>
/// The request-byte tests in the TTS test project freeze what the plain source-generated context
/// writes; they cannot see which options a send site passes. This guard holds that wiring: switching
/// a send to <c>TtsJsonReadOptions.For&lt;T&gt;.TypeInfo</c> turns it red, where every behavioural test
/// stays green because the options only differ on a null non-nullable member.
/// This syntax guard proves which named options reach calls; it cannot prove the options' runtime
/// behaviour. No TTS response reference member remains non-nullable after triage, so observable
/// receive-loop behaviour alone cannot distinguish strict from plain metadata at these four sites.
/// </remarks>
public sealed class TtsSendContextGuardTests
{
    private const string TtsPackageRoot = "src/Verbara.Sdk.VoiceAi.Tts/";

    private const string ReadOptionsType = "TtsJsonReadOptions";

    // The tree has twelve sends: LMNT four, Deepgram three, ElevenLabs three, Cartesia one,
    // Speechmatics one. Keep a conservative send floor of eleven and all four fenced reads
    // (Cartesia, Deepgram, ElevenLabs, LMNT), so removing any read's wiring turns this red.
    private const int MinimumSerializeCalls = 11;
    private const int MinimumStrictReads = 4;

    [Fact]
    public void Guard_ShouldFindNoSendOnTheStrictOptions_InTheTtsPackage()
    {
        var violations = TtsPackageSources()
            .SelectMany(file => SttSendContextScanner.Scan(File.ReadAllText(file.FullPath), file.RelativePath, ReadOptionsType))
            .ToList();

        violations.Should().BeEmpty(BuildFailureMessage(violations));
    }

    [Fact]
    public void Guard_ShouldSeeTheSendAndReadSites_WhenWalkingTheTtsPackage()
    {
        var sources = TtsPackageSources().Select(file => File.ReadAllText(file.FullPath)).ToList();

        sources.Sum(SttSendContextScanner.CountSerializeCalls).Should().BeGreaterThanOrEqualTo(
            MinimumSerializeCalls,
            "the guard must see the package's send sites; fewer means the locator or the call matcher broke");
        sources.Sum(source => SttSendContextScanner.CountStrictReads(source, ReadOptionsType)).Should().BeGreaterThanOrEqualTo(
            MinimumStrictReads,
            "the guard must recognise the fenced reads as allowed uses; fewer means the matcher broke");
    }

    [Fact]
    public void Scan_ShouldReportTheSend_WhenASerializeCallPassesTheStrictTypeInfo()
    {
        const string source =
            "namespace Fixture;\n" +
            "static class C {\n" +
            "    static string M(Msg m) => JsonSerializer.Serialize(\n" +
            "        m,\n" +
            "        TtsJsonReadOptions.For<Msg>.TypeInfo);\n" +
            "}\n";

        var violations = SttSendContextScanner.Scan(source, "x.cs", ReadOptionsType);

        violations.Should().ContainSingle().Which.Line.Should().Be(5);
        violations[0].Reason.Should().Contain("JsonSerializer.Serialize");
    }

    [Fact]
    public void Scan_ShouldReportTheSend_WhenSerializeToUtf8BytesPassesTheStrictOptions()
    {
        const string source =
            "static class C {\n" +
            "    static byte[] M(Msg m) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(m, TtsJsonReadOptions.StrictOptions);\n" +
            "}\n";

        var violations = SttSendContextScanner.Scan(source, "x.cs", ReadOptionsType);

        violations.Should().ContainSingle().Which.Reason.Should().Contain("SerializeToUtf8Bytes");
    }

    [Fact]
    public void Scan_ShouldReportTheUse_WhenTheStrictTypeInfoIsHeldOutsideASerializerCall()
    {
        // The indirection a call-site check alone would miss: a field reaches a send unnamed.
        const string source =
            "static class C {\n" +
            "    static readonly JsonTypeInfo<Msg> Info = TtsJsonReadOptions.For<Msg>.TypeInfo;\n" +
            "    static string M(Msg m) => JsonSerializer.Serialize(m, Info);\n" +
            "}\n";

        var violations = SttSendContextScanner.Scan(source, "x.cs", ReadOptionsType);

        violations.Should().ContainSingle().Which.Line.Should().Be(2);
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheStrictOptionsOnlyReachDeserializeCalls()
    {
        const string source =
            "static class C {\n" +
            "    static Msg? R(string json) => JsonSerializer.Deserialize(json, TtsJsonReadOptions.For<Msg>.TypeInfo);\n" +
            "    static string W(Msg m) => JsonSerializer.Serialize(m, VoiceAiTtsJsonContext.Default.Msg);\n" +
            "}\n";

        SttSendContextScanner.Scan(source, "x.cs", ReadOptionsType).Should().BeEmpty();
        SttSendContextScanner.CountSerializeCalls(source).Should().Be(1);
        SttSendContextScanner.CountStrictReads(source, ReadOptionsType).Should().Be(1);
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheStrictOptionsAreNamedOnlyInACommentOrAString()
    {
        const string source =
            "static class C {\n" +
            "    // JsonSerializer.Serialize(m, TtsJsonReadOptions.For<Msg>.TypeInfo);\n" +
            "    const string S = \"TtsJsonReadOptions\";\n" +
            "}\n";

        SttSendContextScanner.Scan(source, "x.cs", ReadOptionsType).Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldSelectTheNamedPackage_WhenOtherReadOptionsAppear()
    {
        const string source =
            "static class C {\n" +
            "    static string W(Msg m) => JsonSerializer.Serialize(m, SttJsonReadOptions.For<Msg>.TypeInfo);\n" +
            "    static Msg? R(string json) => JsonSerializer.Deserialize(json, TtsJsonReadOptions.For<Msg>.TypeInfo);\n" +
            "}\n";

        SttSendContextScanner.Scan(source, "x.cs", ReadOptionsType).Should().BeEmpty();
        SttSendContextScanner.CountStrictReads(source, ReadOptionsType).Should().Be(1);
        SttSendContextScanner.Scan(source, "x.cs").Should().ContainSingle().Which.Line.Should().Be(2);
        SttSendContextScanner.CountStrictReads(source).Should().Be(0);
    }

    private static IEnumerable<(string FullPath, string RelativePath)> TtsPackageSources()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;
        return SrcTreeSource.EnumerateSrcSources()
            .Select(file => (FullPath: file, RelativePath: Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/')))
            .Where(file => file.RelativePath.StartsWith(TtsPackageRoot, StringComparison.Ordinal));
    }

    private static string BuildFailureMessage(List<SttSendContextViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count).AppendLine(" use(s) of TtsJsonReadOptions outside a read:");
        foreach (var v in violations)
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line).Append("  ").AppendLine(v.Reason);

        sb.AppendLine(
            "The strict options respect nullable annotations on writes too: a request that reaches them " +
            "throws on a null non-nullable member instead of sending null. Serialize with " +
            "VoiceAiTtsJsonContext.Default and keep TtsJsonReadOptions inside Deserialize calls. " +
            "This syntax guard checks named wiring, not the options' runtime behaviour.");
        return sb.ToString();
    }
}
