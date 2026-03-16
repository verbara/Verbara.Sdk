using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Diagnostics;
using Asterisk.Sdk.Enums;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace Asterisk.Sdk.Ami.Tests.Diagnostics;

public class AmiHealthCheckTests
{
    [Fact]
    public async Task CheckHealth_ShouldReturnHealthy_WhenConnected()
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.State.Returns(AmiConnectionState.Connected);
        var check = new AmiHealthCheck(connection);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealth_ShouldReturnDegraded_WhenReconnecting()
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.State.Returns(AmiConnectionState.Reconnecting);
        var check = new AmiHealthCheck(connection);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task CheckHealth_ShouldReturnUnhealthy_WhenDisconnected()
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.State.Returns(AmiConnectionState.Disconnected);
        var check = new AmiHealthCheck(connection);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
}
