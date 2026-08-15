using System.Text.Json;
using Verbara.Sdk.Audio;
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

    private DeepgramSpeechSynthesizer BuildSynthesizer(
        string model = DeepgramVoices.Thalia,
        string encoding = "linear16",
        int sampleRate = 16000)
        => new(Options.Create(new DeepgramTtsOptions
        {
            ApiKey = "test-key",
            Model = model,
            Encoding = encoding,
            SampleRate = sampleRate,
        }), fakeServerPort: _server.Port);

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

    [Fact]
    public async Task SynthesizeAsync_ShouldComplete_WhenServerAbortsAfterSend()
    {
        _server.AbortAfterSend = true;
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        await act.Should().NotThrowAsync();
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

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}
