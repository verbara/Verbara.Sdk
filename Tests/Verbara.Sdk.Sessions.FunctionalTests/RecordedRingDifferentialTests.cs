using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.Diagnostics;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// An originated call — its leg born <c>Down</c>, its dial naming no calling channel, so the call stays in its initial
/// state until it is queued or ends — run twice, once with its leg's <c>Newstate: Ringing</c> and once without it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The differential pins</b> assert that the ring moves nothing: the two runs' state trails, terminal states, kinds
/// of domain event and session measurements are equal, and the run without the ring records no ring. They also bind
/// today's flow in absolute terms: the trail, the queued time where the call was queued, and, for the call an agent
/// takes, a wait that includes the time in the queue and a talk time that starts at the agent's answer. Whether a
/// <see cref="CallConnectedEvent"/> is published is compared between the runs, never asserted on its own.
/// </para>
/// <para>
/// <b>The ring pins</b> assert what the ring does add: the run with the ring has its ring time set and exactly one
/// <c>Ringing</c> audit entry naming the originated leg, and its audit trail is the other run's plus that entry.
/// </para>
/// <para>
/// Three flows: (i) the leg answers, the call joins a queue, waits, and an agent answers; (ii) the leg answers, the
/// call joins a queue, and hangs up before any agent; (iii) the leg hangs up unanswered. Every step is delivered on
/// the test's thread and handled inline; the state trail is the call's state read after each step.
/// </para>
/// </remarks>
public sealed class RecordedRingDifferentialTests
{
    /// <summary>How long the call an agent takes waits in the queue.</summary>
    private static readonly TimeSpan QueueWait = TimeSpan.FromMilliseconds(200);

    /// <summary>How long that call talks before it hangs up.</summary>
    private static readonly TimeSpan Talk = TimeSpan.FromMilliseconds(50);

    private const string OriginatedUid = "orig-ring-001";
    private const string OriginatedChannel = "PJSIP/dialer-out-001";
    private const string AgentUid = "orig-agent-001";
    private const string AgentChannel = "PJSIP/agent1-001";
    private const string AgentId = "1001";
    private const string AgentInterface = "PJSIP/agent1";
    private const string LinkedId = "orig-linked-001";
    private const string Queue = "sales";

    public enum Flow
    {
        /// <summary>(i) Answered, queued, and taken by an agent.</summary>
        AnsweredQueuedAndTakenByAnAgent,

        /// <summary>(ii) Answered, queued, and abandoned before any agent.</summary>
        AnsweredQueuedAndAbandoned,

        /// <summary>(iii) Never answered.</summary>
        NeverAnswered,
    }

    private static readonly Dictionary<Flow, CallSessionState[]> TrailToday = new()
    {
        [Flow.AnsweredQueuedAndTakenByAnAgent] =
            [CallSessionState.Created, CallSessionState.Queued, CallSessionState.Connected, CallSessionState.Completed],
        [Flow.AnsweredQueuedAndAbandoned] = [CallSessionState.Created, CallSessionState.Queued, CallSessionState.Failed],
        [Flow.NeverAnswered] = [CallSessionState.Created, CallSessionState.Failed],
    };

    // --- the differential pins ------------------------------------------------------------------

