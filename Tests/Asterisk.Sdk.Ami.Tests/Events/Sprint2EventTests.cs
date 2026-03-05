using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Ami.Events.Base;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Events;

public class Sprint2EventTests
{
    // --- Presence events ---

    [Fact]
    public void PresenceStateChangeEvent_ShouldHaveCorrectMapping()
    {
        var attr = typeof(PresenceStateChangeEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("PresenceStateChange");
    }

    [Fact]
    public void PresenceStateChangeEvent_ShouldHaveProperties()
    {
        var evt = new PresenceStateChangeEvent
        {
            Presentity = "PJSIP/2000",
            Status = "available",
            Subtype = "online",
            Message = "On the phone"
        };
        evt.Presentity.Should().Be("PJSIP/2000");
        evt.Status.Should().Be("available");
        evt.Subtype.Should().Be("online");
        evt.Message.Should().Be("On the phone");
    }

    [Fact]
    public void PresenceStatusEvent_ShouldHaveCorrectMapping()
    {
        var attr = typeof(PresenceStatusEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("PresenceStatus");
    }

    [Fact]
    public void PresenceStateListCompleteEvent_ShouldInheritFromManagerEvent()
    {
        typeof(PresenceStateListCompleteEvent).Should().BeAssignableTo<ManagerEvent>();
        new PresenceStateListCompleteEvent { ListItems = 5 }.ListItems.Should().Be(5);
    }

    [Fact]
    public void DeviceStateListCompleteEvent_ShouldInheritFromManagerEvent()
    {
        typeof(DeviceStateListCompleteEvent).Should().BeAssignableTo<ManagerEvent>();
        new DeviceStateListCompleteEvent { ListItems = 10 }.ListItems.Should().Be(10);
    }

    [Fact]
    public void ExtensionStateListCompleteEvent_ShouldInheritFromManagerEvent()
    {
        typeof(ExtensionStateListCompleteEvent).Should().BeAssignableTo<ManagerEvent>();
        new ExtensionStateListCompleteEvent { ListItems = 3 }.ListItems.Should().Be(3);
    }

    // --- UserEvent ---

    [Fact]
    public void UserEventEvent_ShouldHaveCorrectMapping()
    {
        var attr = typeof(UserEventEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("UserEvent");
    }

    [Fact]
    public void UserEventEvent_ShouldHaveProperties()
    {
        var evt = new UserEventEvent { UserEvent = "CustomAlert", Channel = "PJSIP/2000-0001" };
        evt.UserEvent.Should().Be("CustomAlert");
        evt.Channel.Should().Be("PJSIP/2000-0001");
    }

    // --- Parking ---

    [Fact]
    public void ParkedCallSwapEvent_ShouldInheritFromChannelEventBase()
    {
        typeof(ParkedCallSwapEvent).Should().BeAssignableTo<ChannelEventBase>();
    }

    [Fact]
    public void ParkedCallSwapEvent_ShouldHaveProperties()
    {
        var evt = new ParkedCallSwapEvent
        {
            ParkeeChannel = "PJSIP/2000-0001",
            ParkerChannel = "PJSIP/3000-0001",
            ParkingSpace = "701",
            ParkingLot = "default",
            ParkingTimeout = "45"
        };
        evt.ParkeeChannel.Should().Be("PJSIP/2000-0001");
        evt.ParkingSpace.Should().Be("701");
        evt.ParkingLot.Should().Be("default");
    }

    // --- ConfBridge ---

    [Fact]
    public void ConfbridgeMuteEvent_ShouldInheritFromConfbridgeEventBase()
    {
        typeof(ConfbridgeMuteEvent).Should().BeAssignableTo<ConfbridgeEventBase>();
        var attr = typeof(ConfbridgeMuteEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("ConfbridgeMute");
    }

    [Fact]
    public void ConfbridgeUnmuteEvent_ShouldInheritFromConfbridgeEventBase()
    {
        typeof(ConfbridgeUnmuteEvent).Should().BeAssignableTo<ConfbridgeEventBase>();
        var attr = typeof(ConfbridgeUnmuteEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle().Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("ConfbridgeUnmute");
    }

    [Fact]
    public void ConfbridgeRecordEvent_ShouldInheritFromConfbridgeEventBase()
    {
        typeof(ConfbridgeRecordEvent).Should().BeAssignableTo<ConfbridgeEventBase>();
    }

    [Fact]
    public void ConfbridgeStopRecordEvent_ShouldInheritFromConfbridgeEventBase()
    {
        typeof(ConfbridgeStopRecordEvent).Should().BeAssignableTo<ConfbridgeEventBase>();
    }

    // --- HangupHandlerPop ---

    [Fact]
    public void HangupHandlerPopEvent_ShouldInheritFromChannelEventBase()
    {
        typeof(HangupHandlerPopEvent).Should().BeAssignableTo<ChannelEventBase>();
        var evt = new HangupHandlerPopEvent { Handler = "default,s,1" };
        evt.Handler.Should().Be("default,s,1");
    }

    // --- MixMonitorMute ---

    [Fact]
    public void MixMonitorMuteEvent_ShouldInheritFromChannelEventBase()
    {
        typeof(MixMonitorMuteEvent).Should().BeAssignableTo<ChannelEventBase>();
        var evt = new MixMonitorMuteEvent { Direction = 1, State = "1" };
        evt.Direction.Should().Be(1);
        evt.State.Should().Be("1");
    }

    // --- Sprint 3: TechCause and LoginTime fields ---

    [Fact]
    public void HangupEvent_ShouldHaveTechCause()
    {
        var evt = new HangupEvent { Cause = 16, CauseTxt = "Normal Clearing", TechCause = "200" };
        evt.TechCause.Should().Be("200");
    }

    [Fact]
    public void HangupEvent_TechCause_ShouldBeNullForOlderAsterisk()
    {
        var evt = new HangupEvent { Cause = 16 };
        evt.TechCause.Should().BeNull();
    }

    [Fact]
    public void HangupRequestEvent_ShouldHaveTechCause()
    {
        var evt = new HangupRequestEvent { Cause = 16, TechCause = "487" };
        evt.TechCause.Should().Be("487");
    }

    [Fact]
    public void SoftHangupRequestEvent_ShouldHaveTechCause()
    {
        var evt = new SoftHangupRequestEvent { Cause = 16, TechCause = "503" };
        evt.TechCause.Should().Be("503");
    }

    [Fact]
    public void QueueMemberStatusEvent_ShouldHaveLoginTime()
    {
        var evt = new QueueMemberStatusEvent { Queue = "support", LoginTime = 3600 };
        evt.LoginTime.Should().Be(3600);
    }
}
