namespace Asterisk.Sdk.Push.AspNetCore.Tests;

public class PushAspNetCoreServiceExtensionsTests
{
    [Fact]
    public void AddAsteriskPushAspNetCore_ShouldRegisterAllRequiredServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAsteriskPushAspNetCore();
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IPushEventBus>().Should().NotBeNull();
        sp.GetRequiredService<IEventDeliveryFilter>().Should().NotBeNull();
        sp.GetRequiredService<ISubscriptionAuthorizer>().Should().NotBeNull();
    }

    [Fact]
    public void AddAsteriskPushAspNetCore_ShouldRegisterDefaultAuthorizer_WhenNoneRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAsteriskPushAspNetCore();
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<ISubscriptionAuthorizer>()
            .Should().BeOfType<AllowAllSubscriptionAuthorizer>();
    }

    [Fact]
    public void AddAsteriskPushAspNetCore_ShouldPassConfigureDelegate_ToPushOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAsteriskPushAspNetCore(o => o.BufferCapacity = 64);
        using var sp = services.BuildServiceProvider();

        var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PushEventBusOptions>>().Value;
        opts.BufferCapacity.Should().Be(64);
    }

    [Fact]
    public void AddAsteriskPushAspNetCore_ShouldResolveEventBus_WhenCalledTwice()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAsteriskPushAspNetCore();
        services.AddAsteriskPushAspNetCore();
        using var sp = services.BuildServiceProvider();

        // GetRequiredService always returns the last registered instance — should not throw.
        var bus = sp.GetRequiredService<IPushEventBus>();
        bus.Should().NotBeNull();
    }

    [Fact]
    public void AddAsteriskPushAspNetCore_ThrowsArgumentNullException_WhenServicesIsNull()
    {
        IServiceCollection? services = null;
        var act = () => services!.AddAsteriskPushAspNetCore();
        act.Should().Throw<ArgumentNullException>().WithParameterName("services");
    }
}

public class TopicPatternMatchingTests
{
    // These tests exercise the pattern matching logic used internally by the SSE endpoint
    // via TopicPattern directly (the endpoint logic is unit-tested via the authorizer/filter).

    [Theory]
    [InlineData("queue.42.updated", "queue.*.updated", true)]
    [InlineData("queue.42.updated", "queue.**", true)]
    [InlineData("queue.42.updated", "queue.42.updated", true)]
    [InlineData("queue.42.updated", "agent.**", false)]
    [InlineData("queue.42.updated", "**", true)]
    public void TopicPattern_Matches_ShouldMatchExpected_WhenPatternGiven(
        string topicStr, string patternStr, bool expected)
    {
        var topic = TopicName.Parse(topicStr);
        var pattern = TopicPattern.Parse(patternStr);

        pattern.Matches(topic).Should().Be(expected);
    }

    [Fact]
    public void TopicPattern_Matches_ShouldResolveSelf_WhenUserIdProvided()
    {
        var topic = TopicName.Parse("agent.user-123.status");
        var pattern = TopicPattern.Parse("agent.{self}.status");

        pattern.Matches(topic, "user-123").Should().BeTrue();
        pattern.Matches(topic, "user-456").Should().BeFalse();
    }

    [Fact]
    public void TopicPattern_Matches_ShouldNotMatchSelf_WhenUserIdIsNull()
    {
        var topic = TopicName.Parse("agent.user-123.status");
        var pattern = TopicPattern.Parse("agent.{self}.status");

        pattern.Matches(topic, null).Should().BeFalse();
    }
}

public class AllowAllSubscriptionAuthorizerTests
{
    [Fact]
    public void CanSubscribe_ShouldAllow_ForAnySubscriberAndPattern()
    {
        var authorizer = new AllowAllSubscriptionAuthorizer();
        var subscriber = new SubscriberContext(
            TenantId: "t1",
            UserId: "u1",
            Roles: new HashSet<string>(),
            Permissions: new HashSet<string>());
        var pattern = TopicPattern.Parse("queue.**");

        var result = authorizer.CanSubscribe(subscriber, pattern);

        result.Allowed.Should().BeTrue();
    }
}
