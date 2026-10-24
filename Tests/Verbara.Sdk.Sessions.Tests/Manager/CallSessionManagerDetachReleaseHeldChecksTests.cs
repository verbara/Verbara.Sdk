using FluentAssertions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Sessions.Tests.Harness;
using Verbara.Sdk.Tests.Shared.Sessions;
using static Verbara.Sdk.Sessions.Tests.Manager.WrapUpFrames;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// The checks that keep a release and a handler already past its lookup apart: the walk's own check that the call it
/// selected is still held and has not ended once it holds the call's lock (a), the held check a handler makes first
/// thing under the call's lock after it found the call by a lookup (b), and the attachment gate's closed check for a
/// raise that took its handler list before the release closed the gate (c).
/// </summary>
/// <remarks>
/// Each test fails when the check it names is deleted; the rest of the suite stays green without it. Every window is
/// held open by parking a handler on the manager's step seam (<see cref="StepParker"/>), by a call-released subscriber,
/// or by a Live subscriber the test put ahead of the manager's — never by a sleep, and every wait is bounded.
/// </remarks>
[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class CallSessionManagerDetachReleaseHeldChecksTests
{
    private static readonly TimeSpan WrapUp = TimeSpan.FromSeconds(30);
    private const string Member = "PJSIP/agent2";

    // --- (a) The walk checks again, under the call's lock, that the selected call has not ended -------------------

    /// <summary>
    /// A call the walk selected and that a hangup ends before the walk takes its lock is left to its retention: it is
    /// ended, not released. The hangup is a handler of an attachment detached earlier with "keep", parked after its
    /// lookup and let go from the release notification of the call the walk takes first.
    /// </summary>
    [Fact]
    public async Task DetachFromServer_ShouldLeaveACallEndedAfterTheSelectionToItsRetention_WhenAHangupEndsItBeforeTheWalkTakesItsLock()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var x = rig.Arrival("a", "wx");
        var y = rig.Arrival("a", "wy");
        // The walk's own order: oldest first, then by session id.
        var (first, second, secondId) = x.CreatedAt < y.CreatedAt
            || (x.CreatedAt == y.CreatedAt && string.CompareOrdinal(x.SessionId, y.SessionId) < 0)
                ? (x, y, "wy")
                : (y, x, "wx");
        using var parker = StepParker.Install(rig.Manager, HandlerStep.RemovalFound, "a", ReleaseRig.CallerLeg(secondId));
        var hangup = ReleaseRig.OnThread(() => rig.HangUp("a", ReleaseRig.CallerLeg(secondId)));
        parker.WaitUntilParked(ReleaseRig.Bound);
        rig.Manager.DetachFromServer("a", DetachedCalls.Keep);

        var hangupDoneInWindow = false;
        var asked = 0;
        using var subscription = rig.Manager.Events.Subscribe(new CallSessionManagerDetachReleaseTests.Observer(e =>
        {
            if (e is CallReleasedEvent && e.SessionId == first.SessionId && Interlocked.Exchange(ref asked, 1) == 0)
            {
                // The walk holds the first call's lock and has selected the second; the hangup takes the second's.
                parker.LetGo();
                hangupDoneInWindow = ReleaseRig.Returns(hangup);
            }
        }));

        var release = ReleaseRig.OnThread(() => rig.Release("a"));
        var returned = ReleaseRig.Returns(release);
        parker.LetGo();

        new
        {
            returned,
            hangupDoneInWindow,
            Released = ReleaseRig.ResultOf(release)?.Select(c => c.SessionId).ToArray(),
            SecondKinds = rig.KindsOf(second.SessionId),
            SecondEnded = second.State is CallSessionState.Completed or CallSessionState.Failed,
            SecondInRetention = ReferenceEquals(rig.Manager.GetById(second.SessionId), second),
        }.Should().BeEquivalentTo(
            new
            {
                returned = true,
                hangupDoneInWindow = true,
                Released = new[] { first.SessionId },
                SecondKinds = new[] { nameof(CallStartedEvent), nameof(CallEndedEvent) },
                SecondEnded = true,
                SecondInRetention = true,
            },
            "a call the walk selected and a hangup ended before the walk took its lock is ended, not released: no "
            + "release notification follows its ending, it is not returned, and it stays held for its retention. "
            + rig.Describe());
    }

    // --- (b) A handler past its lookup checks, under the call's lock, that the call is still held -------------------

    /// <summary>
    /// A known agent's connect on server b that found a's call by its linkedid and is parked before the call's lock
    /// until a's release has let go of the call changes, counts and announces nothing: the agent's next call on b
    /// opens its wrap-up when it ends.
    /// </summary>
    [Fact]
    public async Task OnAgentConnected_ShouldCountNoLegAndAnnounceNothing_WhenItsCallIsReleasedBeforeItTakesTheLock()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await ReleaseRig.StartAsync(clock, new SessionOptions { WrapUpDuration = WrapUp },
            serverIds: ["a", "b"]);
        var call = rig.Arrival("a", "ac");
        var linkedId = ReleaseRig.LinkedIdOf("ac");
        rig["b"].Deliver(AgentLogin("agent-2", Member, "login-b2"));
        using var parker = StepParker.Install(rig.Manager, HandlerStep.AgentConnectFound, "b", linkedId);
        var connect = ReleaseRig.OnThread(() => rig["b"].Deliver(
            AgentConnect("", "b-caller-ac", "PJSIP/trunk-b-caller-ac", linkedId, Member, "b-agent-ac",
                MemberChannel(Member, "b-agent-ac"), "agent-2")));
        parker.WaitUntilParked(ReleaseRig.Bound);

        var release = ReleaseRig.OnThread(() => rig.Release("a"));
        var returned = ReleaseRig.Returns(release);
        parker.LetGo();
        var connected = ReleaseRig.Returns(connect);
        var leftovers = rig.Manager.ReleaseLeftovers([call]);

        rig["b"].Deliver(NamedAgentCallConnected("an", "anm", Member, "agent-2"));
        var next = rig.Manager.GetByLinkedId("an")!;
        rig["b"].Deliver(NamedAgentCallEnds("an", "anm"));
        clock.Advance(WrapUp);
        clock.Advance(TimeSpan.FromMinutes(5));

        new
        {
            returned,
            connected,
            Released = ReleaseRig.ResultOf(release)?.Select(c => c.SessionId).ToArray(),
            AfterRelease = rig.KindsOf(call.SessionId).SkipWhile(k => k != nameof(CallReleasedEvent)).Skip(1).ToArray(),
            AgentRecorded = call.AgentId is not null,
            Leftovers = leftovers,
            WrapUps = rig.Events.Select(e => e.Event).OfType<CallWrapUpEvent>().Select(w => w.SessionId).ToArray(),
        }.Should().BeEquivalentTo(
            new
            {
                returned = true,
                connected = true,
                Released = new[] { call.SessionId },
                AfterRelease = Array.Empty<string>(),
                AgentRecorded = false,
                Leftovers = Array.Empty<string>(),
                WrapUps = new[] { next.SessionId },
            },
            "an agent's connect let go after the release of the call it found finds the call no longer held: it "
            + "records no agent, counts no leg on the released call and announces nothing after the release, so the "
            + "agent's connected count returns to zero and its next call opens a wrap-up. " + rig.Describe());
    }

    /// <summary>
    /// A queue join on server b that found a's call by the caller's channel id and is parked before the call's lock
    /// until a's release has let go of the call opens no visit: nothing is queued after the release and the queue
    /// counts no caller waiting.
    /// </summary>
    [Fact]
    public async Task OnQueueCallerJoined_ShouldOpenNoVisit_WhenItsCallIsReleasedBeforeItTakesTheLock()
    {
        await using var rig = await ReleaseRig.StartAsync("a", "b");
        using var queues = new QueueSessionTracker(rig.Manager, Options.Create(new SessionOptions()));
        var call = rig.Arrival("a", "qf");
        var caller = ReleaseRig.CallerLeg("qf");
        using var parker = StepParker.Install(rig.Manager, HandlerStep.QueueJoinFound, "b", caller);
        var join = ReleaseRig.OnThread(() =>
            rig["b"].Deliver(Join("q1", "PJSIP/trunk-qf-a", caller, ReleaseRig.LinkedIdOf("qf"))));
        parker.WaitUntilParked(ReleaseRig.Bound);

        var release = ReleaseRig.OnThread(() => rig.Release("a"));
        var returned = ReleaseRig.Returns(release);
        parker.LetGo();
        var joined = ReleaseRig.Returns(join);

        new
        {
            returned,
            joined,
            Released = ReleaseRig.ResultOf(release)?.Select(c => c.SessionId).ToArray(),
            Kinds = rig.KindsOf(call.SessionId),
            VisitWritten = call.QueueName is not null,
            Waiting = queues.GetByQueueName("q1")?.CallsWaiting ?? 0,
        }.Should().BeEquivalentTo(
            new
            {
                returned = true,
                joined = true,
                Released = new[] { call.SessionId },
                Kinds = new[] { nameof(CallStartedEvent), nameof(CallReleasedEvent) },
                VisitWritten = false,
                Waiting = 0,
            },
            "a queue join let go after the release of the call it found finds the call no longer held: it writes no "
            + "visit, announces no queueing after the release, and the queue counts no caller waiting. "
            + rig.Describe());
    }

    /// <summary>
    /// A hangup of a's call parked after its lookup, from an attachment detached earlier with "keep", and let go once
    /// a's release has let go of the call and a call of b has taken the same channel id, leaves b's call its entry:
    /// its removal is by object.
    /// </summary>
    [Fact]
    public async Task OnChannelRemoved_ShouldKeepTheEntryOfAnotherServersCall_WhenItIsLetGoAfterItsCallWasReleased()
    {
        await using var rig = await ReleaseRig.StartAsync("a", "b");
        var call = rig.Arrival("a", "ro");
        var leg = ReleaseRig.CallerLeg("ro");
        using var parker = StepParker.Install(rig.Manager, HandlerStep.RemovalFound, "a", leg);
        var hangup = ReleaseRig.OnThread(() => rig.HangUp("a", leg));
        parker.WaitUntilParked(ReleaseRig.Bound);
        rig.Manager.DetachFromServer("a", DetachedCalls.Keep);

        var released = rig.Release("a");
        rig["b"].Server.Channels.OnNewChannel(leg, "PJSIP/trunk-ro-b", Enums.ChannelState.Up, context: "from-trunk",
            linkedId: "ro-b-L");
        var ofB = rig.Manager.GetByLinkedId("ro-b-L");
        parker.LetGo();
        var hungUp = ReleaseRig.Returns(hangup);

        new
        {
            hungUp,
            Released = released.Select(c => c.SessionId).ToArray(),
            OfB = ofB is not null && ofB.ServerId == "b",
            ByChannelIsB = ofB is not null && ReferenceEquals(rig.Manager.GetByChannelId(leg), ofB),
            Kinds = rig.KindsOf(call.SessionId),
        }.Should().BeEquivalentTo(
            new
            {
                hungUp = true,
                Released = new[] { call.SessionId },
                OfB = true,
                ByChannelIsB = true,
                Kinds = new[] { nameof(CallStartedEvent), nameof(CallReleasedEvent) },
            },
            "a hangup let go after its call was released ends nothing, and removes the channel id's entry only while "
            + "it names that call: the call of b now holding the id keeps it. " + rig.Describe());
    }

    // --- (c) A raise that took its handler list before the release closed the gate ---------------------------------

    /// <summary>
    /// A channel arrival whose raise has taken its handler list — the manager's handler included — and is held by a
    /// subscriber ahead of the manager's while the release closes the attachment does not run the manager's handler
    /// once let go: no call is opened under the released id and nothing is announced.
    /// </summary>
    [Fact]
    public async Task OnChannelAdded_ShouldNotRun_WhenItsRaiseTookTheHandlerListBeforeTheReleaseClosedTheGate()
    {
        await using var rig = await ReleaseRig.StartAsync();
        var server = await rig.NewInstanceAsync("a");
        var leg = ReleaseRig.CallerLeg("gc");
        using var reached = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();
        var heldPastBound = false;
        Action<AsteriskChannel> ahead = channel =>
        {
            if (channel.UniqueId != leg)
                return;
            reached.Set();
            heldPastBound = !letGo.Wait(ReleaseRig.Bound);
        };
        server.Server.Channels.ChannelAdded += ahead;
        try
        {
            rig.Manager.AttachToServer(server.Server, "a");
            rig.Current(server);
            var arrival = ReleaseRig.OnThread(() => server.Server.Channels.OnNewChannel(leg, "PJSIP/trunk-gc-a",
                Enums.ChannelState.Up, context: "from-trunk", linkedId: ReleaseRig.LinkedIdOf("gc")));
            reached.Wait(ReleaseRig.Bound).Should().BeTrue("premise: the arrival's raise reached the subscriber ahead");

            var release = ReleaseRig.OnThread(() => rig.Release("a"));
            var returned = ReleaseRig.Returns(release);
            letGo.Set();
            var arrived = ReleaseRig.Returns(arrival);

            new
            {
                returned,
                arrived,
                heldPastBound,
                Released = ReleaseRig.ResultOf(release)?.Count,
                ByLinkedId = rig.Manager.GetByLinkedId(ReleaseRig.LinkedIdOf("gc")) is not null,
                ByChannel = rig.Manager.GetByChannelId(leg) is not null,
                HeldUnderA = rig.Manager.ActiveSessions.Count(s => s.ServerId == "a"),
                Notifications = rig.Events.Count,
            }.Should().BeEquivalentTo(
                new
                {
                    returned = true,
                    arrived = true,
                    heldPastBound = false,
                    Released = (int?)0,
                    ByLinkedId = false,
                    ByChannel = false,
                    HeldUnderA = 0,
                    Notifications = 0,
                },
                "the manager's handler, taken into the raise before the release closed the gate and reached only "
                + "after it, is refused by the closed gate: no call opens under the released id and nothing is "
                + "announced. " + rig.Describe());
        }
        finally
        {
            letGo.Set();
            server.Server.Channels.ChannelAdded -= ahead;
        }
    }
}
