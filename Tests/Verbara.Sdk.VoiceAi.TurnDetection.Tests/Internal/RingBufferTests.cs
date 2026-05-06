using Verbara.Sdk.VoiceAi.TurnDetection.Internal;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.TurnDetection.Tests.Internal;

public sealed class RingBufferTests
{
    [Fact]
    public void Write_ShouldStoreAllSamples_WhenUnderCapacity()
    {
        var buffer = new RingBuffer(10);
        buffer.Write([1f, 2f, 3f]);
        buffer.Count.Should().Be(3);

        var output = new float[3];
        buffer.CopyTo(output);
        output.Should().BeEquivalentTo([1f, 2f, 3f]);
    }

    [Fact]
    public void Write_ShouldOverwriteOldest_WhenOverCapacity()
    {
        var buffer = new RingBuffer(4);
        buffer.Write([1f, 2f, 3f, 4f]);
        buffer.Write([5f, 6f]);
        buffer.Count.Should().Be(4);

        var output = new float[4];
        buffer.CopyTo(output);
        output.Should().BeEquivalentTo([3f, 4f, 5f, 6f]);
    }

    [Fact]
    public void CopyTo_ShouldReturnOldestFirst()
    {
        var buffer = new RingBuffer(5);
        buffer.Write([10f, 20f, 30f, 40f, 50f]);
        buffer.Write([60f]);

        var output = new float[5];
        buffer.CopyTo(output);
        output.Should().BeEquivalentTo([20f, 30f, 40f, 50f, 60f]);
    }

    [Fact]
    public void Clear_ShouldResetCount()
    {
        var buffer = new RingBuffer(10);
        buffer.Write([1f, 2f, 3f]);
        buffer.Clear();
        buffer.Count.Should().Be(0);
    }

    [Fact]
    public void Write_ShouldHandleMultipleSmallWrites()
    {
        var buffer = new RingBuffer(6);
        buffer.Write([1f, 2f]);
        buffer.Write([3f, 4f]);
        buffer.Write([5f, 6f]);
        buffer.Count.Should().Be(6);

        var output = new float[6];
        buffer.CopyTo(output);
        output.Should().BeEquivalentTo([1f, 2f, 3f, 4f, 5f, 6f]);
    }
}
