using Asterisk.Sdk.Push.Authz;

namespace Asterisk.Sdk.Push.Tests.Authz;

public sealed class AuthorizationTests
{
    [Fact]
    public void AuthorizationResult_Allow_ShouldBeAllowed()
    {
        var result = AuthorizationResult.Allow();
        result.Allowed.Should().BeTrue();
        result.Reason.Should().BeNull();
    }

    [Fact]
    public void AuthorizationResult_Deny_ShouldNotBeAllowed()
    {
        var result = AuthorizationResult.Deny("insufficient permissions");
        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be("insufficient permissions");
    }

    [Fact]
    public void AllowAll_ShouldAlwaysAllow_WhenAnyPatternRequested()
    {
        var authz = new AllowAllSubscriptionAuthorizer();
        var subscriber = new SubscriberContext("tenant-1", null, new HashSet<string>(), new HashSet<string>());
        var result = authz.CanSubscribe(subscriber, TopicPattern.Parse("queue.**"));
        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public void AllowAll_ShouldAlwaysAllow_WhenWildcardAllRequested()
    {
        var authz = new AllowAllSubscriptionAuthorizer();
        var subscriber = new SubscriberContext("tenant-1", null, new HashSet<string>(), new HashSet<string>());
        var result = authz.CanSubscribe(subscriber, TopicPattern.Parse("**"));
        result.Allowed.Should().BeTrue();
    }
}
