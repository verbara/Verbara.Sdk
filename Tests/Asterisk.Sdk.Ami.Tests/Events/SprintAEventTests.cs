using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Events;

public class SprintAEventTests
{
    [Theory]
    [InlineData(typeof(BridgeListItemEvent), "BridgeListItem")]
    [InlineData(typeof(BridgeListCompleteEvent), "BridgeListComplete")]
    [InlineData(typeof(BridgeTechnologyListItemEvent), "BridgeTechnologyListItem")]
    [InlineData(typeof(BridgeTechnologyListCompleteEvent), "BridgeTechnologyListComplete")]
    [InlineData(typeof(ResourceListDetailCompleteEvent), "ResourceListDetailComplete")]
    [InlineData(typeof(SubscriptionsCompleteEvent), "SubscriptionsComplete")]
    public void Event_ShouldHaveCorrectAsteriskMapping(Type eventType, string expectedMapping)
    {
        var attr = eventType.GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be(expectedMapping);
    }

    [Fact]
    public void BridgeListItemEvent_ShouldInheritFromManagerEvent()
        => new BridgeListItemEvent().Should().BeAssignableTo<ManagerEvent>();

    [Fact]
    public void BridgeListCompleteEvent_ShouldInheritFromManagerEvent()
        => new BridgeListCompleteEvent().Should().BeAssignableTo<ManagerEvent>();

    [Fact]
    public void BridgeTechnologyListItemEvent_ShouldInheritFromManagerEvent()
        => new BridgeTechnologyListItemEvent().Should().BeAssignableTo<ManagerEvent>();

    [Fact]
    public void BridgeTechnologyListCompleteEvent_ShouldInheritFromManagerEvent()
        => new BridgeTechnologyListCompleteEvent().Should().BeAssignableTo<ManagerEvent>();

    [Fact]
    public void ResourceListDetailCompleteEvent_ShouldInheritFromManagerEvent()
        => new ResourceListDetailCompleteEvent().Should().BeAssignableTo<ManagerEvent>();

    [Fact]
    public void SubscriptionsCompleteEvent_ShouldInheritFromManagerEvent()
        => new SubscriptionsCompleteEvent().Should().BeAssignableTo<ManagerEvent>();

    [Fact]
    public void BridgeListItemEvent_ShouldHaveExpectedProperties()
    {
        var evt = new BridgeListItemEvent
        {
            BridgeUniqueid = "abc-123",
            BridgeType = "mixing",
            BridgeTechnology = "simple_bridge",
            BridgeCreator = "core",
            BridgeName = "test-bridge",
            BridgeNumChannels = 2,
            Bridgevideosourcemode = "none"
        };

        evt.BridgeUniqueid.Should().Be("abc-123");
        evt.BridgeType.Should().Be("mixing");
        evt.BridgeTechnology.Should().Be("simple_bridge");
        evt.BridgeCreator.Should().Be("core");
        evt.BridgeName.Should().Be("test-bridge");
        evt.BridgeNumChannels.Should().Be(2);
        evt.Bridgevideosourcemode.Should().Be("none");
    }

    [Fact]
    public void BridgeListCompleteEvent_ShouldHaveListItems()
    {
        var evt = new BridgeListCompleteEvent { ListItems = 5 };
        evt.ListItems.Should().Be(5);
    }

    [Fact]
    public void BridgeTechnologyListItemEvent_ShouldHaveExpectedProperties()
    {
        var evt = new BridgeTechnologyListItemEvent
        {
            BridgeTechnology = "simple_bridge",
            BridgeType = "mixing"
        };

        evt.BridgeTechnology.Should().Be("simple_bridge");
        evt.BridgeType.Should().Be("mixing");
    }

    [Fact]
    public void BridgeTechnologyListCompleteEvent_ShouldHaveListItems()
    {
        var evt = new BridgeTechnologyListCompleteEvent { ListItems = 3 };
        evt.ListItems.Should().Be(3);
    }

    [Fact]
    public void ResourceListDetailCompleteEvent_ShouldHaveExpectedProperties()
    {
        var evt = new ResourceListDetailCompleteEvent
        {
            ListItems = 10,
            EventList = "Complete"
        };

        evt.ListItems.Should().Be(10);
        evt.EventList.Should().Be("Complete");
    }

    [Fact]
    public void SubscriptionsCompleteEvent_ShouldHaveExpectedProperties()
    {
        var evt = new SubscriptionsCompleteEvent
        {
            ListItems = 7,
            EventList = "Complete"
        };

        evt.ListItems.Should().Be(7);
        evt.EventList.Should().Be("Complete");
    }
}
