using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Sessions.Tests.Harness;
using Verbara.Sdk.Tests.Shared.Sessions;
using static Verbara.Sdk.Sessions.Tests.Manager.CallSessionManagerDetachReleaseTests;
using static Verbara.Sdk.Sessions.Tests.Manager.WrapUpFrames;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// A wrap-up already open when its call is released is the agent's after-call time, not a notification of the call: it
/// keeps running, closes as it would have without the release — by its timer, or at the agent's next connection on any
/// server — and is reported by exactly one <see cref="CallWrapUpEvent"/> carrying the released session id, which may
/// follow the call's <see cref="CallReleasedEvent"/>. An ended call is not released at all, so its wrap-up is left to its
/// timer.
/// </summary>
/// <remarks>
/// Every test runs on a fake clock, so a wrap-up closes only when the test advances it. Runs in
/// <see cref="SessionSubscriberFaultsGroup"/> with the other release tests, which read the process-wide counters of the
/// <c>Verbara.Sdk.Sessions</c> meter.
/// </remarks>
[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class CallSessionManagerDetachReleaseWrapUpTests
{
    private static readonly TimeSpan WrapUp = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BeforeItsDuration = TimeSpan.FromSeconds(12);
    private static readonly string[] ReleasedThenWrapUp = [nameof(CallReleasedEvent), nameof(CallWrapUpEvent)];

    /// <summary>
    /// The released call's wrap-up closes once, after the release: by its timer when the duration passes, or, in the
    /// second run, when the same agent connects on a call of another server first — then with the time measured up to
    /// that connection, and nothing more when the duration later passes. The agent tracker adds the measured time and
    /// moves no state for that close.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetachFromServer_ShouldReportAnOpenWrapUpOnce_WhenItsCallIsReleased(bool connectsElsewhere)
    {
        var clock = new FakeTimeProvider();
        await using var rig = await ReleaseRig.StartAsync(clock, new SessionOptions { WrapUpDuration = WrapUp },
            serverIds: ["a", "b"]);
        using var agents = new AgentSessionTracker(rig.Manager, Options.Create(new SessionOptions()),
            NullLogger<AgentSessionTracker>.Instance, clock);
        var sessionId = OpenWrapUpWhileTheCallerIsUp(rig["a"], rig.Manager);
        var stateChanges = new List<AgentSessionStateChanged>();
        using var changes = agents.StateChanges.Subscribe(new StateObserver(stateChanges.Add));

        var released = rig.Release("a");
        var wrapUpsAtRelease = WrapUpsOf(rig, sessionId).Length;
        TimeSpan expected;
        string? connectedOn = null;
        if (connectsElsewhere)
        {
            clock.Advance(BeforeItsDuration);
            var b = rig["b"];
            b.Deliver(AgentLogin("agent-1", "PJSIP/agent1", "login-b"));
            b.Deliver(NamedAgentCallConnected("d1", "d1m", "PJSIP/agent1", "agent-1"));
            connectedOn = rig.Manager.GetByLinkedId("d1")?.ServerId;
            expected = BeforeItsDuration;
        }
        else
        {
            expected = WrapUp;
        }

        clock.Advance(WrapUp);
        clock.Advance(TimeSpan.FromMinutes(5));

        new
        {
            Released = released.Select(c => c.SessionId).ToArray(),
            wrapUpsAtRelease,
            connectedOn,
            Order = rig.KindsOf(sessionId).SkipWhile(k => k != nameof(CallReleasedEvent)).ToArray(),
            WrapUps = WrapUpsOf(rig, sessionId),
            WrapUpTime = agents.GetByAgentId("agent-1")!.TotalWrapUpTime,
            StateChangesForTheReleasedCall = stateChanges.Count(c => c.SessionId == sessionId),
        }.Should().BeEquivalentTo(
            new
            {
                Released = new[] { sessionId },
                wrapUpsAtRelease = 0,
                connectedOn = connectsElsewhere ? "b" : null,
                Order = ReleasedThenWrapUp,
                WrapUps = new[] { (sessionId, "agent-1", expected) },
                WrapUpTime = expected,
                StateChangesForTheReleasedCall = 0,
            },
            options => options.WithStrictOrdering(),
            "the wrap-up open when its call was released is not closed by the release; it is reported once, after the "
            + "call-released notification, with the time measured "
            + (connectsElsewhere ? "up to the agent's connection on server b, and nothing when its duration later passes"
                : "up to its duration")
            + "; the agent tracker adds that time and announces no state change for it. " + rig.Describe());
    }

    /// <summary>
    /// The wrap-up's timer fires while the release is parked after its drain, before its walk: the timer's close and the
    /// walk's release are both raised, and the wrap-up is reported exactly once — the release neither closes it again
    /// nor stops the close the timer already made.
    /// </summary>
    [Fact]
    public async Task DetachFromServer_ShouldReportTheWrapUpOnce_WhenItsTimerRacesTheRelease()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await ReleaseRig.StartAsync(clock, new SessionOptions { WrapUpDuration = WrapUp },
            serverIds: ["a"]);
        var sessionId = OpenWrapUpWhileTheCallerIsUp(rig["a"], rig.Manager);
        using var parker = StepParker.Install(rig.Manager, HandlerStep.ReleaseWalkStarted, "a");

        var release = ReleaseRig.OnThread(() => rig.Release("a"));
        parker.WaitUntilParked(ReleaseRig.Bound);
        clock.Advance(WrapUp);
        var whileParked = new { WrapUps = WrapUpsOf(rig, sessionId).Length, Released = rig.Released.Count };
        parker.LetGo();
        var returned = ReleaseRig.Returns(release);
        clock.Advance(TimeSpan.FromMinutes(5));
        parker.ThrowIfFaulted();

        new
        {
            WhileParked = whileParked,
            returned,
            Released = ReleaseRig.ResultOf(release)?.Select(c => c.SessionId).ToArray(),
            Held = rig.Manager.GetById(sessionId) is not null,
            Kinds = rig.KindsOf(sessionId).SkipWhile(k => k != nameof(CallWrapUpEvent)).ToArray(),
            WrapUps = WrapUpsOf(rig, sessionId),
        }.Should().BeEquivalentTo(
            new
            {
                WhileParked = new { WrapUps = 1, Released = 0 },
                returned = true,
                Released = new[] { sessionId },
                Held = false,
                Kinds = new[] { nameof(CallWrapUpEvent), nameof(CallReleasedEvent) },
                WrapUps = new[] { (sessionId, "agent-1", WrapUp) },
            },
            options => options.WithStrictOrdering(),
            "the timer closes the wrap-up while the release is parked before its walk, the walk then releases the call, "
            + "and the wrap-up is reported exactly once, with its full duration. " + rig.Describe());
    }

    /// <summary>
    /// An ended call is not released (it stays until its retention): a release of its server returns and announces
    /// nothing for it, and the wrap-up its member's hangup opened closes by its timer, once, as it does today.
    /// </summary>
    [Fact]
    public async Task DetachFromServer_ShouldLeaveAnEndedCallsWrapUpToItsTimer_WhenTheCallIsInRetention()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await ReleaseRig.StartAsync(clock, new SessionOptions { WrapUpDuration = WrapUp },
            serverIds: ["a"]);
        using var agents = new AgentSessionTracker(rig.Manager, Options.Create(new SessionOptions()),
            NullLogger<AgentSessionTracker>.Instance, clock);
        var a = rig["a"];
        a.Deliver(AgentLogin("agent-1", "PJSIP/agent1", "login-1"));
        a.Deliver(QueueCallConnected("q1", "e1", "em1", "PJSIP/agent1", agent: "agent-1"));
        var call = rig.Manager.GetByLinkedId("e1")!;
        a.Deliver(MemberHangsUp("q1", "e1", "em1", "PJSIP/agent1", agent: "agent-1"));
        var premise = new { call.State, AgentState = agents.GetByAgentId("agent-1")!.State };

        var released = rig.Release("a");
        var wrapUpsAtRelease = WrapUpsOf(rig, call.SessionId).Length;
        clock.Advance(WrapUp);
        clock.Advance(TimeSpan.FromMinutes(5));

        var agent = agents.GetByAgentId("agent-1")!;
        new
        {
            Premise = premise,
            Released = released.Count,
            Notified = rig.Released.Count,
            wrapUpsAtRelease,
            Kinds = rig.KindsOf(call.SessionId).SkipWhile(k => k != nameof(CallEndedEvent)).ToArray(),
            WrapUps = WrapUpsOf(rig, call.SessionId),
            After = new { agent.State, agent.TotalWrapUpTime },
        }.Should().BeEquivalentTo(
            new
            {
                Premise = new { State = CallSessionState.Completed, AgentState = AgentSessionState.WrapUp },
                Released = 0,
                Notified = 0,
                wrapUpsAtRelease = 0,
                Kinds = new[] { nameof(CallEndedEvent), nameof(CallWrapUpEvent) },
                WrapUps = new[] { (call.SessionId, "agent-1", WrapUp) },
                After = new { State = AgentSessionState.Idle, TotalWrapUpTime = WrapUp },
            },
            options => options.WithStrictOrdering(),
            "an ended call is not released: the release returns and announces nothing, and the call's wrap-up closes "
            + "by its timer once, with its full duration, sending the agent to Idle as today. " + rig.Describe());
    }

    /// <summary>
    /// Agent <c>agent-1</c> logs in on <paramref name="server"/> and takes queue call <c>w1</c>; its leg leaves and
    /// app_queue reports its <c>AgentComplete</c> while the caller stays up, so its wrap-up opens keyed to the call's
    /// session id, which is returned.
    /// </summary>
    private static string OpenWrapUpWhileTheCallerIsUp(RigServer server, CallSessionManager manager)
    {
        server.Deliver(AgentLogin("agent-1", "PJSIP/agent1", "login-1"));
        server.Deliver(QueueCallConnected("q1", "w1", "wm1", "PJSIP/agent1", agent: "agent-1"));
        server.Deliver(BridgeLeave(Bridge("w1"), "wm1"), Hangup("wm1"),
            AgentComplete("q1", "w1", CallerChannel("w1"), "w1", "PJSIP/agent1", "wm1",
                MemberChannel("PJSIP/agent1", "wm1"), 30, "agent", "agent-1"));
        var call = manager.GetByLinkedId("w1")
            ?? throw new InvalidOperationException("premise: no session holds the queue call w1");
        if (call.State == CallSessionState.Completed)
            throw new InvalidOperationException("premise: the queue call w1 ended; its caller should still be up");
        return call.SessionId;
    }

    private static (string SessionId, string? AgentId, TimeSpan Duration)[] WrapUpsOf(ReleaseRig rig, string sessionId) =>
    [
        .. rig.Events.Select(e => e.Event).OfType<CallWrapUpEvent>().Where(w => w.SessionId == sessionId)
            .Select(w => (w.SessionId, w.AgentId, w.WrapUpDuration)),
    ];
}
