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
/// call whose channel reports again after its visit (one dialing before it included, and the overflow dial that answers
/// it), an unhold of a call that is not on hold, or a held call blind-transferred and then unheld.
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

    private static DialBeginEvent DialBegin(string destUniqueId, string destChannel) => new()
    {
        EventType = "DialBegin", UniqueId = Caller, Channel = CallerChan, DestUniqueid = destUniqueId,
        DestChannel = destChannel, DialString = destChannel,
    };

    private static DialEndEvent DialEnd(string destUniqueId, string destChannel, string dialStatus) => new()
    {
        EventType = "DialEnd", UniqueId = Caller, Channel = CallerChan, DestUniqueid = destUniqueId,
        DestChannel = destChannel, DialStatus = dialStatus,
    };

    /// <summary>
    /// The caller dials an extension that does not answer (the call is then <see cref="CallSessionState.Ringing"/>, or
    /// still <see cref="CallSessionState.Dialing"/> when the extension never rings), and then waits in <c>q-tmo</c>,
    /// which times out.
    /// </summary>
    private static ManagerEvent[] DialFailsThenVisitTimesOut(bool extensionRings) =>
    [
        NewChannel(Caller, CallerChan, "Ring", Caller, context: "from-trunk", exten: "500"),
        NewChannel("d1", "PJSIP/ext-d1", "Down", Caller, context: "ext"),
        DialBegin("d1", "PJSIP/ext-d1"),
        .. (extensionRings ? new ManagerEvent[] { NewState("d1", "Ringing") } : Array.Empty<ManagerEvent>()),
        DialEnd("d1", "PJSIP/ext-d1", "NOANSWER"),
        HangupWith("d1", HangupCause.NoAnswer),
        .. VisitTimesOut("q-tmo", RingingLeg),
    ];

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
    /// <summary>
    /// A never-connected call that was dialing or ringing before its visit is back there after the visit ends
    /// unanswered, and nobody has answered it: its own answer (the dialplan's <c>Answer()</c>) or its entering a bridge
    /// alone does not connect it, so it ends <see cref="CallSessionState.Failed"/> with no
    /// <see cref="CallSession.ConnectedAt"/>.
    /// </summary>
    [Theory]
    [InlineData(true, "none")]
    [InlineData(true, "hold")]
    [InlineData(true, "bridge")]
    [InlineData(true, "answer")]
    [InlineData(true, "hold-then-answer")]
    [InlineData(false, "none")]
    [InlineData(false, "bridge")]
    [InlineData(false, "answer")]
    [InlineData(false, "hold-then-answer")]
    public async Task CallSession_ShouldEndFailedWithNoConnection_WhenACallDialingBeforeItsVisitReportsAfterItEndsUnanswered(
        bool extensionRings, string shape)
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(DialFailsThenVisitTimesOut(extensionRings));
        var session = rig.Session(Caller);
        var afterVisit = session.State;
        switch (shape)
        {
            case "hold":
                rig.Deliver(Hold(Caller));
                break;
            case "bridge":
                rig.Deliver(BridgeCreate("lone"), BridgeEnter("lone", Caller));
                break;
            case "answer":
                rig.Deliver(NewState(Caller, "Up"));
                break;
            case "hold-then-answer":
                rig.Deliver(Hold(Caller), NewState(Caller, "Up"));
                break;
        }
        var afterReport = session.State;

        rig.Deliver(Hangup(Caller));

        using var scope = new AssertionScope();
        afterVisit.Should().Be(CallSessionState.Queued, "premise: the call waits until its caller reports again");
        if (shape == "answer")
            afterReport.Should().Be(CallSessionState.Queued,
                "by choice, the caller's own answer is not a report that returns the call");
        else if (shape != "none")
            afterReport.Should().Be(extensionRings ? CallSessionState.Ringing : CallSessionState.Dialing,
                "the caller's report returns the call to the state it had before the visit, and connects nothing");
        session.State.Should().Be(CallSessionState.Failed,
            "a call that entered a queue and that nobody answered is abandoned, whatever its channel reports after");
        session.ConnectedAt.Should().BeNull("nobody answered the call");
        rig.Events.OfType<CallConnectedEvent>().Should().BeEmpty("nothing connected the call");
        rig.Events.OfType<CallEndedEvent>().Should().ContainSingle("the call ends once")
            .Which.FinalState.Should().Be(CallSessionState.Failed);
    }

    /// <summary>
    /// A never-connected call back in its initial state after an unanswered visit that dials on from there is connected
    /// by the dialled party answering, not by its own channel entering a bridge alone or answering.
    /// </summary>
    [Fact]
    public async Task CallSession_ShouldEndFailedWithNoConnection_WhenACallBackInItsInitialStateDialsAndOnlyItsOwnChannelReports()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(UnansweredThenTimesOut());
        const string overflowLeg = "o1";
        const string overflowChannel = "PJSIP/overflow-o1";
        rig.Deliver(
            NewChannel(overflowLeg, overflowChannel, "Down", Caller, context: "overflow"),
            DialBegin(overflowLeg, overflowChannel),
            BridgeCreate("lone"),
            BridgeEnter("lone", Caller),
            NewState(Caller, "Up"));
        var session = rig.Session(Caller);
        var afterReports = session.State;

        rig.Deliver(HangupWith(overflowLeg, HangupCause.NoAnswer), Hangup(Caller));

        using var scope = new AssertionScope();
        afterReports.Should().Be(CallSessionState.Dialing, "the dial began, and nobody answered it");
        session.State.Should().Be(CallSessionState.Failed, "nobody answered the call");
        session.ConnectedAt.Should().BeNull("nobody answered the call");
        rig.Events.OfType<CallConnectedEvent>().Should().BeEmpty("nothing connected the call");
    }

    /// <summary>
    /// The control: an overflow <c>Dial</c> that answers a call dialing before its unanswered visit connects it, once,
    /// whichever report of the answer comes first. <c>leg-up</c> is the order the q-timeout-dial capture shows (the
    /// dialled leg's answer, the <c>DialEnd</c> ANSWER, the caller's own answer, the dialled leg and then the caller
    /// entering the bridge); <c>dial-end</c> has no answer of the dialled leg; <c>bridge</c> has only the bridge, the
    /// caller entering it first.
    /// </summary>
    [Theory]
    [InlineData("leg-up")]
    [InlineData("dial-end")]
    [InlineData("bridge")]
    public async Task CallConnectedEvent_ShouldBePublishedOnce_WhenAnOverflowDialAnswersACallDialingBeforeItsUnansweredVisit(
        string shape)
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(DialFailsThenVisitTimesOut(extensionRings: true));
        const string overflowLeg = "o1";
        const string overflowChannel = "PJSIP/overflow-o1";
        rig.Deliver(
            NewChannel(overflowLeg, overflowChannel, "Down", Caller, context: "overflow"),
            DialBegin(overflowLeg, overflowChannel),
            NewState(overflowLeg, "Ringing"));
        switch (shape)
        {
            case "leg-up":
                rig.Deliver(
                    NewState(overflowLeg, "Up"),
                    DialEnd(overflowLeg, overflowChannel, "ANSWER"),
                    NewState(Caller, "Up"),
                    BridgeCreate("overflow"),
                    BridgeEnter("overflow", overflowLeg),
                    BridgeEnter("overflow", Caller));
                break;
            case "dial-end":
                rig.Deliver(
                    DialEnd(overflowLeg, overflowChannel, "ANSWER"),
                    NewState(Caller, "Up"),
                    BridgeCreate("overflow"),
                    BridgeEnter("overflow", Caller),
                    BridgeEnter("overflow", overflowLeg));
                break;
            case "bridge":
                rig.Deliver(
                    BridgeCreate("overflow"),
                    BridgeEnter("overflow", Caller),
                    BridgeEnter("overflow", overflowLeg));
                break;
        }
        var session = rig.Session(Caller);
        var afterAnswer = session.State;

        rig.Deliver(Hangup(Caller), Hangup(overflowLeg));

        using var scope = new AssertionScope();
        afterAnswer.Should().Be(CallSessionState.Connected, "the overflow dial's answer connects the call");
        rig.Events.OfType<CallConnectedEvent>().Should().ContainSingle("the overflow dial's answer is announced once")
            .Which.QueueName.Should().BeNull("the connection answers no queue visit");
        session.ConnectedAt.Should().NotBeNull("someone answered the call");
        session.State.Should().Be(CallSessionState.Completed, "a connected call that hangs up normally completed");
    }

    /// <summary>
    /// A call on hold that is blind-transferred stays on hold until its held channel is taken off it: the transfer does
    /// not stop the hold clock, and the held caller's unhold stops it although the call, now transferring, does not
    /// resume. An unhold of another channel (Asterisk reports one when a channel enters a bridge) stops nothing.
    /// </summary>
    [Fact]
    public async Task HoldTime_ShouldStopGrowing_WhenTheHeldCallerIsUnheldAfterABlindTransferFromHold()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(QueueCallConnected("q-first", Caller, FirstMemberLeg, FirstMember));
        rig.Deliver(Hold(Caller));
        var session = rig.Session(Caller);
        var afterHold = session.State;

        rig.Deliver(BlindTransfer(Bridge(Caller), Caller, CallerChan, "ext", "200"));
        var afterTransfer = session.State;
        var clockRunningAfterTransfer = session._holdStartedAt is not null;
        rig.Deliver(Unhold(FirstMemberLeg));
        var clockRunningAfterOtherUnhold = session._holdStartedAt is not null;
        rig.Deliver(Unhold(Caller));
        var afterUnhold = session.State;
        var holdTime = session.HoldTime;
        var holdTimeLater = session.HoldTime;

        using var scope = new AssertionScope();
        afterHold.Should().Be(CallSessionState.OnHold, "premise: the connected call's hold is counted");
        afterTransfer.Should().Be(CallSessionState.Transferring, "premise: the blind transfer moves the held call");
        clockRunningAfterTransfer.Should().BeTrue("the caller is still on hold while it is transferred");
        clockRunningAfterOtherUnhold.Should().BeTrue("only the held channel's unhold ends its hold");
        afterUnhold.Should().Be(CallSessionState.Transferring, "an unhold resumes only a call on hold");
        session._holdStartedAt.Should().BeNull("the held caller's unhold ends the hold");
        holdTimeLater.Should().Be(holdTime, "the hold is over, so its time no longer grows");
        rig.Events.OfType<CallResumedEvent>().Should().BeEmpty("the call was not on hold when it was unheld");
    }
}
