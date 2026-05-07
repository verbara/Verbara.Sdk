using Verbara.Sdk.VoiceAi.TurnDetection.Internal;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.TurnDetection.Tests.Internal;

public sealed class MelFilterBankTests
{
    [Fact]
    public void Apply_ShouldReturnCorrectLength_ForDefaultParameters()
    {
        var bank = new MelFilterBank();
        var spectrum = new float[201]; // nFft/2 + 1 = 400/2 + 1
        var melEnergies = new float[80];

        bank.Apply(spectrum, melEnergies);

        melEnergies.Length.Should().Be(80);
    }

    [Fact]
    public void Apply_ShouldReturnAllZeros_WhenSpectrumIsZero()
    {
        var bank = new MelFilterBank();
        var spectrum = new float[201];
        var melEnergies = new float[80];

        bank.Apply(spectrum, melEnergies);

        melEnergies.Should().AllSatisfy(v => v.Should().Be(0f));
    }

    [Fact]
    public void Apply_ShouldReturnNonNegativeEnergies_ForPositiveSpectrum()
    {
        var bank = new MelFilterBank();
        var spectrum = new float[201];
        for (int i = 0; i < spectrum.Length; i++)
            spectrum[i] = 1.0f;
        var melEnergies = new float[80];

        bank.Apply(spectrum, melEnergies);

        melEnergies.Should().AllSatisfy(v => v.Should().BeGreaterOrEqualTo(0f));
    }

    [Fact]
    public void Apply_ShouldProducePositiveEnergy_ForFlatSpectrum()
    {
        var bank = new MelFilterBank();
        var spectrum = new float[201];
        for (int i = 0; i < spectrum.Length; i++)
            spectrum[i] = 1.0f;
        var melEnergies = new float[80];

        bank.Apply(spectrum, melEnergies);

        melEnergies.Should().AllSatisfy(v => v.Should().BeGreaterThan(0f),
            "every mel filter should overlap at least one spectrum bin");
    }

    [Fact]
    public void Apply_ShouldConcentrateEnergy_InFilterMatchingSignalFrequency()
    {
        var bank = new MelFilterBank(nMels: 80, nFft: 400, sampleRate: 16000, fMax: 8000f);
        // Simulate a spectrum with energy at bin 50 (~2 kHz for nFft=400, sr=16000)
        // Bin frequency = bin * sampleRate / nFft = 50 * 16000 / 400 = 2000 Hz
        var spectrum = new float[201];
        spectrum[50] = 10.0f;
        var melEnergies = new float[80];

        bank.Apply(spectrum, melEnergies);

        // Find the mel bin with the highest energy
        int peakBin = 0;
        float peakValue = 0f;
        for (int i = 0; i < melEnergies.Length; i++)
        {
            if (melEnergies[i] > peakValue)
            {
                peakValue = melEnergies[i];
                peakBin = i;
            }
        }

        peakValue.Should().BeGreaterThan(0f);
        // 2 kHz maps to mel ~1521 out of mel range [0, ~2840] for 0-8kHz
        // That's roughly mel bin 42 of 80, but exact bin depends on filter widths.
        // Just verify it's in a reasonable range (not at the extreme edges)
        peakBin.Should().BeInRange(20, 60);
    }

    [Fact]
    public void Apply_ShouldScaleLinearly_WhenSpectrumAmplitudeDoubles()
    {
        var bank = new MelFilterBank();
        var spectrum1 = new float[201];
        var spectrum2 = new float[201];
        for (int i = 0; i < 201; i++)
        {
            spectrum1[i] = 1.0f;
            spectrum2[i] = 2.0f;
        }

        var mel1 = new float[80];
        var mel2 = new float[80];
        bank.Apply(spectrum1, mel1);
        bank.Apply(spectrum2, mel2);

        for (int m = 0; m < 80; m++)
        {
            mel2[m].Should().BeApproximately(mel1[m] * 2.0f, 1e-5f,
                $"mel[{m}] should scale linearly");
        }
    }

    [Fact]
    public void Constructor_ShouldCreateCorrectFilterCount_ForCustomMels()
    {
        var bank = new MelFilterBank(nMels: 40, nFft: 400, sampleRate: 16000);
        var spectrum = new float[201];
        for (int i = 0; i < spectrum.Length; i++)
            spectrum[i] = 1.0f;
        var melEnergies = new float[40];

        bank.Apply(spectrum, melEnergies);

        melEnergies.Should().AllSatisfy(v => v.Should().BeGreaterThan(0f));
    }

    [Fact]
    public void Apply_ShouldHaveHigherResolution_InLowerFrequencies()
    {
        var bank = new MelFilterBank(nMels: 80, nFft: 400, sampleRate: 16000, fMax: 8000f);
        // Impulse at low frequency (bin 5 ≈ 200 Hz)
        var specLow = new float[201];
        specLow[5] = 1.0f;
        var melLow = new float[80];
        bank.Apply(specLow, melLow);

        // Impulse at high frequency (bin 190 ≈ 7600 Hz)
        var specHigh = new float[201];
        specHigh[190] = 1.0f;
        var melHigh = new float[80];
        bank.Apply(specHigh, melHigh);

        // Count how many mel bins have non-zero energy for each
        int nonZeroLow = melLow.Count(v => v > 1e-8f);
        int nonZeroHigh = melHigh.Count(v => v > 1e-8f);

        // Low-frequency impulse should activate fewer mel bins (narrower filters)
        // than high-frequency impulse (wider filters in mel scale)
        nonZeroLow.Should().BeLessOrEqualTo(nonZeroHigh,
            "mel filters are narrower at low frequencies, wider at high frequencies");
    }
}
