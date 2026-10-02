using System.Globalization;
using FluentAssertions;
using Verbara.Sdk.Enums;
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
}
