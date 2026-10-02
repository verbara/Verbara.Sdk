using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.Diagnostics;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// How a call ends when the dialplan answers it and no dial or queue ever reaches it: an IVR, a
/// recorded announcement, a voice bot handed the call by the dialplan. The call stays in its initial
/// state while it talks, because nothing connected it, and the SDK observed its answer.
/// </summary>
/// <remarks>
/// Every step is delivered on the test's thread and the manager handles it inline: the channel table
/// raises its events synchronously, the manager publishes its domain events synchronously, and the store
/// is handed each save before the step returns. Nothing waits.
/// </remarks>
public sealed class AnsweredWithNoDialTests : IAsyncLifetime
{
    private const string CallerUid = "ivr-caller-001";
    private const string CallerChannel = "PJSIP/trunk-ivr-001";
    private const string LinkedId = "ivr-linked-001";

    private readonly RecordingSessionStore _store = new();
    private readonly SessionTestFixture _fixture;
    private readonly List<SessionDomainEvent> _events = [];
    private readonly IDisposable _subscription;

    public AnsweredWithNoDialTests()
    {
        _fixture = new SessionTestFixture(new SessionOptions(), store: _store);
        _subscription = _fixture.SessionManager.Events.Subscribe(_events.Add);
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public async Task DisposeAsync()
    {
        _subscription.Dispose();
        await _fixture.DisposeAsync();
    }

    private CallSession GivenAnInboundCallInItsInitialState()
    {
        _fixture.SimulateNewChannel(CallerUid, CallerChannel, ChannelState.Ring,
            linkedId: LinkedId, callerIdNum: "5551234", context: "from-trunk", exten: "300");
        var session = _fixture.SessionManager.GetByLinkedId(LinkedId)
            ?? throw new InvalidOperationException("premise: the inbound leg opened a call");
        session.State.Should().Be(CallSessionState.Created, "premise: nothing has happened to the call yet");
        return session;
    }

    private List<CallEndedEvent> EndingsOf(CallSession session) =>
        [.. _events.OfType<CallEndedEvent>().Where(e => e.SessionId == session.SessionId)];

    private string Describe(CallSession session) => string.Create(CultureInfo.InvariantCulture,
        $"state={session.State} cause={session.HangupCause?.ToString() ?? "null"} "
        + $"connectedAt={session.ConnectedAt:O} completedAt={session.CompletedAt:O} talk={session.TalkTime} "
        + $"trail=[{string.Join(">", session.Events.Select(e => e.Type))}] "
        + $"events=[{string.Join(",", _events.Select(e => e.GetType().Name))}] "
        + $"saves=[{string.Join(",", _store.Saves.Select(s => $"{s.State}/{(s.ConnectedAt.HasValue ? "connected" : "-")}"))}]");

    // --- the answered call that hangs up normally ------------------------------------------------

    [Fact]
    public void Hangup_ShouldEndTheCallCompletedFromItsObservedAnswer_WhenTheDialplanAnsweredItAndNoDialOrQueueReachedIt()
    {
        var session = GivenAnInboundCallInItsInitialState();
        var justBeforeTheAnswer = DateTimeOffset.UtcNow;

        _fixture.SimulateAnswer(CallerUid);
        _fixture.SimulateHangup(CallerUid, HangupCause.NormalClearing);

        var endings = EndingsOf(session);
        new
        {
            session.State,
            ConnectedFromTheAnswerToTheEnding = session.ConnectedAt >= justBeforeTheAnswer
                && session.ConnectedAt <= session.CompletedAt,
            Endings = endings.Count,
            EndingCarriesATalkTime = endings.All(e => e.TalkTime.HasValue),
            StoreWasHandedTheCompletedCall = _store.Saves.Any(s => s.SessionId == session.SessionId
                && s.State == CallSessionState.Completed && s.ConnectedAt.HasValue),
        }.Should().BeEquivalentTo(
            new
            {
                State = CallSessionState.Completed,
                ConnectedFromTheAnswerToTheEnding = true,
                Endings = 1,
                EndingCarriesATalkTime = true,
                StoreWasHandedTheCompletedCall = true,
            },
            "the SDK observed the answer and the hangup was a normal clearing, so the call took place and is "
            + "over: it ends completed, connected from its observed answer, once, with a talk time, and the store "
            + $"is handed it that way. Measured: {Describe(session)}");
    }

    // --- the answered call that hangs up abnormally ----------------------------------------------

    [Fact]
    public void Hangup_ShouldEndTheCallFailed_WhenTheDialplanAnsweredItAndItHangsUpWithACauseOtherThanNormalClearing()
    {
        var session = GivenAnInboundCallInItsInitialState();

        _fixture.SimulateAnswer(CallerUid);
        _fixture.SimulateHangup(CallerUid, HangupCause.NormalTemporaryFailure);

        new
        {
            session.State,
            session.HangupCause,
            Endings = EndingsOf(session).Count,
        }.Should().BeEquivalentTo(
            new { State = CallSessionState.Failed, HangupCause = (HangupCause?)HangupCause.NormalTemporaryFailure, Endings = 1 },
            "a cause other than normal clearing ends an answered call failed, the rule a connected call already "
            + $"follows. Measured: {Describe(session)}");
    }

    // --- the call that was never answered --------------------------------------------------------

    [Fact]
    public void Hangup_ShouldEndTheCallFailedWithNoConnectedOrTalkTime_WhenTheCallWasNeverAnswered()
    {
        var session = GivenAnInboundCallInItsInitialState();

        _fixture.SimulateHangup(CallerUid, HangupCause.NormalClearing);

        var endings = EndingsOf(session);
        new
        {
            session.State,
            ConnectedAtIsSet = session.ConnectedAt.HasValue,
            TalkTimeIsSet = session.TalkTime.HasValue,
            Endings = endings.Count,
            AnEndingCarriesATalkTime = endings.Any(e => e.TalkTime.HasValue),
        }.Should().BeEquivalentTo(
            new
            {
                State = CallSessionState.Failed,
                ConnectedAtIsSet = false,
                TalkTimeIsSet = false,
                Endings = 1,
                AnEndingCarriesATalkTime = false,
            },
            "no answer was ever observed, so whatever the cause the call never became one: it ends failed, once, "
            + $"with no connected time and no talk time. Measured: {Describe(session)}");
    }

    // --- the answer itself moves nothing --------------------------------------------------------

    /// <summary>
    /// Across the dialplan's answer — from just before it to just after — the call stays in its initial state
    /// with no connected time, and nothing leaves the manager: no domain event, no measurement on any session
    /// instrument, no save. Read while it talks, the call is a call that has not connected.
    /// </summary>
    [Fact]
    public void Answer_ShouldPublishMeasureAndSaveNothing_WhenTheDialplanAnswersACallInItsInitialState()
    {
        var session = GivenAnInboundCallInItsInitialState();
        var eventsBefore = _events.Count;
        var savesBefore = _store.SavesOf(session.SessionId);

        using (var measurements = new SessionMeasurements())
        {
            _fixture.SimulateAnswer(CallerUid);

            new
            {
                session.State,
                ConnectedAtIsSet = session.ConnectedAt.HasValue,
                ConnectedEvents = _events.Skip(eventsBefore).OfType<CallConnectedEvent>().Count(),
                DomainEvents = string.Join(",", _events.Skip(eventsBefore).Select(e => e.GetType().Name)),
                Measurements = measurements.Describe(),
                Saves = _store.SavesOf(session.SessionId) - savesBefore,
            }.Should().BeEquivalentTo(
                new
                {
                    State = CallSessionState.Created,
                    ConnectedAtIsSet = false,
                    ConnectedEvents = 0,
                    DomainEvents = "",
                    Measurements = "",
                    Saves = 0,
                },
                "an answer that no dial or queue connected is recorded and nothing else: the live call reads as one that "
                + $"has not connected, and nothing is published, measured or saved. Measured: {Describe(session)}");
        }
    }

    /// <summary>
    /// The dialplan answers, then queues the call, and an agent takes it. The dialplan's answer is not the call's
    /// connection, so the connected time stays unset through the answer and the queue join, and is set only when
    /// the agent answers and the queue application reports the connection.
    /// </summary>
    [Fact]
    public void ConnectedAt_ShouldBeSetOnlyWhenTheAgentAnswers_WhenTheDialplanAnswersAndThenQueuesTheCall()
    {
        const string agentId = "1001";
        const string agentUid = "ivr-agent-001";
        const string agentChannel = "PJSIP/agent1-001";
        _fixture.Server.Agents.OnAgentLogin(agentId, "PJSIP/agent1");
        var session = GivenAnInboundCallInItsInitialState();

        _fixture.SimulateAnswer(CallerUid);
        var afterTheDialplansAnswer = session.ConnectedAt;
        _fixture.SimulateQueueCallerJoined("support", CallerChannel, "5551234");
        var afterTheQueueJoin = session.ConnectedAt;

        var justBeforeTheAgentAnswers = DateTimeOffset.UtcNow;
        _fixture.SimulateNewChannel(agentUid, agentChannel, ChannelState.Ring, linkedId: LinkedId);
        _fixture.SimulateAnswer(agentUid);
        _fixture.Server.Agents.OnAgentConnect(agentId, CallerChannel, LinkedId, "PJSIP/agent1");

        new
        {
            AfterTheDialplansAnswer = afterTheDialplansAnswer.HasValue,
            AfterTheQueueJoin = afterTheQueueJoin.HasValue,
            AfterTheAgentAnswers = session.ConnectedAt >= justBeforeTheAgentAnswers,
        }.Should().BeEquivalentTo(
            new { AfterTheDialplansAnswer = false, AfterTheQueueJoin = false, AfterTheAgentAnswers = true },
            "the call connects when the agent answers it, not when the dialplan did, so its wait and talk time are "
            + $"measured from the agent's answer. Measured: {Describe(session)}");
    }

    /// <summary>
    /// What the session meter records on the test's thread while it is open: one entry per instrument that moved,
    /// as <c>name=sum</c> for a counter and <c>name×count</c> for a histogram. The instruments are process-wide and
    /// carry no tags, and other classes run in parallel on other threads, so only the test's own thread counts.
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

        public string Describe() => string.Join(",", _moved.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key + m.Value));

        private void Moved(Instrument instrument, string how)
        {
            if (Environment.CurrentManagedThreadId == _thread)
                _moved.AddOrUpdate(instrument.Name, how, (_, before) => before + how);
        }

        public void Dispose() => _listener.Dispose();
    }
}
