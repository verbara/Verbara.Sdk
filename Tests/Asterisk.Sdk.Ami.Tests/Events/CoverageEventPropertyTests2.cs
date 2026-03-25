using Asterisk.Sdk.Ami.Events;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Events;

public sealed class CoverageEventPropertyTests2
{
    [Fact]
    public void MessageWaitingEvent_ShouldHaveAllProperties()
    {
        var evt = new MessageWaitingEvent { Mailbox = "100@default", Waiting = 1, New = 3, Old = 5 };
        evt.Mailbox.Should().Be("100@default");
        evt.Waiting.Should().Be(1);
        evt.New.Should().Be(3);
        evt.Old.Should().Be(5);
    }

    [Fact]
    public void ShowDialplanCompleteEvent_ShouldHaveAllProperties()
    {
        var evt = new ShowDialplanCompleteEvent
        {
            EventList = "Complete",
            ListItems = 10,
            ListExtensions = 5,
            ListPriorities = 20,
            ListContexts = 3
        };
        evt.EventList.Should().Be("Complete");
        evt.ListItems.Should().Be(10);
        evt.ListExtensions.Should().Be(5);
        evt.ListPriorities.Should().Be(20);
        evt.ListContexts.Should().Be(3);
    }

    [Fact]
    public void RtpReceiverStatEvent_ShouldHaveAllProperties()
    {
        var evt = new RtpReceiverStatEvent
        {
            ReceivedPackets = 1000, Transit = 1.5, RrCount = 10, AccountCode = "acc1"
        };
        evt.ReceivedPackets.Should().Be(1000);
        evt.Transit.Should().Be(1.5);
        evt.RrCount.Should().Be(10);
        evt.AccountCode.Should().Be("acc1");
    }

    [Fact]
    public void VoicemailUserEntryEvent_ShouldHaveAllProperties()
    {
        var evt = new VoicemailUserEntryEvent
        {
            VmContext = "default", Voicemailbox = "100", Fullname = "John Doe",
            Email = "john@example.com", Pager = "pager@example.com",
            ServerEmail = "server@example.com", MailCommand = "/usr/sbin/sendmail",
            Language = "en", Timezone = "eastern", Callback = "default",
            Dialout = "default", ExitContext = "default",
            SayDurationMinimum = 2, SayEnvelope = true, SayCid = true,
            AttachMessage = true, AttachmentFormat = "wav49",
            DeleteMessage = false, VolumeGain = 1.5, CanReview = true,
            CallOperator = false, MaxMessageCount = 100, MaxMessageLength = 180,
            NewMessageCount = 3, OldMessageCount = 10, ImapUser = "john"
        };
        evt.VmContext.Should().Be("default");
        evt.Voicemailbox.Should().Be("100");
        evt.Fullname.Should().Be("John Doe");
        evt.Email.Should().Be("john@example.com");
        evt.Pager.Should().Be("pager@example.com");
        evt.ServerEmail.Should().Be("server@example.com");
        evt.MailCommand.Should().Be("/usr/sbin/sendmail");
        evt.Language.Should().Be("en");
        evt.Timezone.Should().Be("eastern");
        evt.Callback.Should().Be("default");
        evt.Dialout.Should().Be("default");
        evt.ExitContext.Should().Be("default");
        evt.SayDurationMinimum.Should().Be(2);
        evt.SayEnvelope.Should().BeTrue();
        evt.SayCid.Should().BeTrue();
        evt.AttachMessage.Should().BeTrue();
        evt.AttachmentFormat.Should().Be("wav49");
        evt.DeleteMessage.Should().BeFalse();
        evt.VolumeGain.Should().Be(1.5);
        evt.CanReview.Should().BeTrue();
        evt.CallOperator.Should().BeFalse();
        evt.MaxMessageCount.Should().Be(100);
        evt.MaxMessageLength.Should().Be(180);
        evt.NewMessageCount.Should().Be(3);
        evt.OldMessageCount.Should().Be(10);
        evt.ImapUser.Should().Be("john");
    }

