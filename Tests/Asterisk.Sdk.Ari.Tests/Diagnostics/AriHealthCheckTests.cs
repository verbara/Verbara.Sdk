using Asterisk.Sdk.Enums;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace Asterisk.Sdk.Ari.Tests.Diagnostics;

public sealed class AriHealthCheckTests
{
    [Fact]
    public async Task CheckHealth_ShouldReturnHealthy_WhenConnected()
    {
        var client = Substitute.For<IAriClient>();
        client.State.Returns(AriConnectionState.Connected);
        var sut = new Asterisk.Sdk.Ari.Diagnostics.AriHealthCheck(client);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealth_ShouldReturnDegraded_WhenReconnecting()
    {
        var client = Substitute.For<IAriClient>();
        client.State.Returns(AriConnectionState.Reconnecting);
        var sut = new Asterisk.Sdk.Ari.Diagnostics.AriHealthCheck(client);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Theory]
    [InlineData(AriConnectionState.Disconnected)]
    [InlineData(AriConnectionState.Faulted)]
    [InlineData(AriConnectionState.Initial)]
    public async Task CheckHealth_ShouldReturnUnhealthy_WhenNotConnected(AriConnectionState state)
    {
        var client = Substitute.For<IAriClient>();
        client.State.Returns(state);
        var sut = new Asterisk.Sdk.Ari.Diagnostics.AriHealthCheck(client);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
}
