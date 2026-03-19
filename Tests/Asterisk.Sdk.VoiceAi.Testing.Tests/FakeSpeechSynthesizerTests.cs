using Asterisk.Sdk.Audio;
using FluentAssertions;

namespace Asterisk.Sdk.VoiceAi.Testing.Tests;

public class FakeSpeechSynthesizerTests
{
    [Fact]
    public async Task SynthesizeAsync_ShouldGenerateSilenceFrames_WithConfiguredDuration()
    {
        var fake = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(60));
        var frames = await fake.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();
        // 60ms / 20ms per frame = 3 frames of 320 bytes each (160 samples * 2 bytes)
        frames.Should().HaveCount(3);
        frames.All(f => f.Length == 320).Should().BeTrue();
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldTrackSynthesizedTexts()
    {
        var fake = new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20));
        await fake.SynthesizeAsync("hola", AudioFormat.Slin16Mono8kHz).ToListAsync();
        await fake.SynthesizeAsync("mundo", AudioFormat.Slin16Mono8kHz).ToListAsync();
        fake.SynthesizedTexts.Should().Equal("hola", "mundo");
    }

    [Fact]
    public async Task SynthesizeAsync_ShouldThrow_WhenConfiguredToError()
    {
        var fake = new FakeSpeechSynthesizer().WithError(new InvalidOperationException("tts fail"));
        var act = async () => await fake.SynthesizeAsync("test", AudioFormat.Slin16Mono8kHz).ToListAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("tts fail");
    }
}
