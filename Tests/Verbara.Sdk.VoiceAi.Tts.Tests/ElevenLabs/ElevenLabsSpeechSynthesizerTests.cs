using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Tts.ElevenLabs;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.ElevenLabs;

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
        var synth = new ElevenLabsSpeechSynthesizer(
            Options.Create(new ElevenLabsOptions
            {
                ApiKey = "test-key",
                VoiceId = "test-voice",
                LatencyOptimization = ElevenLabsLatencyOptimization.Mid
            }),
            fakeServerPort: _server.Port);

        await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.LastRequestUrl.Should().Contain("optimize_streaming_latency=2");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldIncludePcm24000OutputFormat_WhenOutputFormatPcm24k()
    {
        var synth = new ElevenLabsSpeechSynthesizer(
            Options.Create(new ElevenLabsOptions
            {
                ApiKey = "test-key",
                VoiceId = "test-voice",
                OutputFormat = ElevenLabsOutputFormat.Pcm24k
            }),
            fakeServerPort: _server.Port);

        await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.LastRequestUrl.Should().Contain("output_format=pcm_24000");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldIncludeTurbo2ModelId_WhenModelExplicitlySetToTurbo2()
    {
        var synth = new ElevenLabsSpeechSynthesizer(
            Options.Create(new ElevenLabsOptions
            {
                ApiKey = "test-key",
                VoiceId = "test-voice",
                ModelId = ElevenLabsModels.Turbo2
            }),
            fakeServerPort: _server.Port);

        await synth.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();

        _server.LastRequestUrl.Should().Contain("model_id=eleven_turbo_v2");
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}
