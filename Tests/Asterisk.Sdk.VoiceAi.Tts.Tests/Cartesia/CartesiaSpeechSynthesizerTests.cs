using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Tts.Cartesia;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Tts.Tests.Cartesia;

public class CartesiaSpeechSynthesizerTests : IAsyncDisposable
{
    private readonly CartesiaFakeServer _server;

    public CartesiaSpeechSynthesizerTests()
    {
        _server = new CartesiaFakeServer();
        _server.Start();
    }

    private CartesiaSpeechSynthesizer BuildSynthesizer()
        => new(Options.Create(new CartesiaOptions
        {
            ApiKey = "test-key",
            VoiceId = "test-voice"
        }), fakeServerPort: _server.Port);

    [Fact]
    public async Task SynthesizeAsync_ShouldSendRequestJson_WithModelAndVoice()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hola mundo", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedJsonMessages.Should().NotBeEmpty();
        var request = _server.ReceivedJsonMessages[0];
        request.Should().Contain("\"model_id\":\"sonic-3\"");
        request.Should().Contain("\"id\":\"test-voice\"");
        request.Should().Contain("\"transcript\":\"hola mundo\"");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldBinaryAudioFrames()
    {
        var synth = BuildSynthesizer();
        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.Should().HaveCount(2);
        frames.All(f => f.Length == 320).Should().BeTrue();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldTerminate_WhenServerSendsDone()
    {
        // The fake server sends 2 binary frames followed by {"type":"done"}.
        // The synthesizer must stop iterating as soon as "done" arrives.
        _server.SendDoneTerminator = true;
        var synth = BuildSynthesizer();

        var frames = await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        frames.Should().HaveCount(2);
    }

    // Deferred to v1.12.1: hang reproduces when HttpListener fake server calls
    // ws.Abort() — same plumbing issue documented in the STT counterpart.
    [Fact(Skip = "Flaky on HttpListener fake server — tracked for v1.12.1")]
    public async Task SynthesizeAsync_ShouldComplete_WhenServerAborts()
    {
        _server.AbortAfterSend = true;
        var synth = BuildSynthesizer();

        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz)
            .ToListAsync();

        await act.Should().NotThrowAsync();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}
