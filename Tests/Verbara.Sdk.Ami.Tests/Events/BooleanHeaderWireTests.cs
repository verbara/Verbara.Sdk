using System.IO.Pipelines;
using System.Text;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Generated;
using Verbara.Sdk.Ami.Internal;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Ami.Tests.Events;

/// <summary>
/// A header the SDK types <c>bool?</c> reads the value Asterisk sent, in Asterisk's own true/false vocabulary.
/// Each frame goes through the protocol reader and the generated event deserializer, as on a live connection.
/// </summary>
/// <remarks>
/// The captured frames are pasted verbatim from Asterisk 22.10.1; the 20.20.1 and 23.4.1 captures of the same
/// probe carry the same values for every header asserted here. The protocol reader trims the space after an
/// empty header's colon, so the frames are written without trailing spaces.
/// </remarks>
public sealed class BooleanHeaderWireTests
{
    // ConfbridgeListRooms after ConfbridgeLock — Asterisk 20.20.1, 22.10.1, 23.4.1.
    private const string ListRoomsLocked = """
        Event: ConfbridgeListRooms
        ActionID: rooms-1
        Conference: 100
        Parties: 3
        Marked: 1
        Locked: Yes
        Muted: No
        """;

    // ConfbridgeListRooms after ConfbridgeUnlock — Asterisk 20.20.1, 22.10.1, 23.4.1.
    private const string ListRoomsUnlocked = """
        Event: ConfbridgeListRooms
        ActionID: rooms-2
        Conference: 100
        Parties: 3
        Marked: 1
        Locked: No
        Muted: No
        """;

    // ConfbridgeList row of a participant joined with an admin, marked profile — Asterisk 20.20.1, 22.10.1, 23.4.1.
    private const string ListAdminMarkedRow = """
        Event: ConfbridgeList
        ActionID: list-1
        Conference: 100
        Admin: Yes
        MarkedUser: Yes
        WaitMarked: No
        EndMarked: No
        EndMarkedAny: No
        Waiting: No
        Muted: Yes
        Talking: No
        AnsweredTime: 6
        Channel: Local/admin@probe-00000000;2
        ChannelState: 6
        ChannelStateDesc: Up
        CallerIDNum: <unknown>
        CallerIDName: <unknown>
        ConnectedLineNum: <unknown>
        ConnectedLineName: <unknown>
        Language: en
        AccountCode:
        Context: probe
        Exten: admin
        Priority: 2
        Uniqueid: 1792109177.1
        Linkedid: 1792109177.0
        """;

    // ConfbridgeList row of a plain participant — Asterisk 20.20.1, 22.10.1, 23.4.1.
    private const string ListPlainRow = """
        Event: ConfbridgeList
        ActionID: list-1
        Conference: 100
        Admin: No
        MarkedUser: No
        WaitMarked: No
        EndMarked: No
        EndMarkedAny: No
        Waiting: No
        Muted: No
        Talking: Yes
        AnsweredTime: 3
        Channel: Local/conf@probe-00000003;2
        ChannelState: 6
        ChannelStateDesc: Up
        CallerIDNum: <unknown>
        CallerIDName: <unknown>
        ConnectedLineNum: <unknown>
        ConnectedLineName: <unknown>
        Language: en
        AccountCode:
        Context: probe
        Exten: conf
        Priority: 2
        Uniqueid: 1792109181.7
        Linkedid: 1792109181.6
        """;

    // ConfbridgeTalking when the participant starts talking — Asterisk 20.20.1, 22.10.1, 23.4.1.
    private const string TalkingOn = """
        Event: ConfbridgeTalking
        Privilege: call,all
        Conference: 100
        BridgeUniqueid: 1c1c18c5-eca7-4523-bdcc-e0823653eb5f
        BridgeType: base
        BridgeTechnology: softmix
        BridgeCreator: ConfBridge
        BridgeName: 100
        BridgeNumChannels: 4
        BridgeVideoSourceMode: none
        Channel: Local/conf@probe-00000003;2
        ChannelState: 6
        ChannelStateDesc: Up
        CallerIDNum: <unknown>
        CallerIDName: <unknown>
        ConnectedLineNum: <unknown>
        ConnectedLineName: <unknown>
        Language: en
        AccountCode:
        Context: probe
        Exten: conf
        Priority: 2
        Uniqueid: 1792109181.7
        Linkedid: 1792109181.6
        TalkingStatus: on
        Admin: No
        """;

