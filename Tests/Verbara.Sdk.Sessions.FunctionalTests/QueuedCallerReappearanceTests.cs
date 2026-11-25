using System.Globalization;
using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Which channel's return forgets the hang-up cause recorded for a call's caller: the channel that waits in the queue,
/// not the call's first channel. An agent's phone dials a customer, the customer answers, the agent blind-transfers the
/// customer into a queue and hangs up, a member's channel rings, and the customer hangs up with a cause other than
/// normal clearing. A first reconnect's reload lists one of the two channels alive again beside the ringing member leg,
/// and a second lists nothing, proving the call gone.
/// </summary>
/// <remarks>
/// The live frames are typed and synthetic, in the order Asterisk sends them for a blind transfer: no capture holds a
/// call that an agent originated and transferred into a queue, and in every captured queue shape the call's first
/// channel is the one that waits, so the two readings agree there. The reloads' <c>Status</c> rows are synthetic as
/// well, parsed by the SDK's own reader: a channel listed alive after its hang-up is in no capture, and Asterisk does not
/// reuse a uniqueid.
/// </remarks>
public sealed class QueuedCallerReappearanceTests
{
    private const string AgentLeg = "1700000000.1";
    private const string CustomerLeg = "1700000000.2";
    private const string MemberLeg = "1700000000.3";
    private const string AgentChannel = "PJSIP/agent1-00000001";
    private const string CustomerChannel = "PJSIP/trunk-00000002";
    private const string MemberChannel = "PJSIP/member1-00000003";
    private const string BridgeId = "bridge-agent-customer";

    /// <summary>
    /// The customer's channel, the one that waits in the queue, listed alive again: the cause recorded for it no longer
    /// says how the caller left, so the reload that proves the call gone ends it as a reload ends a connected call
    /// waiting in a queue, completed with no cause. The agent's channel, the call's first, listed alive again says
    /// nothing about the customer: the customer's cause stands and ends the call failed.
    /// </summary>
    [Theory]
    [InlineData(CustomerLeg, CustomerChannel, CallSessionState.Completed, null)]
    [InlineData(AgentLeg, AgentChannel, CallSessionState.Failed, HangupCause.UserBusy)]
    public async Task CallSession_ShouldForgetTheCallersCauseOnlyWhenTheWaitingChannelReturns_WhenAnAgentsCallTransferredIntoAQueueIsReloaded(
        string returningLeg, string returningChannel, CallSessionState expected, HangupCause? expectedCause)
    {
        var frames = LiveFrames();
        var alive = await StatusAsync((returningChannel, returningLeg, "6", "Up"), (MemberChannel, MemberLeg, "5", "Ringing"));
        var gone = await StatusAsync();
        var queueEnd = await QueueStatusEndAsync();
        var call = new SignalMatrix.Call("agent-transfer-into-queue", frames, AgentLeg, []);

        var run = await SignalMatrix.ReconnectingAsync(call,
            new SignalMatrix.Reconnect(frames.Count, alive, queueEnd),
            new SignalMatrix.Reconnect(frames.Count, gone, queueEnd));

        using var scope = new AssertionScope();
        scope.AddReportable("run", run.Describe);
        run.Session.Should().NotBeNull("premise: the agent's leg opened the call");
        run.Session!.Participants[0].UniqueId.Should().Be(AgentLeg, "premise: the call's first channel is the agent's");
        run.StateAt("reconnected-1").Should().Be(CallSessionState.Queued,
            "premise: the first reload finds the call still waiting in the queue, its member's channel ringing");
        run.Session.State.Should().Be(expected, "only the waiting channel's return forgets the caller's recorded cause");
        run.Session.HangupCause.Should().Be(expectedCause, "the call keeps the waiting customer's cause unless its channel returned");
        run.Endings.Should().ContainSingle("the call ends once").Which.Cause.Should().Be(expectedCause,
            "the ending carries the cause that decided it");
    }

