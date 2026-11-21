using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for "a provider reply is held to its contract, and a request is not" (openspec
/// change <c>a-provider-reply-is-held-to-its-contract</c>): in <c>src/Verbara.Sdk.VoiceAi.Stt</c>,
/// <c>SttJsonReadOptions</c> appears only inside the arguments of a <c>JsonSerializer.Deserialize*</c>
/// call, so no <c>Serialize*</c> call (no send) can use the strict options.
/// </summary>
/// <remarks>
/// The request-byte tests in the STT test project freeze what the plain source-generated context
/// writes; they cannot see which options a send site passes. This guard holds that wiring: switching
/// a send to <c>SttJsonReadOptions.TypeInfo&lt;T&gt;()</c> turns it red, where every behavioural test
/// stays green because the options only differ on a null non-nullable member.
/// </remarks>
public sealed class SttSendContextGuardTests
{
    private const string SttPackageRoot = "src/Verbara.Sdk.VoiceAi.Stt/";

    // Floors at the package as this guard landed: Google's request plus Speechmatics'
    // StartRecognition and EndOfStream; and eight fenced reads (Whisper, AzureWhisper, Google,
    // Speechmatics twice, AssemblyAI, Cartesia, Deepgram).
    private const int MinimumSerializeCalls = 3;
    private const int MinimumStrictReads = 8;

    [Fact]
    public void Guard_ShouldFindNoSendOnTheStrictOptions_InTheSttPackage()
    {
        var violations = SttPackageSources()
            .SelectMany(file => SttSendContextScanner.Scan(File.ReadAllText(file.FullPath), file.RelativePath))
            .ToList();

        violations.Should().BeEmpty(BuildFailureMessage(violations));
    }

    [Fact]
    public void Guard_ShouldSeeTheSendAndReadSites_WhenWalkingTheSttPackage()
    {
        var sources = SttPackageSources().Select(file => File.ReadAllText(file.FullPath)).ToList();

        sources.Sum(SttSendContextScanner.CountSerializeCalls).Should().BeGreaterThanOrEqualTo(
            MinimumSerializeCalls,
            "the guard must see the package's send sites; fewer means the locator or the call matcher broke");
        sources.Sum(SttSendContextScanner.CountStrictReads).Should().BeGreaterThanOrEqualTo(
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
            "        SttJsonReadOptions.TypeInfo<Msg>());\n" +
            "}\n";

        var violations = SttSendContextScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Line.Should().Be(5);
        violations[0].Reason.Should().Contain("JsonSerializer.Serialize");
    }

    [Fact]
    public void Scan_ShouldReportTheSend_WhenSerializeToUtf8BytesPassesTheStrictOptions()
    {
        const string source =
            "static class C {\n" +
            "    static byte[] M(Msg m) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(m, SttJsonReadOptions.StrictOptions);\n" +
            "}\n";

        var violations = SttSendContextScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Reason.Should().Contain("SerializeToUtf8Bytes");
    }

    [Fact]
    public void Scan_ShouldReportTheUse_WhenTheStrictTypeInfoIsHeldOutsideASerializerCall()
    {
        // The indirection a call-site check alone would miss: a field reaches a send unnamed.
        const string source =
            "static class C {\n" +
            "    static readonly JsonTypeInfo<Msg> Info = SttJsonReadOptions.TypeInfo<Msg>();\n" +
            "    static string M(Msg m) => JsonSerializer.Serialize(m, Info);\n" +
            "}\n";

        var violations = SttSendContextScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Line.Should().Be(2);
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheStrictOptionsOnlyReachDeserializeCalls()
    {
        const string source =
            "static class C {\n" +
            "    static Msg? R(string json) => JsonSerializer.Deserialize(json, SttJsonReadOptions.TypeInfo<Msg>());\n" +
            "    static string W(Msg m) => JsonSerializer.Serialize(m, VoiceAiSttJsonContext.Default.Msg);\n" +
            "}\n";

        SttSendContextScanner.Scan(source, "x.cs").Should().BeEmpty();
        SttSendContextScanner.CountSerializeCalls(source).Should().Be(1);
        SttSendContextScanner.CountStrictReads(source).Should().Be(1);
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheStrictOptionsAreNamedOnlyInACommentOrAString()
    {
        const string source =
            "static class C {\n" +
            "    // JsonSerializer.Serialize(m, SttJsonReadOptions.TypeInfo<Msg>());\n" +
            "    const string S = \"SttJsonReadOptions\";\n" +
            "}\n";

        SttSendContextScanner.Scan(source, "x.cs").Should().BeEmpty();
    }

    private static IEnumerable<(string FullPath, string RelativePath)> SttPackageSources()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;
        return SrcTreeSource.EnumerateSrcSources()
            .Select(file => (FullPath: file, RelativePath: Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/')))
            .Where(file => file.RelativePath.StartsWith(SttPackageRoot, StringComparison.Ordinal));
    }

    private static string BuildFailureMessage(List<SttSendContextViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count).AppendLine(" use(s) of SttJsonReadOptions outside a read:");
        foreach (var v in violations)
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line).Append("  ").AppendLine(v.Reason);

        sb.AppendLine(
            "The strict options respect nullable annotations on writes too: a request that reaches them " +
            "throws on a null non-nullable member instead of sending null. Serialize with " +
            "VoiceAiSttJsonContext.Default and keep SttJsonReadOptions inside Deserialize calls.");
        return sb.ToString();
    }
}
