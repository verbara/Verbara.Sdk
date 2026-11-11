using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for "a receive loop's decode catch covers the decode and nothing after it": in every
/// file of <c>src/Verbara.Sdk.VoiceAi.Stt</c> and <c>src/Verbara.Sdk.VoiceAi.Tts</c> that declares a
/// WebSocket speech client, and in <c>OpenAiRealtimeBridge.cs</c>, every <c>try</c> caught by
/// <c>JsonException</c> or <c>FormatException</c> holds exactly one statement whose only invocation is the
/// decode call (<see cref="ReceiveLoopCatchScopeScanner"/>). Zero tolerance.
/// </summary>
/// <remarks>
/// Why a structural rule and not a test: a test that sends a malformed message fails inside the decode,
/// so it cannot see a <c>try</c> that also covers the write, publish or handler after it. A
/// <c>JsonException</c> raised there would be counted as an unparseable frame and skipped instead of
/// reaching the caller.
/// </remarks>
public sealed class ReceiveLoopCatchScopeGuardTests
{
    /// <summary>
    /// The decode-guarded <c>try</c> statements in scope: the Deepgram recognizer's decode and peek, the
    /// three other streaming recognizers' decode and peek (Speechmatics' greeting decode as well), the
    /// realtime bridge's six plus its base64 audio decode, and the four streaming synthesizers' decode and
    /// peek plus the base64 audio decode of Cartesia and ElevenLabs. A floor, not a count: a site added
    /// later only raises it.
    /// </summary>
    private const int MinimumGuardedTries = 26;

    private static readonly string[] SpeechPackageRoots =
    [
        "src/Verbara.Sdk.VoiceAi.Tts/",
        "src/Verbara.Sdk.VoiceAi.Stt/",
    ];

    private const string RealtimeBridge = "src/Verbara.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeBridge.cs";

    /// <summary>The eight WebSocket speech clients plus the bridge: each must be in the scanned set.</summary>
    private static readonly string[] ExpectedFiles =
    [
        "src/Verbara.Sdk.VoiceAi.Stt/Deepgram/DeepgramSpeechRecognizer.cs",
        "src/Verbara.Sdk.VoiceAi.Stt/Speechmatics/SpeechmaticsSpeechRecognizer.cs",
        "src/Verbara.Sdk.VoiceAi.Stt/AssemblyAi/AssemblyAiSpeechRecognizer.cs",
        "src/Verbara.Sdk.VoiceAi.Stt/Cartesia/CartesiaSpeechRecognizer.cs",
        "src/Verbara.Sdk.VoiceAi.Tts/Deepgram/DeepgramSpeechSynthesizer.cs",
        "src/Verbara.Sdk.VoiceAi.Tts/Lmnt/LmntSpeechSynthesizer.cs",
        "src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs",
        "src/Verbara.Sdk.VoiceAi.Tts/ElevenLabs/ElevenLabsSpeechSynthesizer.cs",
        RealtimeBridge,
    ];

    [Fact]
    public void Guard_ShouldFindOnlyTheDecodeInsideEveryDecodeCatch_InTheReceiveLoops()
    {
        var violations = ScannedFiles()
            .SelectMany(file => ReceiveLoopCatchScopeScanner.Scan(File.ReadAllText(file.FullPath), file.RelativePath))
            .ToList();

        violations.Should().BeEmpty(BuildFailureMessage(violations));
    }

    [Fact]
    public void Guard_ShouldScanEveryReceiveLoopFile_WhenWalkingTheVoiceAiPackages()
    {
        var files = ScannedFiles().Select(file => file.RelativePath).ToList();

        files.Should().Contain(
            ExpectedFiles,
            "each WebSocket speech client and the realtime bridge holds a receive loop; a file missing here is " +
            "one whose decode catches are no longer examined");
    }

    [Fact]
    public void Guard_ShouldJudgeAtLeastTheGuardedTriesItLandedWith_WhenWalkingTheReceiveLoops()
    {
        var count = ScannedFiles().Sum(file => ReceiveLoopCatchScopeScanner.CountGuardedTries(File.ReadAllText(file.FullPath)));

        count.Should().BeGreaterThanOrEqualTo(
            MinimumGuardedTries,
            "a count below the floor means the scanner stopped recognising decode catches, and the rule " +
            "would pass having judged nothing");
    }

