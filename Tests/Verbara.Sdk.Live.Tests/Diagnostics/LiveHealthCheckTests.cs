using Verbara.Sdk;
using Verbara.Sdk.Live.Diagnostics;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Diagnostics;

public class LiveHealthCheckTests
{
    private static VerbaraServer CreateServer()
    {
        var connection = Substitute.For<IAmiConnection>();
        var logger = Substitute.For<ILogger<VerbaraServer>>();
        return new VerbaraServer(connection, logger);
    }

    [Fact]
    public async Task CheckHealth_ShouldReturnDegraded_WhenStateIsEmpty()
    {
        var server = CreateServer();
        var check = new LiveHealthCheck(server);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data.Should().ContainKey("channels").WhoseValue.Should().Be(0);
    }

    [Fact]
    public async Task CheckHealth_ShouldReturnHealthy_WhenStateIsLoaded()
    {
        var server = CreateServer();
        // Add a channel to simulate loaded state
        server.Channels.OnNewChannel("uid-1", "SIP/100-0001", Enums.ChannelState.Up, "100");
        var check = new LiveHealthCheck(server);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("channels").WhoseValue.Should().Be(1);
    }
}
