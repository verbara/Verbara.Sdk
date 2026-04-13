using Asterisk.Sdk.Push.Diagnostics;
using Asterisk.Sdk.Push.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace Asterisk.Sdk.Push.Tests.Diagnostics;

public class PushHealthCheckTests
{
    [Fact]
    public async Task CheckHealth_ShouldReturnHealthy_WhenNoSubscribers()
    {
        var registry = Substitute.For<ISubscriptionRegistry>();
        registry.ActiveCount.Returns(0);
        var check = new PushHealthCheck(registry);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("activeSubscribers").WhoseValue.Should().Be(0);
    }

    [Fact]
    public async Task CheckHealth_ShouldReturnHealthy_WhenSubscribersExist()
    {
        var registry = Substitute.For<ISubscriptionRegistry>();
        registry.ActiveCount.Returns(5);
        var check = new PushHealthCheck(registry);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("activeSubscribers").WhoseValue.Should().Be(5);
    }
}
