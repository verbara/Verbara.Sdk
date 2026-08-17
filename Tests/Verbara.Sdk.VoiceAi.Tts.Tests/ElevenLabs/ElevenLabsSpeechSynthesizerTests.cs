using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Tts.ElevenLabs;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.ElevenLabs;

/// <summary>
/// Transport: WebSocket. Deliberately NOT migrated to the WireMock substrate — WireMock.NET matches
/// HTTP/1.1 requests and cannot hold the duplex session these tests drive (ADR-0041 D2), so
/// <c>ElevenLabsFakeServer</c> on <c>WebSocketTestServer</c> stays. Fidelity here comes from recorded
/// frames (D4), not from a different server.
/// </summary>
public class ElevenLabsSpeechSynthesizerTests : IAsyncDisposable
{
    private readonly ElevenLabsFakeServer _server;

    public ElevenLabsSpeechSynthesizerTests()
    {
        _server = new ElevenLabsFakeServer();
        _server.Start();
    }

    /// <summary>The credential every test in this class sends, asserted on by the fake.</summary>
    private const string TestApiKey = "test-key";

    /// <summary>The voice id every test sends — a path segment, so the fake sees it in the route.</summary>
    private const string TestVoiceId = "test-voice";

    /// <summary>
    /// Reaches the fake through <see cref="ElevenLabsOptions.BaseUri"/>, so the route (voice segment
    /// included), the query and the <c>xi-api-key</c> header all come from shipped code. Takes a
    /// configure action because three URL-parameter tests vary one option each — they used to
    /// duplicate the whole options block and the test-only constructor with it.
    /// </summary>
    private ElevenLabsSpeechSynthesizer BuildSynthesizer(Action<ElevenLabsOptions>? configure = null)
    {
        var opts = new ElevenLabsOptions
        {
            ApiKey = TestApiKey,
            VoiceId = TestVoiceId,
            BaseUri = $"ws://127.0.0.1:{_server.Port}/v1/text-to-speech"
        };
        configure?.Invoke(opts);
        return new ElevenLabsSpeechSynthesizer(Options.Create(opts));
    }

