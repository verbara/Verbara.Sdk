using System.Net.WebSockets;
using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.TestInfrastructure.WebSocket;
using Verbara.Sdk.VoiceAi.Tts.Deepgram;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Deepgram;

/// <summary>
/// Transport: WebSocket. Deliberately NOT migrated to the WireMock substrate — WireMock.NET matches
/// HTTP/1.1 requests and cannot hold the duplex session these tests drive (ADR-0041 D2), so
/// <c>DeepgramTtsFakeServer</c> on <c>WebSocketTestServer</c> stays. Fidelity here comes from
/// recorded frames (D4), not from a different server.
/// </summary>
public class DeepgramSpeechSynthesizerTests : IAsyncDisposable
{
    private readonly DeepgramTtsFakeServer _server;

    public DeepgramSpeechSynthesizerTests()
    {
        _server = new DeepgramTtsFakeServer();
        _server.Start();
    }

    /// <summary>The credential every test in this class sends, asserted on by the fake.</summary>
    private const string TestApiKey = "test-key";

    private DeepgramSpeechSynthesizer BuildSynthesizer(
        string model = DeepgramVoices.Thalia,
        string encoding = "linear16",
        int sampleRate = 16000)
        => new(Options.Create(new DeepgramTtsOptions
        {
            ApiKey = TestApiKey,
            Model = model,
            Encoding = encoding,
            SampleRate = sampleRate,
            // The operator-facing seam, not a test-only constructor: route, query and credential all
            // come from shipped code. Path as the production default carries it.
            BaseUri = $"ws://127.0.0.1:{_server.Port}/v1/speak",
        }));

    /// <summary>The audio the fake replays, read from the same tree the fake reads.</summary>
    private static byte[] RecordedAudio => DeepgramTtsFakeServer.ReadFrameBytes(DeepgramTtsFakeServer.AudioChunk);

    // ─── Options binding ─────────────────────────────────────────────────────

    [Fact]
    public void DeepgramTtsOptions_ShouldHaveExpectedDefaults()
    {
        var opts = new DeepgramTtsOptions();

        opts.BaseUri.Should().Be("wss://api.deepgram.com/v1/speak");
        opts.Model.Should().Be(DeepgramVoices.Thalia);
        opts.Encoding.Should().Be("linear16");
        opts.SampleRate.Should().Be(24000);
        opts.Speed.Should().Be(1.0);
        opts.ConnectTimeoutSeconds.Should().Be(5);
    }

    [Fact]
    public void DeepgramTtsOptionsValidator_ShouldFail_WhenApiKeyEmpty()
    {
        var opts = new DeepgramTtsOptions { ApiKey = string.Empty };
        var validator = new DeepgramTtsOptionsValidator();

        var result = validator.Validate(null, opts);

        result.Failed.Should().BeTrue();
    }

    // ─── WS request URL ──────────────────────────────────────────────────────

