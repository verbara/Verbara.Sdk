using Asterisk.Sdk.Ami.Events;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Events;

public sealed class CoverageEventPropertyTests
{
    [Fact]
    public void QueueSummaryEvent_ShouldHaveAllProperties()
    {
        var evt = new QueueSummaryEvent
        {
            Queue = "sales",
            LoggedIn = 5,
            Available = 3,
            Callers = 2,
            HoldTime = 30,
            TalkTime = 120,
            LongestHoldTime = 45
        };
        evt.Queue.Should().Be("sales");
        evt.LoggedIn.Should().Be(5);
        evt.Available.Should().Be(3);
        evt.Callers.Should().Be(2);
        evt.HoldTime.Should().Be(30);
        evt.TalkTime.Should().Be(120);
        evt.LongestHoldTime.Should().Be(45);
    }

    [Fact]
    public void JitterBufStatsEvent_ShouldHaveAllProperties()
    {
        var evt = new JitterBufStatsEvent
        {
            Owner = "SIP/100",
            Ping = 20,
            LocalJitter = 5,
            LocalJbDelay = 10,
            LocalTotalLost = 0,
            LocalLossPercent = 0,
            LocalDropped = 0,
            Localooo = 0,
            LocalReceived = 1000,
            RemoteJitter = 8,
            RemoteJbDelay = 15,
            RemoteTotalLost = 2,
            RemoteLossPercent = 1,
            RemoteDropped = 1,
            Remoteooo = 0,
            RemoteReceived = 998
        };
        evt.Owner.Should().Be("SIP/100");
        evt.Ping.Should().Be(20);
        evt.LocalJitter.Should().Be(5);
        evt.LocalJbDelay.Should().Be(10);
        evt.LocalReceived.Should().Be(1000);
        evt.RemoteJitter.Should().Be(8);
        evt.RemoteReceived.Should().Be(998);
        evt.RemoteLossPercent.Should().Be(1);
        evt.RemoteDropped.Should().Be(1);
        evt.LocalTotalLost.Should().Be(0);
        evt.LocalLossPercent.Should().Be(0);
        evt.LocalDropped.Should().Be(0);
        evt.Localooo.Should().Be(0);
        evt.RemoteJbDelay.Should().Be(15);
        evt.RemoteTotalLost.Should().Be(2);
        evt.Remoteooo.Should().Be(0);
    }

    [Fact]
    public void RtpSenderStatEvent_ShouldHaveAllProperties()
    {
        var evt = new RtpSenderStatEvent { SentPackets = 5000, SrCount = 10, Rtt = 0.025 };
        evt.SentPackets.Should().Be(5000);
        evt.SrCount.Should().Be(10);
        evt.Rtt.Should().Be(0.025);
    }

    [Fact]
    public void RtcpSentEvent_ShouldHaveAllProperties()
    {
        var evt = new RtcpSentEvent
        {
            FromPort = 10000,
            ToPort = 20000,
            Pt = 200,
            OurSsrc = 123456789,
            SentNtp = 1234567890.0,
            SentRtp = 100,
            SentPackets = 5000,
            SentOctets = 800000,
            CumulativeLoss = 5,
            TheirLastSr = 9876,
            Channel = "SIP/100-0001",
            Language = "en",
            Ssrc = "0x12345",
            LinkedId = "linked-1",
            Report0lsr = "0xABCD",
            Report0Sourcessrc = "0x5678",
            Report0dlsr = 0.015,
            Uniqueid = "unique-1",
            ReportCount = 1,
            Report0CumulativeLost = 2,
            Report0FractionLost = 10,
            Report0iaJitter = 3,
            Report0HighestSequence = 65535,
            AccountCode = "acc-1",
            Mes = 4.5,
            Report0SequenceNumberCycles = "0"
        };
        evt.FromPort.Should().Be(10000);
        evt.ToPort.Should().Be(20000);
        evt.OurSsrc.Should().Be(123456789);
        evt.SentPackets.Should().Be(5000);
        evt.SentOctets.Should().Be(800000);
        evt.Channel.Should().Be("SIP/100-0001");
        evt.ReportCount.Should().Be(1);
        evt.Report0dlsr.Should().Be(0.015);
        evt.Mes.Should().Be(4.5);
    }

