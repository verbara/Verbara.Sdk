using Asterisk.Sdk.Ari.Diagnostics;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Diagnostics;

public sealed class AudioStreamMetricsTests
{
    [Fact]
    public void Meter_ShouldHaveCorrectName()
    {
        AudioStreamMetrics.Meter.Name.Should().Be("Asterisk.Sdk.Ari.Audio");
    }

    [Fact]
    public void StreamsOpened_ShouldBeCounter()
    {
        AudioStreamMetrics.StreamsOpened.Should().NotBeNull();
        AudioStreamMetrics.StreamsOpened.Name.Should().Be("audio.streams.opened");
    }

    [Fact]
    public void StreamsClosed_ShouldBeCounter()
    {
        AudioStreamMetrics.StreamsClosed.Should().NotBeNull();
        AudioStreamMetrics.StreamsClosed.Name.Should().Be("audio.streams.closed");
    }

    [Fact]
    public void FramesReceived_ShouldBeCounter()
    {
        AudioStreamMetrics.FramesReceived.Should().NotBeNull();
        AudioStreamMetrics.FramesReceived.Name.Should().Be("audio.frames.received");
    }

    [Fact]
    public void FramesSent_ShouldBeCounter()
    {
        AudioStreamMetrics.FramesSent.Should().NotBeNull();
    }

    [Fact]
    public void BytesReceived_ShouldBeCounter()
    {
        AudioStreamMetrics.BytesReceived.Should().NotBeNull();
    }

    [Fact]
    public void BytesSent_ShouldBeCounter()
    {
        AudioStreamMetrics.BytesSent.Should().NotBeNull();
    }

    [Fact]
    public void BufferUnderruns_ShouldBeCounter()
    {
        AudioStreamMetrics.BufferUnderruns.Should().NotBeNull();
    }

    [Fact]
    public void HangupFrames_ShouldBeCounter()
    {
        AudioStreamMetrics.HangupFrames.Should().NotBeNull();
    }

    [Fact]
    public void ErrorFrames_ShouldBeCounter()
    {
        AudioStreamMetrics.ErrorFrames.Should().NotBeNull();
    }

    [Fact]
    public void FrameLatency_ShouldBeHistogram()
    {
        AudioStreamMetrics.FrameLatency.Should().NotBeNull();
        AudioStreamMetrics.FrameLatency.Name.Should().Be("audio.frame.latency");
    }
}
