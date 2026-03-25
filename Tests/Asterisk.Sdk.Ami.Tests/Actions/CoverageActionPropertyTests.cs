using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Actions;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Actions;

public sealed class CoverageActionPropertyTests
{
    [Fact]
    public void CommandAction_ShouldHaveProperties()
    {
        var action = new CommandAction { Command = "core show channels" };
        action.Command.Should().Be("core show channels");
    }

    [Fact]
    public void LoginAction_ShouldHaveProperties()
    {
        var action = new LoginAction
        {
            Username = "admin",
            Secret = "password",
            AuthType = "MD5",
            Key = "key123",
            Events = "on"
        };
        action.Username.Should().Be("admin");
        action.Secret.Should().Be("password");
        action.AuthType.Should().Be("MD5");
        action.Key.Should().Be("key123");
        action.Events.Should().Be("on");
    }

    [Fact]
    public void GetVarAction_ShouldHaveProperties()
    {
        var action = new GetVarAction { Channel = "SIP/100", Variable = "CDR(billsec)" };
        action.Channel.Should().Be("SIP/100");
        action.Variable.Should().Be("CDR(billsec)");
    }

    [Fact]
    public void QueueAddAction_ShouldHaveAllProperties()
    {
        var action = new QueueAddAction
        {
            Queue = "sales",
            Interface = "SIP/100",
            Penalty = 5,
            Paused = true,
            Reason = "break",
            MemberName = "Agent 100",
            StateInterface = "SIP/100"
        };
        action.Queue.Should().Be("sales");
        action.Interface.Should().Be("SIP/100");
        action.Penalty.Should().Be(5);
        action.Paused.Should().BeTrue();
        action.Reason.Should().Be("break");
        action.MemberName.Should().Be("Agent 100");
        action.StateInterface.Should().Be("SIP/100");
    }

    [Fact]
    public void QueueRemoveAction_ShouldHaveProperties()
    {
        var action = new QueueRemoveAction { Queue = "sales", Interface = "SIP/100" };
        action.Queue.Should().Be("sales");
        action.Interface.Should().Be("SIP/100");
    }

    [Fact]
    public void QueuePauseAction_ShouldHaveProperties()
    {
        var action = new QueuePauseAction
        {
            Interface = "SIP/100",
            Queue = "support",
            Paused = true,
            Reason = "lunch"
        };
        action.Interface.Should().Be("SIP/100");
        action.Queue.Should().Be("support");
        action.Paused.Should().BeTrue();
        action.Reason.Should().Be("lunch");
    }

    [Fact]
    public void ChallengeAction_ShouldHaveProperties()
    {
        var action = new ChallengeAction { AuthType = "MD5" };
        action.AuthType.Should().Be("MD5");
    }

    [Fact]
    public void EventsAction_ShouldHaveProperties()
    {
        var action = new EventsAction { EventMask = "on" };
        action.EventMask.Should().Be("on");
    }

    [Fact]
    public void DbGetAction_ShouldHaveProperties()
    {
        var action = new DbGetAction { Family = "cidname", Key = "12345" };
        action.Family.Should().Be("cidname");
        action.Key.Should().Be("12345");
    }

    [Fact]
    public void DbPutAction_ShouldHaveProperties()
    {
        var action = new DbPutAction { Family = "cidname", Key = "12345", Val = "John" };
        action.Family.Should().Be("cidname");
        action.Key.Should().Be("12345");
        action.Val.Should().Be("John");
    }

    [Fact]
    public void DbDelAction_ShouldHaveProperties()
    {
        var action = new DbDelAction { Family = "cidname", Key = "12345" };
        action.Family.Should().Be("cidname");
        action.Key.Should().Be("12345");
    }

    [Fact]
    public void DbDelTreeAction_ShouldHaveProperties()
    {
        var action = new DbDelTreeAction { Family = "cidname", Key = "12345" };
        action.Family.Should().Be("cidname");
    }

