using Verbara.Sdk;
using Verbara.Sdk.Enums;
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
        // A connected connection: these tests read the table, and an unset substitute reads Initial, which the live
        // check reports as Degraded whatever the table holds.
        connection.State.Returns(AmiConnectionState.Connected);
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

    // ── The AMI connection behind the table ──────────────────────────────────────────────────────────────────────
    // A table nothing updates is not healthy: while the connection is not Connected, the live check must not answer
    // Healthy over the channels it still holds. These assert "not Healthy" only; which status it reports instead is the
    // owner's ruling (design D8, Q1), pinned once it is made.

    private static (VerbaraServer Server, IAmiConnection Connection) CreateServerOver(AmiConnectionState state)
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.State.Returns(state);
        var server = new VerbaraServer(connection, Substitute.For<ILogger<VerbaraServer>>());
        return (server, connection);
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldNotReportHealthy_WhenTheAmiConnectionIsReconnecting()
    {
        var (server, _) = CreateServerOver(AmiConnectionState.Reconnecting);
        server.Channels.OnNewChannel("uid-1", "PJSIP/100-0001", ChannelState.Up, "100");
        var check = new LiveHealthCheck(server);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().NotBe(HealthStatus.Healthy,
            "the connection is reconnecting, so nothing updates the channel the table still holds");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldNotReportHealthy_WhenTheAmiConnectionIsDisconnected()
    {
        var (server, _) = CreateServerOver(AmiConnectionState.Disconnected);
        server.Channels.OnNewChannel("uid-1", "PJSIP/100-0001", ChannelState.Up, "100");
        var check = new LiveHealthCheck(server);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().NotBe(HealthStatus.Healthy,
            "the connection has ended, so nothing will update the channel the table still holds");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportHealthy_WhenConnectedWithStateLoaded()
    {
        var (server, _) = CreateServerOver(AmiConnectionState.Connected);
        server.Channels.OnNewChannel("uid-1", "PJSIP/100-0001", ChannelState.Up, "100");
        var check = new LiveHealthCheck(server);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy, "the connection is up and the table holds what it loaded");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportDegraded_WhenConnectedAndEmpty()
    {
        var (server, _) = CreateServerOver(AmiConnectionState.Connected);
        var check = new LiveHealthCheck(server);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded, "the connection is up and the table is still empty");
    }
}
