using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.Tests.Harness;
using static Verbara.Sdk.Sessions.Tests.Manager.WrapUpFrames;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// A <see cref="CallEndedEvent"/> carries the state its call ended in, <see cref="CallSessionState.Completed"/> or
/// <see cref="CallSessionState.Failed"/>, already set when the first subscriber receives it.
/// </summary>
/// <remarks>Runs on the real manager over <see cref="WrapUpRig"/>'s frames, on <see cref="FakeTimeProvider"/>.</remarks>
[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class CallSessionManagerEndingFinalStateTests
{
    private const int UserBusy = (int)HangupCause.UserBusy;

    [Fact]
    public async Task CallEndedEvent_ShouldCarryCompletedAndFailed_WhenOneConnectedCallHangsUpNormallyAndAnotherWithAnotherCause()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock);
        rig.Deliver(DialedCallAnswers("n1", "nm1", "PJSIP/agent1"));
        rig.Deliver(DialedCallAnswers("f1", "fm1", "PJSIP/agent2"));
        var normal = rig.Session("n1");
        var abnormal = rig.Session("f1");
        var seen = new List<(string SessionId, CallSessionState? Carried, CallSessionState Reads)>();
        using var subscription = rig.Manager.Events.Subscribe(evt =>
        {
            if (evt is CallEndedEvent ended)
                seen.Add((ended.SessionId, ended.FinalState,
                    ended.SessionId == normal.SessionId ? normal.State : abnormal.State));
        });
        var premise = (normal.State, abnormal.State);
        clock.Advance(TimeSpan.FromSeconds(5));

        rig.Deliver(Hangup("nm1"), Hangup("n1"));
        rig.Deliver(HangupWith("fm1", UserBusy), HangupWith("f1", UserBusy));

        using var scope = new AssertionScope();
        premise.Should().Be((CallSessionState.Connected, CallSessionState.Connected), "premise: both calls are connected");
        seen.Should().Equal(
            [
                (normal.SessionId, CallSessionState.Completed, CallSessionState.Completed),
                (abnormal.SessionId, CallSessionState.Failed, CallSessionState.Failed),
            ],
            "each ending carries the state its call ended in, the state the call reads when the ending is delivered");
    }

    [Fact]
    public async Task CallEndedEvent_ShouldCarryFailed_WhenAConnectedCallWaitingInALaterQueueHangsUpWithAnotherCause()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock);
        rig.Deliver(DialedCallAnswers("w1", "wm1", "PJSIP/agent1"));
        rig.Deliver(HangupWith("wm1", (int)HangupCause.NormalClearing), Join("support", CallerChannel("w1"), "w1", "w1"));
        var session = rig.Session("w1");
        var premise = session.State;
        CallSessionState? reads = null;
        using var subscription = rig.Manager.Events.Subscribe(evt =>
        {
            if (evt is CallEndedEvent ended && ended.SessionId == session.SessionId)
                reads = session.State;
        });
        clock.Advance(TimeSpan.FromSeconds(5));

        rig.Deliver(HangupWith("w1", UserBusy));

        using var scope = new AssertionScope();
        premise.Should().Be(CallSessionState.Queued, "premise: the call connected and now waits in a later queue");
        rig.Events.OfType<CallEndedEvent>().Where(e => e.SessionId == session.SessionId).Select(e => e.FinalState)
            .Should().Equal([CallSessionState.Failed], "the caller's own channel hung up with a cause other than normal clearing");
        reads.Should().Be(CallSessionState.Failed, "the call reads the state its ending carries when it is delivered");
    }

    /// <summary>A caller outside any queue that dials <paramref name="device"/>, whose leg answers.</summary>
    private static ManagerEvent[] DialedCallAnswers(string caller, string calleeLeg, string device) =>
    [
        NewChannel(caller, CallerChannel(caller), "Ring", caller, context: "from-trunk", exten: "600"),
        NewChannel(calleeLeg, MemberChannel(device, calleeLeg), "Down", caller, context: "agents"),
        new DialBeginEvent
        {
            EventType = "DialBegin", UniqueId = caller, Channel = CallerChannel(caller), DestUniqueid = calleeLeg,
            DestChannel = MemberChannel(device, calleeLeg), DialString = device,
        },
        NewState(calleeLeg, "Up"),
    ];

    private static HangupEvent HangupWith(string uniqueId, int cause) =>
        new() { EventType = "Hangup", UniqueId = uniqueId, Cause = cause };
}
