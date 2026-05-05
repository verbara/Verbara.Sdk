using Verbara.Sdk.Resilience.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Sdk.Resilience.Tests.DependencyInjection;

public sealed class ResilienceServiceCollectionExtensionsTests
{
    [Fact]
    public void AddVerbaraResilience_ShouldRegisterNoOpPolicy_WhenNoConfigureProvided()
    {
        var services = new ServiceCollection();
        services.AddVerbaraResilience();

        using var sp = services.BuildServiceProvider();
        var policy = sp.GetService<ResiliencePolicy>();

        policy.Should().NotBeNull().And.BeSameAs(ResiliencePolicy.NoOp);
    }

    [Fact]
    public void AddVerbaraResilience_ShouldRegisterBuiltPolicy_WhenConfigureProvided()
    {
        var services = new ServiceCollection();
        services.AddVerbaraResilience(b => b
            .WithCircuitBreaker(3, TimeSpan.FromSeconds(30))
            .WithRetry(2, TimeSpan.FromMilliseconds(50)));

        using var sp = services.BuildServiceProvider();
        var policy = sp.GetService<ResiliencePolicy>();

        policy.Should().NotBeNull()
            .And.NotBeSameAs(ResiliencePolicy.NoOp,
                "a configured builder must produce a distinct policy instance");
    }

    [Fact]
    public void AddVerbaraResilience_ShouldRegisterSystemTimeProvider_WhenNoneRegistered()
    {
        var services = new ServiceCollection();
        services.AddVerbaraResilience();

        using var sp = services.BuildServiceProvider();
        var tp = sp.GetService<TimeProvider>();

        tp.Should().BeSameAs(TimeProvider.System);
    }

    [Fact]
    public void AddVerbaraResilience_ShouldPreserveExistingTimeProvider_WhenAlreadyRegistered()
    {
        var services = new ServiceCollection();
        var custom = new FakeTimeProvider();
        services.AddSingleton<TimeProvider>(custom);

        services.AddVerbaraResilience();

        using var sp = services.BuildServiceProvider();
        var tp = sp.GetService<TimeProvider>();

        tp.Should().BeSameAs(custom,
            "TryAddSingleton must not replace an already-registered TimeProvider");
    }
}
