using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions.Diagnostics;
using Asterisk.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace Asterisk.Sdk.Sessions.Tests.Diagnostics;

public class SessionHealthCheckTests
{
    [Fact]
    public async Task CheckHealth_ShouldReturnHealthy_WhenNoActiveSessions()
    {
        var manager = Substitute.For<ICallSessionManager>();
        manager.ActiveSessions.Returns(Enumerable.Empty<CallSession>());
        manager.GetRecentCompleted(100).Returns(Enumerable.Empty<CallSession>());
        var check = new SessionHealthCheck(manager);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("activeSessions").WhoseValue.Should().Be(0);
    }

    [Fact]
    public async Task CheckHealth_ShouldReturnHealthy_WhenActiveSessionsExist()
    {
        var manager = Substitute.For<ICallSessionManager>();
        var session = new CallSession("s1", "linked1", "srv1", CallDirection.Inbound);
        manager.ActiveSessions.Returns(new[] { session });
        manager.GetRecentCompleted(100).Returns(Enumerable.Empty<CallSession>());
        var check = new SessionHealthCheck(manager);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("activeSessions").WhoseValue.Should().Be(1);
    }
}
