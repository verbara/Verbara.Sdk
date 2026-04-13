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
}