    private static List<ManagerEvent> LiveFrames() =>
    [
        NewChannel(AgentLeg, AgentChannel, "Ring", "from-internal", "5550100"),
        NewChannel(CustomerLeg, CustomerChannel, "Down", "from-internal", null),
        new DialBeginEvent
        {
            EventType = "DialBegin", UniqueId = AgentLeg, Channel = AgentChannel, DestUniqueid = CustomerLeg,
            DestChannel = CustomerChannel, DialString = "trunk/5550100",
        },
        NewState(CustomerLeg, "Up"),
        NewState(AgentLeg, "Up"),
        new BridgeCreateEvent { EventType = "BridgeCreate", BridgeUniqueid = BridgeId, BridgeType = "basic", BridgeTechnology = "simple_bridge" },
        new BridgeEnterEvent { EventType = "BridgeEnter", BridgeUniqueid = BridgeId, UniqueId = CustomerLeg },
        new BridgeEnterEvent { EventType = "BridgeEnter", BridgeUniqueid = BridgeId, UniqueId = AgentLeg },
        new BlindTransferEvent
        {
            EventType = "BlindTransfer", BridgeUniqueid = BridgeId, TransfereeUniqueId = CustomerLeg,
            TransfereeChannel = CustomerChannel, TransfereeContext = "queues", Extension = "support", Result = "Success",
        },
        new BridgeLeaveEvent { EventType = "BridgeLeave", BridgeUniqueid = BridgeId, UniqueId = AgentLeg },
        new BridgeLeaveEvent { EventType = "BridgeLeave", BridgeUniqueid = BridgeId, UniqueId = CustomerLeg },
        Hangup(AgentLeg, HangupCause.NormalClearing),
        new QueueCallerJoinEvent
        {
            EventType = "QueueCallerJoin", UniqueId = CustomerLeg, LinkedId = AgentLeg, Position = 1,
            RawFields = new Dictionary<string, string>
            {
                ["Queue"] = "support", ["Channel"] = CustomerChannel, ["CallerIDNum"] = "5551234",
                ["Uniqueid"] = CustomerLeg, ["Linkedid"] = AgentLeg, ["Position"] = "1",
            },
        },
        NewChannel(MemberLeg, MemberChannel, "Down", "from-queue", null),
        new DialBeginEvent
        {
            EventType = "DialBegin", UniqueId = CustomerLeg, Channel = CustomerChannel, DestUniqueid = MemberLeg,
            DestChannel = MemberChannel, DialString = "member1",
        },
        NewState(MemberLeg, "Ringing"),
        Hangup(CustomerLeg, HangupCause.UserBusy),
    ];

    private static NewChannelEvent NewChannel(string uniqueId, string channel, string state, string context, string? exten) => new()
    {
        EventType = "Newchannel", UniqueId = uniqueId, Channel = channel, ChannelState = state, CallerIdNum = "5551234",
        Context = context, Exten = exten, Priority = 1, Linkedid = AgentLeg,
        RawFields = new Dictionary<string, string>
        {
            ["Channel"] = channel, ["ChannelStateDesc"] = state, ["Uniqueid"] = uniqueId, ["Linkedid"] = AgentLeg,
            ["CallerIDNum"] = "5551234",
        },
    };

    private static NewStateEvent NewState(string uniqueId, string state) =>
        new() { EventType = "Newstate", UniqueId = uniqueId, ChannelState = state };

    private static HangupEvent Hangup(string uniqueId, HangupCause cause) =>
        new() { EventType = "Hangup", UniqueId = uniqueId, Cause = (int)cause };

    /// <summary>A <c>Status</c> answer listing <paramref name="rows"/> (channel, uniqueid, state code, state) and its end.</summary>
    private static async Task<IReadOnlyList<ManagerEvent>> StatusAsync(
        params (string Channel, string UniqueId, string StateCode, string StateDesc)[] rows)
    {
        var text = new StringBuilder("Asterisk Call Manager/11.0.0\r\n");
        foreach (var (channel, uniqueId, stateCode, stateDesc) in rows)
            text.Append("Event: Status\r\nPrivilege: Call\r\nChannel: ").Append(channel)
                .Append("\r\nChannelState: ").Append(stateCode).Append("\r\nChannelStateDesc: ").Append(stateDesc)
                .Append("\r\nUniqueid: ").Append(uniqueId).Append("\r\nLinkedid: ").Append(AgentLeg)
                .Append("\r\nActionID: synthetic-status\r\n\r\n");
        var items = rows.Length.ToString(CultureInfo.InvariantCulture);
        text.Append("Event: StatusComplete\r\nActionID: synthetic-status\r\nEventList: Complete\r\nListItems: ")
            .Append(items).Append("\r\nItems: ").Append(items).Append("\r\n\r\n");
        return await AmiCaptureReplay.ReadCaptureAsync(Encoding.Latin1.GetBytes(text.ToString()));
    }

    /// <summary>A <c>QueueStatus</c> answer with no rows: its list end only.</summary>
    private static async Task<IReadOnlyList<ManagerEvent>> QueueStatusEndAsync() =>
        await AmiCaptureReplay.ReadCaptureAsync(Encoding.Latin1.GetBytes("Asterisk Call Manager/11.0.0\r\n"
            + "Event: QueueStatusComplete\r\nActionID: synthetic-queue-status\r\nEventList: Complete\r\nListItems: 0\r\n\r\n"));
}
