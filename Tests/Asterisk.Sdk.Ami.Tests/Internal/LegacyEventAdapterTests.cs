#pragma warning disable CS0618 // Obsolete members — testing legacy event adapter
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Ami.Internal;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Internal;

public class LegacyEventAdapterTests
{
    private static AmiMessage CreateEventMessage(string eventType, string? uniqueId = null, string? privilege = null)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = eventType
        };
        if (uniqueId is not null)
            fields["Uniqueid"] = uniqueId;
        if (privilege is not null)
            fields["Privilege"] = privilege;
        return new AmiMessage(fields);
    }

    [Fact]
    public void CreateLegacyEvent_ShouldReturnDialEvent_WhenDialBegin()
    {
        var message = CreateEventMessage("DialBegin");

        var result = LegacyEventAdapter.CreateLegacyEvent(message);

        result.Should().BeOfType<DialEvent>();
        result!.EventType.Should().Be("Dial");
    }

    [Fact]
    public void CreateLegacyEvent_ShouldReturnLinkEvent_WhenBridgeEnter()
    {
        var message = CreateEventMessage("BridgeEnter");

        var result = LegacyEventAdapter.CreateLegacyEvent(message);

        result.Should().BeOfType<LinkEvent>();
        result!.EventType.Should().Be("Link");
    }

    [Fact]
    public void CreateLegacyEvent_ShouldReturnUnlinkEvent_WhenBridgeLeave()
    {
        var message = CreateEventMessage("BridgeLeave");

        var result = LegacyEventAdapter.CreateLegacyEvent(message);

        result.Should().BeOfType<UnlinkEvent>();
        result!.EventType.Should().Be("Unlink");
    }

    [Fact]
    public void CreateLegacyEvent_ShouldReturnNull_WhenUnknownEventType()
    {
        var message = CreateEventMessage("Hangup");

        var result = LegacyEventAdapter.CreateLegacyEvent(message);

        result.Should().BeNull();
    }

    [Fact]
    public void CreateLegacyEvent_ShouldReturnNull_WhenEventTypeIsNull()
    {
        var message = new AmiMessage(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        var result = LegacyEventAdapter.CreateLegacyEvent(message);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("dialbegin")]
    [InlineData("DIALBEGIN")]
    [InlineData("DialBegin")]
    [InlineData("dialBEGIN")]
    public void CreateLegacyEvent_ShouldBeCaseInsensitive_ForDialBegin(string eventType)
    {
        var message = CreateEventMessage(eventType);

        var result = LegacyEventAdapter.CreateLegacyEvent(message);

        result.Should().BeOfType<DialEvent>();
    }

    [Theory]
    [InlineData("bridgeenter")]
    [InlineData("BRIDGEENTER")]
    [InlineData("BridgeEnter")]
    public void CreateLegacyEvent_ShouldBeCaseInsensitive_ForBridgeEnter(string eventType)
    {
        var message = CreateEventMessage(eventType);

        var result = LegacyEventAdapter.CreateLegacyEvent(message);

        result.Should().BeOfType<LinkEvent>();
    }

    [Theory]
    [InlineData("bridgeleave")]
    [InlineData("BRIDGELEAVE")]
    [InlineData("BridgeLeave")]
    public void CreateLegacyEvent_ShouldBeCaseInsensitive_ForBridgeLeave(string eventType)
    {
        var message = CreateEventMessage(eventType);

        var result = LegacyEventAdapter.CreateLegacyEvent(message);

        result.Should().BeOfType<UnlinkEvent>();
    }

    [Fact]
    public void CreateLegacyEvent_ShouldCopyUniqueId_FromSourceMessage()
    {
        var message = CreateEventMessage("DialBegin", uniqueId: "1234567890.42");

        var result = LegacyEventAdapter.CreateLegacyEvent(message);

        result!.UniqueId.Should().Be("1234567890.42");
    }

    [Fact]
    public void CreateLegacyEvent_ShouldCopyPrivilege_FromSourceMessage()
    {
        var message = CreateEventMessage("BridgeEnter", privilege: "call,all");

        var result = LegacyEventAdapter.CreateLegacyEvent(message);

        result!.Privilege.Should().Be("call,all");
    }

    [Fact]
    public void CreateLegacyEvent_ShouldCopyBothUniqueIdAndPrivilege_ForAllEventTypes()
    {
        var dialMsg = CreateEventMessage("DialBegin", uniqueId: "uid-1", privilege: "priv-1");
        var enterMsg = CreateEventMessage("BridgeEnter", uniqueId: "uid-2", privilege: "priv-2");
        var leaveMsg = CreateEventMessage("BridgeLeave", uniqueId: "uid-3", privilege: "priv-3");

        var dial = LegacyEventAdapter.CreateLegacyEvent(dialMsg)!;
        var link = LegacyEventAdapter.CreateLegacyEvent(enterMsg)!;
        var unlink = LegacyEventAdapter.CreateLegacyEvent(leaveMsg)!;

        dial.UniqueId.Should().Be("uid-1");
        dial.Privilege.Should().Be("priv-1");

        link.UniqueId.Should().Be("uid-2");
        link.Privilege.Should().Be("priv-2");

        unlink.UniqueId.Should().Be("uid-3");
        unlink.Privilege.Should().Be("priv-3");
    }

    [Fact]
    public void CreateLegacyEvent_ShouldSetNullFields_WhenSourceMessageLacksUniqueIdAndPrivilege()
    {
        var message = CreateEventMessage("DialBegin");

        var result = LegacyEventAdapter.CreateLegacyEvent(message);

        result!.UniqueId.Should().BeNull();
        result.Privilege.Should().BeNull();
    }
}