    [Fact]
    public void FaxDocumentStatusEvent_ShouldHaveAllProperties()
    {
        var evt = new FaxDocumentStatusEvent
        {
            DocumentNumber = 1, LastError = 0, PageCount = 3, StartPage = 1,
            LastPageProcessed = 3, RetransmitCount = 0, TransferPels = 1728,
            TransferRate = 14400, TransferDuration = "00:01:30",
            BadLineCount = 2, ProcessedStatus = "OK", DocumentTime = "00:01:30",
            LocalSid = "local-station", LocalDis = "local-dis",
            RemoteSid = "remote-station", RemoteDis = "remote-dis",
            AccountCode = "acc1", Language = "en", LinkedId = "linked-1"
        };
        evt.DocumentNumber.Should().Be(1);
        evt.LastError.Should().Be(0);
        evt.PageCount.Should().Be(3);
        evt.StartPage.Should().Be(1);
        evt.LastPageProcessed.Should().Be(3);
        evt.RetransmitCount.Should().Be(0);
        evt.TransferPels.Should().Be(1728);
        evt.TransferRate.Should().Be(14400);
        evt.TransferDuration.Should().Be("00:01:30");
        evt.BadLineCount.Should().Be(2);
        evt.ProcessedStatus.Should().Be("OK");
        evt.DocumentTime.Should().Be("00:01:30");
        evt.LocalSid.Should().Be("local-station");
        evt.LocalDis.Should().Be("local-dis");
        evt.RemoteSid.Should().Be("remote-station");
        evt.RemoteDis.Should().Be("remote-dis");
        evt.AccountCode.Should().Be("acc1");
        evt.Language.Should().Be("en");
        evt.LinkedId.Should().Be("linked-1");
    }

    [Fact]
    public void FaxReceivedEvent_ShouldHaveAllProperties()
    {
        var evt = new FaxReceivedEvent
        {
            CallerId = "5551234", RemoteStationId = "remote", LocalStationId = "local",
            PagesTransferred = 3, Resolution = 200, TransferRate = 14400, Filename = "/tmp/fax.tif"
        };
        evt.CallerId.Should().Be("5551234");
        evt.RemoteStationId.Should().Be("remote");
        evt.LocalStationId.Should().Be("local");
        evt.PagesTransferred.Should().Be(3);
        evt.Resolution.Should().Be(200);
        evt.TransferRate.Should().Be(14400);
        evt.Filename.Should().Be("/tmp/fax.tif");
    }

    [Fact]
    public void PeerEntryEvent_ShouldHaveAllProperties()
    {
#pragma warning disable CS0618
        var evt = new PeerEntryEvent
        {
            ChannelType = "SIP", ObjectName = "100", ObjectUserName = "100",
            ChanObjectType = "peer", IpAddress = "192.168.1.100", IpPort = 5060,
            Port = 5060, Dynamic = true, NatSupport = true, ForceRport = true,
            VideoSupport = false, TextSupport = false, Acl = true, Status = "OK (10 ms)",
            RealtimeDevice = "no", Trunk = false, Encryption = "no",
            AutoComedia = "yes", AutoForcerport = "yes", Comedia = "no",
            Description = "Test Peer", Accountcode = "acc1"
        };
#pragma warning restore CS0618
        evt.ChannelType.Should().Be("SIP");
        evt.ObjectName.Should().Be("100");
        evt.ObjectUserName.Should().Be("100");
        evt.ChanObjectType.Should().Be("peer");
        evt.IpAddress.Should().Be("192.168.1.100");
        evt.IpPort.Should().Be(5060);
        evt.Port.Should().Be(5060);
        evt.Dynamic.Should().BeTrue();
        evt.NatSupport.Should().BeTrue();
        evt.ForceRport.Should().BeTrue();
        evt.VideoSupport.Should().BeFalse();
        evt.TextSupport.Should().BeFalse();
        evt.Acl.Should().BeTrue();
        evt.Status.Should().Be("OK (10 ms)");
        evt.RealtimeDevice.Should().Be("no");
        evt.Trunk.Should().BeFalse();
        evt.Encryption.Should().Be("no");
        evt.AutoComedia.Should().Be("yes");
        evt.AutoForcerport.Should().Be("yes");
        evt.Comedia.Should().Be("no");
        evt.Description.Should().Be("Test Peer");
        evt.Accountcode.Should().Be("acc1");
    }

