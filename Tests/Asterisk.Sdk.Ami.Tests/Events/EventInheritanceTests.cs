using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Ami.Events.Base;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Events;

public class EventInheritanceTests
{
    [Fact]
    public void HangupEvent_ShouldInheritFromChannelEventBase()
    {
        typeof(HangupEvent).Should().BeAssignableTo<ChannelEventBase>();
    }

    [Fact]
    public void HangupEvent_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(HangupEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("Hangup");
    }

    [Fact]
    public void HangupEvent_ShouldHaveLeafProperties()
    {
        var evt = new HangupEvent { Cause = 16, CauseTxt = "Normal Clearing" };
        evt.Cause.Should().Be(16);
        evt.CauseTxt.Should().Be("Normal Clearing");
    }

    [Fact]
    public void NewChannelEvent_ShouldInheritFromChannelEventBase()
    {
        typeof(NewChannelEvent).Should().BeAssignableTo<ChannelEventBase>();
    }

    [Fact]
    public void NewChannelEvent_ShouldHaveAsteriskMapping()
    {
        var attr = typeof(NewChannelEvent).GetCustomAttributes(typeof(AsteriskMappingAttribute), false);
        attr.Should().ContainSingle()
            .Which.Should().BeOfType<AsteriskMappingAttribute>()
            .Which.Name.Should().Be("NewChannel");
    }

    [Fact]
    public void ChannelEventBase_ShouldInheritFromManagerEvent()
    {
        typeof(ChannelEventBase).Should().BeAssignableTo<ManagerEvent>();
    }

    [Fact]
    public void ChannelEventBase_ShouldHaveExpectedProperties()
    {
        var evt = new NewChannelEvent
        {
            Channel = "SIP/2000-0001",
            ChannelState = "6",
            ChannelStateDesc = "Up",
            CallerIdNum = "2000",
            CallerIdName = "Test User",
            ConnectedLineNum = "3000",
            ConnectedLineName = "Other User",
            AccountCode = "acc1",
            Context = "default",
            Exten = "100",
            Priority = 1,
            Language = "en",
            Linkedid = "123.1"
        };

        evt.Channel.Should().Be("SIP/2000-0001");
        evt.ChannelState.Should().Be("6");
        evt.CallerIdNum.Should().Be("2000");
        evt.Context.Should().Be("default");
        evt.Priority.Should().Be(1);
    }

    [Fact]
    public void AgentEventBase_ShouldInheritFromManagerEvent()
    {
        typeof(AgentEventBase).Should().BeAssignableTo<ManagerEvent>();
    }

    [Fact]
    public void AgentEventBase_ShouldHaveAgentAndChannelProperties()
    {
        var evt = new AgentLoginEvent { Agent = "1001", Channel = "SIP/2000-0001" };
        evt.Agent.Should().Be("1001");
        evt.Channel.Should().Be("SIP/2000-0001");
    }

    [Fact]
    public void QueueMemberEventBase_ShouldInheritFromManagerEvent()
    {
        typeof(QueueMemberEventBase).Should().BeAssignableTo<ManagerEvent>();
    }

    [Fact]
    public void QueueMemberEventBase_ShouldHaveQueueProperties()
    {
        var evt = new QueueMemberAddedEvent
        {
            Queue = "support",
            MemberName = "Agent/1001",
            Interface = "SIP/2000",
            Penalty = 0,
            Paused = false,
            Status = 1
        };

        evt.Queue.Should().Be("support");
        evt.MemberName.Should().Be("Agent/1001");
        evt.Interface.Should().Be("SIP/2000");
        evt.Penalty.Should().Be(0);
    }

    [Fact]
    public void ManagerEvent_BaseProperties_ShouldWork()
    {
        var evt = new ManagerEvent
        {
            Privilege = "call,all",
            UniqueId = "123.1",
            Timestamp = 1234567890.123,
            EventType = "TestEvent"
        };

        evt.Privilege.Should().Be("call,all");
        evt.UniqueId.Should().Be("123.1");
        evt.Timestamp.Should().Be(1234567890.123);
        evt.EventType.Should().Be("TestEvent");
    }

    [Fact]
    public void DialEvent_ShouldInheritFromManagerEvent()
    {
        typeof(DialEvent).Should().BeAssignableTo<ManagerEvent>();
    }

    [Fact]
    public void DialEvent_ShouldHaveExpectedProperties()
    {
        var evt = new DialEvent
        {
            SubEvent = "Begin",
            Channel = "SIP/2000-0001",
            Destination = "SIP/3000-0002"
        };

        evt.SubEvent.Should().Be("Begin");
        evt.Channel.Should().Be("SIP/2000-0001");
        evt.Destination.Should().Be("SIP/3000-0002");
    }

    [Fact]
    public void AllChannelEvents_ShouldInheritFromChannelEventBase()
    {
        typeof(HangupEvent).Should().BeAssignableTo<ChannelEventBase>();
        typeof(NewChannelEvent).Should().BeAssignableTo<ChannelEventBase>();
        typeof(NewStateEvent).Should().BeAssignableTo<ChannelEventBase>();
    }
}
