using System.Diagnostics.CodeAnalysis;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Diagnostics;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Diagnostics;

/// <summary>
/// The pool health check reads the AMI connection of every server the pool holds. The owner's table (design D9, Q2,
/// 2026-09-30): every server connected, or no server at all → Healthy; every server ended (Disconnecting or
/// Disconnected) → Unhealthy; anything else → Degraded, so a server that is recovering (Initial, Connecting,
/// Reconnecting) reads as the live check reads it, never Unhealthy.
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class VerbaraServerPoolHealthCheckTests : IAsyncLifetime
{
    private readonly VerbaraServerPool _pool =
        new(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

    private void AddServer(string serverId, AmiConnectionState state)
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.State.Returns(state);
        _pool.AddExistingServer(serverId, new VerbaraServer(connection, Substitute.For<ILogger<VerbaraServer>>()));
    }

    private Task<HealthCheckResult> CheckAsync() =>
        new VerbaraServerPoolHealthCheck(_pool).CheckHealthAsync(new HealthCheckContext());

    [Fact]
    public async Task CheckHealthAsync_ShouldReportDegraded_WhenOneOfTwoServersIsReconnecting()
    {
        AddServer("pbx-a", AmiConnectionState.Connected);
        AddServer("pbx-b", AmiConnectionState.Reconnecting);

        var result = await CheckAsync();

        result.Status.Should().Be(HealthStatus.Degraded, "one server is down and the other is connected");
        result.Description.Should().Be("1 of 2 servers not connected");
        result.Data.Should().HaveCount(2);
        result.Data.Should().ContainKey("pbx-a").WhoseValue.Should().Be("Connected");
        result.Data.Should().ContainKey("pbx-b").WhoseValue.Should().Be("Reconnecting");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportDegraded_WhenTheOnlyServerIsReconnecting()
    {
        AddServer("pbx-a", AmiConnectionState.Reconnecting);

        var result = await CheckAsync();

        result.Status.Should().Be(HealthStatus.Degraded,
            "a reconnecting server may still recover on its own, as the live check reads it, so it is not Unhealthy");
    }

    [Theory]
    [InlineData(AmiConnectionState.Initial)]
    [InlineData(AmiConnectionState.Connecting)]
    public async Task CheckHealthAsync_ShouldReportDegraded_WhenTheOnlyServerHasNotConnectedYet(AmiConnectionState state)
    {
        AddServer("pbx-a", state);

        var result = await CheckAsync();

        result.Status.Should().Be(HealthStatus.Degraded, "a server that has not connected yet is recovering, not ended");
        result.Data.Should().ContainKey("pbx-a").WhoseValue.Should().Be(state.ToString());
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportUnhealthy_WhenEveryServerIsDisconnected()
    {
        AddServer("pbx-a", AmiConnectionState.Disconnected);
        AddServer("pbx-b", AmiConnectionState.Disconnected);

        var result = await CheckAsync();

        result.Status.Should().Be(HealthStatus.Unhealthy, "no server is connected and none will reconnect on its own");
        result.Description.Should().Be("No server connected: all 2 AMI connections have ended");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportUnhealthy_WhenEveryServerIsDisconnectingOrDisconnected()
    {
        AddServer("pbx-a", AmiConnectionState.Disconnecting);
        AddServer("pbx-b", AmiConnectionState.Disconnected);

        var result = await CheckAsync();

        result.Status.Should().Be(HealthStatus.Unhealthy, "a disconnecting server has ended like a disconnected one");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportDegraded_WhenOneServerHasEndedAndAnotherIsRecovering()
    {
        AddServer("pbx-a", AmiConnectionState.Disconnected);
        AddServer("pbx-b", AmiConnectionState.Reconnecting);

        var result = await CheckAsync();

        result.Status.Should().Be(HealthStatus.Degraded, "not every server has ended: one is still recovering");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportHealthy_WhenEveryServerIsConnected()
    {
        AddServer("pbx-a", AmiConnectionState.Connected);
        AddServer("pbx-b", AmiConnectionState.Connected);

        var result = await CheckAsync();

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Be("All 2 servers connected");
        result.Data.Values.Should().AllBeOfType<string>("a reflection-free JSON writer cannot serialize a boxed enum");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportHealthy_WhenThePoolHoldsNoServers()
    {
        var result = await CheckAsync();

        result.Status.Should().Be(HealthStatus.Healthy, "servers are added at run time, so an empty pool is not a fault");
        result.Description.Should().Be("The pool holds no servers");
        result.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReadTheStateWhenItRuns_WhenAServerChangesStateBetweenRuns()
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.State.Returns(AmiConnectionState.Connected);
        _pool.AddExistingServer("pbx-a", new VerbaraServer(connection, Substitute.For<ILogger<VerbaraServer>>()));
        var check = new VerbaraServerPoolHealthCheck(_pool);

        var first = await check.CheckHealthAsync(new HealthCheckContext());
        connection.State.Returns(AmiConnectionState.Disconnected);
        var second = await check.CheckHealthAsync(new HealthCheckContext());

        first.Status.Should().Be(HealthStatus.Healthy);
        second.Status.Should().Be(HealthStatus.Unhealthy, "the check reads each connection when it runs, not when it was built");
    }
}
