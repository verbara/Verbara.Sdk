using Asterisk.Sdk.Enums;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace Asterisk.Sdk.Agi.Tests.Diagnostics;

public sealed class AgiHealthCheckTests
{
    [Fact]
    public async Task CheckHealth_ShouldReturnHealthy_WhenListening()
    {
        var server = Substitute.For<IAgiServer>();
        server.State.Returns(AgiServerState.Listening);
        var sut = new Asterisk.Sdk.Agi.Diagnostics.AgiHealthCheck(server);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealth_ShouldReturnDegraded_WhenStarting()
    {
        var server = Substitute.For<IAgiServer>();
        server.State.Returns(AgiServerState.Starting);
        var sut = new Asterisk.Sdk.Agi.Diagnostics.AgiHealthCheck(server);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Theory]
    [InlineData(AgiServerState.Stopped)]
    [InlineData(AgiServerState.Faulted)]
    [InlineData(AgiServerState.Stopping)]
    public async Task CheckHealth_ShouldReturnUnhealthy_WhenNotListening(AgiServerState state)
    {
        var server = Substitute.For<IAgiServer>();
        server.State.Returns(state);
        var sut = new Asterisk.Sdk.Agi.Diagnostics.AgiHealthCheck(server);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
}
