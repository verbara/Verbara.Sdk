using Asterisk.Sdk.Audio.Processing;
using FluentAssertions;

namespace Asterisk.Sdk.Audio.Tests.Processing;

public sealed class AudioProcessorTests
{
    [Fact]
    public void ApplyGain_ShouldAmplify_WhenPositiveDb()
    {
        short[] samples = [1000, 2000, 3000];
        AudioProcessor.ApplyGain(samples, 6.0f); // +6 dB ~ 2x
        samples[0].Should().BeGreaterThan(1500);
        samples[1].Should().BeGreaterThan(3000);
    }

    [Fact]
    public void ApplyGain_ShouldAttenuate_WhenNegativeDb()
    {
        short[] samples = [10000, 20000];
        AudioProcessor.ApplyGain(samples, -6.0f); // -6 dB ~ 0.5x
        samples[0].Should().BeLessThan(7000);
        samples[1].Should().BeLessThan(12000);
    }

    [Fact]
    public void ApplyGain_ShouldClamp_WhenResultExceedsShortRange()
    {
        short[] samples = [short.MaxValue];
        AudioProcessor.ApplyGain(samples, 40.0f); // huge amplification
        samples[0].Should().Be(short.MaxValue);
    }

    [Fact]
    public void ConvertToFloat32_ShouldNormalize_ToPlusMinusOne()
    {
        short[] pcm = [32767, -32768, 0];
        float[] f32 = new float[3];
        AudioProcessor.ConvertToFloat32(pcm, f32);
        f32[0].Should().BeApproximately(1.0f, 0.0001f);
        f32[1].Should().BeApproximately(-1.0f, 0.0001f);
        f32[2].Should().Be(0.0f);
    }

    [Fact]
    public void ConvertToPcm16_ShouldDenormalize_FromPlusMinusOne()
    {
        float[] f32 = [1.0f, -1.0f, 0.0f];
        short[] pcm = new short[3];
        AudioProcessor.ConvertToPcm16(f32, pcm);
        pcm[0].Should().Be(short.MaxValue);
        pcm[1].Should().Be((short)(-32767));
        pcm[2].Should().Be(0);
    }

    [Fact]
    public void ConvertToPcm16_ShouldClamp_WhenOutOfRange()
    {
        float[] f32 = [2.0f, -2.0f];
        short[] pcm = new short[2];
        AudioProcessor.ConvertToPcm16(f32, pcm);
        pcm[0].Should().Be(short.MaxValue);
        pcm[1].Should().Be(short.MinValue);
    }

    [Fact]
    public void CalculateRmsEnergy_ShouldReturnZero_ForEmptyInput()
    {
        double rms = AudioProcessor.CalculateRmsEnergy([]);
        rms.Should().Be(0.0);
    }

    [Fact]
    public void CalculateRmsEnergy_ShouldReturnZero_ForAllZeroSamples()
    {
        double rms = AudioProcessor.CalculateRmsEnergy(new short[160]);
        rms.Should().Be(0.0);
    }

    [Fact]
    public void CalculateRmsEnergy_ShouldReturnPositive_ForNonZeroSamples()
    {
        short[] samples = Enumerable.Range(0, 160).Select(_ => (short)1000).ToArray();
        double rms = AudioProcessor.CalculateRmsEnergy(samples);
        rms.Should().BeApproximately(1000.0, 1.0);
    }

    [Fact]
    public void IsSilence_ShouldReturnTrue_ForZeroSamples()
    {
        AudioProcessor.IsSilence(new short[160]).Should().BeTrue();
    }

    [Fact]
    public void IsSilence_ShouldReturnTrue_ForLowEnergySignal()
    {
        // Very quiet signal -- 10 samples at value 1
        short[] samples = Enumerable.Range(0, 160).Select(i => i < 10 ? (short)1 : (short)0).ToArray();
        AudioProcessor.IsSilence(samples, thresholdDb: -40.0).Should().BeTrue();
    }

    [Fact]
    public void IsSilence_ShouldReturnFalse_ForLoudSignal()
    {
        short[] samples = Enumerable.Range(0, 160).Select(_ => (short)10000).ToArray();
        AudioProcessor.IsSilence(samples, thresholdDb: -40.0).Should().BeFalse();
    }
}