    [Fact]
    public void RedirectAction_ShouldHaveProperties()
    {
        var action = new RedirectAction
        {
            Channel = "SIP/100-0001",
            Context = "default",
            Exten = "200",
            Priority = 1,
            ExtraChannel = "SIP/200-0002",
            ExtraContext = "default",
            ExtraExten = "300",
            ExtraPriority = 1
        };
        action.Channel.Should().Be("SIP/100-0001");
        action.ExtraChannel.Should().Be("SIP/200-0002");
        action.ExtraContext.Should().Be("default");
        action.ExtraExten.Should().Be("300");
        action.ExtraPriority.Should().Be(1);
    }

    [Fact]
    public void MixMonitorAction_ShouldHaveProperties()
    {
        var action = new MixMonitorAction { Channel = "SIP/100", File = "/tmp/rec.wav", Options = "r" };
        action.Channel.Should().Be("SIP/100");
        action.File.Should().Be("/tmp/rec.wav");
        action.Options.Should().Be("r");
    }

    [Fact]
    public void MonitorAction_ShouldHaveProperties()
    {
        var action = new MonitorAction { Channel = "SIP/100", File = "test", Format = "wav", Mix = true };
        action.Channel.Should().Be("SIP/100");
        action.File.Should().Be("test");
        action.Format.Should().Be("wav");
        action.Mix.Should().BeTrue();
    }

    [Fact]
    public void PlayDtmfAction_ShouldHaveProperties()
    {
        var action = new PlayDtmfAction { Channel = "SIP/100", Digit = "5", Duration = 250 };
        action.Channel.Should().Be("SIP/100");
        action.Digit.Should().Be("5");
        action.Duration.Should().Be(250);
    }

    [Fact]
    public void QueueStatusAction_ShouldHaveProperties()
    {
        var action = new QueueStatusAction { Queue = "sales", Member = "SIP/100" };
        action.Queue.Should().Be("sales");
        action.Member.Should().Be("SIP/100");
    }

    [Fact]
    public void QueueSummaryAction_ShouldHaveProperties()
    {
        var action = new QueueSummaryAction { Queue = "sales" };
        action.Queue.Should().Be("sales");
    }

    [Fact]
    public void ExecAction_ShouldHaveProperties()
    {
        var action = new ExecAction { Command = "Playback(hello-world)" };
        action.Command.Should().Be("Playback(hello-world)");
    }

    [Fact]
    public void AbsoluteTimeoutAction_ShouldHaveProperties()
    {
        var action = new AbsoluteTimeoutAction { Channel = "SIP/100", Timeout = 30 };
        action.Channel.Should().Be("SIP/100");
        action.Timeout.Should().Be(30);
    }

    [Fact]
    public void SendTextAction_ShouldHaveProperties()
    {
        var action = new SendTextAction { Channel = "SIP/100", Message = "Hello" };
        action.Channel.Should().Be("SIP/100");
        action.Message.Should().Be("Hello");
    }

    [Fact]
    public void StopMixMonitorAction_ShouldHaveProperties()
    {
        var action = new StopMixMonitorAction { Channel = "SIP/100", MixMonitorId = "mix-1" };
        action.Channel.Should().Be("SIP/100");
        action.MixMonitorId.Should().Be("mix-1");
    }

    [Fact]
    public void ShowDialplanAction_ShouldHaveProperties()
    {
        var action = new ShowDialplanAction { Context = "default" };
        action.Context.Should().Be("default");
    }

    [Fact]
    public void AgiAction_ShouldHaveProperties()
    {
        var action = new AgiAction { Channel = "SIP/100", Command = "VERBOSE test" };
        action.Channel.Should().Be("SIP/100");
        action.Command.Should().Be("VERBOSE test");
    }

    [Fact]
    public void ParkAction_ShouldHaveProperties()
    {
        var action = new ParkAction { Channel = "SIP/100", Channel2 = "SIP/200", Timeout = 30 };
        action.Channel.Should().Be("SIP/100");
        action.Channel2.Should().Be("SIP/200");
        action.Timeout.Should().Be(30);
    }

    [Fact]
    public void QueueLogAction_ShouldHaveProperties()
    {
        var action = new QueueLogAction
        {
            Queue = "sales",
            Event = "CUSTOM",
            Interface = "SIP/100",
            Message = "test message",
            UniqueId = "1234"
        };
        action.Queue.Should().Be("sales");
        action.Event.Should().Be("CUSTOM");
        action.Interface.Should().Be("SIP/100");
    }