    [Fact]
    public void AlarmClearEvent_ShouldHaveProperties()
    {
        var evt = new AlarmClearEvent { Channel = 1 };
        evt.Channel.Should().Be(1);
    }

    [Fact]
    public void AlarmEvent_ShouldHaveProperties()
    {
        var evt = new AlarmEvent { Channel = 1, Alarm = "Red Alarm" };
        evt.Channel.Should().Be(1);
        evt.Alarm.Should().Be("Red Alarm");
    }

    [Fact]
    public void ConfbridgeListRoomsEvent_ShouldHaveAllProperties()
    {
        var evt = new ConfbridgeListRoomsEvent
        {
            Conference = "1000", Parties = 5, Marked = 1, Locked = false, Muted = "No"
        };
        evt.Conference.Should().Be("1000");
        evt.Parties.Should().Be(5);
        evt.Marked.Should().Be(1);
        evt.Locked.Should().BeFalse();
        evt.Muted.Should().Be("No");
    }

    [Fact]
    public void ChannelReloadEvent_ShouldHaveAllProperties()
    {
        var evt = new ChannelReloadEvent
        {
            ChannelType = "SIP", ReloadReason = "RELOAD", UserCount = 10,
            PeerCount = 5, RegistryCount = 2
        };
        evt.ChannelType.Should().Be("SIP");
        evt.ReloadReason.Should().Be("RELOAD");
        evt.UserCount.Should().Be(10);
        evt.PeerCount.Should().Be(5);
        evt.RegistryCount.Should().Be(2);
    }

    [Fact]
    public void FullyBootedEvent_ShouldHaveAllProperties()
    {
        var evt = new FullyBootedEvent { Status = "Fully Booted", Uptime = 3600, Lastreload = "1200" };
        evt.Status.Should().Be("Fully Booted");
        evt.Uptime.Should().Be(3600);
        evt.Lastreload.Should().Be("1200");
    }

    [Fact]
    public void QueueParamsEvent_ShouldHaveAllProperties()
    {
        var evt = new QueueParamsEvent
        {
            Queue = "sales", Max = 10, Strategy = "ringall",
            Calls = 5, HoldTime = 120, Completed = 50, Abandoned = 3,
            ServiceLevel = 80, ServiceLevelPerf = 95.0, Weight = 0, TalkTime = 60,
            ServiceLevelPerf2 = 90.0
        };
        evt.Queue.Should().Be("sales");
        evt.Max.Should().Be(10);
        evt.Strategy.Should().Be("ringall");
        evt.Calls.Should().Be(5);
        evt.HoldTime.Should().Be(120);
        evt.Completed.Should().Be(50);
        evt.Abandoned.Should().Be(3);
        evt.ServiceLevel.Should().Be(80);
        evt.ServiceLevelPerf.Should().Be(95.0);
        evt.Weight.Should().Be(0);
        evt.TalkTime.Should().Be(60);
        evt.ServiceLevelPerf2.Should().Be(90.0);
    }

    [Fact]
    public void JoinEvent_ShouldHaveAllProperties()
    {
#pragma warning disable CS0618
        var evt = new JoinEvent { CallerId = "5551234", Position = 3 };
#pragma warning restore CS0618
        evt.CallerId.Should().Be("5551234");
        evt.Position.Should().Be(3);
    }

    [Fact]
    public void QueueCallerAbandonEvent_ShouldHaveAllProperties()
    {
        var evt = new QueueCallerAbandonEvent
        {
            Position = 2, OriginalPosition = 1, HoldTime = 45,
            Language = "en", LinkedId = "linked-1", Accountcode = "acc1"
        };
        evt.Position.Should().Be(2);
        evt.OriginalPosition.Should().Be(1);
        evt.HoldTime.Should().Be(45);
        evt.Language.Should().Be("en");
        evt.LinkedId.Should().Be("linked-1");
        evt.Accountcode.Should().Be("acc1");
    }

