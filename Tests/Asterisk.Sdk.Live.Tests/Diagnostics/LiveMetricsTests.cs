using Asterisk.Sdk.Live.Diagnostics;
using FluentAssertions;

namespace Asterisk.Sdk.Live.Tests.Diagnostics;

public class LiveMetricsTests
{
    [Fact]
    public void MeterName_ShouldBeCorrect()
    {
        LiveMetrics.Meter.Name.Should().Be("Asterisk.Sdk.Live");
    }

    [Fact]
    public void AllCounters_ShouldBeInitialized()
    {
        LiveMetrics.ChannelsCreated.Should().NotBeNull();
        LiveMetrics.ChannelsDestroyed.Should().NotBeNull();
        LiveMetrics.QueueCallsJoined.Should().NotBeNull();
        LiveMetrics.QueueCallsLeft.Should().NotBeNull();
        LiveMetrics.AgentStateChanges.Should().NotBeNull();
        LiveMetrics.QueueWaitTimeMs.Should().NotBeNull();
    }
}
