using FluentAssertions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Sessions.Tests.Harness;
using static Verbara.Sdk.Sessions.Tests.Manager.WrapUpFrames;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// An agent's or a queue member's wrap-up as the session manager reports it: when it opens, when it closes, and the
/// one <see cref="CallWrapUpEvent"/> that reports each, on the real manager with no agent tracker resolved.
/// </summary>
/// <remarks>
/// <para>
/// A queue call's wrap-up opens at the member's <c>AgentComplete</c>, keyed on the member that frame names; a call
/// outside any queue that an agent known by name took opens it when the call ends. It closes when the configured
/// duration has passed on the manager's clock, or earlier at the same agent's or member's next connect, and opens
/// only when that agent or member has no other connected call left. A member interface names a member only together
/// with the server that reported it.
/// </para>
/// <para>
/// Every case but the races runs on <see cref="FakeTimeProvider"/>, whose timers fire on the test's thread inside
/// <see cref="FakeTimeProvider.Advance"/>. The races run on <see cref="ThreadedTimeProvider"/>, whose timers fire on
/// the thread pool. No test sleeps.
/// </para>
/// </remarks>
[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class CallSessionManagerWrapUpTests
{
    private const string Queue = "sales";
    private const string Member = "PJSIP/agent1";
    private static readonly TimeSpan WrapUp = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly string[] FirstOnly = ["first"];

    private static SessionOptions Options(TimeSpan? wrapUp = null) => new() { WrapUpDuration = wrapUp ?? WrapUp };

    // --- Reds: the manager raises no CallWrapUpEvent on unfixed code -------------------------------------------

    [Fact]
    public async Task AgentComplete_ShouldRaiseOneWrapUpWhenTheDurationPasses_WhenAQueueMemberThatIsNoNamedAgentHangsUp()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, Options());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
        var sessionId = rig.Session("c1").SessionId;

        var atComplete = rig.WrapUps.Count;
        clock.Advance(WrapUp - TimeSpan.FromMilliseconds(1));
        var justBefore = rig.WrapUps.Count;
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var atDue = rig.WrapUps.ToArray();
        clock.Advance(TimeSpan.FromMinutes(5));

        new { AtComplete = atComplete, JustBefore = justBefore, AtDue = atDue, After = rig.WrapUps.ToArray() }
            .Should().BeEquivalentTo(
                new
                {
                    AtComplete = 0,
                    JustBefore = 0,
                    AtDue = new[] { new CallWrapUpEvent(sessionId, ServerId, completedAt + WrapUp, null, Queue, WrapUp) },
                    After = new[] { new CallWrapUpEvent(sessionId, ServerId, completedAt + WrapUp, null, Queue, WrapUp) },
                },
                "the member's AgentComplete opens its wrap-up: nothing is raised then or a millisecond before the "
                + "duration passes, and exactly one CallWrapUpEvent is raised when it does, stamped on the manager's "
                + "clock, with no agent (the member is no agent known by name) and the call's queue. "
                + Describe(rig));
    }

    [Fact]
    public async Task CallEnded_ShouldRaiseOneWrapUpCarryingTheAgentAfterTheEnding_WhenAnAgentKnownByNameTookACallOutsideAnyQueue()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, Options());
        rig.Deliver(AgentLogin("agent-1", "PJSIP/agent1", "login-1"));
        rig.Deliver(NamedAgentCallConnected("n1", "nm1", Member, "agent-1"));
        clock.Advance(TimeSpan.FromSeconds(45));
        var endedAt = clock.GetUtcNow();
        rig.Deliver(NamedAgentCallEnds("n1", "nm1"));
        var sessionId = rig.Session("n1").SessionId;

        var atEnd = rig.WrapUps.Count;
        clock.Advance(WrapUp);
        clock.Advance(TimeSpan.FromMinutes(5));
        var kinds = Kinds(rig, sessionId);

        new { AtEnd = atEnd, WrapUps = rig.WrapUps.ToArray(), Order = kinds[^2..] }.Should().BeEquivalentTo(
            new
            {
                AtEnd = 0,
                WrapUps = new[] { new CallWrapUpEvent(sessionId, ServerId, endedAt + WrapUp, "agent-1", null, WrapUp) },
                Order = new[] { nameof(CallEndedEvent), nameof(CallWrapUpEvent) },
            },
            "a call outside any queue that an agent known by name took opens the agent's wrap-up when the call ends: "
            + "one CallWrapUpEvent when the duration passes, carrying the agent and no queue, raised after the call's "
            + "CallEndedEvent. " + Describe(rig));
    }

    [Fact]
    public async Task AgentConnect_ShouldCloseTheOpenWrapUpBeforeAnnouncingTheCall_WhenTheSameMemberTakesTheNextCallDuringIt()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, Options());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
        var first = rig.Session("c1").SessionId;

        clock.Advance(TimeSpan.FromSeconds(12));
        var before = rig.Events.Count;
        rig.Deliver(QueueCallConnected(Queue, "c2", "m2", Member));
        var second = rig.Session("c2").SessionId;
        var atConnect = rig.Events.Skip(before)
            .Where(e => e is CallWrapUpEvent or CallConnectedEvent)
            .Select(e => $"{e.GetType().Name}:{(e.SessionId == first ? "first" : e.SessionId == second ? "second" : "?")}")
            .ToArray();
        clock.Advance(TimeSpan.FromMinutes(5));

        new { AtConnect = atConnect, WrapUps = rig.WrapUps.ToArray() }.Should().BeEquivalentTo(
            new
            {
                AtConnect = new[] { $"{nameof(CallWrapUpEvent)}:first", $"{nameof(CallConnectedEvent)}:second" },
                WrapUps = new[]
                {
                    new CallWrapUpEvent(first, ServerId, completedAt + TimeSpan.FromSeconds(12), null, Queue,
                        TimeSpan.FromSeconds(12)),
                },
            },
            "the member's next connect, 12 s into its wrap-up, closes it with the 12 s measured, before that call's "
            + "CallConnectedEvent, and nothing more is raised for it when 30 s would have passed. " + Describe(rig));
    }

    [Fact]
    public async Task AgentComplete_ShouldCloseTheOpenWrapUpAndOpenANewOne_WhenAnotherLegOfTheSameMemberEndsDuringIt()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, Options());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        // The second call's connect is not observed (a frame lost, or a call the SDK learned already connected), so
        // its member's leg is still up when the first call's wrap-up opens.
        rig.Deliver(QueueCallConnected(Queue, "c2", "m2", Member, withConnect: false));
        clock.Advance(TimeSpan.FromSeconds(60));
        var firstCompletedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(8));
        var secondCompletedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp(Queue, "c2", "m2", Member));
        var first = rig.Session("c1").SessionId;
        var second = rig.Session("c2").SessionId;

        var atSecondComplete = rig.WrapUps.ToArray();
        clock.Advance(WrapUp);
        clock.Advance(TimeSpan.FromMinutes(5));

        new { AtSecondComplete = atSecondComplete, WrapUps = rig.WrapUps.ToArray() }.Should().BeEquivalentTo(
            new
            {
                AtSecondComplete = new[]
                {
                    new CallWrapUpEvent(first, ServerId, secondCompletedAt, null, Queue, TimeSpan.FromSeconds(8)),
                },
                WrapUps = new[]
                {
                    new CallWrapUpEvent(first, ServerId, firstCompletedAt + TimeSpan.FromSeconds(8), null, Queue,
                        TimeSpan.FromSeconds(8)),
                    new CallWrapUpEvent(second, ServerId, secondCompletedAt + WrapUp, null, Queue, WrapUp),
                },
            },
            "another leg of the same member ending 8 s into its wrap-up closes it with the 8 s measured and opens "
            + "a new one, which closes when its own 30 s have passed. " + Describe(rig));
    }

    [Fact]
    public async Task AgentComplete_ShouldRaiseAZeroLengthWrapUpAtOnceWithNoTimer_WhenWrapUpDurationIsZero()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, Options(TimeSpan.Zero));
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
        var sessionId = rig.Session("c1").SessionId;

        var atComplete = rig.WrapUps.ToArray();
        clock.Advance(TimeSpan.FromMinutes(5));

        new { AtComplete = atComplete, After = rig.WrapUps.Count, Timers = clock.TimersCreated.Count }
            .Should().BeEquivalentTo(
                new
                {
                    AtComplete = new[] { new CallWrapUpEvent(sessionId, ServerId, completedAt, null, Queue, TimeSpan.Zero) },
                    After = 1,
                    Timers = 0,
                },
                "a wrap-up duration of zero closes the wrap-up in the instant it opens, with a zero-length event "
                + "raised while the AgentComplete is handled, and creates no timer. " + Describe(rig));
    }

    [Fact]
    public async Task AgentComplete_ShouldOpenTheWrapUpWhileTheCallerIsStillUp_WhenTheMemberBlindTransfersTheCallerToAnIvr()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, Options());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var transferredAt = clock.GetUtcNow();
        rig.Deliver(MemberBlindTransfers(Queue, "c1", "m1", Member, "ivr", "700"));
        var sessionId = rig.Session("c1").SessionId;

        clock.Advance(WrapUp);
        var whileCallerInIvr = Kinds(rig, sessionId);
        var wrapUpsWhileCallerInIvr = rig.WrapUps.ToArray();
        clock.Advance(TimeSpan.FromSeconds(90));
        rig.Deliver(Hangup("c1"));
        clock.Advance(TimeSpan.FromMinutes(5));

        new
        {
            WrapUpsWhileCallerInIvr = wrapUpsWhileCallerInIvr,
            EndedWhileCallerInIvr = whileCallerInIvr.Contains(nameof(CallEndedEvent)),
            WrapUps = rig.WrapUps.Count,
            Ended = Kinds(rig, sessionId).Count(k => k == nameof(CallEndedEvent)),
        }.Should().BeEquivalentTo(
            new
            {
                WrapUpsWhileCallerInIvr = new[]
                {
                    new CallWrapUpEvent(sessionId, ServerId, transferredAt + WrapUp, null, Queue, WrapUp),
                },
                EndedWhileCallerInIvr = false,
                WrapUps = 1,
                Ended = 1,
            },
            "a blind transfer to an IVR raises the member's AgentComplete (Reason: transfer) while the caller stays "
            + "up: the wrap-up opens there and closes 30 s later with the caller still in the IVR, and the caller's "
            + "later hangup ends the call and opens nothing more. " + Describe(rig));
    }

    [Fact]
    public async Task AgentConnect_ShouldLeaveTheFirstMembersWrapUpOpen_WhenOneCallVisitsTwoQueuesTakenByTwoMembers()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, Options());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var firstCompletedAt = clock.GetUtcNow();
        rig.Deliver(MemberBlindTransfers(Queue, "c1", "m1", Member, "queues", "support"));
        var sessionId = rig.Session("c1").SessionId;

        clock.Advance(TimeSpan.FromSeconds(2));
        rig.Deliver(SecondVisitConnected("support", "c1", "m2", "PJSIP/agent2"));
        clock.Advance(TimeSpan.FromSeconds(3));
        var atSecondConnect = rig.WrapUps.Count;
        clock.Advance(TimeSpan.FromSeconds(15));
        var secondCompletedAt = clock.GetUtcNow();
        rig.Deliver(SecondMemberHangsUp("support", "c1", "m2", "PJSIP/agent2", Bridge("c1") + "-2"));
        clock.Advance(TimeSpan.FromMinutes(5));

        new { AtSecondConnect = atSecondConnect, WrapUps = rig.WrapUps.ToArray() }.Should().BeEquivalentTo(
            new
            {
                AtSecondConnect = 0,
                WrapUps = new[]
                {
                    new CallWrapUpEvent(sessionId, ServerId, firstCompletedAt + WrapUp, null, Queue, WrapUp),
                    new CallWrapUpEvent(sessionId, ServerId, secondCompletedAt + WrapUp, null, "support", WrapUp),
                },
            },
            "each member's AgentComplete opens its own wrap-up, keyed on that member: the second member's connect "
            + "does not close the first member's, and each closes after its own 30 s. " + Describe(rig));
    }

    [Fact]
    public async Task AgentComplete_ShouldOpenNoWrapUpUntilTheMembersLastConnectedCallEnds_WhenTheMemberTakesTwoCallsAtOnce()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, Options());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        rig.Deliver(QueueCallConnected(Queue, "c2", "m2", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(40));
        var whileSecondUp = rig.WrapUps.Count;
        var secondCompletedAt = clock.GetUtcNow();
        rig.Deliver(MemberHangsUp(Queue, "c2", "m2", Member));
        var second = rig.Session("c2").SessionId;
        clock.Advance(WrapUp);
        clock.Advance(TimeSpan.FromMinutes(5));

        new { WhileSecondUp = whileSecondUp, WrapUps = rig.WrapUps.ToArray() }.Should().BeEquivalentTo(
            new
            {
                WhileSecondUp = 0,
                WrapUps = new[] { new CallWrapUpEvent(second, ServerId, secondCompletedAt + WrapUp, null, Queue, WrapUp) },
            },
            "with ringinuse a member can finish one call while another is up: that AgentComplete opens nothing, and "
            + "the AgentComplete of its last connected call opens the one wrap-up. " + Describe(rig));
    }

    [Fact]
    public async Task AgentConnect_ShouldLeaveTheWrapUpOnTheFirstServerOpen_WhenTheSecondServerConnectsItsOwnMemberOfTheSameInterface()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, Options(), "s1", "s2");
        rig.DeliverTo("s1", QueueCallConnected(Queue, "s1c1", "s1m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        var completedAt = clock.GetUtcNow();
        rig.DeliverTo("s1", MemberHangsUp(Queue, "s1c1", "s1m1", Member));
        var sessionId = rig.Session("s1c1").SessionId;

        clock.Advance(TimeSpan.FromSeconds(5));
        rig.DeliverTo("s2", QueueCallConnected(Queue, "s2c1", "s2m1", Member));
        var atOtherConnect = rig.WrapUps.Count;
        clock.Advance(TimeSpan.FromSeconds(25));
        clock.Advance(TimeSpan.FromMinutes(5));

        new { AtOtherConnect = atOtherConnect, WrapUps = rig.WrapUps.ToArray() }.Should().BeEquivalentTo(
            new
            {
                AtOtherConnect = 0,
                WrapUps = new[] { new CallWrapUpEvent(sessionId, "s1", completedAt + WrapUp, null, Queue, WrapUp) },
            },
            "a member interface names a member only on the server that reported it: the second server's "
            + $"{Member} taking a call leaves the first server's wrap-up open, and it closes after its own 30 s. "
            + Describe(rig));
    }

    // --- Guards: green on unfixed code, and must stay green ------------------------------------------------------

    [Fact]
    public async Task CallEnded_ShouldNeverRaiseAWrapUp_WhenNoAgentOrMemberTookTheCall()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, Options());
        rig.Deliver(NewChannel("ivr1", CallerChannel("ivr1"), "Up", "ivr1", context: "ivr", exten: "700"));
        clock.Advance(TimeSpan.FromSeconds(20));
        rig.Deliver(Hangup("ivr1"));
        clock.Advance(TimeSpan.FromHours(1));

        rig.WrapUps.Should().BeEmpty(
            "Guard: an IVR call that no dial or queue connected to anyone opens no wrap-up. " + Describe(rig));
    }

    [Fact]
    public async Task DisposeAsync_ShouldRaiseNoWrapUpAndCompleteTheStream_WhenAWrapUpIsOpen()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, Options());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(10));

        Exception? escaped = null;
        try
        {
            await rig.DisposeManagerAsync();
            clock.Advance(TimeSpan.FromMinutes(5));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            escaped = ex;
        }

        new { WrapUps = rig.WrapUps.Count, rig.Completed, Escaped = escaped?.GetType().Name }.Should().BeEquivalentTo(
            new { WrapUps = 0, Completed = true, Escaped = default(string) },
            "Guard: a manager disposed 10 s into a wrap-up raises no CallWrapUpEvent for it, then or when its timer "
            + "would have come due, and its event stream completes. " + Describe(rig));
    }

    // --- Races: on the threaded fake, timers fire on the thread pool ---------------------------------------------

    [Fact]
    public async Task DisposeAsync_ShouldCompleteOnlyAfterARunningCloseReturnsAndThrowNothing_WhenASubscriberIsStillHandlingTheWrapUp()
    {
        var clock = new ThreadedTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, Options());
        var gate = new GatedSubscriber();
        using var subscription = rig.Manager.Events.Subscribe(gate);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));

        // Guard on unfixed code: no wrap-up opens, so no timer exists and there is no close to race.
        if (clock.TimersCreated == 0)
        {
            await rig.DisposeManagerAsync();
            new { clock.Faults, rig.Completed }.Should().BeEquivalentTo(
                new { Faults = Array.Empty<Exception>(), Completed = true },
                "Guard (nothing fires on unfixed code): with no wrap-up open, disposal throws nothing and completes "
                + "the stream. " + Describe(rig));
            return;
        }

        var fired = clock.Advance(WrapUp);
        (await Task.WhenAny(gate.Entered, Task.Delay(Bound, TimeProvider.System))) // fence-allow: GUARD-TIMEOUT — failure bound on the close reaching the subscriber, never the winning arm of a green run
            .Should().BeSameAs(gate.Entered, "premise: the wrap-up's close reached the subscriber. " + Describe(rig));

        // Started on the test's thread: the disposal runs synchronously up to its first wait that has not completed,
        // so when the call returns it has either completed or is waiting, and nothing has been released yet.
        var released = 0;
        var disposal = DisposeThenRead(rig, () => Volatile.Read(ref released));
        var completedBeforeRelease = disposal.IsCompleted;
        Volatile.Write(ref released, 1);
        gate.Release();

        Exception? escaped = null;
        var releasedWhenDisposed = -1;
        try
        {
            releasedWhenDisposed = await disposal.WaitAsync(Bound);
            await Task.WhenAll(fired).WaitAsync(Bound);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            escaped = ex;
        }

        new
        {
            CompletedBeforeRelease = completedBeforeRelease,
            ReleasedWhenDisposed = releasedWhenDisposed,
            Escaped = escaped?.ToString(),
            Faults = clock.Faults.Select(f => f.ToString()).ToArray(),
            WrapUps = rig.WrapUps.Count,
            rig.Completed,
        }.Should().BeEquivalentTo(
            new
            {
                CompletedBeforeRelease = false,
                ReleasedWhenDisposed = 1,
                Escaped = default(string),
                Faults = Array.Empty<string>(),
                WrapUps = 1,
                Completed = true,
            },
            "a disposal that starts while a subscriber is still handling a wrap-up's close completes only after "
            + "that close has returned, and nothing is thrown: not by the disposal, not on the pool thread that ran "
            + "the close. " + Describe(rig));
    }

    [Fact]
    public async Task TimerCallback_ShouldNotCloseTheWrapUpThatReplacedItsOwn_WhenItRunsAfterTheReplacement()
    {
        var clock = new ThreadedTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, Options());
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        rig.Deliver(QueueCallConnected(Queue, "c2", "m2", Member, withConnect: false));
        clock.Advance(TimeSpan.FromSeconds(60));
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));

        // Guard on unfixed code: no wrap-up opens, so no timer exists and nothing can be replaced.
        if (clock.TimersCreated == 0)
        {
            rig.Deliver(MemberHangsUp(Queue, "c2", "m2", Member));
            clock.Advance(TimeSpan.FromMinutes(5));
            clock.Faults.Should().BeEmpty("Guard (nothing fires on unfixed code). " + Describe(rig));
            return;
        }

        var hold = clock.HoldNextFire();
        var firstFired = clock.Advance(WrapUp);
        (await Task.WhenAny(hold.Reached, Task.Delay(Bound, TimeProvider.System))) // fence-allow: GUARD-TIMEOUT — failure bound on the parked callback, never the winning arm of a green run
            .Should().BeSameAs(hold.Reached, "premise: the first wrap-up's timer fired and its callback is parked. "
                + Describe(rig));

        // The replacement, while the first callback is in flight; bounded, so a replacement that waits for that
        // callback reports instead of hanging the run.
        var replacedAt = clock.GetUtcNow();
        var replacement = Task.Run(() => rig.Deliver(MemberHangsUp(Queue, "c2", "m2", Member)));
        var replacedInTime = await Task.WhenAny(replacement, Task.Delay(Bound, TimeProvider.System)) == replacement; // fence-allow: GUARD-TIMEOUT — failure bound on the replacement, never the winning arm of a green run
        hold.Release();
        await Task.WhenAll(firstFired).WaitAsync(Bound);
        var afterStaleCallback = rig.WrapUps.ToArray();
        await Task.WhenAll(clock.Advance(WrapUp)).WaitAsync(Bound);
        var first = rig.Session("c1").SessionId;
        var second = rig.Session("c2").SessionId;

        new
        {
            ReplacedInTime = replacedInTime,
            AfterStaleCallback = afterStaleCallback.Select(e => e.SessionId == second ? "second" : "first").ToArray(),
            Second = rig.WrapUps.Where(e => e.SessionId == second).ToArray(),
            Faults = clock.Faults.Select(f => f.ToString()).ToArray(),
        }.Should().BeEquivalentTo(
            new
            {
                ReplacedInTime = true,
                AfterStaleCallback = FirstOnly,
                Second = new[] { new CallWrapUpEvent(second, ServerId, replacedAt + WrapUp, null, Queue, WrapUp) },
                Faults = Array.Empty<string>(),
            },
            $"the first wrap-up ({first}) is closed once, by the replacement; its timer's callback, in flight when "
            + "the replacement opened the second, raises nothing and leaves the second open until its own timer. "
            + Describe(rig));
    }

    // --- 1.9: a subscriber that throws on the wrap-up -----------------------------------------------------------

    [Fact]
    public async Task TimerClose_ShouldLetNothingEscapeAndReachTheOtherSubscriber_WhenASubscriberThrowsOnTheWrapUp()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, Options());
        using var thrower = rig.Manager.Events.Subscribe(new ThrowsOnWrapUp());
        var second = new List<SessionDomainEvent>();
        using var recorder = rig.Manager.Events.Subscribe(second.Add);
        rig.Deliver(QueueCallConnected(Queue, "c1", "m1", Member));
        clock.Advance(TimeSpan.FromSeconds(60));
        rig.Deliver(MemberHangsUp(Queue, "c1", "m1", Member));

        Exception? escaped = null;
        try
        {
            clock.Advance(WrapUp);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            escaped = ex;
        }

        new { Escaped = escaped?.ToString(), SecondReceived = second.OfType<CallWrapUpEvent>().Count() }
            .Should().BeEquivalentTo(
                new { Escaped = default(string), SecondReceived = 1 },
                "a subscriber that throws on CallWrapUpEvent does not let its exception out of the timer's close "
                + "(on a pool-thread timer that would end the process), and the second subscriber still receives "
                + "the one wrap-up. " + Describe(rig));
    }

    // --- Frames and helpers ---------------------------------------------------------------------------------------

    /// <summary>Disposes the rig's manager, then returns what <paramref name="read"/> reads at that moment.</summary>
    private static async Task<int> DisposeThenRead(WrapUpRig rig, Func<int> read)
    {
        await rig.DisposeManagerAsync();
        return read();
    }

    private static List<string> Kinds(WrapUpRig rig, string sessionId) =>
        [.. rig.Events.Where(e => e.SessionId == sessionId).Select(e => e.GetType().Name)];

    private static string Describe(WrapUpRig rig) =>
        "Measured events: [" + string.Join(", ", rig.Events.Select(e => e is CallWrapUpEvent w
            ? $"CallWrapUpEvent({w.SessionId}, {w.ServerId}, {w.Timestamp:HH:mm:ss.fff}, {w.AgentId ?? "null"}, "
              + $"{w.QueueName ?? "null"}, {w.WrapUpDuration})"
            : $"{e.GetType().Name}({e.SessionId})")) + "]";

    /// <summary>Blocks inside <c>OnNext</c> on the first <see cref="CallWrapUpEvent"/> until released.</summary>
    private sealed class GatedSubscriber : IObserver<SessionDomainEvent>
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _released.TrySetResult();

        public void OnNext(SessionDomainEvent value)
        {
            if (value is not CallWrapUpEvent || !_entered.TrySetResult())
                return;

            if (!_released.Task.Wait(Bound))
                throw new TimeoutException("The gated subscriber was never released.");
        }

        public void OnError(Exception error)
        {
            // The manager's stream never faults; nothing to do.
        }

        public void OnCompleted()
        {
            // Completion is read from the rig's own recorder.
        }
    }

    /// <summary>A subscriber that throws on every <see cref="CallWrapUpEvent"/>.</summary>
    private sealed class ThrowsOnWrapUp : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value)
        {
            if (value is CallWrapUpEvent)
                throw new InvalidOperationException("A subscriber failed on CallWrapUpEvent.");
        }

        public void OnError(Exception error)
        {
            // The manager's stream never faults; nothing to do.
        }

        public void OnCompleted()
        {
            // Completion is not under test here.
        }
    }
}