    [Fact]
    public void QueueCallerLeaveEvent_ShouldHaveAllProperties()
    {
        var evt = new QueueCallerLeaveEvent
        {
            Position = 1, Language = "en", LinkedId = "linked-1", Accountcode = "acc1"
        };
        evt.Position.Should().Be(1);
        evt.Language.Should().Be("en");
        evt.LinkedId.Should().Be("linked-1");
        evt.Accountcode.Should().Be("acc1");
    }

    [Fact]
    public void MeetMeMuteEvent_ShouldHaveAllProperties()
    {
#pragma warning disable CS0618
        var evt = new MeetMeMuteEvent { Meetme = "1000", Status = true };
#pragma warning restore CS0618
        evt.Meetme.Should().Be("1000");
        evt.Status.Should().BeTrue();
    }

    [Fact]
    public void MeetMeTalkingEvent_ShouldHaveAllProperties()
    {
#pragma warning disable CS0618
        var evt = new MeetMeTalkingEvent { Meetme = "1000", Status = true };
#pragma warning restore CS0618
        evt.Meetme.Should().Be("1000");
        evt.Status.Should().BeTrue();
    }

    [Fact]
    public void MeetMeTalkingRequestEvent_ShouldHaveAllProperties()
    {
#pragma warning disable CS0618
        var evt = new MeetMeTalkingRequestEvent { Meetme = "1000", Status = true };
#pragma warning restore CS0618
        evt.Meetme.Should().Be("1000");
        evt.Status.Should().BeTrue();
    }

    [Fact]
    public void ParkedCallEvent_ShouldHaveAllProperties()
    {
        var evt = new ParkedCallEvent { Timeout = 30, Parkeelinkedid = "linked-1" };
        evt.Timeout.Should().Be(30);
        evt.Parkeelinkedid.Should().Be("linked-1");
    }

    [Fact]
    public void PeerStatusEvent_ShouldHaveAllProperties()
    {
        var evt = new PeerStatusEvent
        {
            ChannelType = "SIP", Peer = "SIP/100", PeerStatus = "Registered",
            Address = "192.168.1.100:5060"
        };
        evt.ChannelType.Should().Be("SIP");
        evt.Peer.Should().Be("SIP/100");
        evt.PeerStatus.Should().Be("Registered");
        evt.Address.Should().Be("192.168.1.100:5060");
    }

    [Fact]
    public void PriEventEvent_ShouldHaveAllProperties()
    {
        var evt = new PriEventEvent { PriEvent = "RING" };
        evt.PriEvent.Should().Be("RING");
    }

    [Fact]
    public void DndStateEvent_ShouldHaveAllProperties()
    {
        var evt = new DndStateEvent { Channel = "SIP/100", State = true };
        evt.Channel.Should().Be("SIP/100");
        evt.State.Should().BeTrue();
    }

    [Fact]
    public void ShutdownEvent_ShouldHaveAllProperties()
    {
        var evt = new ShutdownEvent { Shutdown = "Cleanly", Restart = true };
        evt.Shutdown.Should().Be("Cleanly");
        evt.Restart.Should().BeTrue();
    }

    [Fact]
    public void LogChannelEvent_ShouldHaveAllProperties()
    {
        var evt = new LogChannelEvent { Channel = "console", Enabled = true };
        evt.Channel.Should().Be("console");
        evt.Enabled.Should().BeTrue();
    }

    [Fact]
    public void MasqueradeEvent_ShouldHaveAllProperties()
    {
        var evt = new MasqueradeEvent
        {
            Clone = "SIP/100-001", CloneState = 6, CloneStateDesc = "Up",
            Original = "SIP/200-001", OriginalState = 6, OriginalStateDesc = "Up"
        };
        evt.Clone.Should().Be("SIP/100-001");
        evt.CloneState.Should().Be(6);
        evt.CloneStateDesc.Should().Be("Up");
        evt.Original.Should().Be("SIP/200-001");
        evt.OriginalState.Should().Be(6);
        evt.OriginalStateDesc.Should().Be("Up");
    }