    // ConfbridgeTalking when the same participant stops talking — Asterisk 20.20.1, 22.10.1, 23.4.1.
    private const string TalkingOff = """
        Event: ConfbridgeTalking
        Privilege: call,all
        Conference: 100
        BridgeUniqueid: 1c1c18c5-eca7-4523-bdcc-e0823653eb5f
        BridgeType: base
        BridgeTechnology: softmix
        BridgeCreator: ConfBridge
        BridgeName: 100
        BridgeNumChannels: 4
        BridgeVideoSourceMode: none
        Channel: Local/conf@probe-00000003;2
        ChannelState: 6
        ChannelStateDesc: Up
        CallerIDNum: <unknown>
        CallerIDName: <unknown>
        ConnectedLineNum: <unknown>
        ConnectedLineName: <unknown>
        Language: en
        AccountCode:
        Context: probe
        Exten: conf
        Priority: 2
        Uniqueid: 1792109181.7
        Linkedid: 1792109181.6
        TalkingStatus: off
        Admin: No
        """;

    // VoicemailUsersList entry for a box configured with every option on — Asterisk 20.20.1, 22.10.1, 23.4.1.
    private const string VoicemailAllOn = """
        Event: VoicemailUserEntry
        ActionID: vm
        VMContext: default
        VoiceMailbox: 1000
        Fullname: Probe Yes
        Email:
        Pager:
        ServerEmail: asterisk
        FromString:
        MailCommand: /usr/sbin/sendmail -t
        Language:
        TimeZone:
        Callback:
        Dialout:
        UniqueID:
        ExitContext:
        SayDurationMinimum: 2
        SayEnvelope: Yes
        SayCID: Yes
        AttachMessage: Yes
        AttachmentFormat:
        DeleteMessage: Yes
        VolumeGain: 0.00
        CanReview: Yes
        CanMarkUrgent: Yes
        CallOperator: Yes
        MaxMessageCount: 100
        MaxMessageLength: 0
        NewMessageCount: 0
        OldMessageCount: 0
        """;

    // VoicemailUsersList entry for a box configured with every option off — Asterisk 20.20.1, 22.10.1, 23.4.1.
    private const string VoicemailAllOff = """
        Event: VoicemailUserEntry
        ActionID: vm
        VMContext: default
        VoiceMailbox: 1001
        Fullname: Probe No
        Email:
        Pager:
        ServerEmail: asterisk
        FromString:
        MailCommand: /usr/sbin/sendmail -t
        Language:
        TimeZone:
        Callback:
        Dialout:
        UniqueID:
        ExitContext:
        SayDurationMinimum: 2
        SayEnvelope: No
        SayCID: No
        AttachMessage: No
        AttachmentFormat:
        DeleteMessage: No
        VolumeGain: 0.00
        CanReview: No
        CanMarkUrgent: Yes
        CallOperator: No
        MaxMessageCount: 100
        MaxMessageLength: 0
        NewMessageCount: 0
        OldMessageCount: 0
        """;

    // IAXpeerlist entry of a dynamic, non-trunk IAX peer — Asterisk 20.20.1, 22.10.1, 23.4.1 (identical).
    private const string IaxPeerEntry = """
        Event: PeerEntry
        ActionID: IAXpeerlist
        Channeltype: IAX
        ObjectName: peerone
        ChanObjectType: peer
        IPaddress: (null)
        Mask: (null)
        Port: (null)
        Dynamic: yes
        Trunk: no
        Encryption: no
        Status: Unmonitored
        """;

