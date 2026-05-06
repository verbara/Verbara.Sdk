using Verbara.Sdk.VoiceAi.TurnDetection.Internal;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.TurnDetection.Tests.Internal;

public sealed class MelSpectrogramTests
{
    [Fact]
    public void Compute_ShouldReturnCorrectShape_ForFullAudio()
    {
        // 8 seconds at 16kHz = 128,000 samples
        var audio = new float[128_000];
        // Fill with a simple sine wave
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] = MathF.Sin(2.0f * MathF.PI * 440.0f * i / 16000.0f);
        }

        var mel = new MelSpectrogram();
        var result = mel.Compute(audio);

        // Output should be [80, 800] = 64,000 values
        result.Length.Should().Be(80 * 800);
    }

    [Fact]
    public void Compute_ShouldPadAtBeginning_ForShortAudio()
    {
        // 1 second = 16,000 samples → ~98 frames; padded to 800
        var audio = new float[16_000];
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] = MathF.Sin(2.0f * MathF.PI * 440.0f * i / 16000.0f);
        }

        var mel = new MelSpectrogram();
        var result = mel.Compute(audio);

        result.Length.Should().Be(80 * 800);

        // The first columns (beginning) should be padding (all mel bins same value),
        // while later columns should have actual signal content (varying values).
        // Check that early frames are uniform (padded) and later frames are not.
        var earlyBins = new float[80];
        for (int m = 0; m < 80; m++)
        {
            earlyBins[m] = result[m * 800]; // frame 0 for each mel bin
        }

        // Early frames should be padding — check they're all the same value
        earlyBins.Should().AllSatisfy(v => v.Should().BeApproximately(earlyBins[0], 0.01f));
    }

    [Fact]
    public void Compute_ShouldProduceNormalizedValues_InExpectedRange()
    {
        // Generate audio with content
        var audio = new float[32_000]; // 2 seconds
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] = 0.5f * MathF.Sin(2.0f * MathF.PI * 1000.0f * i / 16000.0f);
        }

        var mel = new MelSpectrogram();
        var result = mel.Compute(audio);

        // After (log10 + 4) / 4 normalization, values should generally be in [-1, 1+] range
        // The exact range depends on signal, but we should not see extreme values
        var min = result.Min();
        var max = result.Max();
        min.Should().BeGreaterThan(-2.0f);
        max.Should().BeLessThan(2.0f);
    }

    [Fact]
    public void Fft_ShouldComputeCorrectSpectrum_ForKnownSignal()
    {
        // Impulse at index 0 → flat spectrum (all bins = 1+0i)
        int n = 512;
        var real = new float[n];
        var imag = new float[n];
        real[0] = 1.0f;

        MelSpectrogram.Fft(real, imag);

        // All bins should be 1.0 + 0.0i
        for (int k = 0; k < n; k++)
        {
            real[k].Should().BeApproximately(1.0f, 1e-5f, $"real[{k}]");
            imag[k].Should().BeApproximately(0.0f, 1e-5f, $"imag[{k}]");
        }
    }
}
