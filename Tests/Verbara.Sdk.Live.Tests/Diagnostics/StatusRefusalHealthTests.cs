using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Diagnostics;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;

namespace Verbara.Sdk.Live.Tests.Diagnostics;

public sealed class StatusRefusalHealthTests
{
    [Fact]
    public async Task CheckHealthAsync_ShouldReportDegraded_WhenAConnectedServerReceivesAStatusRefusal()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.StartAsync(peer);
        await using var pool = new VerbaraServerPool(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);
        pool.AddExistingServer("n1", run.Server);
        var check = new VerbaraServerPoolHealthCheck(pool);
        var before = await check.CheckHealthAsync(new HealthCheckContext());

        peer.StatusRefusedFromAsk = 2;
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        var refused = await check.CheckHealthAsync(new HealthCheckContext());

        before.Status.Should().Be(HealthStatus.Healthy);
        refused.Status.Should().Be(HealthStatus.Degraded, "the connected server's actual Status answer was a refusal");
        refused.Data.Should().ContainKey("n1").WhoseValue.Should().Be("Connected");
        refused.Data.Should().ContainKey("status-refused").WhoseValue.Should().Be("n1: Permission denied");
        peer.StatusRefusedFromAsk = 0;
        await run.Server.ReconcileChannelsAsync().AsTask().WaitAsync(Run.Bound);
        var restored = await check.CheckHealthAsync(new HealthCheckContext());
        restored.Status.Should().Be(HealthStatus.Healthy);
        restored.Data.Should().NotContainKey("status-refused");
        restored.Data["n1"].Should().Be("Connected");
        peer.Fault.Should().BeNull();
    }
    [Theory]
    [InlineData("b", "a", "a", "b")]
    [InlineData("ä", "z", "z", "ä")]
    [InlineData("a", "A", "A", "a")]
    public async Task CheckHealthAsync_ShouldAggregateRefusalsUnderOneOrdinalStringKey_WhenTwoServersRefuseStatus(
        string firstId, string secondId, string ordinalFirst, string ordinalSecond)
    {
        var firstPeer = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        var secondPeer = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        await using var first = await Run.StartAsync(firstPeer);
        await using var second = await Run.StartAsync(secondPeer);
        await using var pool = new VerbaraServerPool(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);
        pool.AddExistingServer(firstId, first.Server);
        pool.AddExistingServer(secondId, second.Server);
        var connected = Substitute.For<IAmiConnection>();
        connected.State.Returns(Verbara.Sdk.Enums.AmiConnectionState.Connected);
        var compoundId = firstId + ":status-refused";
        pool.AddExistingServer(compoundId, new VerbaraServer(connected, NullLogger<VerbaraServer>.Instance));

        var result = await new VerbaraServerPoolHealthCheck(pool).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data.Should().HaveCount(4);
        result.Data.Keys.Should().BeEquivalentTo(new[] { firstId, secondId, compoundId, "status-refused" });
        result.Data["status-refused"].Should().Be($"{ordinalFirst}: Permission denied; {ordinalSecond}: Permission denied");
        result.Data[firstId].Should().Be("Connected");
        result.Data[secondId].Should().Be("Connected");
        result.Data[compoundId].Should().Be("Connected", "a server id is never composed into the refusal key");
        result.Data.Values.Should().AllBeOfType<string>();
        firstPeer.Fault.Should().BeNull();
        secondPeer.Fault.Should().BeNull();
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldStillReportUnhealthy_WhenARefusedServersConnectionEnds()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusRefusedFromAsk = 1 };
        await using var run = await Run.StartAsync(peer);
        await using var pool = new VerbaraServerPool(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);
        pool.AddExistingServer("n1", run.Server);
        await run.Connection.DisconnectAsync();

        var result = await new VerbaraServerPoolHealthCheck(pool).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy, "connection aggregation is preserved when a refusal is also retained");
        result.Data["n1"].Should().Be("Disconnected");
        result.Data["status-refused"].Should().Be("n1: Permission denied");
        peer.Fault.Should().BeNull();
    }


}