    // PJSIPShowEndpoint transport of a UDP transport — Asterisk 20.20.1, 22.10.1, 23.4.1.
    private const string UdpTransportDetail = """
        Event: TransportDetail
        ActionID: ep-yes
        ObjectType: transport
        ObjectName: udp-t
        WebsocketWriteTimeout: 100
        Cos: 0
        TcpKeepaliveEnable: false
        Protocol: udp
        SymmetricTransport: false
        Domain:
        VerifyServer: No
        LocalNet:
        Bind: 0.0.0.0:5060
        AllowWildcardCerts: No
        TcpKeepaliveIntervalTime: 1
        AsyncOperations: 1
        ExternalMediaAddress:
        Method: unspecified
        ExternalSignalingAddress:
        RequireClientCert: No
        TcpKeepaliveIdleTime: 30
        Tos: 0
        CaListPath:
        Cipher:
        CertFile:
        ExternalSignalingPort: 0
        Password:
        AllowReload: false
        VerifyClient: No
        TcpKeepaliveProbeCount: 5
        PrivKeyFile:
        CaListFile:
        EndpointName: probe-ep
        """;

    // LocalOptimizationEnd of a swap optimization that completed (both Local halves hung up right after and the
    // two sinks stayed bridged) — Asterisk 20.20.1, 22.10.1, 23.4.1. Asterisk reports a completed swap as No.
    private const string LocalOptimizationSwapCompleted = """
        Event: LocalOptimizationEnd
        Privilege: call,all
        LocalOneChannel: Local/opt@probe-00000004;1
        LocalOneChannelState: 6
        LocalOneChannelStateDesc: Up
        LocalOneCallerIDNum: <unknown>
        LocalOneCallerIDName: <unknown>
        LocalOneConnectedLineNum: <unknown>
        LocalOneConnectedLineName: <unknown>
        LocalOneLanguage: en
        LocalOneAccountCode:
        LocalOneContext: probe
        LocalOneExten: optpeer
        LocalOnePriority: 1
        LocalOneUniqueid: 1792109194.8
        LocalOneLinkedid: 1792109194.8
        LocalTwoChannel: Local/opt@probe-00000004;2
        LocalTwoChannelState: 6
        LocalTwoChannelStateDesc: Up
        LocalTwoCallerIDNum: <unknown>
        LocalTwoCallerIDName: <unknown>
        LocalTwoConnectedLineNum: <unknown>
        LocalTwoConnectedLineName: <unknown>
        LocalTwoLanguage: en
        LocalTwoAccountCode:
        LocalTwoContext: probe
        LocalTwoExten: opt
        LocalTwoPriority: 2
        LocalTwoUniqueid: 1792109194.9
        LocalTwoLinkedid: 1792109194.8
        Success: No
        Id: 0
        """;

    // ---- Captured: a true value ----

    [Fact]
    public async Task Deserialize_ShouldReadLockedTrue_WhenConfbridgeListRoomsSaysYes()
    {
        var evt = await ParseAsync(ListRoomsLocked);

        evt.Should().BeOfType<ConfbridgeListRoomsEvent>().Which.Locked.Should().BeTrue();
    }

