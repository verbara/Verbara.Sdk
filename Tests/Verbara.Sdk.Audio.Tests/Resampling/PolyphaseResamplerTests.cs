using Verbara.Sdk.Audio.Resampling;
using FluentAssertions;

namespace Verbara.Sdk.Audio.Tests.Resampling;

public sealed class PolyphaseResamplerTests
{
    [Fact]
    public void Create_ShouldSucceed_ForAllSupportedRatePairs()
    {
        var pairs = new[]
        {
            (8000, 16000), (16000, 8000),
            (8000, 24000), (24000, 8000),
            (16000, 24000), (24000, 16000),
            (8000, 48000), (48000, 8000),
            (16000, 48000), (48000, 16000),
            (24000, 48000), (48000, 24000),
        };

        foreach (var (input, output) in pairs)
        {
            using var resampler = ResamplerFactory.Create(input, output);
            resampler.Should().NotBeNull($"rate pair {input}->{output} should be supported");
        }
    }

    [Fact]
    public void Create_ShouldThrow_ForUnsupportedRatePair()
    {
        var act = () => ResamplerFactory.Create(8000, 22050);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Process_8kTo16k_ShouldProduceApproximatelyDoubleOutputSamples()
    {
        using var resampler = ResamplerFactory.Create(8000, 16000);

        // 160 samples @ 8kHz = 20ms
        short[] input = new short[160];
        for (int i = 0; i < input.Length; i++)
            input[i] = (short)(short.MaxValue * 0.5f * Math.Sin(2 * Math.PI * 440 * i / 8000.0));

        short[] output = new short[ResamplerFactory.CalculateOutputSize(input.Length, 8000, 16000)];
        int written = resampler.Process(input.AsSpan(), output.AsSpan());

        written.Should().BeGreaterThanOrEqualTo(input.Length * 2 - 4);
        written.Should().BeLessThanOrEqualTo(input.Length * 2 + 4);
    }

    [Fact]
    public void Process_16kTo8k_ShouldProduceApproximatelyHalfOutputSamples()
    {
        using var resampler = ResamplerFactory.Create(16000, 8000);

        short[] input = new short[320]; // 20ms @ 16kHz
        for (int i = 0; i < input.Length; i++)
            input[i] = (short)(short.MaxValue * 0.5f * Math.Sin(2 * Math.PI * 440 * i / 16000.0));

        short[] output = new short[ResamplerFactory.CalculateOutputSize(input.Length, 16000, 8000)];
        int written = resampler.Process(input.AsSpan(), output.AsSpan());

        written.Should().BeGreaterThanOrEqualTo(input.Length / 2 - 4);
        written.Should().BeLessThanOrEqualTo(input.Length / 2 + 4);
    }

    [Fact]
    public void Process_8kTo24k_ShouldProduceApproximatelyTripleOutputSamples()
    {
        using var resampler = ResamplerFactory.Create(8000, 24000);

        short[] input = new short[160];
        for (int i = 0; i < input.Length; i++)
            input[i] = (short)(short.MaxValue * 0.5f * Math.Sin(2 * Math.PI * 440 * i / 8000.0));

        short[] output = new short[ResamplerFactory.CalculateOutputSize(input.Length, 8000, 24000)];
        int written = resampler.Process(input.AsSpan(), output.AsSpan());

        written.Should().BeGreaterThanOrEqualTo(input.Length * 3 - 4);
        written.Should().BeLessThanOrEqualTo(input.Length * 3 + 4);
    }

    [Fact]
    public void Process_ShouldMaintainContinuityAcrossFrames()
    {
        using var resampler = ResamplerFactory.Create(8000, 16000);

        // Two consecutive 20ms frames of sine wave
        short[] frame1 = new short[160];
        short[] frame2 = new short[160];
        for (int i = 0; i < 160; i++)
        {
            frame1[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / 8000.0));
            frame2[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * (i + 160) / 8000.0));
        }

        short[] out1 = new short[400];
        short[] out2 = new short[400];
        int n1 = resampler.Process(frame1.AsSpan(), out1.AsSpan());
        int n2 = resampler.Process(frame2.AsSpan(), out2.AsSpan());

        // Both frames should produce output
        n1.Should().BeGreaterThan(0);
        n2.Should().BeGreaterThan(0);

