using Verbara.Sdk.Ami.Actions;
using FluentAssertions;

namespace Verbara.Sdk.Ami.Tests.Actions;

public sealed class CoverageActionPropertyTests2
{
    [Fact]
    public void ConfbridgeStartRecordAction_ShouldHaveProperties()
    {
        var action = new ConfbridgeStartRecordAction { Conference = "1000", RecordFile = "/tmp/conf.wav" };
        action.Conference.Should().Be("1000");
        action.RecordFile.Should().Be("/tmp/conf.wav");
    }

    [Fact]
    public void ConfbridgeStopRecordAction_ShouldHaveProperties()
    {
        var action = new ConfbridgeStopRecordAction { Conference = "1000" };
        action.Conference.Should().Be("1000");
    }

#pragma warning disable VSDK0007 // the marked type is the subject; it leaves in 2.11.0
    [Fact]
    public void DbPrefixGetAction_ShouldHaveProperties()
    {
        var action = new DbPrefixGetAction { Family = "cidname", Key = "123" };
        action.Family.Should().Be("cidname");
        action.Key.Should().Be("123");
        action.Should().BeAssignableTo<IEventGeneratingAction>();
    }
#pragma warning restore VSDK0007

    [Fact]
    public void DongleSendSMSAction_ShouldHaveProperties()
    {
        var action = new DongleSendSMSAction { Device = "dongle0", Number = "+1234567890", Message = "Hello" };
        action.Device.Should().Be("dongle0");
        action.Number.Should().Be("+1234567890");
        action.Message.Should().Be("Hello");
    }

    [Fact]
    public void JabberSendAction_ShouldHaveProperties()
    {
        var action = new JabberSendAction { Jabber = "asterisk", ScreenName = "user@jabber.org", Message = "test" };
        action.Jabber.Should().Be("asterisk");
        action.ScreenName.Should().Be("user@jabber.org");
        action.Message.Should().Be("test");
    }

    [Fact]
    public void LocalOptimizeAwayAction_ShouldHaveProperties()
    {
        var action = new LocalOptimizeAwayAction { Channel = "Local/100@default-0001" };
        action.Channel.Should().Be("Local/100@default-0001");
    }

    [Fact]
    public void MixMonitorMuteAction_ShouldHaveProperties()
    {
        var action = new MixMonitorMuteAction { Channel = "SIP/100", Direction = "both", State = 1 };
        action.Channel.Should().Be("SIP/100");
        action.Direction.Should().Be("both");
        action.State.Should().Be(1);
    }

    [Fact]
    public void ModuleCheckAction_ShouldHaveProperties()
    {
        var action = new ModuleCheckAction { Module = "chan_pjsip.so" };
        action.Module.Should().Be("chan_pjsip.so");
    }

    [Fact]
    public void ModuleLoadAction_ShouldHaveProperties()
    {
        var action = new ModuleLoadAction { Module = "chan_pjsip.so", LoadType = "load" };
        action.Module.Should().Be("chan_pjsip.so");
        action.LoadType.Should().Be("load");
    }

    [Fact]
    public void MWIDeleteAction_ShouldHaveProperties()
    {
        var action = new MWIDeleteAction { Mailbox = "100@default" };
        action.Mailbox.Should().Be("100@default");
    }

    [Fact]
    public void MWIUpdateAction_ShouldHaveProperties()
    {
        var action = new MWIUpdateAction { Mailbox = "100@default", OldMessages = 3, NewMessages = 5 };
        action.Mailbox.Should().Be("100@default");
        action.OldMessages.Should().Be(3);
        action.NewMessages.Should().Be(5);
    }

    [Fact]
    public void PauseMixMonitorAction_ShouldHaveProperties()
    {
        var action = new PauseMixMonitorAction { Channel = "SIP/100", State = 1, Direction = "both" };
        action.Channel.Should().Be("SIP/100");
        action.State.Should().Be(1);
        action.Direction.Should().Be("both");
    }

    [Fact]
    public void PJSIPNotifyAction_ShouldHaveProperties()
    {
        var action = new PJSIPNotifyAction { Channel = "PJSIP/100", Endpoint = "100", Uri = "sip:100@host" };
        action.Channel.Should().Be("PJSIP/100");
        action.Endpoint.Should().Be("100");
        action.Uri.Should().Be("sip:100@host");
    }

    [Fact]
    public void PJSipShowEndpointAction_ShouldHaveProperties()
    {
        var action = new PJSipShowEndpointAction { Endpoint = "100" };
        action.Endpoint.Should().Be("100");
        action.Should().BeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void PlayMfAction_ShouldHaveProperties()
    {
        var action = new PlayMfAction { Channel = "SIP/100", Digit = "123" };
        action.Channel.Should().Be("SIP/100");
        action.Digit.Should().Be("123");
    }

    [Fact]
    public void QueueChangePriorityCallerAction_ShouldHaveProperties()
    {
        var action = new QueueChangePriorityCallerAction { Queue = "sales", Caller = "SIP/100-001", Priority = 5 };
        action.Queue.Should().Be("sales");
        action.Caller.Should().Be("SIP/100-001");
        action.Priority.Should().Be(5);
    }

    [Fact]
    public void QueueResetAction_ShouldHaveProperties()
    {
        var action = new QueueResetAction { Queue = "sales" };
        action.Queue.Should().Be("sales");
    }

    [Fact]
    public void QueueWithdrawCallerAction_ShouldHaveProperties()
    {
        var action = new QueueWithdrawCallerAction { Queue = "sales", Caller = "SIP/100-001" };
        action.Queue.Should().Be("sales");
        action.Caller.Should().Be("SIP/100-001");
    }

    [Fact]
    public void VoicemailBoxSummaryAction_ShouldHaveProperties()
    {
        var action = new VoicemailBoxSummaryAction { Mailbox = "100", Context = "default" };
        action.Mailbox.Should().Be("100");
        action.Context.Should().Be("default");
        action.Should().BeAssignableTo<IEventGeneratingAction>();
    }

    [Fact]
    public void VoicemailForwardAction_ShouldHaveProperties()
    {
        var action = new VoicemailForwardAction
        {
            Mailbox = "100", Context = "default", Folder = "INBOX", ID = "msg001",
            ToMailbox = "200", ToContext = "default", ToFolder = "Old"
        };
        action.Mailbox.Should().Be("100");
        action.Context.Should().Be("default");
        action.Folder.Should().Be("INBOX");
        action.ID.Should().Be("msg001");
        action.ToMailbox.Should().Be("200");
        action.ToContext.Should().Be("default");
        action.ToFolder.Should().Be("Old");
    }

    [Fact]
    public void VoicemailMoveAction_ShouldHaveProperties()
    {
        var action = new VoicemailMoveAction
        {
            Mailbox = "100", Context = "default", Folder = "INBOX", ID = "msg001", ToFolder = "Old"
        };
        action.Mailbox.Should().Be("100");
        action.Context.Should().Be("default");
        action.Folder.Should().Be("INBOX");
        action.ID.Should().Be("msg001");
        action.ToFolder.Should().Be("Old");
    }

    [Fact]
    public void VoicemailRemoveAction_ShouldHaveProperties()
    {
        var action = new VoicemailRemoveAction { Mailbox = "100", Context = "default", Folder = "INBOX", ID = "msg001" };
        action.Mailbox.Should().Be("100");
        action.Context.Should().Be("default");
        action.Folder.Should().Be("INBOX");
        action.ID.Should().Be("msg001");
    }

}
