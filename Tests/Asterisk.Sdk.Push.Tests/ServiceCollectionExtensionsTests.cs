using Asterisk.Sdk.Push.Authz;

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asterisk.Sdk.Push.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAsteriskPush_ShouldRegisterAllRequiredServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAsteriskPush();
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IPushEventBus>().Should().BeOfType<RxPushEventBus>();
        sp.GetRequiredService<IEventDeliveryFilter>().Should().BeOfType<DefaultDeliveryFilter>();
        sp.GetRequiredService<ISubscriptionRegistry>().Should().BeOfType<InMemorySubscriptionRegistry>();
        sp.GetRequiredService<PushMetrics>().Should().NotBeNull();
    }

    [Fact]
    public void AddAsteriskPush_ShouldRegisterTopicRegistry_AsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAsteriskPush();
        using var sp = services.BuildServiceProvider();

        var registry1 = sp.GetRequiredService<ITopicRegistry>();
        var registry2 = sp.GetRequiredService<ITopicRegistry>();

        registry1.Should().BeOfType<TopicRegistry>();
        registry1.Should().BeSameAs(registry2);
    }

    [Fact]
    public void AddAsteriskPush_ShouldRegisterAllowAllAuthorizer_ByDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAsteriskPush();
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<ISubscriptionAuthorizer>()
            .Should().BeOfType<AllowAllSubscriptionAuthorizer>();
    }

    [Fact]
    public void AddAsteriskPush_ShouldNotOverrideAuthorizer_WhenAlreadyRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Register a custom authorizer before calling AddAsteriskPush
        services.TryAddSingleton<ISubscriptionAuthorizer, CustomAuthorizer>();
        services.AddAsteriskPush();
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<ISubscriptionAuthorizer>()
            .Should().BeOfType<CustomAuthorizer>();
    }

    private sealed class CustomAuthorizer : ISubscriptionAuthorizer
    {
        public AuthorizationResult CanSubscribe(SubscriberContext subscriber, TopicPattern requestedPattern)
            => AuthorizationResult.Allow();
    }

    [Fact]
    public void AddAsteriskPush_ShouldApplyConfigureAction_WhenProvided()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAsteriskPush(o =>
        {
            o.BufferCapacity = 32;
            o.BackpressureStrategy = BackpressureStrategy.Block;
        });
        using var sp = services.BuildServiceProvider();

        var opts = sp.GetRequiredService<IOptions<PushEventBusOptions>>().Value;
        opts.BufferCapacity.Should().Be(32);
        opts.BackpressureStrategy.Should().Be(BackpressureStrategy.Block);
    }

    [Fact]
    public void AddAsteriskPush_ShouldValidateOptions_WhenInvalidBufferCapacity()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAsteriskPush(o => o.BufferCapacity = 0);
        using var sp = services.BuildServiceProvider();

        var act = () => sp.GetRequiredService<IOptions<PushEventBusOptions>>().Value;
        act.Should().Throw<OptionsValidationException>();
    }
}
