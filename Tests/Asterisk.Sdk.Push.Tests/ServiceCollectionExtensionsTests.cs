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