    [Fact]
    public void ModuleLoadReportEvent_ShouldHaveAllProperties()
    {
        var evt = new ModuleLoadReportEvent { ModuleLoadStatus = "Done", ModuleCount = 150, ModuleSelection = "All" };
        evt.ModuleLoadStatus.Should().Be("Done");
        evt.ModuleCount.Should().Be(150);
        evt.ModuleSelection.Should().Be("All");
    }

    [Fact]
    public void NewCallerIdEvent_ShouldHaveAllProperties()
    {
        var evt = new NewCallerIdEvent
        {
            CidCallingPres = 0, CidCallingPresTxt = "Presentation Allowed",
            LinkedId = "linked-1"
        };
        evt.CidCallingPres.Should().Be(0);
        evt.CidCallingPresTxt.Should().Be("Presentation Allowed");
        evt.LinkedId.Should().Be("linked-1");
    }

    [Fact]
    public void RegistryEntryEvent_ShouldHaveAllProperties()
    {
        var evt = new RegistryEntryEvent
        {
            Host = "sip.provider.com", Port = 5060, Username = "user",
            Refresh = 120, State = "Registered", RegistrationTime = 1234567890
        };
        evt.Host.Should().Be("sip.provider.com");
        evt.Port.Should().Be(5060);
        evt.Username.Should().Be("user");
        evt.Refresh.Should().Be(120);
        evt.State.Should().Be("Registered");
        evt.RegistrationTime.Should().Be(1234567890);
    }

    [Fact]
    public void ExtensionStatusEvent_ShouldHaveAllProperties()
    {
        var evt = new ExtensionStatusEvent
        {
            Status = 0, Hint = "PJSIP/100", CallerId = "5551234", Statustext = "Idle"
        };
        evt.Status.Should().Be(0);
        evt.Hint.Should().Be("PJSIP/100");
        evt.CallerId.Should().Be("5551234");
        evt.Statustext.Should().Be("Idle");
    }

    [Fact]
    public void ZapShowChannelsEvent_ShouldHaveAllProperties()
    {
#pragma warning disable CS0618
        var evt = new ZapShowChannelsEvent { Channel = 1, Signalling = "FXO Kewlstart", Dnd = false, Alarm = "No Alarm" };
#pragma warning restore CS0618
        evt.Channel.Should().Be(1);
        evt.Signalling.Should().Be("FXO Kewlstart");
        evt.Dnd.Should().BeFalse();
        evt.Alarm.Should().Be("No Alarm");
    }

    [Fact]
    public void SkypeLicenseEvent_ShouldHaveAllProperties()
    {
#pragma warning disable CS0618
        var evt = new SkypeLicenseEvent
        {
            Key = "ABCD-1234", Expires = "2030-01-01", HostId = "host-1",
            Channels = 10, Status = "Active"
        };
#pragma warning restore CS0618
        evt.Key.Should().Be("ABCD-1234");
        evt.Expires.Should().Be("2030-01-01");
        evt.HostId.Should().Be("host-1");
        evt.Channels.Should().Be(10);
        evt.Status.Should().Be("Active");
    }

    [Fact]
    public void ChannelsHungupListComplete_ShouldBeInstantiable()
    {
        var evt = new ChannelsHungupListComplete();
        evt.Should().NotBeNull();
    }

    [Fact]
    public void FAXSessionsCompleteEvent_ShouldBeInstantiable()
    {
        var evt = new FAXSessionsCompleteEvent();
        evt.Should().NotBeNull();
    }

    [Fact]
    public void SkypeBuddyListCompleteEvent_ShouldBeInstantiable()
    {
#pragma warning disable CS0618
        var evt = new SkypeBuddyListCompleteEvent();
#pragma warning restore CS0618
        evt.Should().NotBeNull();
    }

    [Fact]
    public void AgentsCompleteEvent_ShouldHaveProperties()
    {
        var evt = new AgentsCompleteEvent { EventList = "Complete", ListItems = 5 };
        evt.EventList.Should().Be("Complete");
        evt.ListItems.Should().Be(5);
    }

