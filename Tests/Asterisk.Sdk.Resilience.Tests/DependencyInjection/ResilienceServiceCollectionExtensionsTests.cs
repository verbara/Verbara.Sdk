using Asterisk.Sdk.Resilience.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Asterisk.Sdk.Resilience.Tests.DependencyInjection;

public sealed class ResilienceServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAsteriskResilience_ShouldRegisterNoOpPolicy_WhenNoConfigureProvided()
    {
        var services = new ServiceCollection();
        services.AddAsteriskResilience();

        using var sp = services.BuildServiceProvider();
        var policy = sp.GetService<ResiliencePolicy>();

        policy.Should().NotBeNull().And.BeSameAs(ResiliencePolicy.NoOp);
    }

    [Fact]
    public void AddAsteriskResilience_ShouldRegisterBuiltPolicy_WhenConfigureProvided()
    {
        var services = new ServiceCollection();
        services.AddAsteriskResilience(b => b
            .WithCircuitBreaker(3, TimeSpan.FromSeconds(30))
            .WithRetry(2, TimeSpan.FromMilliseconds(50)));

        using var sp = services.BuildServiceProvider();
        var policy = sp.GetService<ResiliencePolicy>();

        policy.Should().NotBeNull()
            .And.NotBeSameAs(ResiliencePolicy.NoOp,
                "a configured builder must produce a distinct policy instance");
    }

    [Fact]
    public void AddAsteriskResilience_ShouldRegisterSystemTimeProvider_WhenNoneRegistered()
    {
        var services = new ServiceCollection();
        services.AddAsteriskResilience();

        using var sp = services.BuildServiceProvider();
        var tp = sp.GetService<TimeProvider>();

        tp.Should().BeSameAs(TimeProvider.System);
    }

    [Fact]
    public void AddAsteriskResilience_ShouldPreserveExistingTimeProvider_WhenAlreadyRegistered()
    {
        var services = new ServiceCollection();
        var custom = new FakeTimeProvider();
        services.AddSingleton<TimeProvider>(custom);

        services.AddAsteriskResilience();

        using var sp = services.BuildServiceProvider();
        var tp = sp.GetService<TimeProvider>();

        tp.Should().BeSameAs(custom,
            "TryAddSingleton must not replace an already-registered TimeProvider");
    }
}
