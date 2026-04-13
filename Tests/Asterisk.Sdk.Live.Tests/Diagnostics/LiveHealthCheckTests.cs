using Asterisk.Sdk;
using Asterisk.Sdk.Live.Diagnostics;
using Asterisk.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Asterisk.Sdk.Live.Tests.Diagnostics;

public class LiveHealthCheckTests
{
    private static AsteriskServer CreateServer()
    {
        var connection = Substitute.For<IAmiConnection>();
        var logger = Substitute.For<ILogger<AsteriskServer>>();
        return new AsteriskServer(connection, logger);
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