    // ── Positive fixtures: the shapes the receive loops use ─────────────────────────────────────────────

    [Fact]
    public void Scan_ShouldPass_WhenTheTryHoldsOnlyTheDeserializeAssignment()
    {
        var source = Method(
            "DeepgramResultMessage? msg;\n" +
            "try { msg = JsonSerializer.Deserialize(json, VoiceAiSttJsonContext.Default.DeepgramResultMessage); }\n" +
            "catch (JsonException) { CountUnparseable(provider); return; }\n" +
            "await writer.WriteAsync(Map(msg), ct);\n");

        ReceiveLoopCatchScopeScanner.Scan(source, "x.cs").Should().BeEmpty();
        ReceiveLoopCatchScopeScanner.CountGuardedTries(source).Should().Be(1);
    }

    [Fact]
    public void Scan_ShouldPass_WhenThePeekParsesTheDocumentAloneInItsTry()
    {
        // The envelope peek: the parse alone in its try, the document read outside it.
        var source = Method(
            "JsonDocument document;\n" +
            "try { document = JsonDocument.Parse(json); }\n" +
            "catch (JsonException) { return false; }\n" +
            "using (document)\n" +
            "{\n" +
            "    var root = document.RootElement;\n" +
            "    return root.ValueKind == JsonValueKind.Object\n" +
            "        && root.TryGetProperty(\"type\", out var type)\n" +
            "        && type.ValueKind == JsonValueKind.String\n" +
            "        && type.ValueEquals(\"Error\");\n" +
            "}\n");

        ReceiveLoopCatchScopeScanner.Scan(source, "x.cs").Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheBase64DecodeIsItsOwnStatementAndTheWriteFollowsTheTry()
    {
        var source = Method(
            "byte[] audio;\n" +
            "try { audio = Convert.FromBase64String(chunk.Data); }\n" +
            "catch (FormatException) { CountUnparseable(provider); continue; }\n" +
            "await writer.WriteAsync(audio, ct);\n");

        ReceiveLoopCatchScopeScanner.Scan(source, "x.cs").Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheDecodeIsReturnedOrDeclared()
    {
        var source = Method(
            "try { return Convert.FromBase64String(base64); }\n" +
            "catch (FormatException) { CountUnparseable(provider); return null; }\n" +
            "try { var started = System.Text.Json.JsonSerializer.Deserialize<Started>(json); }\n" +
            "catch (System.Text.Json.JsonException) { }\n" +
            "try { using var document = JsonDocument.Parse(json); }\n" +
            "catch (JsonException) { }\n" +
            "try { pcm16Bytes = Convert.FromBase64String(audioEvt.Delta); }\n" +
            "catch (FormatException ex) { SkipUnreadable(channelId, ex, baseEvt.Type); break; }\n");

        ReceiveLoopCatchScopeScanner.Scan(source, "x.cs").Should().BeEmpty();
        ReceiveLoopCatchScopeScanner.CountGuardedTries(source).Should().Be(4);
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTheTryIsNotCaughtByADecodeException()
    {
        var source = Method(
            "try { await ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct); Count(); }\n" +
            "catch (WebSocketException) { }\n" +
            "catch (OperationCanceledException) { }\n");

        ReceiveLoopCatchScopeScanner.Scan(source, "x.cs").Should().BeEmpty();
        ReceiveLoopCatchScopeScanner.CountGuardedTries(source).Should().Be(0);
    }

    // ── Negative fixtures: a catch that covers more than the decode ─────────────────────────────────────

    [Fact]
    public void Scan_ShouldFlag_WhenTheTryAlsoHoldsTheWriteToTheCaller()
    {
        // The negative control: the write after a successful parse moved inside the decode's try.
        var source = Method(
            "try\n" +
            "{\n" +
            "    var msg = JsonSerializer.Deserialize(json, VoiceAiSttJsonContext.Default.DeepgramResultMessage);\n" +
            "    await writer.WriteAsync(Map(msg), ct);\n" +
            "}\n" +
            "catch (JsonException) { CountUnparseable(provider); }\n");

        var violations = ReceiveLoopCatchScopeScanner.Scan(source, "src/Fixture.cs");

        violations.Should().ContainSingle().Which.Should().Be(new ReceiveLoopCatchScopeViolation(
            "src/Fixture.cs", 6, "the try holds 2 statements; a decode catch covers one, the decode"));
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheDecodeIsAnArgumentOfTheWrite()
    {
        var source = Method(
            "try { await writer.WriteAsync(Convert.FromBase64String(chunk.Data), ct); }\n" +
            "catch (FormatException) { continue; }\n");

        var violations = ReceiveLoopCatchScopeScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Reason.Should().Contain("2 invocations")
            .And.Contain("writer.WriteAsync").And.Contain("Convert.FromBase64String");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenThePeekReadsTheDocumentInsideItsTry()
    {
        // The shape the Deepgram peek had before it was split: parse, read and return in one try.
        var source = Method(
            "try\n" +
            "{\n" +
            "    using var document = JsonDocument.Parse(json);\n" +
            "    var root = document.RootElement;\n" +
            "    return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(\"type\", out var type);\n" +
            "}\n" +
            "catch (JsonException) { return false; }\n");

        ReceiveLoopCatchScopeScanner.Scan(source, "x.cs").Should().ContainSingle()
            .Which.Reason.Should().Contain("3 statements");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheOnlyInvocationIsNotADecodeCall()
    {
        // A handler that throws JsonException of its own is not an unparseable frame.
        var source = Method(
            "try { await handler.HandleAsync(arguments, ct); }\n" +
            "catch (System.Text.Json.JsonException) { CountUnparseable(provider); }\n" +
            "try { msg = Parser.Deserialize(json); }\n" +
            "catch (JsonException) { }\n");

        var violations = ReceiveLoopCatchScopeScanner.Scan(source, "x.cs");

        violations.Should().HaveCount(2);
        violations.Should().OnlyContain(v => v.Reason.Contains("not JsonSerializer.Deserialize", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldFlag_WhenOneOfSeveralCatchesIsADecodeCatch()
    {
        var source = Method(
            "try { Publish(JsonSerializer.Deserialize(json, Ctx.Default.Evt)); }\n" +
            "catch (InvalidOperationException) { }\n" +
            "catch (JsonException) { }\n");

        ReceiveLoopCatchScopeScanner.Scan(source, "x.cs").Should().ContainSingle()
            .Which.Reason.Should().Contain("2 invocations");
    }

    /// <summary>
    /// A fixture class whose single method holds <paramref name="body"/>; the body's first line is line 6.
    /// </summary>
    private static string Method(string body)
    {
        var sb = new StringBuilder();
        sb.Append("namespace Fixture;\n")
            .Append("internal sealed class Loop\n")
            .Append("{\n")
            .Append("    private async Task<object?> RunAsync(string json, string provider, System.Threading.CancellationToken ct)\n")
            .Append("    {\n");
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            sb.Append("        ").Append(line).Append('\n');

        sb.Append("    }\n")
            .Append("}\n");
        return sb.ToString();
    }

    private static IEnumerable<(string FullPath, string RelativePath)> ScannedFiles()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;
        return SrcTreeSource.EnumerateSrcSources()
            .Select(file => (FullPath: file, RelativePath: ToRelative(repoRoot, file)))
            .Where(file => file.RelativePath == RealtimeBridge
                || (SpeechPackageRoots.Any(root => file.RelativePath.StartsWith(root, StringComparison.Ordinal))
                    && SpeechClientDoorScanner.FindWebSocketClients(File.ReadAllText(file.FullPath)).Count > 0));
    }

    private static string ToRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string BuildFailureMessage(List<ReceiveLoopCatchScopeViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count).AppendLine(" decode catch(es) cover more than the decode:");
        foreach (var v in violations.OrderBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Line))
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line).Append("  ").AppendLine(v.Reason);

        sb.AppendLine(
            "A try caught by JsonException or FormatException in a receive loop holds exactly one statement, and " +
            "its only invocation is JsonSerializer.Deserialize, JsonDocument.Parse or Convert.FromBase64String. " +
            "Anything else inside it — the write to the caller, an audio write, a published event, a handler — " +
            "would turn the caller's own exception into a counted, skipped frame. Move it after the try.");
        return sb.ToString();
    }
}