    [Fact]
    public void ContactListComplete_ShouldHaveProperties()
    {
        var evt = new ContactListComplete { EventList = "Complete", ListItems = 3 };
        evt.EventList.Should().Be("Complete");
        evt.ListItems.Should().Be(3);
    }

    [Fact]
    public void CoreShowChannelsCompleteEvent_ShouldHaveProperties()
    {
        var evt = new CoreShowChannelsCompleteEvent { Eventlist = "Complete", Listitems = 10 };
        evt.Eventlist.Should().Be("Complete");
        evt.Listitems.Should().Be(10);
    }

    [Fact]
    public void DahdiShowChannelsCompleteEvent_ShouldHaveProperties()
    {
        var evt = new DahdiShowChannelsCompleteEvent { Eventlist = "Complete", Listitems = 24, Items = 24 };
        evt.Eventlist.Should().Be("Complete");
        evt.Listitems.Should().Be(24);
        evt.Items.Should().Be(24);
    }

    [Fact]
    public void EndpointDetailComplete_ShouldHaveProperties()
    {
        var evt = new EndpointDetailComplete { EventList = "Complete", ListItems = 5 };
        evt.EventList.Should().Be("Complete");
        evt.ListItems.Should().Be(5);
    }

    [Fact]
    public void EndpointListComplete_ShouldHaveProperties()
    {
        var evt = new EndpointListComplete { EventList = "Complete", ListItems = 8 };
        evt.EventList.Should().Be("Complete");
        evt.ListItems.Should().Be(8);
    }

    [Fact]
    public void PeerlistCompleteEvent_ShouldHaveProperties()
    {
        var evt = new PeerlistCompleteEvent { EventList = "Complete", ListItems = 15 };
        evt.EventList.Should().Be("Complete");
        evt.ListItems.Should().Be(15);
    }

    [Fact]
    public void QueueRuleListCompleteEvent_ShouldHaveProperties()
    {
        var evt = new QueueRuleListCompleteEvent { EventList = "Complete" };
        evt.EventList.Should().Be("Complete");
    }

    [Fact]
    public void QueueStatusCompleteEvent_ShouldHaveProperties()
    {
        var evt = new QueueStatusCompleteEvent { EventList = "Complete" };
        evt.EventList.Should().Be("Complete");
    }

    [Fact]
    public void QueueSummaryCompleteEvent_ShouldHaveProperties()
    {
        var evt = new QueueSummaryCompleteEvent { Eventlist = "Complete", Listitems = 5 };
        evt.Eventlist.Should().Be("Complete");
        evt.Listitems.Should().Be(5);
    }

    [Fact]
    public void RegistrationsCompleteEvent_ShouldHaveProperties()
    {
        var evt = new RegistrationsCompleteEvent { EventList = "Complete", ListItems = 2 };
        evt.EventList.Should().Be("Complete");
        evt.ListItems.Should().Be(2);
    }

    [Fact]
    public void StatusCompleteEvent_ShouldHaveAllProperties()
    {
        var evt = new StatusCompleteEvent { EventList = "Complete", ListItems = 5, Items = 5 };
        evt.EventList.Should().Be("Complete");
        evt.ListItems.Should().Be(5);
        evt.Items.Should().Be(5);
    }

    [Fact]
    public void DongleShowDevicesCompleteEvent_ShouldHaveProperties()
    {
        var evt = new DongleShowDevicesCompleteEvent { Eventlist = "Complete", Listitems = 2, Items = 2 };
        evt.Eventlist.Should().Be("Complete");
        evt.Listitems.Should().Be(2);
        evt.Items.Should().Be(2);
    }

    [Fact]
    public void DahdiShowChannelsEvent_ShouldHaveAllProperties()
    {
        var evt = new DahdiShowChannelsEvent
        {
            Channel = "DAHDI/1", Signalling = "FXO Kewlstart", Alarm = "No Alarm",
            Dahdichannel = 1, Signallingcode = "20", Uniqueid = "uid-1",
            Dnd = false, Accountcode = "acc1"
        };
        evt.Channel.Should().Be("DAHDI/1");
        evt.Signalling.Should().Be("FXO Kewlstart");
        evt.Alarm.Should().Be("No Alarm");
        evt.Dahdichannel.Should().Be(1);
        evt.Dnd.Should().BeFalse();
        evt.Accountcode.Should().Be("acc1");
    }

