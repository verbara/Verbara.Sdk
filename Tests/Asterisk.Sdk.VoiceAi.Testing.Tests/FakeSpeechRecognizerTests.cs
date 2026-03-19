using Asterisk.Sdk.Audio;
using FluentAssertions;

namespace Asterisk.Sdk.VoiceAi.Testing.Tests;

public class FakeSpeechRecognizerTests
{
    [Fact]
    public async Task StreamAsync_ShouldReturnConfiguredTranscript()
    {
        var fake = new FakeSpeechRecognizer().WithTranscript("hola mundo");
        var results = await fake.StreamAsync(EmptyFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        results.Should().ContainSingle(r => r.Transcript == "hola mundo" && r.IsFinal);
    }

    [Fact]
    public async Task StreamAsync_ShouldCycleTranscripts_WhenCalledMultipleTimes()
    {
        var fake = new FakeSpeechRecognizer().WithTranscripts(["uno", "dos"]);
        var r1 = await fake.StreamAsync(EmptyFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        var r2 = await fake.StreamAsync(EmptyFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        var r3 = await fake.StreamAsync(EmptyFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        r1[0].Transcript.Should().Be("uno");
        r2[0].Transcript.Should().Be("dos");
        r3[0].Transcript.Should().Be("uno"); // cycles
    }

    [Fact]
    public async Task StreamAsync_ShouldTrackCallCount()
    {
        var fake = new FakeSpeechRecognizer().WithTranscript("test");
        await fake.StreamAsync(EmptyFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await fake.StreamAsync(EmptyFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        fake.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task StreamAsync_ShouldThrow_WhenConfiguredToError()
    {
        var fake = new FakeSpeechRecognizer().WithError(new InvalidOperationException("stt fail"));
        var act = async () => await fake.StreamAsync(EmptyFrames(), AudioFormat.Slin16Mono8kHz).ToListAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("stt fail");
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> EmptyFrames()
    {
        await Task.CompletedTask;
        yield break;
    }
}
