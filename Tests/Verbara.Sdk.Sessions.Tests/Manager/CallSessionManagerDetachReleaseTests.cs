using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Sessions.Tests.Harness;
using Verbara.Sdk.Tests.Shared.Sessions;
using static Verbara.Sdk.Sessions.Tests.Manager.WrapUpFrames;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// A detach that names what happens to the server's calls: "keep" (and the one-argument detach) changes nothing, and
/// "release" lets go of every call of that server still in progress without ending it, removes it from every lookup
/// and from the default store, and tells the caller (the returned calls) and every subscriber (one
/// <see cref="CallReleasedEvent"/> per call); the SDK's agent and queue trackers let go of it too.
/// </summary>
/// <remarks>
/// Written first, against a stub of the overload that detaches as the one-argument detach and releases nothing: every
/// test fails on its assertions there, save the pins named as such, which hold today's behaviour and are green on the
/// stub by construction. Runs in <see cref="SessionSubscriberFaultsGroup"/> because it reads the process-wide counters
/// of the <c>Verbara.Sdk.Sessions</c> meter.
/// </remarks>
[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class CallSessionManagerDetachReleaseTests
{
    private static readonly TimeSpan WrapUp = TimeSpan.FromSeconds(30);
    private static readonly string[] OnlyA = ["a"];

    // --- A detach SHALL release a server's calls only when the caller asks for it -------------------------------

    /// <summary>Pin (green on the stub): the default detach and "keep" keep every call.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetachFromServer_ShouldKeepEveryCall_WhenTheDetachNamesOnlyTheIdOrKeep(bool namesKeep)
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var calls = new[] { rig.ConnectedCall("a", "k1"), rig.ConnectedCall("a", "k2"), rig.ConnectedCall("a", "k3") };

        IReadOnlyList<CallSession>? returned = null;
        if (namesKeep)
            returned = rig.Manager.DetachFromServer("a", DetachedCalls.Keep);
        else
            rig.Manager.DetachFromServer("a");

        new
        {
            Active = rig.Manager.ActiveSessions.Count(s => s.ServerId == "a"),
            FoundById = calls.Count(c => ReferenceEquals(rig.Manager.GetById(c.SessionId), c)),
            Returned = returned?.Count ?? 0,
        }.Should().BeEquivalentTo(new { Active = 3, FoundById = 3, Returned = 0 },
            "a detach naming only the id, or naming keep, keeps the 3 calls held and listed, and keep returns no call. "
            + rig.Describe());
    }

    [Fact]
    public async Task DetachFromServer_ShouldReturnExactlyTheServersCallsInProgress_WhenItReleases()
    {
        await using var rig = await ReleaseRig.StartAsync("a", "b");
        var ofA = new[] { rig.ConnectedCall("a", "a1"), rig.ConnectedCall("a", "a2"), rig.ConnectedCall("a", "a3") };
        var ended = rig.ConnectedCall("a", "a4");
        rig.HangUpCall("a", "a4");
        var ofB = new[] { rig.ConnectedCall("b", "b1"), rig.ConnectedCall("b", "b2") };

        var released = rig.Release("a");

        new
        {
            Released = released.Select(c => c.SessionId).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            BHeld = ofB.Count(c => ReferenceEquals(rig.Manager.GetById(c.SessionId), c)),
            EndedHeld = ReferenceEquals(rig.Manager.GetById(ended.SessionId), ended),
        }.Should().BeEquivalentTo(
            new
            {
                Released = ofA.Select(c => c.SessionId).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                BHeld = 2,
                EndedHeld = true,
            },
            "a release returns exactly a's 3 calls in progress; b's calls and a's ended call (left to its retention) "
            + "stay held. " + rig.Describe());
    }

    [Fact]
    public async Task DetachFromServer_ShouldReleaseTheKeptCallsOnceWithoutWaiting_WhenAReleaseFollowsAKeep()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var c1 = rig.ConnectedCall("a", "r1");
        var c2 = rig.ConnectedCall("a", "r2");

        // A handler of the attachment the keep leaves behind, parked before it publishes its call: a release with no
        // attachment of "a" left to close has nothing to wait for.
        using var parker = StepParker.Install(rig.Manager, HandlerStep.ArrivalBuilt, "a", ReleaseRig.CallerLeg("late"));
        var arrival = ReleaseRig.OnThread(() => rig.Arrival("a", "late"));
        parker.WaitUntilParked(ReleaseRig.Bound);

        rig.Manager.DetachFromServer("a", DetachedCalls.Keep);
        var second = ReleaseRig.OnThread(() => rig.Release("a"));
        // Bounded below the 5 s drain: a release that waited for the parked handler would not return within it.
        var secondReturnedWhileParked = ReleaseRig.Returns(second, TimeSpan.FromSeconds(2)) && !parker.HasResumed();
        var third = ReleaseRig.OnThread(() => rig.Release("a"));
        var thirdReturned = ReleaseRig.Returns(third);
        parker.LetGo();
        ReleaseRig.Returns(arrival).Should().BeTrue("the parked arrival completes once let go");

        new
        {
            secondReturnedWhileParked,
            Second = ReleaseRig.ResultOf(second)?.Select(c => c.SessionId).ToArray(),
            thirdReturned,
            Third = ReleaseRig.ResultOf(third)?.Count ?? -1,
            ThirdFaulted = third.IsFaulted,
        }.Should().BeEquivalentTo(
            new
            {
                secondReturnedWhileParked = true,
                Second = new[] { c1.SessionId, c2.SessionId },
                thirdReturned = true,
                Third = 0,
                ThirdFaulted = false,
            },
            options => options.WithoutStrictOrderingFor(x => x.Second),
            "a release after a keep returns the 2 kept calls without waiting for a handler of the earlier attachment, "
            + "and a third release returns none and does not throw. " + rig.Describe());
    }

    [Theory]
    [InlineData("", 1, nameof(ArgumentException))]
    [InlineData("a", 7, nameof(ArgumentOutOfRangeException))]
    public async Task DetachFromServer_ShouldThrowAndChangeNothing_WhenAnArgumentIsInvalid(
        string serverId, int calls, string expected)
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var call = rig.ConnectedCall("a", "i1");

        Exception? thrown = null;
        try
        {
            rig.Manager.DetachFromServer(serverId, (DetachedCalls)calls);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        // Still attached: a's hold report still reaches the call.
        rig["a"].Server.Channels.OnHold(ReleaseRig.CallerLeg("i1"), "default");

        new
        {
            Thrown = thrown?.GetType().Name,
            Held = ReferenceEquals(rig.Manager.GetById(call.SessionId), call),
            StillAttached = call.State == CallSessionState.OnHold,
        }.Should().BeEquivalentTo(new { Thrown = expected, Held = true, StillAttached = true },
            "an empty server id is refused with ArgumentException and an undefined value with "
            + "ArgumentOutOfRangeException, with a left attached and its call held. " + rig.Describe());
    }

    /// <summary>
    /// Pin (green on the stub by construction): a consumer's implementation written against 2.8.0 compiles with no
    /// new member, and the overload on it calls its own detach and returns no call.
    /// </summary>
    [Fact]
    public void DetachFromServer_ShouldCallTheImplementationsOwnDetachAndReturnNoCall_WhenAnImplementationOf280OverridesNothing()
    {
        var implementation = new ImplementationOf280();

        var returned = ((ICallSessionManager)implementation).DetachFromServer("a", DetachedCalls.Release);

        new { Returned = returned.Count, Detached = implementation.Detached.ToArray() }.Should().BeEquivalentTo(
            new { Returned = 0, Detached = OnlyA },
            "the interface's default implementation detaches through the implementation's own one-argument detach, "
            + "releases nothing and returns no call");
    }

    // --- A released call SHALL leave everything the SDK holds for it without being ended -------------------------

    [Fact]
    public async Task DetachFromServer_ShouldRemoveTheCallFromEveryLookup_WhenItReleasesIt()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var call = rig.ConnectedCall("a", "lk");
        var activeBefore = ActiveGauge(rig.Manager);

        var released = rig.Release("a");

        new
        {
            Released = released.Select(c => c.SessionId).ToArray(),
            ById = rig.Manager.GetById(call.SessionId) is null,
            ByLinkedId = rig.Manager.GetByLinkedId(ReleaseRig.LinkedIdOf("lk")) is null,
            ByCaller = rig.Manager.GetByChannelId(ReleaseRig.CallerLeg("lk")) is null,
            ByCallee = rig.Manager.GetByChannelId(ReleaseRig.CalleeLeg("lk")) is null,
            ByBridge = rig.Manager.GetByBridgeId(ReleaseRig.BridgeOf("lk")) is null,
            Listed = rig.Manager.ActiveSessions.Any(s => s.SessionId == call.SessionId)
                || rig.Manager.GetRecentCompleted().Any(s => s.SessionId == call.SessionId),
            ActiveDrop = activeBefore - ActiveGauge(rig.Manager),
        }.Should().BeEquivalentTo(
            new
            {
                Released = new[] { call.SessionId }, ById = true, ByLinkedId = true, ByCaller = true, ByCallee = true,
                ByBridge = true, Listed = false, ActiveDrop = 1L,
            },
            "a released call is found by none of its ids, legs or bridge, is listed nowhere, and sessions.active drops "
            + "by one. " + rig.Describe());
    }

    [Fact]
    public async Task DetachFromServer_ShouldLeaveTheCallAsItWasAndRecordNoEnding_WhenItReleasesIt()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var call = rig.ConnectedCall("a", "ne");
        using var completed = new InstrumentTally("sessions.completed");
        using var failed = new InstrumentTally("sessions.failed");
        using var duration = new InstrumentTally("sessions.duration");

        var released = rig.Release("a");

        new
        {
            Released = released.Select(c => c.SessionId).ToArray(),
            call.State,
            call.CompletedAt,
            call.HangupCause,
            Provenance = call.Metadata.GetValueOrDefault("cause"),
            Ended = rig.KindsOf(call.SessionId).Count(k => k == nameof(CallEndedEvent)),
            Measured = completed.Measurements + failed.Measurements + duration.Measurements,
        }.Should().BeEquivalentTo(
            new
            {
                Released = new[] { call.SessionId },
                State = CallSessionState.Connected,
                CompletedAt = (DateTimeOffset?)null,
                HangupCause = (HangupCause?)null,
                Provenance = (string?)null,
                Ended = 0,
                Measured = 0L,
            },
            "a released call stays connected with no completion time, cause or provenance, no CallEndedEvent is raised "
            + "and no completion, failure or duration is measured. " + rig.Describe());
    }

    [Fact]
    public async Task DetachFromServer_ShouldLetTheDefaultStoreGoAndNoLaterSaveBringItBack_WhenItReleasesACall()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var call = rig.ConnectedCall("a", "st");
        var storedBefore = await rig.Store.GetAsync(call.SessionId, CancellationToken.None);

        rig.Release("a");

        // A leg of the released call reported again, by a new instance attached under the same id: whatever it opens
        // or joins is saved, and the released call must not be the one saved.
        var returning = await rig.AttachAsync("a");
        returning.Server.Channels.OnNewChannel(ReleaseRig.CallerLeg("st"), "PJSIP/100-st-a", ChannelState.Up,
            context: "from-internal", linkedId: ReleaseRig.LinkedIdOf("st"));

        var active = await rig.Store.GetActiveAsync(CancellationToken.None);
        new
        {
            StoredBefore = ReferenceEquals(storedBefore, call),
            StoredAfter = await rig.Store.GetAsync(call.SessionId, CancellationToken.None) is not null,
            ListedActive = active.Any(s => s.SessionId == call.SessionId),
        }.Should().BeEquivalentTo(new { StoredBefore = true, StoredAfter = false, ListedActive = false },
            "the default store lets go of the released call, and a later save of what its leg reaches does not write "
            + "it again. " + rig.Describe());
    }

    /// <summary>
    /// The by-object rule on the two lookups an id of a released call can name for another call. The channel id: a
    /// leg of server <c>b</c> reusing the caller leg's id holds it for <c>b</c>'s call. The <c>linkedid</c>: no public
    /// route lets another call hold the <c>linkedid</c> of a call still in progress (one call per <c>linkedid</c>), so
    /// it is shown on <c>a</c>'s ended call, whose <c>linkedid</c> <c>b</c>'s call took over — an implementation that
    /// cleared the <c>linkedid</c> of every call of <c>a</c> would remove it.
    /// </summary>
    [Fact]
    public async Task DetachFromServer_ShouldNotRemoveAnEntryOfAnotherCall_WhenThatCallHoldsAnIdOfAReleasedOne()
    {
        await using var rig = await ReleaseRig.StartAsync("a", "b");
        var c1 = rig.ConnectedCall("a", "nw");
        rig["b"].Server.Channels.OnNewChannel(ReleaseRig.CallerLeg("nw"), "PJSIP/300-nw", ChannelState.Up,
            context: "from-trunk", linkedId: "b-own-L");
        var c2 = rig.Manager.GetByLinkedId("b-own-L")!;
        rig.ConnectedCall("a", "old");
        rig.HangUpCall("a", "old");
        rig["b"].Server.Channels.OnNewChannel("b-leg-old", "PJSIP/300-old", ChannelState.Up, context: "from-trunk",
            linkedId: ReleaseRig.LinkedIdOf("old"));
        var c5 = rig.Manager.GetByLinkedId(ReleaseRig.LinkedIdOf("old"))!;

        var released = rig.Release("a");

        new
        {
            Released = released.Select(c => c.SessionId).ToArray(),
            ChannelStillB = ReferenceEquals(rig.Manager.GetByChannelId(ReleaseRig.CallerLeg("nw")), c2),
            LinkedIdStillB = ReferenceEquals(rig.Manager.GetByLinkedId(ReleaseRig.LinkedIdOf("old")), c5),
            C2Held = ReferenceEquals(rig.Manager.GetById(c2.SessionId), c2),
            C5Held = ReferenceEquals(rig.Manager.GetById(c5.SessionId), c5),
        }.Should().BeEquivalentTo(
            new
            {
                Released = new[] { c1.SessionId }, ChannelStillB = true, LinkedIdStillB = true, C2Held = true,
                C5Held = true,
            },
            "the release removes only entries that name the released call: b's calls keep the channel id and the "
            + "linkedid they hold, and stay held. " + rig.Describe());
    }

    /// <summary>
    /// A call the host registers under the released call's session id while the release is letting go of that call
    /// keeps its bridge. The bridge lookup maps a bridge to a session id, not to a call, so the release clears the
    /// released call's bridges while that call still holds the session id — a registration under it is refused until
    /// then — and a registration accepted once the call has left the session-id lookup is never touched by it.
    /// </summary>
    [Fact]
    public async Task DetachFromServer_ShouldKeepTheBridgeOfACallRegisteredUnderTheReleasedSessionId_WhenItIsRegisteredDuringTheRelease()
    {
        await using var rig = await ReleaseRig.StartAsync("a", "b");
        var c1 = rig.ConnectedCall("a", "br");
        var sessionId = c1.SessionId;
        var early = ReconstructedOnB(sessionId, "br-early-L", "br-early-bridge");
        var late = ReconstructedOnB(sessionId, "br-late-L", "br-late-bridge");
        bool? earlyAccepted = null;
        bool? lateAccepted = null;
        rig.Manager.StepHook = (step, serverId, id) =>
        {
            if (step == HandlerStep.ReleaseWalkStarted && serverId == "a")
                earlyAccepted = rig.Manager.RegisterReconstructedSession(early);
            else if (step == HandlerStep.ReleaseForgotById && serverId == "a" && id == sessionId)
                lateAccepted = rig.Manager.RegisterReconstructedSession(late);
        };

        var released = rig.Release("a");
        rig.Manager.StepHook = null;

        new
        {
            Released = released.Select(c => c.SessionId).ToArray(),
            EarlyAccepted = earlyAccepted,
            LateAccepted = lateAccepted,
            ById = ReferenceEquals(rig.Manager.GetById(sessionId), late),
            ByLateBridge = ReferenceEquals(rig.Manager.GetByBridgeId("br-late-bridge"), late),
            ByLateLinkedId = ReferenceEquals(rig.Manager.GetByLinkedId("br-late-L"), late),
            ByReleasedBridge = rig.Manager.GetByBridgeId(ReleaseRig.BridgeOf("br")) is null,
        }.Should().BeEquivalentTo(
            new
            {
                Released = new[] { sessionId }, EarlyAccepted = (bool?)false, LateAccepted = (bool?)true, ById = true,
                ByLateBridge = true, ByLateLinkedId = true, ByReleasedBridge = true,
            },
            "a registration under the session id is refused while the released call holds it, and one accepted after "
            + "the release removed the call by its session id keeps its bridge and its linkedid: the release's pass "
            + "over the bridges, which match by session id, ran before that removal. The released call's own bridge "
            + "answers nothing, rather than the newer call. " + rig.Describe());
    }

    /// <summary>
    /// The post-condition every <see cref="ReleaseRig"/> checks at its disposal, shown red: handed a call the manager
    /// still holds, it names that call's session id, <c>linkedid</c> and channel ids and its connected leg; with the
    /// call's leg list taken away it names the wrap-up key whose count no held call records any more.
    /// </summary>
    [Fact]
    public async Task ReleaseLeftovers_ShouldNameEveryEntryAndLegCount_WhenTheCallsItIsGivenAreStillKept()
    {
        await using var rig = await ReleaseRig.StartAsync(new FakeTimeProvider(), new SessionOptions { WrapUpDuration = WrapUp },
            serverIds: ["a"]);
        rig["a"].Deliver(AgentLogin("agent-1", "PJSIP/agent1", "login-1"));
        rig["a"].Deliver(NamedAgentCallConnected("pc", "pcm", "PJSIP/agent1", "agent-1"));
        var call = rig.Manager.GetByLinkedId("pc")!;

        var held = rig.Manager.ReleaseLeftovers([call]);
        var legs = call.ConnectedAgentLegs;
        call.ConnectedAgentLegs = null;
        var miscounted = rig.Manager.ReleaseLeftovers([]);
        call.ConnectedAgentLegs = legs;

        new
        {
            Held = held.Count(l => l.Contains(call.SessionId, StringComparison.Ordinal)),
            ById = held.Any(l => l.StartsWith("session id ", StringComparison.Ordinal)),
            ByLinkedId = held.Any(l => l.StartsWith("linkedid pc ", StringComparison.Ordinal)),
            ByChannel = held.Count(l => l.StartsWith("channel id ", StringComparison.Ordinal)),
            Legs = held.Any(l => l.Contains("1 connected leg(s)", StringComparison.Ordinal)),
            Miscounted = miscounted.Count(l => l.StartsWith("wrap-up key agent/agent-1 counts 1", StringComparison.Ordinal)),
        }.Should().BeEquivalentTo(
            new
            {
                Held = 3 + call.Participants.Count, ById = true, ByLinkedId = true, ByChannel = call.Participants.Count,
                Legs = true, Miscounted = 1,
            },
            "the helper names every entry and the connected leg of a call it is told was released but is still kept, "
            + $"and a count no held call records: [{string.Join(" | ", held.Concat(miscounted))}]. " + rig.Describe());
    }

    // --- Each released call SHALL be reported once to the caller and to every subscriber ---------------------------

    [Fact]
    public async Task DetachFromServer_ShouldRaiseOneReleaseNotificationPerCallAndCountIt_WhenItReleases()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var calls = new[]
        {
            rig.ConnectedCall("a", "n1", "t1"), rig.ConnectedCall("a", "n2", "t2"), rig.ConnectedCall("a", "n3", "t3"),
        };
        using var counter = new InstrumentTally("sessions.released_on_detach");

        var released = rig.Release("a");

        var expected = released.Select(c => new CallReleasedEvent(c.SessionId, "a", default, c.LinkedId, c.TenantId))
            .ToArray();
        new
        {
            Returned = released.Count,
            Notified = rig.Released.Select(e => e with { Timestamp = default }).ToArray(),
            Counted = counter.For("a"),
            Logged = rig.Log.At(LogLevel.Information).Count(e => e.Values.Values.Contains("a")
                && e.Values.Values.Any(v => v is int or long && Convert.ToInt64(v, null) == 3)),
        }.Should().BeEquivalentTo(
            new { Returned = 3, Notified = expected, Counted = 3d, Logged = 1 },
            options => options.WithStrictOrdering(),
            "3 call-released notifications are raised, one per call, carrying server a and each call's session id, "
            + "linkedid and tenant id in the order of the returned calls; the counter tagged a grows by 3 and one "
            + $"Information entry names a and 3. Calls: {string.Join(", ", calls.Select(c => c.SessionId))}. "
            + rig.Describe());
    }

    [Fact]
    public async Task CallReleasedEvent_ShouldLetASubscriberLookTheCallUp_WhenItIsDeliveredSynchronously()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var call = rig.ConnectedCall("a", "lu");
        CallSession? seen = null;
        var notified = false;
        using var subscription = rig.Manager.Events.Subscribe(new Observer(e =>
        {
            if (e is CallReleasedEvent && e.SessionId == call.SessionId)
            {
                notified = true;
                seen = rig.Manager.GetById(e.SessionId);
            }
        }));

        rig.Release("a");

        new { notified, SeenIsTheCall = ReferenceEquals(seen, call), After = rig.Manager.GetById(call.SessionId) is null }
            .Should().BeEquivalentTo(new { notified = true, SeenIsTheCall = true, After = true },
                "a subscriber told of the release synchronously still finds the call by its session id; after the "
                + "detach returned the lookup returns nothing. " + rig.Describe());
    }

    /// <summary>
    /// Pin (green on the stub by construction — the stub raises and counts nothing): a release with no call in
    /// progress raises no notification and moves no counter.
    /// </summary>
    [Fact]
    public async Task DetachFromServer_ShouldRaiseAndCountNothing_WhenTheServerHoldsNoCallInProgress()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        rig.ConnectedCall("a", "gone");
        rig.HangUpCall("a", "gone");
        using var counter = new InstrumentTally("sessions.released_on_detach");

        var released = rig.Release("a");

        new { Returned = released.Count, Notified = rig.Released.Count, Counted = counter.For("a") }
            .Should().BeEquivalentTo(new { Returned = 0, Notified = 0, Counted = 0d },
                "nothing in progress, nothing released, notified or counted. " + rig.Describe());
    }

    [Fact]
    public async Task DetachFromServer_ShouldReportAWrapUpAlreadyOpenOnceAfterTheRelease_WhenItsDurationPasses()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await ReleaseRig.StartAsync(clock, new SessionOptions { WrapUpDuration = WrapUp },
            serverIds: ["a"]);
        using var agents = new AgentSessionTracker(rig.Manager, Options.Create(new SessionOptions()),
            NullLogger<AgentSessionTracker>.Instance, clock);
        var a = rig["a"];
        a.Deliver(AgentLogin("agent-1", "PJSIP/agent1", "login-1"));
        a.Deliver(QueueCallConnected("q1", "w1", "wm1", "PJSIP/agent1", agent: "agent-1"));
        // The member leaves and app_queue reports its AgentComplete while the caller stays up: the wrap-up opens.
        a.Deliver(BridgeLeave(Bridge("w1"), "wm1"), Hangup("wm1"),
            AgentComplete("q1", "w1", CallerChannel("w1"), "w1", "PJSIP/agent1", "wm1", MemberChannel("PJSIP/agent1", "wm1"),
                30, "agent", "agent-1"));
        var sessionId = rig.Manager.GetByLinkedId("w1")!.SessionId;
        var openedAt = clock.GetUtcNow();
        var stateChanges = new List<AgentSessionStateChanged>();
        using var changes = agents.StateChanges.Subscribe(new StateObserver(stateChanges.Add));

        rig.Release("a");
        clock.Advance(WrapUp);
        clock.Advance(TimeSpan.FromMinutes(5));

        var kinds = rig.KindsOf(sessionId);
        new
        {
            Order = kinds.SkipWhile(k => k != nameof(CallReleasedEvent)).ToArray(),
            WrapUps = rig.Events.Select(e => e.Event).OfType<CallWrapUpEvent>().Select(w => (w.SessionId, w.WrapUpDuration))
                .ToArray(),
            WrapUpTime = agents.GetByAgentId("agent-1")!.TotalWrapUpTime,
            StateChanges = stateChanges.Count,
        }.Should().BeEquivalentTo(
            new
            {
                Order = new[] { nameof(CallReleasedEvent), nameof(CallWrapUpEvent) },
                WrapUps = new[] { (sessionId, WrapUp) },
                WrapUpTime = WrapUp,
                StateChanges = 0,
            },
            $"the wrap-up opened at {openedAt:O} keeps running across the release and is reported once, after the "
            + "call-released notification, with its measured duration; the agent tracker adds it to the agent's "
            + "wrap-up time and announces no state change for it. " + rig.Describe());
    }

    // --- The SDK's agent tracker SHALL let go of a released call --------------------------------------------------

    [Fact]
    public async Task AgentSessionTracker_ShouldDropTheCurrentCallAndKeepTheState_WhenTheAgentsCallIsReleased()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await ReleaseRig.StartAsync(clock, new SessionOptions { WrapUpDuration = WrapUp },
            serverIds: ["a"]);
        using var agents = new AgentSessionTracker(rig.Manager, Options.Create(new SessionOptions()),
            NullLogger<AgentSessionTracker>.Instance, clock);
        var a = rig["a"];
        a.Deliver(AgentLogin("agent-1", "PJSIP/agent1", "login-1"));
        foreach (var n in new[] { "h1", "h2" })
        {
            a.Deliver(NamedAgentCallConnected(n, n + "m", "PJSIP/agent1", "agent-1"));
            a.Deliver(NamedAgentCallEnds(n, n + "m"));
            clock.Advance(WrapUp);
        }

        a.Deliver(NamedAgentCallConnected("h3", "h3m", "PJSIP/agent1", "agent-1"));
        var agent = agents.GetByAgentId("agent-1")!;
        var premise = new { agent.State, agent.CallsHandled, Current = agent.CurrentCall?.LinkedId };
        var stateChanges = new List<AgentSessionStateChanged>();
        using var changes = agents.StateChanges.Subscribe(new StateObserver(stateChanges.Add));

        rig.Release("a");

        new { Premise = premise, After = new { agent.State, agent.CallsHandled, Current = agent.CurrentCall?.LinkedId }, Changes = stateChanges.Count }
            .Should().BeEquivalentTo(
                new
                {
                    Premise = new { State = AgentSessionState.OnCall, CallsHandled = 2, Current = (string?)"h3" },
                    After = new { State = AgentSessionState.OnCall, CallsHandled = 2, Current = (string?)null },
                    Changes = 0,
                },
                "an agent on a released call has no current call afterwards, is still on call, still counts 2 calls "
                + "handled, and no state change is announced for it. " + rig.Describe());
    }

    // --- queue-call-accounting: a caller waiting in a queue whose server is released ---------------------------------

    [Fact]
    public async Task QueueSessionTracker_ShouldStopCountingTheCallerAsWaitingAndCountNothing_WhenItsServerIsReleased()
    {
        await using var rig = await ReleaseRig.StartAsync("n1");
        using var queues = new QueueSessionTracker(rig.Manager, Options.Create(new SessionOptions()));
        var n1 = rig["n1"];
        n1.Deliver(NewChannel("qw", CallerChannel("qw"), "Ring", "qw", context: "from-trunk", exten: "500"),
            Join("q1", CallerChannel("qw"), "qw", "qw"));
        var call = rig.Manager.GetByLinkedId("qw")!;
        var queue = queues.GetByQueueName("q1")!;
        var premise = new { queue.CallsWaiting, queue.CallsOffered };

        rig.Release("n1");
        var afterRelease = new { queue.CallsWaiting, queue.CallsOffered, queue.CallsAnswered, queue.CallsAbandoned };

        // A call registered later under the released call's session id, which then ends: no visit of the released
        // call is open for its ending to close.
        var returning = await rig.AttachAsync("n1");
        var reconstructed = new CallSession(call.SessionId, "qw", "n1", CallDirection.Inbound);
        reconstructed.AddParticipant(new SessionParticipant
        {
            UniqueId = "qw", Channel = CallerChannel("qw"), Technology = "PJSIP", Role = ParticipantRole.Caller, JoinedAt = DateTimeOffset.UtcNow,
        });
        var registered = rig.Manager.RegisterReconstructedSession(reconstructed);
        returning.Deliver(NewChannel("qw", CallerChannel("qw"), "Up", "qw", context: "from-trunk", exten: "500"),
            Hangup("qw"));

        new
        {
            Premise = premise,
            AfterRelease = afterRelease,
            Registered = registered,
            AbandonedAtTheLaterEnding = queue.CallsAbandoned,
        }.Should().BeEquivalentTo(
            new
            {
                Premise = new { CallsWaiting = 1, CallsOffered = 1 },
                AfterRelease = new { CallsWaiting = 0, CallsOffered = 1, CallsAnswered = 0, CallsAbandoned = 0 },
                Registered = true,
                AbandonedAtTheLaterEnding = 0,
            },
            "a released caller no longer counts as waiting, counts neither answered nor abandoned, leaves the offered "
            + "count alone, and a call registered later under its session id finds no visit of it open. "
            + rig.Describe());
    }

    /// <summary>The <c>sessions.active</c> gauge of <paramref name="manager"/>, read now.</summary>
    internal static long ActiveGauge(CallSessionManager manager)
    {
        long value = -1;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, manager.ResidencyMeter) && instrument.Name == "sessions.active")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measured, _, _) => value = measured);
        listener.Start();
        listener.RecordObservableInstruments();
        return value;
    }

    /// <summary>A consumer's implementation of the interface as published in 2.8.0: none of the new members.</summary>
    /// <summary>A reconstructed call of server <c>b</c>, with no legs, mapped to <paramref name="bridgeId"/>.</summary>
    private static CallSession ReconstructedOnB(string sessionId, string linkedId, string bridgeId) =>
        new(sessionId, linkedId, "b", CallDirection.Inbound) { BridgeId = bridgeId };

    private sealed class ImplementationOf280 : ICallSessionManager
    {
        public List<string> Detached { get; } = [];
        public IEnumerable<CallSession> ActiveSessions => [];
        public IObservable<SessionDomainEvent> Events => System.Reactive.Linq.Observable.Empty<SessionDomainEvent>();
        public CallSession? GetById(string sessionId) => null;
        public CallSession? GetByLinkedId(string linkedId) => null;
        public CallSession? GetByChannelId(string uniqueId) => null;
        public CallSession? GetByBridgeId(string bridgeId) => null;
        public IEnumerable<CallSession> GetRecentCompleted(int count = 100) => [];
        public void AttachToServer(VerbaraServer server, string serverId) { }
        public void DetachFromServer(string serverId) => Detached.Add(serverId);
        public bool RegisterReconstructedSession(CallSession session) => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class Observer(Action<SessionDomainEvent> onNext) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value) => onNext(value);

        public void OnError(Exception error)
        {
            // The manager's stream never faults.
        }

        public void OnCompleted()
        {
            // Completed when the manager is disposed.
        }
    }

    internal sealed class StateObserver(Action<AgentSessionStateChanged> onNext) : IObserver<AgentSessionStateChanged>
    {
        public void OnNext(AgentSessionStateChanged value) => onNext(value);

        public void OnError(Exception error)
        {
            // The tracker's stream never faults.
        }

        public void OnCompleted()
        {
            // Completed when the tracker is disposed.
        }
    }
}

internal static class StepParkerExtensions
{
    /// <summary>Whether the parked handler has left the park point.</summary>
    public static bool HasResumed(this StepParker parker)
    {
        try
        {
            parker.WaitUntilResumed(TimeSpan.Zero);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