    [Fact]
    public void RtcpReceivedEvent_ShouldHaveAllProperties()
    {
        var evt = new RtcpReceivedEvent
        {
            FromPort = 10000,
            ToPort = 20000,
            Pt = 201,
            ReceptionReports = 1,
            SenderSsrc = 111,
            PacketsLost = 3,
            HighestSequence = 65000,
            SequenceNumberCycles = 0,
            LastSr = 0.5,
            Rtt = 0.02,
            RttAsMillseconds = 20,
            Channel = "SIP/200",
            Language = "en",
            Ssrc = "0xABC",
            Report0lsr = "0xDEF",
            SentOctets = 500000,
            Report0Sourcessrc = "0x123",
            Report0dlsr = 0.01,
            Uniqueid = "u-1",
            Report0CumulativeLost = 1,
            Report0FractionLost = 5,
            Report0iaJitter = 2,
            Sentntp = "3000000",
            Sentrtp = 200,
            ReportCount = 1,
            Report0HighestSequence = 64000,
            LinkedId = "link-1",
            SentPackets = 4000,
            AccountCode = "acc-2",
            Report0SequenceNumberCycles = "0",
            Mes = 3.2
        };
        evt.Pt.Should().Be(201);
        evt.SenderSsrc.Should().Be(111);
        evt.PacketsLost.Should().Be(3);
        evt.Rtt.Should().Be(0.02);
        evt.RttAsMillseconds.Should().Be(20);
        evt.ReceptionReports.Should().Be(1);
        evt.HighestSequence.Should().Be(65000);
        evt.SequenceNumberCycles.Should().Be(0);
        evt.LastSr.Should().Be(0.5);
    }

    [Fact]
    public void T38FaxStatusEvent_ShouldHaveAllProperties()
    {
        var evt = new T38FaxStatusEvent
        {
            MaxLag = "100",
            TotalLag = "500",
            AverageLag = "50",
            TotalEvents = 10,
            T38SessionDuration = "30",
            T38PacketsSent = 200,
            T38OctetsSent = 40000,
            AverageTxDataRate = "9600",
            T38PacketsReceived = 180,
            T38OctetsReceived = 36000,
            AverageRxDataRate = "9600",
            JitterBufferOverflows = 0,
            MinimumJitterSpace = 50,
            UnrecoverablePackets = 0,
            TotalLagInMilliSeconds = 500,
            MaxLagInMilliSeconds = 100,
            T38SessionDurationInSeconds = 30.5,
            AverageLagInMilliSeconds = 50.0,
            AverageTxDataRateInBps = 9600,
            AverageRxDataRateInBps = 9600,
            AccountCode = "fax-1",
            Language = "en",
            LinkedId = "link-fax"
        };
        evt.T38PacketsSent.Should().Be(200);
        evt.T38OctetsSent.Should().Be(40000);
        evt.T38PacketsReceived.Should().Be(180);
        evt.JitterBufferOverflows.Should().Be(0);
        evt.T38SessionDurationInSeconds.Should().Be(30.5);
        evt.AverageTxDataRateInBps.Should().Be(9600);
        evt.MaxLagInMilliSeconds.Should().Be(100);
        evt.TotalLagInMilliSeconds.Should().Be(500);
        evt.MinimumJitterSpace.Should().Be(50);
        evt.UnrecoverablePackets.Should().Be(0);
        evt.AverageLagInMilliSeconds.Should().Be(50.0);
        evt.AverageRxDataRateInBps.Should().Be(9600);
    }

