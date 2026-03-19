using Asterisk.Sdk.Audio.Resampling;
using FluentAssertions;

namespace Asterisk.Sdk.Audio.Tests.Resampling;

public sealed class ResamplerFactoryTests
{
    [Theory]
    [InlineData(8000, 16000)]
    [InlineData(16000, 8000)]
    [InlineData(8000, 24000)]
    [InlineData(24000, 8000)]
    [InlineData(16000, 24000)]
    [InlineData(24000, 16000)]
    [InlineData(8000, 48000)]
    [InlineData(48000, 8000)]
    [InlineData(16000, 48000)]
    [InlineData(48000, 16000)]
    [InlineData(24000, 48000)]
    [InlineData(48000, 24000)]
    public void IsSupported_ShouldReturnTrue_ForAllSupportedPairs(int inputRate, int outputRate)
    {
        ResamplerFactory.IsSupported(inputRate, outputRate).Should().BeTrue();
    }

    [Theory]
    [InlineData(8000, 22050)]
    [InlineData(44100, 48000)]
    [InlineData(11025, 8000)]
    [InlineData(8000, 8000)]
    public void IsSupported_ShouldReturnFalse_ForUnsupportedPairs(int inputRate, int outputRate)
    {
        ResamplerFactory.IsSupported(inputRate, outputRate).Should().BeFalse();
    }

    [Fact]
    public void Create_ShouldThrow_ForUnsupportedRatePair()
    {
        var act = () => ResamplerFactory.Create(44100, 48000);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*not supported*");
    }

    [Theory]
    [InlineData(160, 8000, 16000, 320)]
    [InlineData(320, 16000, 8000, 160)]
    [InlineData(160, 8000, 24000, 480)]
    [InlineData(160, 8000, 48000, 960)]
    [InlineData(320, 16000, 24000, 480)]
    public void CalculateOutputSize_ShouldReturnAtLeastExpectedSize(
        int inputSamples, int inputRate, int outputRate, int expectedMinimum)
    {
        int result = ResamplerFactory.CalculateOutputSize(inputSamples, inputRate, outputRate);
        result.Should().BeGreaterThanOrEqualTo(expectedMinimum);
    }

    [Theory]
    [InlineData(8000, 16000)]
    [InlineData(16000, 8000)]
    [InlineData(8000, 48000)]
    [InlineData(48000, 8000)]
    public void Create_ShouldReturnDisposableResampler(int inputRate, int outputRate)
    {
        var resampler = ResamplerFactory.Create(inputRate, outputRate);
        var act = () => resampler.Dispose();
        act.Should().NotThrow();
    }
}