    [Fact]
    public void ConfbridgeListEvent_ShouldHaveAllProperties()
    {
        var evt = new ConfbridgeListEvent
        {
            Conference = "1000", Admin = true, MarkedUser = false,
            Waitmarked = "No", Endmarked = "No", Waiting = "No",
            Muted = "No", Talking = "Yes", Answeredtime = 300,
            Channel = "SIP/100-001", Linkedid = "linked-1",
            Language = "en", Uniqueid = "uid-1", Accountcode = "acc1"
        };
        evt.Conference.Should().Be("1000");
        evt.Admin.Should().BeTrue();
        evt.MarkedUser.Should().BeFalse();
        evt.Waitmarked.Should().Be("No");
        evt.Endmarked.Should().Be("No");
        evt.Waiting.Should().Be("No");
        evt.Muted.Should().Be("No");
        evt.Talking.Should().Be("Yes");
        evt.Answeredtime.Should().Be(300);
        evt.Accountcode.Should().Be("acc1");
    }

    [Fact]
    public void AorDetail_ShouldHaveAllProperties()
    {
        var evt = new AorDetail
        {
            ObjectName = "100", MinimumExpiration = 60, MaximumExpiration = 3600,
            DefaultExpiration = 120, MaxContacts = 1, TotalContacts = 1,
            ContactsRegistered = 1, EndpointName = "100"
        };
        evt.ObjectName.Should().Be("100");
        evt.MinimumExpiration.Should().Be(60);
        evt.MaximumExpiration.Should().Be(3600);
        evt.DefaultExpiration.Should().Be(120);
        evt.MaxContacts.Should().Be(1);
        evt.TotalContacts.Should().Be(1);
        evt.ContactsRegistered.Should().Be(1);
        evt.EndpointName.Should().Be("100");
    }

    [Fact]
    public void AgentCallbackLogoffEvent_ShouldHaveAllProperties()
    {
        var evt = new AgentCallbackLogoffEvent { LoginChan = "SIP/100", Reason = "logout", LoginTime = 3600 };
        evt.LoginChan.Should().Be("SIP/100");
        evt.Reason.Should().Be("logout");
        evt.LoginTime.Should().Be(3600);
    }

    [Fact]
    public void OriginateResponseEvent_ShouldHaveAllProperties()
    {
        var evt = new OriginateResponseEvent
        {
            Response = "Success", Channel = "PJSIP/100-001",
            Reason = 4, Data = "some-data", Application = "Dial"
        };
        evt.Response.Should().Be("Success");
        evt.Channel.Should().Be("PJSIP/100-001");
        evt.Reason.Should().Be(4);
        evt.Data.Should().Be("some-data");
        evt.Application.Should().Be("Dial");
    }

    [Fact]
    public void QueueEntryEvent_ShouldHaveAllProperties()
    {
        var evt = new QueueEntryEvent
        {
            Queue = "sales", Position = 1, Channel = "SIP/100-001",
            Uniqueid = "uid-1", CallerId = "5551234", CallerIDName = "John",
            ConnectedLineNum = "5559876", ConnectedLineName = "Jane",
            Wait = 30, CallerIDNum = "5551234"
        };
        evt.Queue.Should().Be("sales");
        evt.Position.Should().Be(1);
        evt.Channel.Should().Be("SIP/100-001");
        evt.Uniqueid.Should().Be("uid-1");
        evt.CallerId.Should().Be("5551234");
        evt.CallerIDName.Should().Be("John");
        evt.ConnectedLineNum.Should().Be("5559876");
        evt.ConnectedLineName.Should().Be("Jane");
        evt.Wait.Should().Be(30);
        evt.CallerIDNum.Should().Be("5551234");
    }