    [Fact]
    public void FaxStatusEvent_ShouldHaveAllProperties()
    {
        var evt = new FaxStatusEvent
        {
            OperatingMode = "sending",
            Result = "SUCCESS",
            Error = null,
            CallDuration = 30.5,
            EcmMode = "on",
            DataRate = 14400,
            ImageResolution = "204x196",
            ImageEncoding = "T.4",
            PageSize = "A4",
            DocumentNumber = 1,
            PageNumber = 3,
            FileName = "/tmp/fax.tif",
            TxPages = 3,
            TxBytes = 50000,
            TotalTxLines = 1000,
            RxPages = 0,
            RxBytes = 0,
            TotalRxLines = 0,
            TotalBadLines = 2,
            DisDcsDtcCtcCount = 1,
            CfrCount = 1,
            FttCount = 0,
            McfCount = 3,
            PprCount = 0,
            RtnCount = 0,
            DcnCount = 1,
            RemoteStationId = "5551234",
            LocalStationId = "5555678",
            CallerId = "5551234",
            Status = "success",
            Operation = "send",
            AccountCode = "fax-acc",
            Language = "en",
            LinkedId = "fax-link"
        };
        evt.OperatingMode.Should().Be("sending");
        evt.Result.Should().Be("SUCCESS");
        evt.DataRate.Should().Be(14400);
        evt.TxPages.Should().Be(3);
        evt.TxBytes.Should().Be(50000);
        evt.TotalBadLines.Should().Be(2);
        evt.McfCount.Should().Be(3);
        evt.DcnCount.Should().Be(1);
        evt.RemoteStationId.Should().Be("5551234");
    }

    [Fact]
    public void QueueMemberEvent_ShouldHaveAllProperties()
    {
        var evt = new QueueMemberEvent
        {
            Queue = "sales",
            Interface = "SIP/100",
            Location = "SIP/100",
            Membership = "dynamic",
            Penalty = 0,
            CallsTaken = 15,
            LastCall = 1700000000,
            LastPause = 1699999000,
            Status = 1,
            Paused = false,
            Name = "Agent 100",
            MemberName = "Agent 100",
            Stateinterface = "SIP/100",
            Incall = 0,
            Pausedreason = null,
            Wrapuptime = 10,
            Logintime = 1699990000
        };
        evt.Queue.Should().Be("sales");
        evt.Interface.Should().Be("SIP/100");
        evt.Membership.Should().Be("dynamic");
        evt.CallsTaken.Should().Be(15);
        evt.LastCall.Should().Be(1700000000);
        evt.LastPause.Should().Be(1699999000);
        evt.Paused.Should().BeFalse();
        evt.Incall.Should().Be(0);
        evt.Wrapuptime.Should().Be(10);
        evt.Logintime.Should().Be(1699990000);
        evt.Location.Should().Be("SIP/100");
        evt.Name.Should().Be("Agent 100");
        evt.MemberName.Should().Be("Agent 100");
        evt.Stateinterface.Should().Be("SIP/100");
    }

    [Fact]
    public void QueueMemberPenaltyEvent_ShouldHaveAllProperties()
    {
        var evt = new QueueMemberPenaltyEvent
        {
            Paused = false,
            Wrapuptime = 5,
            Lastpause = 100,
            Stateinterface = "SIP/100",
            Pausedreason = null,
            Incall = 1,
            Membership = "static",
            Interface = "SIP/100",
            Callstaken = 20,
            Ringinuse = 1,
            Lastcall = 200,
            Membername = "Agent 100",
            Status = 1,
            Queue = "sales",
            Location = "SIP/100",
            Penalty = 10,
            LoginTime = 300
        };
        evt.Penalty.Should().Be(10);
        evt.Queue.Should().Be("sales");
        evt.Callstaken.Should().Be(20);
        evt.Ringinuse.Should().Be(1);
        evt.Incall.Should().Be(1);
        evt.Membership.Should().Be("static");
        evt.Lastpause.Should().Be(100);
        evt.Lastcall.Should().Be(200);
        evt.LoginTime.Should().Be(300);
        evt.Membername.Should().Be("Agent 100");
        evt.Location.Should().Be("SIP/100");
        evt.Wrapuptime.Should().Be(5);
    }

