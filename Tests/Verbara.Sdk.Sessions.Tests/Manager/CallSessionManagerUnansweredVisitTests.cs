using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.Tests.Harness;
using static Verbara.Sdk.Sessions.Tests.Manager.WrapUpFrames;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// A queue visit that ends unanswered: app_queue reports it abandoned (<c>QueueCallerAbandon</c>) and then the caller
/// leaving (<c>QueueCallerLeave</c>). The call waits in <see cref="CallSessionState.Queued"/> until the next report of
/// the channel that waited there, and from that report it is back in the state it had before the visit.
/// </summary>
/// <remarks>
/// Runs on the real manager over <see cref="WrapUpRig"/>'s frames, on <see cref="FakeTimeProvider"/>. The frames are
/// synthetic, in the order the connected-signal-matrix captures show for a queue timeout (the member's ring ends, then
/// the abandon, the leave, and the hang-up of the member's ringing leg): no capture holds two queues in a row, a member
/// leg entering a bridge after an abandon, or a caller returned to transferring that hangs up abnormally.
/// </remarks>
[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class CallSessionManagerUnansweredVisitTests
{
    private const string Caller = "c1";
    private const string FirstMember = "PJSIP/agent1";
    private const string FirstMemberLeg = "m1";
    private const string RingingLeg = "m2";
    private const string OtherRingingLeg = "m3";

    private static string CallerChan => CallerChannel(Caller);

    private static QueueCallerAbandonEvent Abandon(string queue) => new()
    {
        EventType = "QueueCallerAbandon", UniqueId = Caller, LinkedId = Caller,
        RawFields = new Dictionary<string, string>
        {
            ["Queue"] = queue, ["Channel"] = CallerChan, ["Uniqueid"] = Caller, ["Linkedid"] = Caller,
        },
    };

    private static HoldEvent Hold(string uniqueId) => new() { EventType = "Hold", UniqueId = uniqueId };

    private static HangupEvent HangupWith(string uniqueId, HangupCause cause) =>
        new() { EventType = "Hangup", UniqueId = uniqueId, Cause = (int)cause };

    /// <summary>The caller waits in <paramref name="queue"/>, a member's leg rings, and the queue times out.</summary>
    private static ManagerEvent[] VisitTimesOut(string queue, string ringingLeg) =>
    [
        Join(queue, CallerChan, Caller, Caller),
        NewChannel(ringingLeg, MemberChannel("PJSIP/agent9", ringingLeg), "Down", Caller, context: "agents"),
        NewState(ringingLeg, "Ringing"),
        Abandon(queue),
        Leave(queue, CallerChan, Caller, Caller),
        HangupWith(ringingLeg, HangupCause.NotDefined),
    ];

    /// <summary>A call q-first connected to agent1, whose member then hangs up, leaving the caller connected and alone.</summary>
    private static ManagerEvent[] ConnectedAndAlone() =>
    [
        .. QueueCallConnected("q-first", Caller, FirstMemberLeg, FirstMember),
        BridgeLeave(Bridge(Caller), FirstMemberLeg),
        Hangup(FirstMemberLeg),
        AgentComplete("q-first", Caller, CallerChan, Caller, FirstMember, FirstMemberLeg,
            MemberChannel(FirstMember, FirstMemberLeg), 30),
        BridgeLeave(Bridge(Caller), Caller),
    ];

    /// <summary>A call q-first connected to agent1, who blind-transfers it into <c>q-tmo</c>, which times out.</summary>
    private static ManagerEvent[] TransferredIntoAQueueThatTimesOut() =>
    [
        .. QueueCallConnected("q-first", Caller, FirstMemberLeg, FirstMember),
        .. MemberBlindTransfers("q-first", Caller, FirstMemberLeg, FirstMember, "queues", "q-tmo"),
        .. VisitTimesOut("q-tmo", RingingLeg),
    ];

    [Theory]
    [InlineData(HangupCause.NormalClearing, CallSessionState.Completed)]
    [InlineData(HangupCause.UserBusy, CallSessionState.Failed)]
    public async Task CallSession_ShouldEndOnTheWaitingCallersCause_WhenACallBackInTransferringAfterAnUnansweredVisitHangsUp(
        HangupCause callerCause, CallSessionState expected)
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(TransferredIntoAQueueThatTimesOut());
        rig.Deliver(Hold(Caller));
        var session = rig.Session(Caller);
        var returned = session.State;

        rig.Deliver(HangupWith(Caller, callerCause));

        using var scope = new AssertionScope();
        returned.Should().Be(CallSessionState.Transferring, "premise: the caller's hold returned the call to transferring");
        rig.Events.OfType<CallHeldEvent>().Should().BeEmpty("a hold while the call is alone is not counted");
        session.State.Should().Be(expected,
            "until someone answers, the cause of the channel that waited in the queue decides the ending");
        session.HangupCause.Should().Be(callerCause, "the call's cause is the waiting channel's own");
        rig.Events.OfType<CallEndedEvent>().Should().ContainSingle("the call ends once")
            .Which.FinalState.Should().Be(expected);
    }

    [Fact]
    public async Task CallConnectedEvent_ShouldBePublished_WhenADialAnswersACallBackInTransferringAfterAnUnansweredVisit()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(TransferredIntoAQueueThatTimesOut());
        const string overflowLeg = "o1";
        rig.Deliver(
            NewChannel(overflowLeg, "PJSIP/overflow-o1", "Down", Caller, context: "overflow"),
            new DialBeginEvent
            {
                EventType = "DialBegin", UniqueId = Caller, Channel = CallerChan, DestUniqueid = overflowLeg,
                DestChannel = "PJSIP/overflow-o1", DialString = "overflow",
            },
            NewState(overflowLeg, "Up"));
        var session = rig.Session(Caller);

        using var scope = new AssertionScope();
        session.State.Should().Be(CallSessionState.Connected, "the overflow dial's answer connects the call");
        rig.Events.OfType<CallConnectedEvent>().Should().HaveCount(2,
                "q-first's member and the overflow dial's answer are each announced once")
            .And.Subject.Last().QueueName.Should().BeNull("the return from the transfer answers no queue visit");
    }

    [Fact]
    public async Task CallSession_ShouldReturnToTheFirstVisitsPriorState_WhenTwoQueuesInARowEndUnanswered()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(ConnectedAndAlone());
        rig.Deliver(VisitTimesOut("q-second", RingingLeg));
        var session = rig.Session(Caller);
        var betweenVisits = session.State;
        rig.Deliver(VisitTimesOut("q-third", OtherRingingLeg));
        var afterSecond = session.State;

        rig.Deliver(Hold(Caller));

        using var scope = new AssertionScope();
        betweenVisits.Should().Be(CallSessionState.Queued,
            "premise: no report of the caller's channel came between the two queues");
        afterSecond.Should().Be(CallSessionState.Queued, "premise: the call waits until its caller reports again");
        session.QueueName.Should().Be("q-third", "the call names the last queue it joined");
        session.State.Should().Be(CallSessionState.OnHold,
            "the call is back in connected, the state before the first visit of the chain, never queued, and its hold "
            + "is handled there");
        rig.Events.OfType<CallHeldEvent>().Should().ContainSingle("the hold of a call back in connected is counted");
    }

    [Fact]
    public async Task CallSession_ShouldStayQueued_WhenAMemberLegEntersABridgeAfterAnUnansweredVisit()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(ConnectedAndAlone());
        rig.Deliver(
            Join("q-second", CallerChan, Caller, Caller),
            NewChannel(RingingLeg, MemberChannel("PJSIP/agent9", RingingLeg), "Down", Caller, context: "agents"),
            NewState(RingingLeg, "Ringing"),
            Abandon("q-second"),
            Leave("q-second", CallerChan, Caller, Caller),
            NewState(RingingLeg, "Up"),
            BridgeCreate("late-bridge"),
            BridgeEnter("late-bridge", RingingLeg),
            Hold(RingingLeg));
        var session = rig.Session(Caller);
        var announcedBefore = rig.Events.OfType<CallConnectedEvent>().Count();
        var afterMemberFrames = session.State;

        rig.Deliver(Hold(Caller));

        using var scope = new AssertionScope();
        afterMemberFrames.Should().Be(CallSessionState.Queued,
            "only a report of the channel that waited in the queue returns the call: a member leg's answer, bridge "
            + "entry and hold do not");
        announcedBefore.Should().Be(1, "only q-first's member was announced; the late member leg connects nothing");
        session.State.Should().Be(CallSessionState.OnHold, "the caller's own hold returns the call, and is handled there");
    }

    /// <summary>
    /// A declared limit: a visit followed by no report of the caller's channel (a voicemail after the queue) leaves the
    /// call queued to its end, which then follows the ending of a call that hangs up waiting.
    /// </summary>
    [Fact]
    public async Task CallSession_ShouldStayQueuedToItsEnd_WhenTheCallerSendsNoReportAfterAnUnansweredVisit()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(ConnectedAndAlone());
        rig.Deliver(VisitTimesOut("q-second", RingingLeg));
        var session = rig.Session(Caller);
        var beforeEnd = session.State;

        rig.Deliver(Hangup(Caller));

        using var scope = new AssertionScope();
        beforeEnd.Should().Be(CallSessionState.Queued, "no report of the caller's channel came after the leave");
        session.State.Should().Be(CallSessionState.Completed,
            "a call that was connected and hangs up normally while still queued completed");
    }
}
