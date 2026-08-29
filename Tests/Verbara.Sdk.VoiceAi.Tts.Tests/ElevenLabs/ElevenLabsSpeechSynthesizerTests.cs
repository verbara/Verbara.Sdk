using System.Net.WebSockets;
using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.WebSocket;
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
            // ADR-0052 F3: the consumer holds no token. Passing the cancelled one to ToListAsync
            // makes the enumerator throw on our behalf, and the assertion then cannot tell a
            // propagated throw from a silent `yield break` in the subject.
            .ToListAsync(CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();
        _server.ReceivedJsonMessages.Should().BeEmpty();
    }

    /// <summary>
    /// The case this surface never had: cancellation observed while the server still has
    /// audio to give.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sibling above hands <c>SynthesizeAsync</c> a pre-cancelled token, so it exercises the
    /// entry guard and no socket is ever opened. This one cancels from inside the caller's own
    /// <c>await foreach</c>, one audio chunk in, with the fake parked mid-delivery — the shape
    /// §3.3 of <c>voiceai-midstream-cancellation-coverage</c> prescribes.
    /// </para>
    /// <para>
    /// Two conditions are asserted rather than assumed, because "it threw" alone would also pass
    /// if the session had ended on the server's own close and the test were crediting
    /// cancellation with someone else's work: the socket was live at the moment the token fired,
    /// and the gate was holding an undelivered frame. The gate counts the fake's <em>outbound</em>
    /// messages; this surface sends no greeting, so a hold at 1 means the first <c>audio</c> frame
    /// was delivered and the second was not. The recorded audio chunks into nine, and the last
    /// carries <c>isFinal</c>, so the hold is nowhere near the terminator.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SynthesizeAsync_ShouldAbort_WhenCancelledMidDelivery()
    {
        var gate = new OutboundFrameGate(1);
        _server.OutboundGate = gate;

        using var cts = new CancellationTokenSource();
        var synth = BuildSynthesizer();

        WebSocketState? stateAtCancel = null;
        var observed = 0;

        var act = async () =>
        {
            // ADR-0052 F3: the token goes to SynthesizeAsync and nowhere else. The consumer holds
            // none, so a throw here is the synthesizer's own.
            await foreach (var chunk in synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz, cts.Token))
            {
                observed++;
                stateAtCancel = _server.SocketState;
                await cts.CancelAsync();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();

        observed.Should().Be(1, "the cancel must land after audio reached the caller, not before");
        stateAtCancel.Should().Be(
            WebSocketState.Open,
            "cancellation has to be observed on a live socket, or the stream ended on the server's "
            + "close instead and the test would be crediting cancellation with someone else's work");
        gate.Held.IsCompleted.Should().BeTrue("the fake must still have had a frame to send");
        gate.Delivered.Should().Be(1, "exactly the first audio frame was delivered");
    }

    /// <summary>
    /// D2 on this surface (ADR-0050 E5). This test required the opposite until then — a clean close
    /// with zero audio had to complete silently — which is the outcome an invalid credential produced
    /// on this vendor, since its <c>1008</c> failure frame carries no <c>audio</c> member and was
    /// dropped as "carries no audio".
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowEmptyResult_WhenSessionEndsCleanlyWithNoAudio()
    {
        _server.AudioFramesToSend.Clear();
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var empty = (await act.Should().ThrowAsync<SpeechProviderEmptyResultException>()).Which;
        empty.Provider.Should().Be("ElevenLabs");
        empty.Should().NotBeOfType<SpeechProviderFailureException>();
    }

    /// <summary>
    /// Door 1 (ADR-0049 D1) with this vendor's measured frame: an invalid credential answers
    /// <c>{"message":…,"error":"invalid_api_key","code":1008}</c>, which has no <c>audio</c> member
    /// and was therefore skipped by the frame decoder, leaving the caller an empty stream and no
    /// exception. The session closes normally here, so the frame is the only failure signal.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowErrorFrameFailure_WhenCredentialIsRejectedInBand()
    {
        _server.ErrorFrameJson =
            """{"message":"Invalid API key","error":"invalid_api_key","code":1008}""";
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.ErrorFrame);

        // The symbolic code, not the numeric one: `invalid_api_key` is what a retry policy can act on
        // (it must not retry), where 1008 says only "policy violation".
        failure.Code.Should().Be("invalid_api_key");
        failure.Message.Should().Contain("Invalid API key");
    }

    /// <summary>
    /// Door 2 (ADR-0050 E2b) — the same rejection spelled as a close code, which this client also
    /// discarded. Either door alone catches it, and this test proves the second one does.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowCloseCodeFailure_WhenServerClosesAbnormally()
    {
        _server.AudioFramesToSend.Clear();
        _server.CloseStatus = WebSocketCloseStatus.PolicyViolation;   // 1008, as measured
        _server.CloseStatusDescription = "invalid_api_key";
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.CloseCode);
        failure.Code.Should().Be("1008");
        failure.Message.Should().Contain("invalid_api_key");
    }

    /// <summary>
    /// Door 3 (ADR-0050 E2c). Unreachable on this surface before: the fake had no way to kill a
    /// socket, so a truncated synthesis had never been exercised here at all.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowTransportFailure_WhenServerAbortsMidSession()
    {
        _server.AbortAfterSend = true;
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.Transport);
        failure.Code.Should().BeNull();
        failure.InnerException.Should().BeOfType<WebSocketException>();
    }

    /// <summary>
    /// The fourth door, and the one no receive-loop test can reach: a session that never opens. This
    /// vendor was measured validating <em>in band</em> (a <c>1008</c> failure frame), so its handshake
    /// normally succeeds — which is the whole point of <c>ADR-0050</c> E7: where a vendor validates is
    /// the vendor's choice, and a caller should not have to catch a different type when it changes.
    /// A refused connection carries no HTTP answer, hence no code.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowHandshakeFailure_WhenNothingAcceptsTheUpgrade()
    {
        var synth = BuildSynthesizer(
            o => o.BaseUri = $"ws://127.0.0.1:{ClosedPort.Reserve()}/v1/text-to-speech");

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.Handshake);
        failure.Code.Should().BeNull("a refused connection produced no HTTP answer to report");
        failure.InnerException.Should().BeAssignableTo<WebSocketException>();
    }

    /// <summary>
    /// The other half of E5: text carrying no speech is not asked of the provider at all, so the zero
    /// audio that follows is not a failure. Asserted through the fake seeing no session, since "did
    /// not throw" alone would also pass if a session had opened and been lucky.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldYieldNothingWithoutConnecting_WhenTextIsWhitespace()
    {
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync(" \t ", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.Should().BeEmpty();
        _server.ReceivedApiKey.Should().BeNull("no session should have been opened at all");
        _server.ReceivedJsonMessages.Should().BeEmpty();
    }

    /// <summary>
    /// The precedence <c>streaming-session-lifecycle</c> states: a requested cancellation outranks
    /// the empty-input shortcut. This surface already answered correctly before the rule was
    /// written down, its token check sitting ahead of the blank-text branch, — the assertion is here so the next synthesizer added to this
    /// package inherits it rather than the convention.
    /// </summary>
    /// <remarks>
    /// The token goes to the subject and <see cref="CancellationToken.None"/> to the enumerator, so
    /// the exception asserted came out of <c>SynthesizeAsync</c> and not out of the consumer
    /// standing in for it (ADR-0052 F3).
    /// </remarks>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowOperationCanceled_WhenTextIsWhitespaceAndTokenAlreadyCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync(" \t ", AudioFormat.Slin16Mono8kHz, cts.Token)
            .ToListAsync(CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _server.ReceivedApiKey.Should().BeNull("the cancellation is observed before any session is opened");
        _server.ReceivedJsonMessages.Should().BeEmpty();
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
