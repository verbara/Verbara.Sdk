using Verbara.Sdk.VoiceAi.TurnDetection.Internal;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.TurnDetection.Tests.Internal;

public sealed class HannWindowTests
{
    [Fact]
    public void Values_ShouldHaveLength400()
    {
        HannWindow.Values.Length.Should().Be(400);
    }

    [Fact]
    public void Values_ShouldBeZeroAtStart()
    {
        HannWindow.Values[0].Should().BeApproximately(0f, 1e-6f);
    }

    [Fact]
    public void Values_ShouldNotBeZeroAtEnd_PeriodicWindow()
    {
        // Periodic Hann: last sample is NOT zero (unlike symmetric)
        HannWindow.Values[399].Should().BeGreaterThan(0f);
        HannWindow.Values[399].Should().BeLessThan(0.001f);
    }

    [Fact]
    public void Values_ShouldPeakAtCenter()
    {
        var center = HannWindow.Values[200];
        center.Should().BeGreaterThan(0.999f);
    }

    [Fact]
    public void Values_ShouldBeSymmetric_AroundCenter()
    {
        // Periodic Hann of size N: w[k] = w[N-k] for k=1..N-1
        var values = HannWindow.Values;
        for (int i = 1; i < 200; i++)
        {
            values[i].Should().BeApproximately(values[400 - i], 1e-6f,
                $"values[{i}] should equal values[{400 - i}]");
        }
    }
}
