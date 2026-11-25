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
/// leg entering a bridge after an abandon, a caller returned to transferring that hangs up abnormally, a never-connected
/// call whose channel reports again after its visit, or an unhold of a call that is not on hold.
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

    private static UnholdEvent Unhold(string uniqueId) => new() { EventType = "Unhold", UniqueId = uniqueId };

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

    /// <summary>A call the dialplan answers, then queues in <c>q-tmo</c>, which times out; it was never connected.</summary>
    private static ManagerEvent[] AnsweredByTheDialplanThenTimesOut() =>
    [
        NewChannel(Caller, CallerChan, "Ring", Caller, context: "from-trunk", exten: "500"),
        NewState(Caller, "Up"),
        .. VisitTimesOut("q-tmo", RingingLeg),
    ];

    /// <summary>A call queued in <c>q-tmo</c> before anything answered its channel; the queue times out.</summary>
    private static ManagerEvent[] UnansweredThenTimesOut() =>
    [
        NewChannel(Caller, CallerChan, "Ring", Caller, context: "from-trunk", exten: "500"),
        .. VisitTimesOut("q-tmo", RingingLeg),
    ];

    /// <summary>
    /// A never-connected call whose visit ended unanswered ends <see cref="CallSessionState.Failed"/> with no
    /// <see cref="CallSession.ConnectedAt"/>, whatever its channel reports after the visit: the ending of a call the
    /// dialplan answered in its initial state holds only for a call that never entered a queue.
    /// </summary>
    [Theory]
    [InlineData("answered-none")]
    [InlineData("answered-hold")]
    [InlineData("answered-bridge")]
    [InlineData("unanswered-hold-then-answer")]
    [InlineData("unanswered-answer-then-hold")]
    [InlineData("unanswered-answer")]
    public async Task CallSession_ShouldEndFailedWithNoConnection_WhenANeverConnectedCallsVisitEndsUnanswered(string shape)
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(shape.StartsWith("answered", StringComparison.Ordinal)
            ? AnsweredByTheDialplanThenTimesOut()
            : UnansweredThenTimesOut());
        switch (shape)
        {
            case "answered-hold":
                rig.Deliver(Hold(Caller));
                break;
            case "answered-bridge":
                rig.Deliver(BridgeCreate("lone"), BridgeEnter("lone", Caller));
                break;
            case "unanswered-hold-then-answer":
                rig.Deliver(Hold(Caller), NewState(Caller, "Up"));
                break;
            case "unanswered-answer-then-hold":
                rig.Deliver(NewState(Caller, "Up"), Hold(Caller));
                break;
            case "unanswered-answer":
                rig.Deliver(NewState(Caller, "Up"));
                break;
        }
        var session = rig.Session(Caller);

        rig.Deliver(Hangup(Caller));

        using var scope = new AssertionScope();
        session.State.Should().Be(CallSessionState.Failed,
            "a call that entered a queue and that nobody answered is abandoned, whatever its channel reports after");
        session.ConnectedAt.Should().BeNull("nobody answered the call");
        rig.Events.OfType<CallConnectedEvent>().Should().BeEmpty("nothing connected the call");
        rig.Events.OfType<CallEndedEvent>().Should().ContainSingle("the call ends once")
            .Which.FinalState.Should().Be(CallSessionState.Failed);
    }

    [Fact]
    public async Task CallSession_ShouldStayQueued_WhenTheWaitingCallerIsUnheldWithoutAHold()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(
            NewChannel(Caller, CallerChan, "Ring", Caller, context: "from-trunk", exten: "500"),
            Join("q-a", CallerChan, Caller, Caller));
        var session = rig.Session(Caller);

        rig.Deliver(Unhold(Caller));
        var afterUnhold = session.State;
        rig.Deliver(Hangup(Caller));

        using var scope = new AssertionScope();
        afterUnhold.Should().Be(CallSessionState.Queued, "an unhold returns only a call on hold to connected");
        rig.Events.OfType<CallResumedEvent>().Should().BeEmpty("the call was never on hold");
        session.State.Should().Be(CallSessionState.Failed, "a never-connected call that hangs up waiting is abandoned");
        session.ConnectedAt.Should().BeNull("nobody answered the call");
    }

    [Fact]
    public async Task CallSession_ShouldStayQueued_WhenARingingMemberLegIsUnheldWhileTheCallerWaits()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(
            NewChannel(Caller, CallerChan, "Ring", Caller, context: "from-trunk", exten: "500"),
            Join("q-a", CallerChan, Caller, Caller),
            NewChannel(RingingLeg, MemberChannel("PJSIP/agent9", RingingLeg), "Down", Caller, context: "agents"),
            NewState(RingingLeg, "Ringing"));
        var session = rig.Session(Caller);

        rig.Deliver(Unhold(RingingLeg));

        using var scope = new AssertionScope();
        session.State.Should().Be(CallSessionState.Queued, "a member leg's unhold neither connects nor resumes the call");
        rig.Events.OfType<CallResumedEvent>().Should().BeEmpty("the call was never on hold");
        rig.Events.OfType<CallConnectedEvent>().Should().BeEmpty("only app_queue's report connects a queued call");
    }

    [Fact]
    public async Task CallSession_ShouldStayQueued_WhenAMemberLegIsUnheldAfterAnUnansweredVisit()
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
            BridgeEnter("late-bridge", RingingLeg));
        var session = rig.Session(Caller);

        rig.Deliver(Unhold(RingingLeg));
        var afterMemberUnhold = session.State;
        rig.Deliver(Hold(Caller));

        using var scope = new AssertionScope();
        afterMemberUnhold.Should().Be(CallSessionState.Queued,
            "a member leg's unhold returns nothing and resumes nothing");
        rig.Events.OfType<CallResumedEvent>().Should().BeEmpty("the call was never on hold");
        rig.Events.OfType<CallConnectedEvent>().Should().ContainSingle("only q-first's member was announced");
        session.State.Should().Be(CallSessionState.OnHold, "the caller's own hold returns the call, and is handled there");
    }

    [Fact]
    public async Task CallSession_ShouldStayTransferring_WhenTheCallerBackInTransferringIsHeldAndUnheld()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(TransferredIntoAQueueThatTimesOut());
        rig.Deliver(Hold(Caller));
        var session = rig.Session(Caller);
        var afterHold = session.State;

        rig.Deliver(Unhold(Caller));
        var afterUnhold = session.State;
        rig.Deliver(Hangup(Caller));

        using var scope = new AssertionScope();
        afterHold.Should().Be(CallSessionState.Transferring, "premise: the caller's hold returned the call to transferring");
        afterUnhold.Should().Be(CallSessionState.Transferring,
            "the uncounted hold left the call transferring, so its unhold has nothing to resume");
        rig.Events.OfType<CallResumedEvent>().Should().BeEmpty("the call was never on hold");
        rig.Events.OfType<CallConnectedEvent>().Should().ContainSingle("only q-first's member was announced");
        session.State.Should().Be(CallSessionState.Completed,
            "until someone answers, the waiting channel's normal clearing decides the ending");
    }

    [Fact]
    public async Task CallResumedEvent_ShouldBePublished_WhenACallOnHoldIsUnheld()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(QueueCallConnected("q-first", Caller, FirstMemberLeg, FirstMember));
        rig.Deliver(Hold(Caller));
        var session = rig.Session(Caller);
        var afterHold = session.State;

        rig.Deliver(Unhold(Caller));

        using var scope = new AssertionScope();
        afterHold.Should().Be(CallSessionState.OnHold, "premise: the connected call's hold is counted");
        session.State.Should().Be(CallSessionState.Connected, "the unhold of a call on hold returns it to connected");
        rig.Events.OfType<CallResumedEvent>().Should().ContainSingle("the call resumed once");
    }
}