    [Fact]
    public void QueueMemberRingInUseEvent_ShouldHaveAllProperties()
    {
        var evt = new QueueMemberRingInUseEvent
        {
            Paused = true,
            Wrapuptime = 0,
            Lastpause = 0,
            Stateinterface = "SIP/200",
            Pausedreason = "break",
            Incall = 0,
            Membership = "dynamic",
            Interface = "SIP/200",
            Callstaken = 5,
            Ringinuse = 0,
            Lastcall = 0,
            Membername = "Agent 200",
            Status = 5,
            Queue = "support",
            Penalty = 3,
            LoginTime = 400
        };
        evt.Paused.Should().BeTrue();
        evt.Pausedreason.Should().Be("break");
        evt.Queue.Should().Be("support");
        evt.Penalty.Should().Be(3);
        evt.Ringinuse.Should().Be(0);
        evt.LoginTime.Should().Be(400);
        evt.Stateinterface.Should().Be("SIP/200");
    }

    [Fact]
    public void DtmfEndEvent_ShouldHaveProperties()
    {
        var evt = new DtmfEndEvent { DurationMs = 250 };
        evt.DurationMs.Should().Be(250);
    }

    [Fact]
    public void LeaveEvent_ShouldHaveProperties()
    {
#pragma warning disable CS0618 // Obsolete
        var evt = new LeaveEvent { Position = 1 };
#pragma warning restore CS0618
        evt.Position.Should().Be(1);
    }

    [Fact]
    public void ChannelTalkingStopEvent_ShouldHaveProperties()
    {
        var evt = new ChannelTalkingStopEvent { Duration = 5000 };
        evt.Duration.Should().Be(5000);
    }

    [Fact]
    public void MeetMeJoinEvent_ShouldHaveProperties()
    {
#pragma warning disable CS0618 // Obsolete
        var evt = new MeetMeJoinEvent { Meetme = "1000", Usernum = 1, Duration = 0 };
        evt.Meetme.Should().Be("1000");
        evt.Usernum.Should().Be(1);
#pragma warning restore CS0618
    }

    [Fact]
    public void MeetMeLeaveEvent_ShouldHaveProperties()
    {
#pragma warning disable CS0618 // Obsolete
        var evt = new MeetMeLeaveEvent { Meetme = "1000", Usernum = 2, Duration = 120 };
        evt.Meetme.Should().Be("1000");
        evt.Usernum.Should().Be(2);
        evt.Duration.Should().Be(120);
#pragma warning restore CS0618
    }

    [Fact]
    public void MeetMeMuteEvent_ShouldHaveProperties()
    {
#pragma warning disable CS0618 // Obsolete
        var evt = new MeetMeMuteEvent { Meetme = "1000", Usernum = 1, Status = true };
        evt.Meetme.Should().Be("1000");
        evt.Status.Should().BeTrue();
#pragma warning restore CS0618
    }

    [Fact]
    public void MeetMeTalkingEvent_ShouldHaveProperties()
    {
#pragma warning disable CS0618 // Obsolete
        var evt = new MeetMeTalkingEvent { Meetme = "1000", Usernum = 1, Status = true };
        evt.Status.Should().BeTrue();
#pragma warning restore CS0618
    }

    [Fact]
    public void MeetMeTalkingRequestEvent_ShouldHaveProperties()
    {
#pragma warning disable CS0618 // Obsolete
        var evt = new MeetMeTalkingRequestEvent { Meetme = "1000", Usernum = 1, Status = true };
        evt.Status.Should().BeTrue();
#pragma warning restore CS0618
    }
}
