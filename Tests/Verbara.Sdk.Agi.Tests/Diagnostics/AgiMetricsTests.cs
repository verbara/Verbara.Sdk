using Verbara.Sdk.Agi.Diagnostics;
using FluentAssertions;

namespace Verbara.Sdk.Agi.Tests.Diagnostics;

public class AgiMetricsTests
{
    [Fact]
    public void MeterName_ShouldBeCorrect()
    {
        AgiMetrics.Meter.Name.Should().Be("Verbara.Sdk.Agi");
    }

    [Fact]
    public void AllCounters_ShouldBeInitialized()
    {
        AgiMetrics.ConnectionsAccepted.Should().NotBeNull();
        AgiMetrics.ScriptsExecuted.Should().NotBeNull();
        AgiMetrics.ScriptsFailed.Should().NotBeNull();
        AgiMetrics.ScriptsNotFound.Should().NotBeNull();
        AgiMetrics.Hangups.Should().NotBeNull();
        AgiMetrics.ScriptDurationMs.Should().NotBeNull();
    }
}