    [Theory]
    [InlineData(Flow.AnsweredQueuedAndTakenByAnAgent)]
    [InlineData(Flow.AnsweredQueuedAndAbandoned)]
    [InlineData(Flow.NeverAnswered)]
    public async Task Ring_ShouldMoveNothing_WhenTheOriginatedCallIsRunWithAndWithoutItsRing(Flow flow)
    {
        var withRing = await RunAsync(flow, ring: true);
        var withoutRing = await RunAsync(flow, ring: false);

        using var scope = new AssertionScope();
        scope.AddReportable("with the ring", withRing.Describe());
        scope.AddReportable("without the ring", withoutRing.Describe());

        withRing.StateTrail.Should().Equal(withoutRing.StateTrail, "the ring does not move the call");
        withRing.Session.State.Should().Be(withoutRing.Session.State, "the ring does not change how the call ends");
        withRing.DomainEvents.Should().Equal(withoutRing.DomainEvents, "the ring publishes nothing and changes nothing published");
        withRing.Measurements.Should().Be(withoutRing.Measurements, "the ring measures nothing and changes nothing measured");

        withoutRing.Session.RingingAt.Should().BeNull("no ring was observed in the run without it");
        withoutRing.RingEntries.Should().BeEmpty("no ring was observed in the run without it");

        foreach (var run in new[] { withRing, withoutRing })
        {
            var which = run.Ring ? "with the ring" : "without the ring";
            run.StateTrail.Should().Equal(TrailToday[flow], "the call moves as it does today ({0})", which);

            if (flow is Flow.NeverAnswered)
                continue;

            run.Session.QueuedAt.Should().NotBeNull("the call joined a queue ({0})", which);
            if (flow is not Flow.AnsweredQueuedAndTakenByAnAgent)
                continue;

            run.Session.WaitTime.Should().NotBeNull("the agent answered ({0})", which)
                .And.BeGreaterThanOrEqualTo(QueueWait, "the wait runs from the call's creation to the agent's answer ({0})", which);
            run.Session.ConnectedAt.Should().NotBeNull("the agent answered ({0})", which)
                .And.BeOnOrAfter(run.BeforeTheAgentAnswered ?? DateTimeOffset.MaxValue,
                    "the call connects, and its talk time starts, at the agent's answer ({0})", which);
        }
    }

    // --- the ring pins --------------------------------------------------------------------------

    [Theory]
    [InlineData(Flow.AnsweredQueuedAndTakenByAnAgent)]
    [InlineData(Flow.AnsweredQueuedAndAbandoned)]
    [InlineData(Flow.NeverAnswered)]
    public async Task Ring_ShouldBeRecordedAndBeTheOnlyDifference_WhenTheOriginatedCallIsRunWithAndWithoutItsRing(Flow flow)
    {
        var withRing = await RunAsync(flow, ring: true);
        var withoutRing = await RunAsync(flow, ring: false);

        using var scope = new AssertionScope();
        scope.AddReportable("with the ring", withRing.Describe());
        scope.AddReportable("without the ring", withoutRing.Describe());

        withRing.Session.RingingAt.Should().NotBeNull("the originated leg's ring was observed while the call was in its initial state");
        withRing.RingEntries.Should().ContainSingle("the leg rang once")
            .Which.SourceChannel.Should().Be(OriginatedChannel, "the ringing entry names the originated leg");
        withRing.AuditTrail.Where((_, i) => i != withRing.AuditTrail.IndexOf(CallSessionEventType.Ringing)).Should()
            .Equal(withoutRing.AuditTrail, "the one ringing entry is all the ring adds to the audit trail");
    }

    // --- one run --------------------------------------------------------------------------------

