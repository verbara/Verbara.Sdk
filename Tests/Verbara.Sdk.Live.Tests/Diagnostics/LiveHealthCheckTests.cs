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
    // A table nothing updates is not healthy. The owner's table (design D8, Q1, 2026-09-30): Connected reads the
    // collections as before; Reconnecting, Connecting and Initial report Degraded (stale, may recover on its own);
    // Disconnecting and Disconnected report Unhealthy (nothing updates the table until a new connect). Every row
    // holds a loaded channel, so a check that skipped the connection would answer Healthy.

    private static (VerbaraServer Server, IAmiConnection Connection) CreateServerOver(AmiConnectionState state)
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.State.Returns(state);
        var server = new VerbaraServer(connection, Substitute.For<ILogger<VerbaraServer>>());
        return (server, connection);
    }

    private static async Task<HealthCheckResult> CheckLoadedTableOverAsync(AmiConnectionState state)
    {
        var (server, _) = CreateServerOver(state);
        server.Channels.OnNewChannel("uid-1", "PJSIP/100-0001", ChannelState.Up, "100");
        var check = new LiveHealthCheck(server);
        return await check.CheckHealthAsync(new HealthCheckContext());
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportDegraded_WhenTheAmiConnectionIsReconnecting()
    {
        var result = await CheckLoadedTableOverAsync(AmiConnectionState.Reconnecting);

        result.Status.Should().Be(HealthStatus.Degraded,
            "the connection is reconnecting, so nothing updates the channel the table still holds, but it may recover");
        result.Description.Should().Be("Live state is not being updated: AMI Reconnecting");
        result.Data.Should().ContainKey("amiState").WhoseValue.Should().Be("Reconnecting");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportDegraded_WhenTheAmiConnectionIsConnecting()
    {
        var result = await CheckLoadedTableOverAsync(AmiConnectionState.Connecting);

        result.Status.Should().Be(HealthStatus.Degraded,
            "the connection is connecting, so nothing updates the table yet, but it may come up");
        result.Description.Should().Be("Live state is not being updated: AMI Connecting");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportDegraded_WhenTheAmiConnectionHasNotConnectedYet()
    {
        var result = await CheckLoadedTableOverAsync(AmiConnectionState.Initial);

        result.Status.Should().Be(HealthStatus.Degraded,
            "the connection has not connected yet, so nothing updates the table");
        result.Description.Should().Be("Live state is not being updated: AMI Initial");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportUnhealthy_WhenTheAmiConnectionIsDisconnecting()
    {
        var result = await CheckLoadedTableOverAsync(AmiConnectionState.Disconnecting);

        result.Status.Should().Be(HealthStatus.Unhealthy,
            "the connection is ending, so nothing will update the table until a new connect");
        result.Description.Should().Be("Live state is not being updated: AMI Disconnecting");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportUnhealthy_WhenTheAmiConnectionIsDisconnected()
    {
        var result = await CheckLoadedTableOverAsync(AmiConnectionState.Disconnected);

        result.Status.Should().Be(HealthStatus.Unhealthy,
            "the connection has ended, so nothing will update the channel the table still holds");
        result.Description.Should().Be("Live state is not being updated: AMI Disconnected");
        result.Data.Should().ContainKey("amiState").WhoseValue.Should().Be("Disconnected");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldCarryTheAmiStateAsAString_WhenConnected()
    {
        var result = await CheckLoadedTableOverAsync(AmiConnectionState.Connected);

        result.Data.Should().ContainKey("amiState").WhoseValue.Should().BeOfType<string>()
            .Which.Should().Be("Connected", "a reflection-free JSON writer cannot serialize a boxed enum");
        result.Data.Should().ContainKey("channels").WhoseValue.Should().Be(1);
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
