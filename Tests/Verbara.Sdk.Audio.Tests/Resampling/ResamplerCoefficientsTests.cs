using Verbara.Sdk.Audio.Resampling;
using FluentAssertions;

namespace Verbara.Sdk.Audio.Tests.Resampling;

public sealed class ResamplerCoefficientsTests
{
    private const int TapsPerPhase = 32;

    /// <summary>
    /// How far a branch's tap sum may sit from one. What keeps it off exactly one is the prototype's response
    /// at the images of DC, which fall in the stopband; across the twelve tables the largest measured miss is
    /// 2.7e-5 (0.0002 dB). 1e-4 is the size of a -80 dB leak, and the defect this guards against missed by
    /// 0.5 to 0.83.
    /// </summary>
    private const double BranchSumTolerance = 1e-4;

    [Theory]
    [InlineData(0.0, 0x3FF0000000000000L)] // I0(0) = 1
    [InlineData(1.0, 0x3FF441CE4B386C2CL)] // I0(1) = 1.2660658777520082
    [InlineData(8.0, 0x407AB9069E35049BL)] // I0(8) = 427.5641157217994, the Kaiser beta of every table
    public void BesselI0_ShouldReturnPinnedBits_WhenEvaluatedAtKaiserDesignPoints(double x, long expectedBits)
    {
        // The series is plain IEEE arithmetic, so the result is reproducible bit for bit.
        BitConverter.DoubleToInt64Bits(ResamplerCoefficients.BesselI0(x)).Should().Be(expectedBits);
    }

    [Theory]
    [InlineData(1.0, 1.2660658777520084)]
    [InlineData(8.0, 427.5641157218048)]
    public void BesselI0_ShouldAgreeWithExactSeries_WhenEvaluatedAtKaiserDesignPoints(double x, double exact)
    {
        ResamplerCoefficients.BesselI0(x).Should().BeApproximately(exact, exact * 1e-12);
    }

    [Theory]
    [MemberData(nameof(PolyphaseResamplerTests.SupportedPairs), MemberType = typeof(PolyphaseResamplerTests))]
    public void Coefficients_ShouldSumToOneInEveryBranch_WhenBuiltForSupportedPair(int inputRate, int outputRate)
    {
        var table = TableFor(inputRate, outputRate);
        var upsampleFactor = outputRate / GreatestCommonDivisor(inputRate, outputRate);
        table.Should().HaveCount(upsampleFactor * TapsPerPhase);

        // Each output sample is computed by exactly one branch, and a branch's tap sum is the gain it gives a
        // constant input. A branch summing to anything but one changes the level of every sample it produces.
        for (var phase = 0; phase < upsampleFactor; phase++)
        {
            double sum = 0;
            for (var tap = 0; tap < TapsPerPhase; tap++)
                sum += table[(phase * TapsPerPhase) + tap];

            sum.Should().BeApproximately(1.0, BranchSumTolerance, "branch {0} of {1} -> {2} Hz", phase, inputRate, outputRate);
        }
    }

    [Theory]
    [MemberData(nameof(PolyphaseResamplerTests.SupportedPairs), MemberType = typeof(PolyphaseResamplerTests))]
    public void Coefficients_ShouldReadTheSameFromEitherEnd_WhenBuiltForSupportedPair(int inputRate, int outputRate)
    {
        // A Kaiser-windowed sinc is linear phase: the polyphase table reads the same from either end.
        var table = TableFor(inputRate, outputRate);

        for (var i = 0; i < table.Length; i++)
            table[i].Should().BeApproximately(table[table.Length - 1 - i], 1e-7f);
    }

    private static float[] TableFor(int inputRate, int outputRate) => (inputRate, outputRate) switch
    {
        (8000, 16000) => ResamplerCoefficients.Coefficients8To16k,
        (16000, 8000) => ResamplerCoefficients.Coefficients16To8k,
        (8000, 24000) => ResamplerCoefficients.Coefficients8To24k,
        (24000, 8000) => ResamplerCoefficients.Coefficients24To8k,
        (16000, 24000) => ResamplerCoefficients.Coefficients16To24k,
        (24000, 16000) => ResamplerCoefficients.Coefficients24To16k,
        (8000, 48000) => ResamplerCoefficients.Coefficients8To48k,
        (48000, 8000) => ResamplerCoefficients.Coefficients48To8k,
        (16000, 48000) => ResamplerCoefficients.Coefficients16To48k,
        (48000, 16000) => ResamplerCoefficients.Coefficients48To16k,
        (24000, 48000) => ResamplerCoefficients.Coefficients24To48k,
        (48000, 24000) => ResamplerCoefficients.Coefficients48To24k,
        _ => throw new ArgumentOutOfRangeException(nameof(outputRate), outputRate, "no coefficient table for this pair"),
    };

    private static int GreatestCommonDivisor(int a, int b) => b == 0 ? a : GreatestCommonDivisor(b, a % b);
}
