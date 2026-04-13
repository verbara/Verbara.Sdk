namespace Asterisk.Sdk.Push.Tests;

public class DefaultDeliveryFilterTests
{
    private static SubscriberContext Sub(string tenant = "tenant-1", string? user = "alice") =>
        new(tenant, user, new HashSet<string>(), new HashSet<string>());

    private readonly DefaultDeliveryFilter _filter = new();

    [Fact]
    public void IsDeliverableToSubscriber_ShouldReturnFalse_WhenTenantMismatch()
    {
        var evt = TestEventFactory.Create(tenantId: "tenant-A");
        _filter.IsDeliverableToSubscriber(evt, Sub(tenant: "tenant-B")).Should().BeFalse();
    }

    [Fact]
    public void IsDeliverableToSubscriber_ShouldReturnTrue_WhenBroadcastAndTenantMatch()
    {
        var evt = TestEventFactory.Create(tenantId: "tenant-1", userId: null);
        _filter.IsDeliverableToSubscriber(evt, Sub(tenant: "tenant-1", user: "anyone")).Should().BeTrue();
    }

    [Fact]
    public void IsDeliverableToSubscriber_ShouldReturnFalse_WhenUserTargetedAndWrongUser()
    {
        var evt = TestEventFactory.Create(tenantId: "tenant-1", userId: "alice");
        _filter.IsDeliverableToSubscriber(evt, Sub(tenant: "tenant-1", user: "bob")).Should().BeFalse();
    }

    [Fact]
    public void IsDeliverableToSubscriber_ShouldReturnTrue_WhenUserTargetedAndMatchingUser()
    {
        var evt = TestEventFactory.Create(tenantId: "tenant-1", userId: "alice");
        _filter.IsDeliverableToSubscriber(evt, Sub(tenant: "tenant-1", user: "alice")).Should().BeTrue();
    }

    [Fact]
    public void IsDeliverableToSubscriber_ShouldReturnFalse_WhenSubscriberNullUserIdAndEventTargeted()
    {
        var evt = TestEventFactory.Create(tenantId: "tenant-1", userId: "alice");
        _filter.IsDeliverableToSubscriber(evt, Sub(tenant: "tenant-1", user: null)).Should().BeFalse();
    }

    // --- Topic pattern filtering ---

    private static SubscriberContext SubWithTopic(
        string tenant = "tenant-1",
        string? user = "alice",
        string? topicPattern = null) =>
        new(tenant, user, new HashSet<string>(), new HashSet<string>(), topicPattern);

    [Fact]
    public void IsDeliverableToSubscriber_ShouldReturnTrue_WhenTopicMatchesPattern()
    {
        var evt = TestEventFactory.Create(tenantId: "tenant-1", userId: null, topicPath: "queue.42.updated");
        var sub = SubWithTopic(tenant: "tenant-1", user: "alice", topicPattern: "queue.*.updated");
        _filter.IsDeliverableToSubscriber(evt, sub).Should().BeTrue();
    }

    [Fact]
    public void IsDeliverableToSubscriber_ShouldReturnFalse_WhenTopicDoesNotMatchPattern()
    {
        var evt = TestEventFactory.Create(tenantId: "tenant-1", userId: null, topicPath: "queue.42.updated");
        var sub = SubWithTopic(tenant: "tenant-1", user: "alice", topicPattern: "agent.**");
        _filter.IsDeliverableToSubscriber(evt, sub).Should().BeFalse();
    }

    [Fact]
    public void IsDeliverableToSubscriber_ShouldReturnTrue_WhenSubscriberHasNoTopicPattern()
    {
        var evt = TestEventFactory.Create(tenantId: "tenant-1", userId: null, topicPath: "queue.42.updated");
        var sub = SubWithTopic(tenant: "tenant-1", user: "alice", topicPattern: null);
        _filter.IsDeliverableToSubscriber(evt, sub).Should().BeTrue();
    }

    [Fact]
    public void IsDeliverableToSubscriber_ShouldReturnTrue_WhenEventHasNoTopicPath()
    {
        var evt = TestEventFactory.Create(tenantId: "tenant-1", userId: null, topicPath: null);
        var sub = SubWithTopic(tenant: "tenant-1", user: "alice", topicPattern: "agent.**");
        _filter.IsDeliverableToSubscriber(evt, sub).Should().BeTrue();
    }

    [Fact]
    public void IsDeliverableToSubscriber_ShouldReturnFalse_WhenTopicMatchesButTenantMismatch()
    {
        var evt = TestEventFactory.Create(tenantId: "tenant-A", userId: null, topicPath: "queue.42.updated");
        var sub = SubWithTopic(tenant: "tenant-B", user: "alice", topicPattern: "queue.*.updated");
        _filter.IsDeliverableToSubscriber(evt, sub).Should().BeFalse();
    }

    [Fact]
    public void IsDeliverableToSubscriber_ShouldResolveSelf_WhenPatternContainsSelfPlaceholder()
    {
        var evt = TestEventFactory.Create(tenantId: "tenant-1", userId: null, topicPath: "agent.agent-123.state.changed");
        var sub = SubWithTopic(tenant: "tenant-1", user: "agent-123", topicPattern: "agent.{self}.**");
        _filter.IsDeliverableToSubscriber(evt, sub).Should().BeTrue();
    }
}