    [Fact]
    public void TransferEvent_ShouldHaveAllProperties()
    {
        var evt = new TransferEvent
        {
            TransferMethod = "SIP", TransferType = "Blind",
            TargetChannel = "SIP/200-001", TargetUniqueId = "uid-2",
            SipCallId = "call-1", TransferExten = "200",
            TransferContext = "default", Transfer2Parking = false
        };
        evt.TransferMethod.Should().Be("SIP");
        evt.TransferType.Should().Be("Blind");
        evt.TargetChannel.Should().Be("SIP/200-001");
        evt.TargetUniqueId.Should().Be("uid-2");
        evt.SipCallId.Should().Be("call-1");
        evt.TransferExten.Should().Be("200");
        evt.TransferContext.Should().Be("default");
        evt.Transfer2Parking.Should().BeFalse();
    }

    [Fact]
    public void ListDialplanEvent_ShouldHaveAllProperties()
    {
        var evt = new ListDialplanEvent
        {
            Context = "default", Extension = "100", Priority = 1,
            Application = "Dial", AppData = "PJSIP/100,30",
            Registrar = "pbx_config", IncludeContext = "includes"
        };
        evt.Context.Should().Be("default");
        evt.Extension.Should().Be("100");
        evt.Priority.Should().Be(1);
        evt.Application.Should().Be("Dial");
        evt.AppData.Should().Be("PJSIP/100,30");
        evt.Registrar.Should().Be("pbx_config");
        evt.IncludeContext.Should().Be("includes");
    }

    [Fact]
    public void TransportDetail_ShouldHaveAllProperties()
    {
        var evt = new TransportDetail
        {
            PbjectName = "transport-udp", Protocol = "udp", Bind = "0.0.0.0:5060",
            AsycOperations = 1, CaListFile = "/etc/ssl/ca.pem",
            CertFile = "/etc/ssl/cert.pem", PrivKeyFile = "/etc/ssl/key.pem",
            Password = "secret", ExternalSignalingAddress = "1.2.3.4",
            ExternalSignalingPort = 5060, ExternalMediaAddress = "1.2.3.4",
            Domain = "example.com", Cos = 3, Tos = "cs3"
        };
        evt.PbjectName.Should().Be("transport-udp");
        evt.Protocol.Should().Be("udp");
        evt.Bind.Should().Be("0.0.0.0:5060");
        evt.AsycOperations.Should().Be(1);
        evt.CaListFile.Should().Be("/etc/ssl/ca.pem");
        evt.CertFile.Should().Be("/etc/ssl/cert.pem");
        evt.PrivKeyFile.Should().Be("/etc/ssl/key.pem");
        evt.Password.Should().Be("secret");
        evt.ExternalSignalingAddress.Should().Be("1.2.3.4");
        evt.ExternalSignalingPort.Should().Be(5060);
        evt.ExternalMediaAddress.Should().Be("1.2.3.4");
        evt.Domain.Should().Be("example.com");
        evt.Cos.Should().Be(3);
        evt.Tos.Should().Be("cs3");
    }

    [Fact]
    public void DAHDIChannelEvent_ShouldHaveAllProperties()
    {
        var evt = new DAHDIChannelEvent
        {
            Dahdichannel = "1", Dahdispan = "1", Dahdigroup = 1,
            Channel = "DAHDI/1", Uniqueid = "uid-1"
        };
        evt.Dahdichannel.Should().Be("1");
        evt.Dahdispan.Should().Be("1");
        evt.Dahdigroup.Should().Be(1);
    }

    [Fact]
    public void BridgeMergeEvent_ShouldHaveAllProperties()
    {
        var evt = new BridgeMergeEvent
        {
            ToBridgeUniqueId = "br-1", FromBridgeUniqueId = "br-2",
            ToBridgeNumChannels = 2, FromBridgeNumChannels = 3,
            ToBridgeName = "to-bridge", FromBridgeName = "from-bridge"
        };
        evt.ToBridgeUniqueId.Should().Be("br-1");
        evt.FromBridgeUniqueId.Should().Be("br-2");
        evt.ToBridgeNumChannels.Should().Be(2);
        evt.FromBridgeNumChannels.Should().Be(3);
        evt.ToBridgeName.Should().Be("to-bridge");
        evt.FromBridgeName.Should().Be("from-bridge");
    }
}