    [Fact]
    public void FilterAction_ShouldHaveProperties()
    {
        var action = new FilterAction { Operation = "Add", Filter = "Event: Newchannel" };
        action.Operation.Should().Be("Add");
        action.Filter.Should().Be("Event: Newchannel");
    }

    [Fact]
    public void QueuePenaltyAction_ShouldHaveProperties()
    {
        var action = new QueuePenaltyAction { Interface = "SIP/100", Penalty = 10, Queue = "sales" };
        action.Interface.Should().Be("SIP/100");
        action.Penalty.Should().Be(10);
    }

    [Fact]
    public void QueueMemberRingInUseAction_ShouldHaveProperties()
    {
        var action = new QueueMemberRingInUseAction { Interface = "SIP/100", RingInUse = true, Queue = "sales" };
        action.Interface.Should().Be("SIP/100");
        action.RingInUse.Should().BeTrue();
    }

    [Fact]
    public void SetVarAction_ShouldHaveProperties()
    {
        var action = new SetVarAction { Channel = "SIP/100", Variable = "MY_VAR", Value = "test" };
        action.Channel.Should().Be("SIP/100");
        action.Variable.Should().Be("MY_VAR");
        action.Value.Should().Be("test");
    }

    [Fact]
    public void MuteAudioAction_ShouldHaveProperties()
    {
        var action = new MuteAudioAction { Channel = "SIP/100" };
        action.Channel.Should().Be("SIP/100");
    }

    [Fact]
    public void ConfbridgeKickAction_ShouldHaveProperties()
    {
        var action = new ConfbridgeKickAction { Conference = "1000", Channel = "SIP/100" };
        action.Conference.Should().Be("1000");
        action.Channel.Should().Be("SIP/100");
    }

    [Fact]
    public void ConfbridgeListAction_ShouldHaveProperties()
    {
        var action = new ConfbridgeListAction { Conference = "1000" };
        action.Conference.Should().Be("1000");
    }

    [Fact]
    public void ConfbridgeMuteAction_ShouldHaveProperties()
    {
        var action = new ConfbridgeMuteAction { Conference = "1000", Channel = "SIP/100" };
        action.Conference.Should().Be("1000");
    }

    [Fact]
    public void ConfbridgeLockAction_ShouldHaveProperties()
    {
        var action = new ConfbridgeLockAction { Conference = "1000" };
        action.Conference.Should().Be("1000");
    }

    [Fact]
    public void ConfbridgeUnlockAction_ShouldHaveProperties()
    {
        var action = new ConfbridgeUnlockAction { Conference = "1000" };
        action.Conference.Should().Be("1000");
    }

    [Fact]
    public void ConfbridgeUnmuteAction_ShouldHaveProperties()
    {
        var action = new ConfbridgeUnmuteAction { Conference = "1000", Channel = "SIP/100" };
        action.Conference.Should().Be("1000");
    }

    [Fact]
    public void GetConfigAction_ShouldHaveProperties()
    {
        var action = new GetConfigAction { Filename = "sip.conf" };
        action.Filename.Should().Be("sip.conf");
    }

    [Fact]
    public void ExtensionStateAction_ShouldHaveProperties()
    {
        var action = new ExtensionStateAction { Exten = "100", Context = "default" };
        action.Exten.Should().Be("100");
        action.Context.Should().Be("default");
    }

    [Fact]
    public void MailboxCountAction_ShouldHaveProperties()
    {
        var action = new MailboxCountAction { Mailbox = "100@default" };
        action.Mailbox.Should().Be("100@default");
    }

    [Fact]
    public void MailboxStatusAction_ShouldHaveProperties()
    {
        var action = new MailboxStatusAction { Mailbox = "100@default" };
        action.Mailbox.Should().Be("100@default");
    }

    [Fact]
    public void MessageSendAction_ShouldHaveProperties()
    {
        var action = new MessageSendAction { To = "sip:100@host", From = "sip:200@host", Body = "Hello" };
        action.To.Should().Be("sip:100@host");
        action.From.Should().Be("sip:200@host");
        action.Body.Should().Be("Hello");
    }
}