    [Fact]
    public async Task Deserialize_ShouldReadAdminAndMarkedUserTrue_WhenConfbridgeListRowSaysYes()
    {
        var row = (await ParseAsync(ListAdminMarkedRow)).Should().BeOfType<ConfbridgeListEvent>().Subject;

        using (new AssertionScope())
        {
            row.Admin.Should().BeTrue();
            row.MarkedUser.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Deserialize_ShouldReadTalkingStatusTrue_WhenConfbridgeTalkingSaysOn()
    {
        var evt = await ParseAsync(TalkingOn);

        evt.Should().BeOfType<ConfbridgeTalkingEvent>().Which.TalkingStatus.Should().BeTrue();
    }

    [Fact]
    public async Task Deserialize_ShouldReadEveryVoicemailOptionTrue_WhenVoicemailUserEntrySaysYes()
    {
        var box = (await ParseAsync(VoicemailAllOn)).Should().BeOfType<VoicemailUserEntryEvent>().Subject;

        using (new AssertionScope())
        {
            box.SayEnvelope.Should().BeTrue();
            box.SayCid.Should().BeTrue();
            box.AttachMessage.Should().BeTrue();
            box.DeleteMessage.Should().BeTrue();
            box.CanReview.Should().BeTrue();
            box.CallOperator.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Deserialize_ShouldReadDynamicTrue_WhenIaxPeerEntrySaysLowerCaseYes()
    {
        var evt = await ParseAsync(IaxPeerEntry);

#pragma warning disable CS0618 // PeerEntryEvent is marked obsolete; IAX still sends it.
        evt.Should().BeOfType<PeerEntryEvent>().Which.Dynamic.Should().BeTrue();
#pragma warning restore CS0618
    }

    // ---- Captured: a false value ----

    [Fact]
    public async Task Deserialize_ShouldReadLockedFalse_WhenConfbridgeListRoomsSaysNo()
    {
        var evt = await ParseAsync(ListRoomsUnlocked);

        evt.Should().BeOfType<ConfbridgeListRoomsEvent>().Which.Locked.Should().BeFalse();
    }

    [Fact]
    public async Task Deserialize_ShouldReadAdminAndMarkedUserFalse_WhenConfbridgeListRowSaysNo()
    {
        var row = (await ParseAsync(ListPlainRow)).Should().BeOfType<ConfbridgeListEvent>().Subject;

        using (new AssertionScope())
        {
            row.Admin.Should().BeFalse();
            row.MarkedUser.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Deserialize_ShouldReadTalkingStatusFalse_WhenConfbridgeTalkingSaysOff()
    {
        var evt = await ParseAsync(TalkingOff);

        evt.Should().BeOfType<ConfbridgeTalkingEvent>().Which.TalkingStatus.Should().BeFalse();
    }

    [Fact]
    public async Task Deserialize_ShouldReadEveryVoicemailOptionFalse_WhenVoicemailUserEntrySaysNo()
    {
        var box = (await ParseAsync(VoicemailAllOff)).Should().BeOfType<VoicemailUserEntryEvent>().Subject;

        using (new AssertionScope())
        {
            box.SayEnvelope.Should().BeFalse();
            box.SayCid.Should().BeFalse();
            box.AttachMessage.Should().BeFalse();
            box.DeleteMessage.Should().BeFalse();
            box.CanReview.Should().BeFalse();
            box.CallOperator.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Deserialize_ShouldReadTrunkFalse_WhenIaxPeerEntrySaysLowerCaseNo()
    {
        var evt = await ParseAsync(IaxPeerEntry);

#pragma warning disable CS0618 // PeerEntryEvent is marked obsolete; IAX still sends it.
        evt.Should().BeOfType<PeerEntryEvent>().Which.Trunk.Should().BeFalse();
#pragma warning restore CS0618
    }

    [Fact]
    public async Task Deserialize_ShouldReadRequireClientCertFalse_WhenTransportDetailSaysNo()
    {
        var evt = await ParseAsync(UdpTransportDetail);

        evt.Should().BeOfType<TransportDetail>().Which.RequireClientCert.Should().BeFalse();
    }

    [Fact]
    public async Task Deserialize_ShouldReadSuccessFalseVerbatim_WhenCompletedSwapSaysNo()
    {
        // Asterisk's Success header, read as sent: the SDK does not turn it into an outcome flag.
        var evt = await ParseAsync(LocalOptimizationSwapCompleted);

        evt.Should().BeOfType<LocalOptimizationEndEvent>().Which.Success.Should().BeFalse();
    }

    // ---- Source-only: a true value Asterisk's source renders, not captured ----

    [Fact]
    public async Task Deserialize_ShouldReadRequireClientCertTrue_WhenAsteriskSourceRendersYes()
    {
        // Rendered by Asterisk's source (res_pjsip transport, a TLS transport with require_client_cert=yes);
        // not captured. The captured UDP frame with the one header Asterisk renders as Yes.
        var evt = await ParseAsync(UdpTransportDetail.Replace("RequireClientCert: No", "RequireClientCert: Yes", StringComparison.Ordinal));

        evt.Should().BeOfType<TransportDetail>().Which.RequireClientCert.Should().BeTrue();
    }

    [Fact]
    public async Task Deserialize_ShouldReadSuccessTrue_WhenAsteriskSourceRendersYes()
    {
        // Rendered by Asterisk's source; not captured. For a swap optimization Asterisk's Yes means the move did
        // not complete (a merge reports Yes when it completed); the SDK reports the header as sent.
        var evt = await ParseAsync(LocalOptimizationSwapCompleted.Replace("Success: No", "Success: Yes", StringComparison.Ordinal));

        evt.Should().BeOfType<LocalOptimizationEndEvent>().Which.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Deserialize_ShouldReadEnabledTrue_WhenAsteriskSourceRendersYes()
    {
        // Rendered by Asterisk's source (the logger, when a log channel recovers); not captured.
        var evt = await ParseAsync("""
            Event: LogChannel
            Privilege: system,all
            Channel: messages.log
            Enabled: Yes
            """);

        evt.Should().BeOfType<LogChannelEvent>().Which.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Deserialize_ShouldReadDndTrue_WhenAsteriskSourceRendersYes()
    {
        // Rendered by Asterisk's source (chan_dahdi renders DND as Enabled/Disabled); not captured: chan_dahdi is
        // not built in the supported images.
        var evt = await ParseAsync("""
            Event: DAHDIShowChannels
            ActionID: dahdi
            DAHDIChannel: 1
            Signalling: FXS Kewlstart
            Context: default
            DND: Enabled
            Alarm: No Alarm
            """);

        evt.Should().BeOfType<DahdiShowChannelsEvent>().Which.Dnd.Should().BeTrue();
    }

    [Fact]
    public async Task Deserialize_ShouldReadChanSipPeerFlagsTrue_WhenAsteriskSourceRendersYes()
    {
        // Rendered by chan_sip at 20.20.1; not captured; module outside SDK support.
        var evt = await ParseAsync("""
            Event: PeerEntry
            ActionID: SIPpeers
            Channeltype: SIP
            ObjectName: sippeer
            ChanObjectType: peer
            Dynamic: yes
            Forcerport: yes
            VideoSupport: yes
            TextSupport: yes
            ACL: yes
            Status: Unmonitored
            """);

#pragma warning disable CS0618 // PeerEntryEvent is marked obsolete; the chan_sip SIPpeers list sends it.
        var peer = evt.Should().BeOfType<PeerEntryEvent>().Subject;
#pragma warning restore CS0618
        using (new AssertionScope())
        {
            peer.ForceRport.Should().BeTrue();
            peer.Acl.Should().BeTrue();
            peer.VideoSupport.Should().BeTrue();
            peer.TextSupport.Should().BeTrue();
        }
    }

    // ---- Unknown: a value outside the vocabulary ----

    [Theory]
    [InlineData("maybe")]
    [InlineData("2")]
    [InlineData("")]
    public async Task Deserialize_ShouldReadAdminNull_WhenValueIsOutsideTheVocabulary(string value)
    {
        // The captured admin row with its Admin header replaced by a value no supported Asterisk sends.
        var frame = ListAdminMarkedRow.Replace("Admin: Yes", "Admin: " + value, StringComparison.Ordinal);

        var row = (await ParseAsync(frame)).Should().BeOfType<ConfbridgeListEvent>().Subject;

        row.Admin.Should().BeNull();
    }

    private static async Task<ManagerEvent> ParseAsync(string frame)
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(frame.ReplaceLineEndings("\r\n") + "\r\n\r\n"));
        await pipe.Writer.CompleteAsync();

        var msg = await new AmiProtocolReader(pipe.Reader).ReadMessageAsync();

        msg.Should().NotBeNull();
        return GeneratedEventDeserializer.Deserialize(msg!);
    }
}
