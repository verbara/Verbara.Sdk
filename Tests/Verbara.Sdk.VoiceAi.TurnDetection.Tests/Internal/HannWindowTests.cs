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
    public void Values_ShouldBeZeroAtEdges()
    {
        HannWindow.Values[0].Should().BeApproximately(0f, 1e-6f);
        HannWindow.Values[399].Should().BeApproximately(0f, 1e-6f);
    }

    [Fact]
    public void Values_ShouldBeOneAtCenter()
    {
        var center1 = HannWindow.Values[199];
        var center2 = HannWindow.Values[200];
        MathF.Max(center1, center2).Should().BeGreaterThan(0.999f);
    }

    [Fact]
    public void Values_ShouldBeSymmetric()
    {
        var values = HannWindow.Values;
        for (int i = 0; i < 200; i++)
        {
            values[i].Should().BeApproximately(values[399 - i], 1e-6f,
                $"values[{i}] should equal values[{399 - i}]");
        }
    }
}
