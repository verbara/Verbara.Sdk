using System.Collections.Concurrent;
using System.Reactive.Subjects;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Sessions.Tests.Harness;
using Verbara.Sdk.Sessions.Tests.Manager;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Sdk.Sessions.Tests;

[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class AgentSessionTrackerTests : IDisposable
{
    private static readonly string[] AgentAOnCall = ["agent-A: Idle -> OnCall"];

    private readonly Subject<SessionDomainEvent> _events = new();
    private readonly ICallSessionManager _manager;
    private readonly AgentSessionTracker _sut;

    public AgentSessionTrackerTests()
    {
        _manager = Substitute.For<ICallSessionManager>();
        _manager.Events.Returns(_events);
        var options = Options.Create(new SessionOptions());
        _sut = new AgentSessionTracker(_manager, options, NullLogger<AgentSessionTracker>.Instance);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _events.Dispose();
    }

    [Fact]
    public void GetByAgentId_ShouldReturnNull_WhenAgentNotTracked()
    {
        _sut.GetByAgentId("unknown").Should().BeNull();
    }

    [Fact]
    public void OnCallConnected_ShouldTransitionToOnCall_WhenAgentIdPresent()
    {
        var session = CreateCallSession("sess-1");
        _manager.GetById("sess-1").Returns(session);

        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.FromSeconds(5)));

        var agent = _sut.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.State.Should().Be(AgentSessionState.OnCall);
        agent.CurrentCall.Should().BeSameAs(session);
        agent.CurrentQueueName.Should().Be("queue-1");
    }

    [Fact]
    public void OnCallConnected_ShouldIgnore_WhenAgentIdNull()
    {
        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            null, "queue-1", TimeSpan.FromSeconds(5)));

        _sut.ActiveAgents.Should().BeEmpty();
    }

    [Fact]
    public void OnCallEnded_ShouldTransitionToWrapUp_WhenAgentOnCall()
    {
        var session = CreateCallSession("sess-1");
        _manager.GetById("sess-1").Returns(session);

        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.Zero));

        _events.OnNext(new CallEndedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            HangupCause.NormalClearing, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(4)));

        var agent = _sut.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.State.Should().Be(AgentSessionState.WrapUp);
        agent.CallsHandled.Should().Be(1);
    }

    // This test and the two OnWrapUp tests below write CallWrapUpEvent by hand: they test the tracker as a consumer of
    // the event, not the session manager as its producer. The end-to-end cases further down feed the tracker from the
    // real manager.

    [Fact]
    public void OnCallEnded_ShouldAccumulateTalkTime_WhenTheWrapUpBetweenTheCallsIsFedByHand()
    {
        var session1 = CreateCallSession("sess-1");
        var session2 = CreateCallSession("sess-2");
        _manager.GetById("sess-1").Returns(session1);
        _manager.GetById("sess-2").Returns(session2);

        // First call
        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.Zero));
        _events.OnNext(new CallEndedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            HangupCause.NormalClearing, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3)));
        _events.OnNext(new CallWrapUpEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.FromSeconds(10)));

        // Second call
        _events.OnNext(new CallConnectedEvent("sess-2", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.Zero));
        _events.OnNext(new CallEndedEvent("sess-2", "srv-1", DateTimeOffset.UtcNow,
            HangupCause.NormalClearing, TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(6)));

        var agent = _sut.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.CallsHandled.Should().Be(2);
        agent.TotalTalkTime.Should().Be(TimeSpan.FromMinutes(9));
    }

    [Fact]
    public void OnRingNoAnswer_ShouldIncrementCallsMissed()
    {
        _events.OnNext(new CallRingNoAnswerEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1"));
        _events.OnNext(new CallRingNoAnswerEvent("sess-2", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1"));

        var agent = _sut.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.CallsMissed.Should().Be(2);
    }

    [Fact]
    public void OnWrapUp_ShouldTransitionToIdle_WhenAWrapUpEventIsFedByHand()
    {
        var session = CreateCallSession("sess-1");
        _manager.GetById("sess-1").Returns(session);

        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.Zero));
        _events.OnNext(new CallEndedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            HangupCause.NormalClearing, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(4)));
        _events.OnNext(new CallWrapUpEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.FromSeconds(30)));

        var agent = _sut.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.State.Should().Be(AgentSessionState.Idle);
        agent.CurrentCall.Should().BeNull();
        agent.CurrentQueueName.Should().BeNull();
    }

    [Fact]
    public void OnWrapUp_ShouldAccumulateWrapUpTime_WhenAWrapUpEventIsFedByHand()
    {
        var session = CreateCallSession("sess-1");
        _manager.GetById("sess-1").Returns(session);

        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.Zero));
        _events.OnNext(new CallEndedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            HangupCause.NormalClearing, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(4)));
        _events.OnNext(new CallWrapUpEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.FromSeconds(25)));

        var agent = _sut.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.TotalWrapUpTime.Should().Be(TimeSpan.FromSeconds(25));
    }

    [Fact]
    public void StateChanges_ShouldPublish_WhenStateTransitions()
    {
        var session = CreateCallSession("sess-1");
        _manager.GetById("sess-1").Returns(session);

        var changes = new List<AgentSessionStateChanged>();
        using var sub = _sut.StateChanges.Subscribe(new StateChangeObserver(changes));

        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.Zero));

        changes.Should().HaveCount(1);
        changes[0].AgentId.Should().Be("agent-1");
        changes[0].PreviousState.Should().Be(AgentSessionState.Idle);
        changes[0].NewState.Should().Be(AgentSessionState.OnCall);
    }

    [Fact]
    public void ActiveAgents_ShouldReturnAllTrackedAgents()
    {
        _manager.GetById("sess-1").Returns(CreateCallSession("sess-1"));
        _manager.GetById("sess-2").Returns(CreateCallSession("sess-2"));

        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.Zero));
        _events.OnNext(new CallConnectedEvent("sess-2", "srv-1", DateTimeOffset.UtcNow,
            "agent-2", "queue-1", TimeSpan.Zero));

        _sut.ActiveAgents.Should().HaveCount(2);
    }

    [Fact]
    public void GetByState_ShouldFilterCorrectly()
    {
        _manager.GetById("sess-1").Returns(CreateCallSession("sess-1"));
        _manager.GetById("sess-2").Returns(CreateCallSession("sess-2"));

        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.Zero));
        _events.OnNext(new CallConnectedEvent("sess-2", "srv-1", DateTimeOffset.UtcNow,
            "agent-2", "queue-1", TimeSpan.Zero));
        _events.OnNext(new CallEndedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            HangupCause.NormalClearing, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(4)));

        _sut.GetByState(AgentSessionState.OnCall).Should().HaveCount(1);
        _sut.GetByState(AgentSessionState.WrapUp).Should().HaveCount(1);
    }

    [Fact]
    public void StateChanges_ShouldReachALaterSubscriberAndReportTheAgent_WhenAnEarlierSubscriberThrows()
    {
        // The manager's notifications served as the engine serves them: each subscriber behind its own guard, whose
        // report is a session notification's. A state-change failure reported here is reported under the wrong stream.
        using var events = new Subject<SessionDomainEvent>();
        var sessionReports = new List<string>();
        var guardedEvents = new SubscriberGuardedObservable<SessionDomainEvent>(
            events,
            reportNext: (_, evt) => sessionReports.Add(evt.GetType().Name),
            reportTerminal: (_, signal) => sessionReports.Add(signal));
        var manager = Substitute.For<ICallSessionManager>();
        manager.Events.Returns(guardedEvents);
        manager.GetById("sess-1").Returns(CreateCallSession("sess-1"));
        var logger = new RecordingLogger();
        using var faults = new SubscriberFaultCounter();
        using var tracker = new AgentSessionTracker(manager, Options.Create(new SessionOptions()), logger);
        var received = new List<AgentSessionStateChanged>();
        using var thrower = tracker.StateChanges.Subscribe(new ThrowingStateChangeObserver());
        using var recorder = tracker.StateChanges.Subscribe(new StateChangeObserver(received));

        Exception? escaped = null;
        try
        {
            events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
                "agent-A", "queue-1", TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            escaped = ex;
        }

        new
        {
            Received = received.Select(c => $"{c.AgentId}: {c.PreviousState} -> {c.NewState}").ToArray(),
            TrackerState = tracker.GetByAgentId("agent-A")?.State,
            Warnings = logger.Warnings(),
            SessionReports = sessionReports.ToArray(),
            Counted = faults.Total,
            Escaped = escaped?.GetType().Name,
        }.Should().BeEquivalentTo(
            new
            {
                Received = AgentAOnCall,
                TrackerState = (AgentSessionState?)AgentSessionState.OnCall,
                Warnings = new[]
                {
                    new StateChangeReport("agent-A", nameof(AgentSessionState.Idle), nameof(AgentSessionState.OnCall),
                        nameof(InvalidOperationException)),
                },
                SessionReports = Array.Empty<string>(),
                Counted = 1L,
                Escaped = default(string),
            },
            "a state change an earlier subscriber throws on still reaches the later one, the tracker keeps its "
            + "state, and the failure is reported once by the tracker, naming the agent and both states, not as a "
            + "failure of a subscriber of the session notifications");
    }

    // --- End to end: the tracker fed by the real session manager, on one fake clock --------------------------------
    //
    // No CallWrapUpEvent is written by hand here: the manager raises it, as it does for every consumer. The agent is
    // known by name and takes a call outside any queue, so its wrap-up opens when the call ends.

    private const string TrackedAgent = "agent-1";
    private const string TrackedMember = "PJSIP/agent1";
    private static readonly TimeSpan TrackedWrapUp = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WrapUp_ShouldEndInIdleWithTheMeasuredTime_WhenTheManagersWrapUpDurationPasses()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, new SessionOptions { WrapUpDuration = TrackedWrapUp });
        using var tracker = TrackerOn(rig, clock);
        rig.Deliver(WrapUpFrames.AgentLogin(TrackedAgent, TrackedMember, "login-1"));
        rig.Deliver(WrapUpFrames.NamedAgentCallConnected("a1", "am1", TrackedMember, TrackedAgent));
        clock.Advance(TimeSpan.FromMinutes(4));
        rig.Deliver(WrapUpFrames.NamedAgentCallEnds("a1", "am1"));
        var atEnd = tracker.GetByAgentId(TrackedAgent)?.State;

        clock.Advance(TrackedWrapUp);
        var agent = tracker.GetByAgentId(TrackedAgent);

        new { AtEnd = atEnd, agent?.State, agent?.TotalWrapUpTime, agent?.CurrentCall }.Should().BeEquivalentTo(
            new
            {
                AtEnd = (AgentSessionState?)AgentSessionState.WrapUp,
                State = (AgentSessionState?)AgentSessionState.Idle,
                TotalWrapUpTime = (TimeSpan?)TrackedWrapUp,
                CurrentCall = default(CallSession),
            },
            "the agent enters wrap-up when its call ends and leaves it for Idle when the manager's 30 s pass, with "
            + "those 30 s counted. " + Describe(rig));
    }

    [Fact]
    public async Task StateChanges_ShouldPublishWrapUpToIdle_WhenTheManagersWrapUpDurationPasses()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, new SessionOptions { WrapUpDuration = TrackedWrapUp });
        using var tracker = TrackerOn(rig, clock);
        var changes = new List<AgentSessionStateChanged>();
        using var sub = tracker.StateChanges.Subscribe(new StateChangeObserver(changes));
        rig.Deliver(WrapUpFrames.AgentLogin(TrackedAgent, TrackedMember, "login-1"));
        rig.Deliver(WrapUpFrames.NamedAgentCallConnected("a1", "am1", TrackedMember, TrackedAgent));
        clock.Advance(TimeSpan.FromMinutes(4));
        rig.Deliver(WrapUpFrames.NamedAgentCallEnds("a1", "am1"));
        clock.Advance(TrackedWrapUp);

        changes.Select(c => $"{c.AgentId}: {c.PreviousState} -> {c.NewState}").Should().Equal(
            [$"{TrackedAgent}: Idle -> OnCall", $"{TrackedAgent}: OnCall -> WrapUp", $"{TrackedAgent}: WrapUp -> Idle"],
            "the agent's state changes publish the end of its wrap-up as well as its start. " + Describe(rig));
    }

    [Fact]
    public async Task IdleTime_ShouldCountFromTheEndOfTheWrapUpOnTheTrackersClock_WhenTheWrapUpHasEnded()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, new SessionOptions { WrapUpDuration = TrackedWrapUp });
        using var tracker = TrackerOn(rig, clock);
        rig.Deliver(WrapUpFrames.AgentLogin(TrackedAgent, TrackedMember, "login-1"));
        rig.Deliver(WrapUpFrames.NamedAgentCallConnected("a1", "am1", TrackedMember, TrackedAgent));
        clock.Advance(TimeSpan.FromMinutes(4));
        rig.Deliver(WrapUpFrames.NamedAgentCallEnds("a1", "am1"));
        clock.Advance(TrackedWrapUp);
        clock.Advance(TimeSpan.FromSeconds(10));

        tracker.GetByAgentId(TrackedAgent)?.IdleTime.Should().Be(TimeSpan.FromSeconds(10),
            "the agent has been idle for the 10 s since its wrap-up ended, on the tracker's clock: not since its "
            + "call ended (40 s), and not on the wall clock. " + Describe(rig));
    }

    [Fact]
    public async Task AvgHandleTime_ShouldAddTheMeasuredWrapUpToTalkAndHold_WhenTheAgentsOneCallsWrapUpHasEnded()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, new SessionOptions { WrapUpDuration = TrackedWrapUp });
        using var tracker = TrackerOn(rig, clock);
        rig.Deliver(WrapUpFrames.AgentLogin(TrackedAgent, TrackedMember, "login-1"));
        rig.Deliver(WrapUpFrames.NamedAgentCallConnected("a1", "am1", TrackedMember, TrackedAgent));
        clock.Advance(TimeSpan.FromMinutes(4));
        rig.Deliver(WrapUpFrames.NamedAgentCallEnds("a1", "am1"));
        clock.Advance(TrackedWrapUp);
        var agent = tracker.GetByAgentId(TrackedAgent)
            ?? throw new InvalidOperationException("premise: the tracker follows the agent");

        // The call's talk time is the session's own, measured on the wall clock, so the handle time is read as what
        // it adds over talk and hold.
        new { agent.CallsHandled, OverTalkAndHold = agent.AvgHandleTime - (agent.TotalTalkTime + agent.TotalHoldTime) }
            .Should().BeEquivalentTo(
                new { CallsHandled = 1, OverTalkAndHold = TrackedWrapUp },
                "the handle time of the agent's one call is its talk and hold plus the 30 s of wrap-up measured. "
                + Describe(rig));
    }

    [Fact]
    public async Task OnWrapUp_ShouldKeepTheAgentOnItsNewCallAndAddTheTime_WhenTheEarlierCallsWrapUpArrivesAfterTheNextConnect()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, new SessionOptions { WrapUpDuration = TrackedWrapUp });

        // Subscribed before the tracker, so it sees the wrap-up first. When the first call's wrap-up is raised, it
        // delivers the agent's next connect before the event goes on to the tracker: the race in which the timer has
        // won the first wrap-up and the next call's connect is announced before the tracker sees that close.
        var connectedNext = false;
        using var race = rig.Manager.Events.Subscribe(evt =>
        {
            if (evt is not CallWrapUpEvent || connectedNext)
                return;

            connectedNext = true;
            rig.Deliver(WrapUpFrames.NamedAgentCallConnected("b1", "bm1", TrackedMember, TrackedAgent));
        });
        using var tracker = TrackerOn(rig, clock);
        rig.Deliver(WrapUpFrames.AgentLogin(TrackedAgent, TrackedMember, "login-1"));
        rig.Deliver(WrapUpFrames.NamedAgentCallConnected("a1", "am1", TrackedMember, TrackedAgent));
        clock.Advance(TimeSpan.FromMinutes(4));
        rig.Deliver(WrapUpFrames.NamedAgentCallEnds("a1", "am1"));
        clock.Advance(TrackedWrapUp);
        var agent = tracker.GetByAgentId(TrackedAgent);
        var next = rig.Manager.GetByLinkedId("b1")?.SessionId;

        new
        {
            ConnectedNext = connectedNext,
            agent?.State,
            CurrentCall = agent?.CurrentCall?.SessionId,
            agent?.TotalWrapUpTime,
        }.Should().BeEquivalentTo(
            new
            {
                ConnectedNext = true,
                State = (AgentSessionState?)AgentSessionState.OnCall,
                CurrentCall = next,
                TotalWrapUpTime = (TimeSpan?)TrackedWrapUp,
            },
            "a wrap-up event for the agent's earlier call that reaches the tracker after its next call connected "
            + "adds its 30 s and leaves the agent on the new call. " + Describe(rig));
    }

    // A queue call's wrap-up opens at the member's AgentComplete, which app_queue reports before the caller hangs up,
    // so it can close before the call's CallEndedEvent reaches the tracker. The agent is known by name from an
    // AgentLogin and its AgentConnect names it, so the manager's event carries the agent.

    private const string TrackedQueue = "sales";
    private const string SecondAgent = "agent-2";
    private const string SecondMember = "PJSIP/agent2";

    [Fact]
    public async Task OnCallEnded_ShouldReportTheAgentIdle_WhenAZeroWrapUpClosedAtTheAgentsAgentCompleteBeforeTheCallEnded()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, new SessionOptions { WrapUpDuration = TimeSpan.Zero });
        using var tracker = TrackerOn(rig, clock);
        rig.Deliver(WrapUpFrames.AgentLogin(TrackedAgent, TrackedMember, "login-1"));
        rig.Deliver(WrapUpFrames.QueueCallConnected(TrackedQueue, "c1", "m1", TrackedMember, TrackedAgent));
        clock.Advance(TimeSpan.FromMinutes(4));
        rig.Deliver(WrapUpFrames.MemberHangsUp(TrackedQueue, "c1", "m1", TrackedMember, agent: TrackedAgent));
        clock.Advance(TimeSpan.FromMinutes(5));
        var agent = tracker.GetByAgentId(TrackedAgent);

        new { agent?.State, agent?.CurrentCall, agent?.TotalWrapUpTime, agent?.CallsHandled }.Should().BeEquivalentTo(
            new
            {
                State = (AgentSessionState?)AgentSessionState.Idle,
                CurrentCall = default(CallSession),
                TotalWrapUpTime = (TimeSpan?)TimeSpan.Zero,
                CallsHandled = (int?)1,
            },
            "a wrap-up duration of zero closes the agent's wrap-up at its AgentComplete, before the caller's hangup "
            + "ends the call, and the call's later ending finds that wrap-up already closed: the agent is Idle, the "
            + "call counted, no wrap-up time added. " + Describe(rig));
    }

    [Fact]
    public async Task OnCallEnded_ShouldReportTheAgentIdle_WhenTheCallerOutlivesTheWrapUpAfterTheAgentsBlindTransfer()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, new SessionOptions { WrapUpDuration = TrackedWrapUp });
        using var tracker = TrackerOn(rig, clock);
        rig.Deliver(WrapUpFrames.AgentLogin(TrackedAgent, TrackedMember, "login-1"));
        rig.Deliver(WrapUpFrames.QueueCallConnected(TrackedQueue, "c1", "m1", TrackedMember, TrackedAgent));
        clock.Advance(TimeSpan.FromSeconds(60));
        rig.Deliver(WrapUpFrames.MemberBlindTransfers(TrackedQueue, "c1", "m1", TrackedMember, "ivr", "700", TrackedAgent));
        clock.Advance(TimeSpan.FromSeconds(60));
        rig.Deliver(WrapUpFrames.Hangup("c1"));
        clock.Advance(TimeSpan.FromMinutes(5));
        var agent = tracker.GetByAgentId(TrackedAgent);

        new { agent?.State, agent?.CurrentCall, agent?.TotalWrapUpTime, agent?.CallsHandled }.Should().BeEquivalentTo(
            new
            {
                State = (AgentSessionState?)AgentSessionState.Idle,
                CurrentCall = default(CallSession),
                TotalWrapUpTime = (TimeSpan?)TrackedWrapUp,
                CallsHandled = (int?)1,
            },
            "the agent's blind transfer opens its wrap-up while the caller stays up in an IVR; the wrap-up's 30 s pass "
            + "before the caller hangs up, and the call's ending then finds it closed: the agent is Idle with the "
            + "30 s counted. " + Describe(rig));
    }

    [Fact]
    public async Task OnCallEnded_ShouldReportEveryAgentOfTheCallIdle_WhenOneCallVisitsTwoAgentsAndTheFirstWrapUpClosesBeforeTheCallEnds()
    {
        var clock = new FakeTimeProvider();
        await using var rig = await WrapUpRig.StartAsync(clock, new SessionOptions { WrapUpDuration = TrackedWrapUp });
        using var tracker = TrackerOn(rig, clock);
        rig.Deliver(WrapUpFrames.AgentLogin(TrackedAgent, TrackedMember, "login-1"));
        rig.Deliver(WrapUpFrames.AgentLogin(SecondAgent, SecondMember, "login-2"));
        rig.Deliver(WrapUpFrames.QueueCallConnected(TrackedQueue, "c1", "m1", TrackedMember, TrackedAgent));
        clock.Advance(TimeSpan.FromSeconds(60));
        rig.Deliver(WrapUpFrames.MemberBlindTransfers(TrackedQueue, "c1", "m1", TrackedMember, "queues", "support",
            TrackedAgent));
        clock.Advance(TimeSpan.FromSeconds(2));
        rig.Deliver(WrapUpFrames.SecondVisitConnected("support", "c1", "m2", SecondMember, SecondAgent));
        clock.Advance(TimeSpan.FromSeconds(40));
        rig.Deliver(WrapUpFrames.SecondMemberHangsUp("support", "c1", "m2", SecondMember,
            WrapUpFrames.Bridge("c1") + "-2", 40, SecondAgent));
        clock.Advance(TimeSpan.FromMinutes(5));
        var first = tracker.GetByAgentId(TrackedAgent);
        var second = tracker.GetByAgentId(SecondAgent);

        new
        {
            First = new { first?.State, first?.CurrentCall, first?.TotalWrapUpTime, first?.CallsHandled },
            Second = new { second?.State, second?.CurrentCall, second?.TotalWrapUpTime, second?.CallsHandled },
        }.Should().BeEquivalentTo(
            new
            {
                First = new
                {
                    State = (AgentSessionState?)AgentSessionState.Idle,
                    CurrentCall = default(CallSession),
                    TotalWrapUpTime = (TimeSpan?)TrackedWrapUp,
                    CallsHandled = (int?)1,
                },
                Second = new
                {
                    State = (AgentSessionState?)AgentSessionState.Idle,
                    CurrentCall = default(CallSession),
                    TotalWrapUpTime = (TimeSpan?)TrackedWrapUp,
                    CallsHandled = (int?)1,
                },
            },
            "the first agent transfers the caller to a second queue, where the second agent takes it; the first "
            + "agent's 30 s of wrap-up pass while the second still talks, and the call's ending ends it for both "
            + "agents: the first goes Idle at once, the second enters wrap-up and leaves it 30 s later. "
            + Describe(rig));
    }

    private static AgentSessionTracker TrackerOn(WrapUpRig rig, TimeProvider clock) =>
        new(rig.Manager, Options.Create(new SessionOptions { WrapUpDuration = TrackedWrapUp }),
            NullLogger<AgentSessionTracker>.Instance, clock);

    private static string Describe(WrapUpRig rig) =>
        "Measured events: [" + string.Join(", ", rig.Events.Select(e => e is CallWrapUpEvent w
            ? $"CallWrapUpEvent({w.SessionId}, {w.AgentId ?? "null"}, {w.WrapUpDuration})"
            : $"{e.GetType().Name}({e.SessionId})")) + "]";

    private static CallSession CreateCallSession(string sessionId) =>
        new(sessionId, $"linked-{sessionId}", "srv-1", CallDirection.Inbound);

    private sealed class StateChangeObserver(List<AgentSessionStateChanged> changes)
        : IObserver<AgentSessionStateChanged>
    {
        public void OnNext(AgentSessionStateChanged value) => changes.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>A state-change subscriber that throws on every change.</summary>
    private sealed class ThrowingStateChangeObserver : IObserver<AgentSessionStateChanged>
    {
        public void OnNext(AgentSessionStateChanged value) =>
            throw new InvalidOperationException($"subscriber failure on {value.AgentId}");

        public void OnError(Exception error)
        {
            // The tracker's stream never faults; nothing to do.
        }

        public void OnCompleted()
        {
            // Completion is not under test here.
        }
    }

    /// <summary>One Warning as the tracker's report reads it: the agent, the two states, the exception's type.</summary>
    private sealed record StateChangeReport(string? AgentId, string? PreviousState, string? NewState, string? Exception);

    /// <summary>Keeps every Warning written to it, read as a <see cref="StateChangeReport"/>.</summary>
    private sealed class RecordingLogger : ILogger<AgentSessionTracker>
    {
        private readonly ConcurrentQueue<StateChangeReport> _warnings = new();

        public StateChangeReport[] Warnings() => [.. _warnings];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning)
                return;

            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                    properties[pair.Key] = pair.Value;
            }

            _warnings.Enqueue(new StateChangeReport(
                properties.GetValueOrDefault("AgentId")?.ToString(),
                properties.GetValueOrDefault("PreviousState")?.ToString(),
                properties.GetValueOrDefault("NewState")?.ToString(),
                exception?.GetType().Name));
        }
    }
}