    /// <summary>The audio the fake replays, read from the same tree the fake reads.</summary>
    private static byte[] RecordedAudio => ElevenLabsFakeServer.ReadFrameBytes(ElevenLabsFakeServer.AudioChunk);

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldTheAudioInsideTheTextFrame_WhenTheServerAnswersLikeTheVendor()
    {
        // The headline regression. A live run of the shipped request received ZERO binary bytes and
        // four text frames keyed alignment/audio/isFinal/normalizedAlignment, so the retired
        // "only yield binary frames; skip text messages" loop was not a partial defect — the branch
        // it preferred received nothing at all, and every caller got silence.
        var expected = RecordedAudio;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.Should().NotBeEmpty("audio arrives base64 on a text frame, and it must be decoded");
        frames.SelectMany(f => f.ToArray()).Should().Equal(expected,
            "decoding must not alter a single byte of the audio the vendor sent");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldNotHalfCloseTheSocket_AfterTheEmptyTextChunk()
    {
        // Measured against the live endpoint with CloseOutputAsync as the only variable: with it,
        // 0 bytes and close 1006; without it, 86 193 B and a clean 1000. The empty text chunk is
        // already the vendor's end-of-input signal, so the half-close was a second one that
        // contradicted the first. The fake cannot reproduce the vendor's reaction without racing its
        // own send loop, so it records what the client sent and this asserts on that.
        var synth = BuildSynthesizer();

        var audio = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ClientSentCloseFrame.Should().BeFalse(
            "a client Close frame after the request costs every caller all of their audio");
        audio.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldAssembleTheWholeMessage_WhenATextFrameArrivesFragmented()
    {
        // The defect the Class B fix would otherwise have introduced. The vendor sizes these frames
        // — one measured run averaged ~29 KB of base64 — so a frame past the 64 KiB receive buffer
        // arrives in fragments, and a loop that parsed each read as a whole message would hand JSON
        // a truncated document. It is length-dependent, so a short probe cannot trip it: this
        // fragments deliberately instead of waiting for a big enough fixture.
        _server.TextFrameFragmentBytes = 16;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.SelectMany(f => f.ToArray()).Should().Equal(RecordedAudio,
            "fragments must be assembled until EndOfMessage before the frame is parsed");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldTheAudio_WhenTheFrameAlsoCarriesUnmodelledAlignment()
    {
        // The recorded AudioOutput frame, replayed verbatim: audio plus the two alignment structures
        // the synthesizer deliberately does not model. Tolerating an unmapped member must not cost
        // the caller the member that matters.
        _server.SendRecordedAudioOutputFrame = true;
        var expected = Convert.FromBase64String(
            JsonDocument.Parse(ElevenLabsFakeServer.ReadFrame(ElevenLabsFakeServer.AudioOutputFrame))
                .RootElement.GetProperty("audio").GetString()!);
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.SelectMany(f => f.ToArray()).Should().Equal(expected);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldBinaryAudioFrames()
    {
        // The tolerated-without-evidence branch. No binary frame has ever been observed on this
        // surface and the vendor documents no raw-binary mode — but a vendor not mentioning a mode
        // is not evidence the mode does not exist, so the branch stays and this holds it honest.
        _server.Transport = ElevenLabsAudioTransport.Binary;

        // The point of the recording: a real waveform of a length that is NOT chunk-aligned
        // traverses the frame path, so a partial final frame reaches the consumer. Two 320-byte
        // arrays of zeros — exact multiples — could never produce one.
        //
        // Note what is NOT asserted: that the final frame is exactly `length % 320`. Frame count
        // and boundaries are the transport's business, not the client's contract; what the client
        // owes is every byte, in order, with nothing invented and nothing dropped.
        var expected = RecordedAudio;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        (expected.Length % ElevenLabsFakeServer.AudioFrameSize).Should()
            .NotBe(0, "the recording must not be chunk-aligned");
        frames.Should().HaveCountGreaterThan(1, "the recording must actually be chunked");
        frames.Should().OnlyContain(f => f.Length > 0 && f.Length <= ElevenLabsFakeServer.AudioFrameSize);
        frames.Should().Contain(f => f.Length != ElevenLabsFakeServer.AudioFrameSize,
            "a partial frame must reach the consumer");
        frames.SelectMany(f => f.ToArray()).Should().Equal(expected,
            "streaming must not alter a single byte of the recorded audio");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldSendTextChunk()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hola mundo", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedJsonMessages.Should().NotBeEmpty();
        _server.ReceivedJsonMessages.Any(m => m.Contains("hola mundo", StringComparison.Ordinal)).Should().BeTrue();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldNotLeakJsonBytesIntoTheAudio_WhenDecodingATextFrame()
    {
        // What the retired SynthesizeAsync_ShouldFilterAlignmentMessages_NotYieldThem test was
        // really worth: asserting the exact byte sequence rather than a frame count. It proved text
        // frames were dropped whole; it now proves the decoded audio carries no envelope. The
        // assertion survives the inversion because it was about bytes, not about behaviour.
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        var yielded = frames.SelectMany(f => f.ToArray()).ToArray();
        yielded.Should().Equal(RecordedAudio);
        System.Text.Encoding.UTF8.GetString(yielded).Should().NotContain("audio",
            "a client that leaked the envelope would append JSON bytes to the waveform");
    }

    [Fact]
    public void RecordedFixtures_ShouldCarryDocumentedFieldsAndExactByteLength_WhenReadFromRecordingsTree()
    {
        // Fixture-integrity fence. The fake is only as good as what is on disk: trim a documented
        // field, swap the audio or re-save the JSON and the suite would keep passing while quietly
        // testing something smaller. This fails here, next to the sidecar that explains the file.
        var audio = RecordedAudio;
        audio.Should().HaveCount(2808, "the sidecar records this exact length");
        (audio.Length % ElevenLabsFakeServer.AudioFrameSize).Should().NotBe(0);

        using var frame = JsonDocument.Parse(
            ElevenLabsFakeServer.ReadFrame(ElevenLabsFakeServer.AudioOutputFrame));
        var root = frame.RootElement;

        // ElevenLabs documents AudioOutput as `audio` (base64 of the selected output_format) and
        // `isFinal`, plus optional `alignment` / `normalizedAlignment`, each three equal-length
        // parallel arrays. A live run measured exactly this key set and zero binary bytes. The
        // synthesizer models `audio` and `isFinal` and deliberately leaves the two alignment
        // structures unmapped — tolerated, not consumed; see the sidecar.
        root.TryGetProperty("isFinal", out _).Should()
            .BeTrue("the measured frames carry isFinal, so the fixture must too");
        Convert.FromBase64String(root.GetProperty("audio").GetString()!).Should()
            .Equal(audio.Take(ElevenLabsFakeServer.AudioFrameSize),
                "the frame's audio is the first chunk of the sibling recording, not a second waveform");

        foreach (var name in new[] { "alignment", "normalizedAlignment" })
        {
            var alignment = root.GetProperty(name);
            var starts = alignment.GetProperty("charStartTimesMs").EnumerateArray().Count();
            var durations = alignment.GetProperty("charDurationsMs").EnumerateArray().Count();
            var chars = alignment.GetProperty("chars").EnumerateArray().Count();

            chars.Should().BeGreaterThan(0);
            starts.Should().Be(chars, "the three arrays are parallel and equal-length");
            durations.Should().Be(chars, "the three arrays are parallel and equal-length");
        }

        // normalizedAlignment is chunk-relative, alignment is utterance-relative — the offset is
        // what makes them two distinct arrays rather than one duplicated twice.
        root.GetProperty("alignment").GetProperty("charStartTimesMs")[0].GetInt32().Should()
            .BeGreaterThan(root.GetProperty("normalizedAlignment").GetProperty("charStartTimesMs")[0].GetInt32());
    }

    [Fact]
    public void RecordedFixtures_ShouldMatchTheirDocumentedGeneratorParameters_WhenRegeneratedLocally()
    {
        // The "commit a small generator" half of the source-audio rule (protocol guide §6): the
        // committed bytes are reproducible from three numbers in the sidecar, not magic.
        var regenerated = SyntheticPcm.Triangle(
            ElevenLabsFakeServer.AudioSampleCount,
            ElevenLabsFakeServer.AudioPeriodSamples,
            ElevenLabsFakeServer.AudioAmplitude);

        regenerated.Should().Equal(RecordedAudio);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldAbort_WhenCancelled()
    {
        // Deterministic contract (test-determinism fence, ADR-0038): a pre-cancelled token
        // throws OperationCanceledException at iterator entry, before any provider request is
        // issued — independent of scheduling/mock latency. No wall-clock race against the fake
        // server (mirrors the STT deflake, PR#77).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var synth = BuildSynthesizer();
        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz, cts.Token)
            .ToListAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        _server.ReceivedJsonMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldComplete_WhenServerClosesConnection()
    {
        _server.AudioFramesToSend.Clear();
        var synth = BuildSynthesizer();
        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();
        await act.Should().NotThrowAsync();
    }

    // --- Flash 2.5 / options tests ---

    [Fact]
    public void ElevenLabsOptions_ShouldDefaultModelToFlash25()
    {
        var opts = new ElevenLabsOptions();
        opts.ModelId.Should().Be(ElevenLabsModels.Flash25);
    }

    [Fact]
    public void ElevenLabsOptions_ShouldHonorCustomModel_WhenExplicitlySet()
    {
        var opts = new ElevenLabsOptions { ModelId = ElevenLabsModels.Turbo2 };
        opts.ModelId.Should().Be(ElevenLabsModels.Turbo2);
    }

    [Fact]
    public void ElevenLabsOptions_ShouldDefaultLatencyOptimizationToOff()
    {
        var opts = new ElevenLabsOptions();
        opts.LatencyOptimization.Should().Be(ElevenLabsLatencyOptimization.Off);
    }

    [Fact]
    public void ElevenLabsOptions_ShouldDefaultOutputFormatToPcm16k()
    {
        var opts = new ElevenLabsOptions();
        opts.OutputFormat.Should().Be(ElevenLabsOutputFormat.Pcm16k);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldIncludeFlash25ModelId_WhenDefaultOptions()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.LastRequestUrl.Should().Contain("model_id=eleven_flash_v2_5");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldIncludePcm16000OutputFormat_WhenDefaultOptions()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.LastRequestUrl.Should().Contain("output_format=pcm_16000");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldIncludeLatencyParam2_WhenLatencyOptimizationMid()
    {
        var synth = BuildSynthesizer(o => o.LatencyOptimization = ElevenLabsLatencyOptimization.Mid);

        await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.LastRequestUrl.Should().Contain("optimize_streaming_latency=2");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldIncludePcm24000OutputFormat_WhenOutputFormatPcm24k()
    {
        var synth = BuildSynthesizer(o => o.OutputFormat = ElevenLabsOutputFormat.Pcm24k);

        await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.LastRequestUrl.Should().Contain("output_format=pcm_24000");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldIncludeTurbo2ModelId_WhenModelExplicitlySetToTurbo2()
    {
        var synth = BuildSynthesizer(o => o.ModelId = ElevenLabsModels.Turbo2);

        await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.LastRequestUrl.Should().Contain("model_id=eleven_turbo_v2");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldAuthenticateTheUpgrade_WhenOpeningASession()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedApiKey.Should().Be(TestApiKey);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldPutTheConfiguredVoiceInTheRoute_WhenVoiceIdIsSet()
    {
        // Unassertable before the seam changed: the test-only constructor substituted a literal
        // `test-voice` for whatever VoiceId held, so this route segment came from the branch rather
        // than from the option. A voice id distinct from the class default is what shows the
        // difference.
        var synth = BuildSynthesizer(o => o.VoiceId = "voice-from-options");
        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.LastRequestUrl.Should().StartWith("/v1/text-to-speech/voice-from-options/stream-input");
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}
