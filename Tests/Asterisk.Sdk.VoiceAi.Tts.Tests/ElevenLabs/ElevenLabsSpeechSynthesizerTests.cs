using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Tts.ElevenLabs;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Tts.Tests.ElevenLabs;

public class ElevenLabsSpeechSynthesizerTests : IAsyncDisposable
{
    private readonly ElevenLabsFakeServer _server;

    public ElevenLabsSpeechSynthesizerTests()
    {
        _server = new ElevenLabsFakeServer();
        _server.Start();
    }

    private ElevenLabsSpeechSynthesizer BuildSynthesizer()
        => new(Options.Create(new ElevenLabsOptions
        {
            ApiKey = "test-key",
            VoiceId = "test-voice"
        }), fakeServerPort: _server.Port);

    [Fact]
    public async Task SynthesizeAsync_ShouldYieldBinaryAudioFrames()
    {
        var synth = BuildSynthesizer();
        var frames = await synth.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();
        frames.Should().HaveCount(2);
        frames.All(f => f.Length == 320).Should().BeTrue();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldSendTextChunk()
    {
        var synth = BuildSynthesizer();
        await synth.SynthesizeAsync("hola mundo", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.ReceivedJsonMessages.Should().NotBeEmpty();
        _server.ReceivedJsonMessages.Any(m => m.Contains("hola mundo")).Should().BeTrue();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldFilterAlignmentMessages_NotYieldThem()
    {
        _server.SendAlignmentMessages = true;
        var synth = BuildSynthesizer();
        var frames = await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();
        frames.Should().HaveCount(2);
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldAbort_WhenCancelled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var synth = BuildSynthesizer();
        var act = async () => await synth
            .SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz, cts.Token)
            .ToListAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
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

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}
