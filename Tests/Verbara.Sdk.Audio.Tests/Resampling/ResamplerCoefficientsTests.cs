using Verbara.Sdk.Audio.Resampling;
using FluentAssertions;

namespace Verbara.Sdk.Audio.Tests.Resampling;

public sealed class ResamplerCoefficientsTests
{
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
    [InlineData("8To16k", 64, 16, 0.44973403f, 0.49999377784115495)]
    [InlineData("16To8k", 32, 15, 0.22420439f, 0.5000111300178105)]
    [InlineData("8To48k", 192, 95, -1.759156e-05f, 0.16666513132850014)]
    public void Coefficients_ShouldMatchPinnedDesign_WhenBuiltForRatePair(
        string ratePair, int expectedLength, int tapIndex, float expectedTap, double expectedFirstPhaseSum)
    {
        var table = ratePair switch
        {
            "8To16k" => ResamplerCoefficients.Coefficients8To16k,
            "16To8k" => ResamplerCoefficients.Coefficients16To8k,
            "8To48k" => ResamplerCoefficients.Coefficients8To48k,
            _ => throw new ArgumentOutOfRangeException(nameof(ratePair), ratePair, null),
        };

        table.Should().HaveCount(expectedLength);
        table[tapIndex].Should().BeApproximately(expectedTap, 1e-7f);

        // A branch's tap sum is its DC gain as designed today; a change to gain normalisation must update these pins.
        double firstPhaseSum = 0;
        for (var tap = 0; tap < 32; tap++)
            firstPhaseSum += table[tap];
        firstPhaseSum.Should().BeApproximately(expectedFirstPhaseSum, 1e-6);

        // A Kaiser-windowed sinc is linear phase: the polyphase table reads the same from either end.
        for (var i = 0; i < table.Length; i++)
            table[i].Should().BeApproximately(table[table.Length - 1 - i], 1e-7f);
    }
}