    [Fact]
    public async Task SynthesizeAsync_ShouldSendRequestToCorrectPath()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono16kHz).ToListAsync();

        _server.CapturedRequestUri.Should().StartWith("/v1/speak");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldIncludeModelEncodingAndSampleRateInUrl()
    {
        var synth = BuildSynthesizer(model: "aura-2-zeus-en", encoding: "mulaw", sampleRate: 8000);
        await synth.SynthesizeAsync("hello", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.CapturedRequestUri.Should()
            .Contain("model=aura-2-zeus-en")
            .And.Contain("encoding=mulaw")
            .And.Contain("sample_rate=8000");
    }

    // ─── Client → server messages ────────────────────────────────────────────

    [Fact]
    public async Task SynthesizeAsync_ShouldSendSpeakMessageWithText()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hola mundo", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedJsonMessages.Should().NotBeEmpty();
        var speakMsg = _server.ReceivedJsonMessages.FirstOrDefault(m => m.Contains("\"Speak\"", StringComparison.Ordinal));
        speakMsg.Should().NotBeNull();
        speakMsg.Should().Contain("\"text\":\"hola mundo\"");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldSendFlushMessage()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        var flushMsg = _server.ReceivedJsonMessages.FirstOrDefault(m => m.Contains("\"Flush\"", StringComparison.Ordinal));
        flushMsg.Should().NotBeNull("client must send a Flush message to trigger audio generation");
    }

    // ─── Server → client frames ──────────────────────────────────────────────

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldBinaryAudioFrames()
    {
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

        (expected.Length % DeepgramTtsFakeServer.AudioFrameSize).Should()
            .NotBe(0, "the recording must not be chunk-aligned");
        frames.Should().HaveCountGreaterThan(1, "the recording must actually be chunked");
        frames.Should().OnlyContain(f => f.Length > 0 && f.Length <= DeepgramTtsFakeServer.AudioFrameSize);
        frames.Should().Contain(f => f.Length != DeepgramTtsFakeServer.AudioFrameSize,
            "a partial frame must reach the consumer");
        frames.SelectMany(f => f.ToArray()).Should().Equal(expected,
            "streaming must not alter a single byte of the recorded audio");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldTerminate_WhenServerSendsFlushed()
    {
        // Server sends the recorded audio as binary frames, then the recorded Flushed frame.
        // The synthesizer must stop iterating as soon as Flushed arrives — with every audio byte
        // delivered and nothing after it.
        _server.SendFlushedTerminator = true;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.SelectMany(f => f.ToArray()).Should().Equal(RecordedAudio);
    }

    [Fact]
    public void RecordedFixtures_ShouldCarryDocumentedFieldsAndExactByteLength_WhenReadFromRecordingsTree()
    {
        // Fixture-integrity fence. The fake is only as good as what is on disk: trim a documented
        // field, swap the audio or re-save the JSON and the suite would keep passing while quietly
        // testing something smaller. This fails here, next to the sidecar that explains the file.
        var audio = RecordedAudio;
        audio.Should().HaveCount(2408, "the sidecar records this exact length");
        (audio.Length % DeepgramTtsFakeServer.AudioFrameSize).Should().NotBe(0);

        using var metadata = JsonDocument.Parse(
            DeepgramTtsFakeServer.ReadFrame(DeepgramTtsFakeServer.MetadataFrame));
        var meta = metadata.RootElement;
        meta.GetProperty("type").GetString().Should().Be("Metadata");
        meta.GetProperty("request_id").GetString().Should().Be("00000000-0000-0000-0000-000000000000",
            "a correlating identifier is placeholdered, never real (protocol guide §4)");
        meta.GetProperty("model_name").ValueKind.Should().Be(JsonValueKind.String);
        meta.GetProperty("model_version").ValueKind.Should().Be(JsonValueKind.String);

        // The two documented fields DeepgramTtsServerMessage does NOT model. They reach the parser
        // as unmodelled siblings, which the hand-authored four-field object never did.
        meta.GetProperty("model_uuid").ValueKind.Should().Be(JsonValueKind.String);
        meta.GetProperty("additional_model_uuids").EnumerateArray().Should().NotBeEmpty();

        using var warning = JsonDocument.Parse(
            DeepgramTtsFakeServer.ReadFrame(DeepgramTtsFakeServer.WarningFrame));
        warning.RootElement.GetProperty("type").GetString().Should().Be("Warning");
        warning.RootElement.GetProperty("description").ValueKind.Should().Be(JsonValueKind.String);
        warning.RootElement.GetProperty("code").ValueKind.Should().Be(JsonValueKind.String);

        using var flushed = JsonDocument.Parse(
            DeepgramTtsFakeServer.ReadFrame(DeepgramTtsFakeServer.FlushedFrame));
        flushed.RootElement.GetProperty("type").GetString().Should().Be("Flushed");
        flushed.RootElement.GetProperty("sequence_id").ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public void RecordedFixtures_ShouldMatchTheirDocumentedGeneratorParameters_WhenRegeneratedLocally()
    {
        // The "commit a small generator" half of the source-audio rule (protocol guide §6): the
        // committed bytes are reproducible from three numbers in the sidecar, not magic.
        var regenerated = SyntheticPcm.Triangle(
            DeepgramTtsFakeServer.AudioSampleCount,
            DeepgramTtsFakeServer.AudioPeriodSamples,
            DeepgramTtsFakeServer.AudioAmplitude);

        regenerated.Should().Equal(RecordedAudio);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldNotThrow_WhenServerSendsWarningFrame()
    {
        // Warning frames must be swallowed — do not throw or break the audio stream. The frame is
        // now the recorded one, carrying every field Deepgram documents on this message type.
        _server.SendWarningBeforeAudio = true;
        var synth = BuildSynthesizer();
        List<ReadOnlyMemory<byte>> frames = [];

        var act = async () => frames = await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        await act.Should().NotThrowAsync();
        frames.SelectMany(f => f.ToArray()).Should().Equal(RecordedAudio,
            "a warning must not cost the caller a byte of audio");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldNotThrow_WhenServerSendsMetadataFrame()
    {
        // Metadata frames are informational and must be silently ignored — including the two
        // documented fields (model_uuid, additional_model_uuids) the SDK's DTO does not model. A
        // parser that threw on an unmodelled sibling passed against the previous four-field literal
        // and fails against this frame.
        _server.SendMetadataOnConnect = true;
        var synth = BuildSynthesizer();
        List<ReadOnlyMemory<byte>> frames = [];

        var act = async () => frames = await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        await act.Should().NotThrowAsync();
        frames.SelectMany(f => f.ToArray()).Should().Equal(RecordedAudio,
            "an unmodelled sibling field must not cost the caller a byte of audio");
    }

    /// <summary>
    /// Door 3 (<c>ADR-0050</c> E2c), and the inverse of what this test used to assert. Under
    /// <c>NotThrowAsync</c> a socket killed mid-utterance ended the stream exactly as a completed
    /// utterance does, so a caller had no way to tell a truncated synthesis from a whole one.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowTransportFailure_WhenServerAbortsAfterSend()
    {
        _server.AbortAfterSend = true;
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.Transport);
        failure.Code.Should().BeNull("a dead socket carries no vendor code");
        failure.InnerException.Should().BeOfType<WebSocketException>();
    }

    /// <summary>
    /// The fourth door, and on this surface the one that matters most: §1.3a measured this vendor
    /// rejecting a bad credential with <c>HTTP 401</c> at the upgrade, on both its surfaces, which is
    /// exactly the failure <c>ADR-0050</c> E7 wraps. This test drives the no-HTTP-answer half — a
    /// refused connection, hence no code — because no fake in this suite can answer an upgrade with a
    /// status yet; the <c>401</c> mapping itself is asserted on the factory
    /// (<c>SpeechProviderFailureExceptionTests</c>). Built inline rather than through
    /// <c>BuildSynthesizer</c> because the point of this test is that it never reaches the fake.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowHandshakeFailure_WhenNothingAcceptsTheUpgrade()
    {
        var synth = new DeepgramSpeechSynthesizer(Options.Create(new DeepgramTtsOptions
        {
            ApiKey = TestApiKey,
            Model = DeepgramVoices.Thalia,
            Encoding = "linear16",
            SampleRate = 16000,
            BaseUri = $"ws://127.0.0.1:{ClosedPort.Reserve()}/v1/speak",
        }));

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.Handshake);
        failure.Code.Should().BeNull("a refused connection produced no HTTP answer to report");
        failure.InnerException.Should().BeAssignableTo<WebSocketException>();
    }

    /// <summary>
    /// Door 1 (<c>ADR-0050</c> E2a) — the one branch of this change on this surface with no live
    /// measurement behind it. This vendor rejects a credential at the handshake, so no probe has ever
    /// produced an in-band failure frame here and the frame below is the documented shape, not a
    /// recorded one. The test exists for the same reason the branch does: the cost is one frame, and
    /// the alternative is leaving door 1 open on the assumption that this vendor never sends one.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowErrorFrameFailure_WhenTheServerSendsAnErrorFrame()
    {
        _server.ErrorFrameJson =
            """{"type":"Error","code":"UNSUPPORTED_ENCODING","description":"encoding is not supported"}""";
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.ErrorFrame);
        failure.Code.Should().Be("UNSUPPORTED_ENCODING");
        failure.Message.Should().Contain("encoding is not supported");
    }

    /// <summary>
    /// Door 2 (<c>ADR-0050</c> E2b): no frame of any kind, just a close code the client used to read
    /// nowhere. With no audio and no <c>Flushed</c>, the code is the only signal in the session.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_ShouldThrowCloseCodeFailure_WhenServerClosesAbnormally()
    {
        _server.AudioFramesToSend.Clear();
        _server.SendFlushedTerminator = false;
        _server.CloseStatus = WebSocketCloseStatus.PolicyViolation;   // 1008
        _server.CloseStatusDescription = "quota exceeded";
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        var failure = (await act.Should().ThrowAsync<SpeechProviderFailureException>()).Which;
        failure.Signal.Should().Be(SpeechProviderFailureSignal.CloseCode);
        failure.Code.Should().Be("1008");
        failure.Message.Should().Contain("quota exceeded");
    }

    /// <summary>
    /// D2 (<c>ADR-0050</c> E5): the vendor completes the utterance properly — <c>Flushed</c> then a
    /// normal close — having sent no audio. Nothing failed on the wire, so this is not a
    /// <see cref="SpeechProviderFailureException"/>; it is still an empty synthesis, and silence is
    /// not an answer to a request for speech.
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
        empty.Should().NotBeOfType<SpeechProviderFailureException>(
            "nothing failed on the wire — the session was clean and produced nothing");

        // Pinned rather than skipped because this vendor's two surfaces label themselves differently:
        // this synthesizer reports `DeepgramTts`, the recognizer the bare `Deepgram`. Not a contract —
        // Speechmatics and Cartesia also ship both surfaces and use one label for each pair, so the
        // surface is not recoverable from the provider name in general. (This is *not* ADR-0050 E8: E8 is
        // the substitution of D2's operational discriminator — which of the two exception types fires —
        // and says nothing about how a provider spells its own name.)
        empty.Provider.Should().Be("DeepgramTts");
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

        var frames = await synth.SynthesizeAsync("\n\t", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.Should().BeEmpty();
        _server.CapturedAuthorization.Should().BeNull("no session should have been opened at all");
        _server.ReceivedJsonMessages.Should().BeEmpty();
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
    public async Task SynthesizeAsync_ShouldAuthenticateWithTheTokenScheme_WhenOpeningASession()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        // Scheme and key together: Deepgram takes `Token <key>`, so asserting the key alone would
        // pass a client that sent `Bearer`.
        _server.CapturedAuthorization.Should().Be($"Token {TestApiKey}");
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}
