using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Tts.Deepgram;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Deepgram;

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
        var speakMsg = _server.ReceivedJsonMessages.FirstOrDefault(m => m.Contains("\"Speak\""));
        speakMsg.Should().NotBeNull();
        speakMsg.Should().Contain("\"text\":\"hola mundo\"");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldSendFlushMessage()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        var flushMsg = _server.ReceivedJsonMessages.FirstOrDefault(m => m.Contains("\"Flush\""));
        flushMsg.Should().NotBeNull("client must send a Flush message to trigger audio generation");
    }

    // ─── Server → client frames ──────────────────────────────────────────────

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldBinaryAudioFrames()
    {
        var synth = BuildSynthesizer();
        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.Should().HaveCount(2);
        frames.All(f => f.Length == 320).Should().BeTrue();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldTerminate_WhenServerSendsFlushed()
    {
        // Server sends 2 binary frames followed by {"type":"Flushed"}.
        // The synthesizer must stop iterating as soon as Flushed arrives.
        _server.SendFlushedTerminator = true;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.Should().HaveCount(2);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldNotThrow_WhenServerSendsWarningFrame()
    {
        // Warning frames must be swallowed — do not throw or break the audio stream.
        _server.SendWarningBeforeAudio = true;
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldNotThrow_WhenServerSendsMetadataFrame()
    {
        // Metadata frames are informational and must be silently ignored.
        _server.SendMetadataOnConnect = true;
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        await act.Should().NotThrowAsync();
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
        // Server keeps the connection open indefinitely so the synthesizer stays live.
        _server.HangForever = true;

        using var cts = new CancellationTokenSource(millisecondsDelay: 200);
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz, cts.Token)
            .ToListAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}
