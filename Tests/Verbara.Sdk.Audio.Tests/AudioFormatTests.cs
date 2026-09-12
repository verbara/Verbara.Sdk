using FluentAssertions;

namespace Verbara.Sdk.Audio.Tests;

public sealed class AudioFormatTests
{
    [Theory]
    [InlineData(8000, 1, 16, AudioEncoding.LinearPcm, 20, 320)]   // Asterisk slin, 20 ms packetization
    [InlineData(8000, 1, 16, AudioEncoding.LinearPcm, 50, 800)]
    [InlineData(16000, 1, 16, AudioEncoding.LinearPcm, 20, 640)]
    [InlineData(48000, 1, 16, AudioEncoding.LinearPcm, 20, 1920)]
    [InlineData(48000, 2, 16, AudioEncoding.LinearPcm, 10, 1920)]
    [InlineData(16000, 1, 32, AudioEncoding.IeeeFloat, 20, 1280)] // AI model input
    public void BytesPerFrame_ShouldReturnPinnedByteCount_WhenFormatIsRepresentative(
        int sampleRate, int channels, int bitsPerSample, AudioEncoding encoding, int frameMs, int expectedBytes)
    {
        var format = new AudioFormat(sampleRate, channels, bitsPerSample, encoding);

        format.BytesPerFrame(TimeSpan.FromMilliseconds(frameMs)).Should().Be(expectedBytes);
    }

    [Theory]
    [InlineData(8000, 1, 16, AudioEncoding.LinearPcm, 20, 160)]
    [InlineData(48000, 1, 16, AudioEncoding.LinearPcm, 20, 960)]
    [InlineData(48000, 2, 16, AudioEncoding.LinearPcm, 10, 480)]
    [InlineData(16000, 1, 32, AudioEncoding.IeeeFloat, 20, 320)]
    public void SamplesPerFrame_ShouldReturnPinnedSampleCount_WhenFormatIsRepresentative(
        int sampleRate, int channels, int bitsPerSample, AudioEncoding encoding, int frameMs, int expectedSamples)
    {
        var format = new AudioFormat(sampleRate, channels, bitsPerSample, encoding);

        format.SamplesPerFrame(TimeSpan.FromMilliseconds(frameMs)).Should().Be(expectedSamples);
    }

    [Fact]
    public void BytesPerFrame_ShouldMatchPresetFormats_WhenFrameIsTwentyMilliseconds()
    {
        var frame = TimeSpan.FromMilliseconds(20);

        AudioFormat.Slin16Mono8kHz.BytesPerFrame(frame).Should().Be(320);
        AudioFormat.Slin16Mono16kHz.BytesPerFrame(frame).Should().Be(640);
        AudioFormat.Slin16Mono24kHz.BytesPerFrame(frame).Should().Be(960);
        AudioFormat.Slin16Mono48kHz.BytesPerFrame(frame).Should().Be(1920);
        AudioFormat.Float32Mono16kHz.BytesPerFrame(frame).Should().Be(1280);
    }

    [Fact]
    public void BytesPerFrame_ShouldNotWrapNegative_WhenRateTimesSampleWidthExceedsInt32Range()
    {
        // 1 GHz x 4 bytes = 4e9 bytes per second, past int.MaxValue; 100 microseconds of it is 400,000 bytes.
        var format = new AudioFormat(1_000_000_000, 1, 32, AudioEncoding.IeeeFloat);

        format.BytesPerFrame(TimeSpan.FromTicks(1000)).Should().Be(400_000);
    }
}