    private static async Task<Run> RunAsync(Flow flow, bool ring)
    {
        var fixture = new SessionTestFixture(new SessionOptions(), store: new RecordingSessionStore());
        var events = new List<SessionDomainEvent>();
        using var subscription = fixture.SessionManager.Events.Subscribe(events.Add);
        var trail = new List<CallSessionState>();
        DateTimeOffset? beforeTheAgentAnswered = null;
        CallSession session = null!;
        string measurements;

        try
        {
            using (var measured = new SessionMeasurements())
            {
                fixture.Server.Agents.OnAgentLogin(AgentId, AgentInterface);
                fixture.SimulateNewChannel(OriginatedUid, OriginatedChannel, ChannelState.Down,
                    linkedId: LinkedId, callerIdNum: "7000", context: "from-dialer", exten: "s");
                session = fixture.SessionManager.GetByLinkedId(LinkedId)
                    ?? throw new InvalidOperationException("premise: the originated leg opened a call");
                Step();

                if (ring)
                {
                    fixture.SimulateStateChange(OriginatedUid, ChannelState.Ringing);
                    Step();
                }

                if (flow is Flow.NeverAnswered)
                {
                    fixture.SimulateHangup(OriginatedUid, HangupCause.NoAnswer);
                    Step();
                }
                else
                {
                    fixture.SimulateAnswer(OriginatedUid);
                    Step();
                    fixture.SimulateQueueCallerJoined(Queue, OriginatedChannel, "7000");
                    Step();
                    // Slept on the test's thread, so the session meter's thread filter keeps the steps that follow.
                    Thread.Sleep(QueueWait);

                    if (flow is Flow.AnsweredQueuedAndAbandoned)
                    {
                        fixture.SimulateHangup(OriginatedUid, HangupCause.NormalClearing);
                        Step();
                    }
                    else
                    {
                        beforeTheAgentAnswered = DateTimeOffset.UtcNow;
                        fixture.SimulateNewChannel(AgentUid, AgentChannel, ChannelState.Down, linkedId: LinkedId);
                        Step();
                        fixture.SimulateAnswer(AgentUid);
                        Step();
                        fixture.Server.Agents.OnAgentConnect(AgentId, OriginatedChannel, LinkedId, AgentInterface);
                        Step();
                        Thread.Sleep(Talk);
                        fixture.SimulateHangup(AgentUid, HangupCause.NormalClearing);
                        Step();
                        fixture.SimulateHangup(OriginatedUid, HangupCause.NormalClearing);
                        Step();
                    }
                }

                measurements = measured.Describe();
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }

        return new Run(ring, session, [.. trail],
            [.. events.Where(e => e.SessionId == session.SessionId).Select(e => e.GetType().Name)],
            measurements, beforeTheAgentAnswered);

        void Step()
        {
            var state = session.State;
            if (trail.Count == 0 || trail[^1] != state)
                trail.Add(state);
        }
    }

    private sealed record Run(
        bool Ring,
        CallSession Session,
        IReadOnlyList<CallSessionState> StateTrail,
        IReadOnlyList<string> DomainEvents,
        string Measurements,
        DateTimeOffset? BeforeTheAgentAnswered)
    {
        public List<CallSessionEventType> AuditTrail => [.. Session.Events.Select(e => e.Type)];

        public List<CallSessionEvent> RingEntries => [.. Session.Events.Where(e => e.Type == CallSessionEventType.Ringing)];

        public string Describe() => string.Create(CultureInfo.InvariantCulture,
            $"states=[{string.Join(">", StateTrail)}] ringingAt={Session.RingingAt:O} queuedAt={Session.QueuedAt:O} "
            + $"connectedAt={Session.ConnectedAt:O} wait={Session.WaitTime} talk={Session.TalkTime} "
            + $"audit=[{string.Join(">", Session.Events.Select(e => $"{e.Type}({e.SourceChannel})"))}] "
            + $"events=[{string.Join(",", DomainEvents)}] measurements=[{Measurements}]");
    }

    /// <summary>
    /// What the session meter records on the test's thread while it is open: one entry per instrument that moved,
    /// as <c>name=value</c> for each counter add and <c>name×1</c> for each histogram record. The instruments are
    /// process-wide and carry no tags, and other classes run in parallel on other threads, so only the test's own
    /// thread counts.
    /// </summary>
    private sealed class SessionMeasurements : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, string> _moved = new(StringComparer.Ordinal);

        public SessionMeasurements()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == SessionMetrics.Meter.Name)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => Moved(instrument, $"={value}"));
            _listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => Moved(instrument, $"={value}"));
            _listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => Moved(instrument, "×1"));
            _listener.Start();
        }

        public string Describe() =>
            string.Join(",", _moved.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key + m.Value));

        private void Moved(Instrument instrument, string how)
        {
            if (Environment.CurrentManagedThreadId == _thread)
                _moved.AddOrUpdate(instrument.Name, how, (_, before) => before + how);
        }

        public void Dispose() => _listener.Dispose();
    }
}