        // Across the frame boundary the output may move no further than the tone itself can between two output
        // samples (continuity check -- no abrupt jumps from delay line corruption). At unity gain a 440 Hz tone of
        // amplitude 10000 sampled at 16 kHz moves at most 2 * 10000 * sin(pi * 440 / 16000) per sample, plus two
        // for truncating the input and rounding the output.
        var largestToneStep = 2 * 10000 * Math.Sin(Math.PI * 440 / 16000.0);
        Math.Abs((int)out1[n1 - 1] - out2[0]).Should().BeLessThanOrEqualTo((int)Math.Ceiling(largestToneStep) + 2);
    }

    [Fact]
    public void Process_ShouldThrow_AfterDispose()
    {
        var resampler = ResamplerFactory.Create(8000, 16000);
        resampler.Dispose();

        short[] input = new short[160];
        short[] output = new short[400];
#pragma warning disable IDISP016 // Intentional — testing that disposed instance throws
        var act = () => resampler.Process(input.AsSpan(), output.AsSpan());
#pragma warning restore IDISP016
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Process_ShouldNotAllocate_OnHotPath()
    {
        using var resampler = ResamplerFactory.Create(8000, 16000);
        short[] input = new short[160];
        short[] output = new short[400];

        // Warm up (first call may trigger JIT or other one-time allocations)
        resampler.Process(input.AsSpan(), output.AsSpan());

        long before = GC.GetAllocatedBytesForCurrentThread();
        resampler.Process(input.AsSpan(), output.AsSpan());
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).Should().Be(0, "hot path must not allocate");
    }

    [Fact]
    public void Reset_ShouldClearDelayLine_WithoutAffectingSubsequentOutput()
    {
        using var resampler = ResamplerFactory.Create(8000, 16000);
        short[] input = new short[160];
        short[] output = new short[400];

        resampler.Process(input.AsSpan(), output.AsSpan());
        resampler.Reset(); // clear state

        // Should still produce output after reset
        int written = resampler.Process(input.AsSpan(), output.AsSpan());
        written.Should().BeGreaterThan(0);
    }

    [Fact]
    public void MaxOutputBytes_ShouldReturnSufficientBufferSize()
    {
        using var resampler = ResamplerFactory.Create(8000, 16000);
        int inputBytes = 160 * 2; // 160 short samples = 320 bytes
        int maxBytes = resampler.MaxOutputBytes(inputBytes);

        short[] input = new short[160];
        short[] output = new short[maxBytes / 2];
        int written = resampler.Process(input.AsSpan(), output.AsSpan());

        (written * 2).Should().BeLessThanOrEqualTo(maxBytes);
    }

    [Fact]
    public void Process_16kTo24k_ShouldProduceCorrectRatio()
    {
        using var resampler = ResamplerFactory.Create(16000, 24000);

        // 320 samples @ 16kHz -> ~480 samples @ 24kHz (ratio 3:2)
        short[] input = new short[320];
        for (int i = 0; i < input.Length; i++)
            input[i] = (short)(short.MaxValue * 0.5f * Math.Sin(2 * Math.PI * 440 * i / 16000.0));

        short[] output = new short[ResamplerFactory.CalculateOutputSize(320, 16000, 24000)];
        int written = resampler.Process(input.AsSpan(), output.AsSpan());

        // 320 * 24000/16000 = 480
        written.Should().BeGreaterThanOrEqualTo(480 - 4);
        written.Should().BeLessThanOrEqualTo(480 + 4);
    }

    [Fact]
    public void Process_ByteOverload_ShouldReturnCorrectByteCount()
    {
        using var resampler = ResamplerFactory.Create(8000, 16000);

        // 160 samples = 320 bytes
        short[] inputSamples = new short[160];
        for (int i = 0; i < inputSamples.Length; i++)
            inputSamples[i] = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / 8000.0));

        byte[] inputBytes = new byte[320];
        Buffer.BlockCopy(inputSamples, 0, inputBytes, 0, inputBytes.Length);

        byte[] outputBytes = new byte[resampler.MaxOutputBytes(inputBytes.Length)];
        int bytesWritten = resampler.Process(inputBytes.AsSpan(), outputBytes.AsSpan());

        // Should return bytes, not samples -- approximately 320*2 = 640 bytes
        bytesWritten.Should().BeGreaterThanOrEqualTo(320 * 2 - 8);
        bytesWritten.Should().BeLessThanOrEqualTo(320 * 2 + 8);
    }

    [Fact]
    public void InputFormat_ShouldMatchInputRate()
    {
        using var resampler = ResamplerFactory.Create(8000, 16000);
        resampler.InputFormat.SampleRate.Should().Be(8000);
        resampler.InputFormat.Channels.Should().Be(1);
        resampler.InputFormat.BitsPerSample.Should().Be(16);
        resampler.InputFormat.Encoding.Should().Be(AudioEncoding.LinearPcm);
    }

    [Fact]
    public void OutputFormat_ShouldMatchOutputRate()
    {
        using var resampler = ResamplerFactory.Create(8000, 16000);
        resampler.OutputFormat.SampleRate.Should().Be(16000);
        resampler.OutputFormat.Channels.Should().Be(1);
        resampler.OutputFormat.BitsPerSample.Should().Be(16);
        resampler.OutputFormat.Encoding.Should().Be(AudioEncoding.LinearPcm);
    }

    // ── Level ──────────────────────────────────────────────────────────────────
    // A resampler changes the rate, not the level. Each test below analyses whole seconds of steady-state
    // output and reads a tone with a single-frequency projection. Every frequency involved is an integer number
    // of hertz, so over a whole number of seconds the tone, its images and its aliases are orthogonal to one
    // another and each projection reads only its own component.

    private const double DcLevel = 16384;
    private const double ToneLevel = 16000;
    private const double ImageToneLevel = 30000;
    private const int StopbandSeconds = 4;

    /// <summary>
    /// DC tolerance. The largest measured deviation is 0.0003 dB: a branch sum 2.7e-5 short of one, plus
    /// rounding to a whole sample value. 0.01 dB leaves a wide margin and is still far below the 6 to 15.6 dB
    /// the duplicated scale factor used to cost.
    /// </summary>
    private const double DcToleranceDb = 0.01;

    /// <summary>
    /// Passband tone tolerance. The largest measured deviation is -0.087 dB, for 1 kHz through 48 kHz -> 8 kHz,
    /// where that decimator's roll-off has already begun. Everywhere else it is below 0.05 dB.
    /// </summary>
    private const double PassbandToleranceDb = 0.1;

    private static readonly (int InputRate, int OutputRate)[] Pairs =
    [
        (8000, 16000), (16000, 8000),
        (8000, 24000), (24000, 8000),
        (16000, 24000), (24000, 16000),
        (8000, 48000), (48000, 8000),
        (16000, 48000), (48000, 16000),
        (24000, 48000), (48000, 24000),
    ];

    /// <summary>Every pair <see cref="ResamplerFactory.IsSupported"/> accepts.</summary>
    public static TheoryData<int, int> SupportedPairs
    {
        get
        {
            var data = new TheoryData<int, int>();
            foreach (var (inputRate, outputRate) in Pairs)
                data.Add(inputRate, outputRate);
            return data;
        }
    }

    /// <summary>
    /// 300 Hz and 1 kHz for every pair, and 0.8 of the lower Nyquist for every pair whose filter has more than one
    /// branch. Those filters have at least 64 taps, which keeps the band edge flat that far up. The five
    /// single-branch decimators are covered by the roll-off test instead.
    /// </summary>
    public static TheoryData<int, int, double> PassbandTones
    {
        get
        {
            var data = new TheoryData<int, int, double>();
            foreach (var (inputRate, outputRate) in Pairs)
            {
                data.Add(inputRate, outputRate, 300);
                data.Add(inputRate, outputRate, 1000);
                if (UpsampleFactor(inputRate, outputRate) > 1)
                    data.Add(inputRate, outputRate, 0.8 * Math.Min(inputRate, outputRate) / 2);
            }

            return data;
        }
    }

    [Fact]
    public void IsSupported_ShouldAcceptExactlyTheLevelTestedPairs_WhenProbedAcrossCommonRates()
    {
        int[] rates = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 96000];
        var accepted = new List<string>();
        foreach (var inputRate in rates)
        {
            foreach (var outputRate in rates.Where(rate => ResamplerFactory.IsSupported(inputRate, rate)))
            {
                accepted.Add($"{inputRate}->{outputRate}");
            }
        }

        // A pair added to the factory without a row in Pairs would ship with no level test.
        accepted.Should().BeEquivalentTo(Pairs.Select(p => $"{p.InputRate}->{p.OutputRate}"));
    }

    [Theory]
    [MemberData(nameof(SupportedPairs))]
    public void Process_ShouldPassDcAtInputLevel_WhenPairIsSupported(int inputRate, int outputRate)
    {
        using var resampler = ResamplerFactory.Create(inputRate, outputRate);

        var output = SteadyState(resampler, seconds: 1, _ => DcLevel);

        // The extremes, not the mean: consecutive output samples come from different polyphase branches, and a
        // mean would let one branch's excess hide another's shortfall.
        Decibels(output.Min() / DcLevel).Should().BeGreaterThanOrEqualTo(-DcToleranceDb, "{0} -> {1} Hz", inputRate, outputRate);
        Decibels(output.Max() / DcLevel).Should().BeLessThanOrEqualTo(DcToleranceDb, "{0} -> {1} Hz", inputRate, outputRate);
    }

    [Theory]
    [MemberData(nameof(PassbandTones))]
    public void Process_ShouldPassToneAtInputLevel_WhenToneIsInPassband(int inputRate, int outputRate, double frequencyHz)
    {
        using var resampler = ResamplerFactory.Create(inputRate, outputRate);

        var output = SteadyState(resampler, seconds: 1, n => ToneLevel * Math.Sin(2 * Math.PI * frequencyHz * n / inputRate));

        Decibels(Amplitude(output, frequencyHz, outputRate) / ToneLevel)
            .Should().BeApproximately(0, PassbandToleranceDb, "{0} Hz through {1} -> {2} Hz", frequencyHz, inputRate, outputRate);
    }

    [Theory]
    [InlineData(16000, 8000, 3200, -0.33)]
    [InlineData(48000, 24000, 9600, -0.33)]
    [InlineData(24000, 8000, 3200, -1.15)]
    [InlineData(48000, 16000, 6400, -1.15)]
    [InlineData(48000, 8000, 3200, -2.92)]
    public void Process_ShouldRollOffNoFurtherThanDesigned_WhenToneNearsBandEdgeOfSingleBranchDecimator(
        int inputRate, int outputRate, double frequencyHz, double designedGainDb)
    {
        // These five filter with a single 32-tap branch at the input rate. Their transition band is centred on
        // the output Nyquist and is wide enough that 0.8 of it is already rolling off. The level fix scales every
        // tap of a table by one constant, so it cannot move this; the band edge is held at its designed level,
        // and never above the input level.
        using var resampler = ResamplerFactory.Create(inputRate, outputRate);

        var output = SteadyState(resampler, seconds: 1, n => ToneLevel * Math.Sin(2 * Math.PI * frequencyHz * n / inputRate));

        Decibels(Amplitude(output, frequencyHz, outputRate) / ToneLevel)
            .Should().BeInRange(designedGainDb - PassbandToleranceDb, PassbandToleranceDb, "{0} Hz through {1} -> {2} Hz", frequencyHz, inputRate, outputRate);
    }

    // Stopband thresholds, for this test and the next: the whole decibel at or below the rejection measured both
    // before and after the level fix. The fix multiplies every tap of a table by one constant, which cannot change
    // the ratio between the responses at two frequencies, so the two readings differ only by measurement
    // resolution; before the fix, these spurs were smaller than one sample step.
    [Theory]
    [InlineData(8000, 16000, 89)]
    [InlineData(8000, 24000, 89)]
    [InlineData(16000, 24000, 89)]
    [InlineData(24000, 16000, 106)]
    [InlineData(8000, 48000, 88)]
    [InlineData(16000, 48000, 89)]
    [InlineData(24000, 48000, 89)]
    public void Process_ShouldKeepImagesBelowTone_WhenPairInterpolates(int inputRate, int outputRate, double minimumRejectionDb)
    {
        // Interpolating by L leaves copies of each input tone at k * inputRate ± f until the lowpass removes them.
        // A tone at 0.65 of the lower Nyquist puts its nearest image just past the band edge, where the stopband
        // is weakest. The extra 3 Hz keeps its period long, so rounding error spreads across the spectrum instead
        // of landing on the image frequencies.
        var upsampleFactor = UpsampleFactor(inputRate, outputRate);
        var toneHz = (0.65 * Math.Min(inputRate, outputRate) / 2) + 3;
        using var resampler = ResamplerFactory.Create(inputRate, outputRate);

        var output = SteadyState(resampler, StopbandSeconds, n => ImageToneLevel * Math.Sin(2 * Math.PI * toneHz * n / inputRate));

        var tone = Amplitude(output, Fold(toneHz, outputRate), outputRate);
        double strongestImage = 0;
        for (var k = 1; k < upsampleFactor; k++)
        {
            strongestImage = Math.Max(strongestImage, Amplitude(output, Fold(((double)k * inputRate) - toneHz, outputRate), outputRate));
            strongestImage = Math.Max(strongestImage, Amplitude(output, Fold(((double)k * inputRate) + toneHz, outputRate), outputRate));
        }

        Decibels(tone / strongestImage).Should().BeGreaterThanOrEqualTo(minimumRejectionDb, "{0} Hz through {1} -> {2} Hz", toneHz, inputRate, outputRate);
    }

    [Theory]
    [InlineData(16000, 8000, 5607, 84)]
    [InlineData(24000, 8000, 6407, 83)]
    [InlineData(24000, 16000, 11207, 88)]
    [InlineData(48000, 8000, 8407, 83)]
    [InlineData(48000, 16000, 12807, 83)]
    [InlineData(48000, 24000, 16807, 84)]
    public void Process_ShouldKeepAliasesBelowPassband_WhenPairDecimates(int inputRate, int outputRate, double stopbandToneHz, double minimumRejectionDb)
    {
        // A tone above the output Nyquist cannot be carried; whatever the lowpass lets through folds back into the
        // band as an alias. Each stopband tone sits just inside where its filter's stopband begins. A 1009 Hz tone
        // rides along as the passband reference, and at full level it also spreads rounding error across the
        // spectrum, so an alias smaller than one sample step still reads above that error.
        const double passbandToneHz = 1009;
        var aliasHz = Fold(stopbandToneHz, outputRate);
        aliasHz.Should().NotBeApproximately(passbandToneHz, 1, "the alias and the reference must not share a frequency");
        using var resampler = ResamplerFactory.Create(inputRate, outputRate);

        var output = SteadyState(resampler, StopbandSeconds, n =>
            (ToneLevel * Math.Sin(2 * Math.PI * passbandToneHz * n / inputRate))
            + (ToneLevel * Math.Sin(2 * Math.PI * stopbandToneHz * n / inputRate)));

        var passbandGain = Amplitude(output, passbandToneHz, outputRate) / ToneLevel;
        var aliasGain = Amplitude(output, aliasHz, outputRate) / ToneLevel;
        Decibels(passbandGain / aliasGain).Should().BeGreaterThanOrEqualTo(minimumRejectionDb, "{0} Hz through {1} -> {2} Hz", stopbandToneHz, inputRate, outputRate);
    }

    private static short[] SteadyState(PolyphaseResampler resampler, int seconds, Func<int, double> signal)
    {
        var inputRate = resampler.InputFormat.SampleRate;
        var outputRate = resampler.OutputFormat.SampleRate;

        // A quarter second of lead-in fills the delay line many times over before the analysed window starts.
        var input = new short[(seconds * inputRate) + (inputRate / 4)];
        for (var n = 0; n < input.Length; n++)
            input[n] = (short)Math.Round(signal(n));

        var output = new short[resampler.MaxOutputBytes(input.Length * 2) / 2];
        var written = resampler.Process(input.AsSpan(), output.AsSpan());

        var leadIn = outputRate / 4;
        written.Should().BeGreaterThanOrEqualTo(leadIn + (seconds * outputRate));
        return output.AsSpan(leadIn, seconds * outputRate).ToArray();
    }

    private static double Amplitude(short[] samples, double frequencyHz, int sampleRate)
    {
        double inPhase = 0;
        double quadrature = 0;
        for (var n = 0; n < samples.Length; n++)
        {
            var phase = 2 * Math.PI * frequencyHz * n / sampleRate;
            inPhase += samples[n] * Math.Cos(phase);
            quadrature += samples[n] * Math.Sin(phase);
        }

        return 2 * Math.Sqrt((inPhase * inPhase) + (quadrature * quadrature)) / samples.Length;
    }

    /// <summary>Where a frequency lands once sampled at <paramref name="sampleRate"/>.</summary>
    private static double Fold(double frequencyHz, int sampleRate)
    {
        var wrapped = frequencyHz % sampleRate;
        return wrapped <= sampleRate / 2.0 ? wrapped : sampleRate - wrapped;
    }

    private static double Decibels(double ratio) => 20 * Math.Log10(ratio);

    private static int UpsampleFactor(int inputRate, int outputRate) => outputRate / GreatestCommonDivisor(inputRate, outputRate);

    private static int GreatestCommonDivisor(int a, int b) => b == 0 ? a : GreatestCommonDivisor(b, a % b);
}
