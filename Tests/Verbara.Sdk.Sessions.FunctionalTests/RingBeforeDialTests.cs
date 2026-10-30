using System.Globalization;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// A ring the SDK observes while a call is still in its initial state, in fixture order: the far end of a leg
/// alerts (<c>Newstate: Ringing</c>) before any dial is observed. The ring is recorded — the call's ring time and
/// one <c>Ringing</c> audit entry naming the leg — and the call's state does not move. A receiving leg's arrival
/// state (<c>Ring</c>) is not a ring. A ring after the dial still moves the call to <c>Ringing</c>.
/// </summary>
/// <remarks>
/// The legs carry the states the wire shows: a calling or receiving leg is born <c>Ring</c>, a dialed or
/// originated leg is born <c>Down</c>. Every step is delivered on the test's thread and handled inline, and the
/// store is handed each save before the step returns.
/// </remarks>
public sealed class RingBeforeDialTests : IAsyncLifetime
{
    private const string CallerUid = "rbd-caller-001";
    private const string CallerChannel = "PJSIP/100-rbd-001";
    private const string DialedUid = "rbd-dialed-001";
    private const string DialedChannel = "PJSIP/200-rbd-001";
    private const string SecondUid = "rbd-dialed-002";
    private const string SecondChannel = "PJSIP/300-rbd-002";
    private const string LinkedId = "rbd-linked-001";

    private readonly RecordingSessionStore _store = new();
    private readonly SessionTestFixture _fixture;

    public RingBeforeDialTests()
    {
        _fixture = new SessionTestFixture(new SessionOptions(), store: _store);
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private CallSession Session() =>
        _fixture.SessionManager.GetByLinkedId(LinkedId)
        ?? throw new InvalidOperationException("premise: the first leg opened a call");

    private CallSession GivenACallingLegAndADialedLeg()
    {
        _fixture.SimulateNewChannel(CallerUid, CallerChannel, ChannelState.Ring,
            linkedId: LinkedId, callerIdNum: "100", context: "from-internal", exten: "200");
        _fixture.SimulateNewChannel(DialedUid, DialedChannel, ChannelState.Down, linkedId: LinkedId);
        var session = Session();
        session.State.Should().Be(CallSessionState.Created, "premise: nothing has happened to the call yet");
        return session;
    }

    private static List<CallSessionEvent> RingEntries(CallSession session) =>
        [.. session.Events.Where(e => e.Type == CallSessionEventType.Ringing)];

    private static string Describe(CallSession session) => string.Create(CultureInfo.InvariantCulture,
        $"state={session.State} ringingAt={session.RingingAt:O} dialingAt={session.DialingAt:O} "
        + $"connectedAt={session.ConnectedAt:O} cause={session.HangupCause?.ToString() ?? "null"} "
        + $"trail=[{string.Join(">", session.Events.Select(e => $"{e.Type}({e.SourceChannel})"))}]");

    // --- the far end rings before the dial is observed -------------------------------------------

    [Fact]
    public void Ring_ShouldBeRecordedAndTheDialShouldStillMoveTheCall_WhenTheDialedLegRingsBeforeTheDialIsObserved()
    {
        var session = GivenACallingLegAndADialedLeg();

        _fixture.SimulateStateChange(DialedUid, ChannelState.Ringing);
        var afterTheRing = new
        {
            session.State,
            RingingAtIsSet = session.RingingAt.HasValue,
            RingEntries = RingEntries(session).Select(e => e.SourceChannel).ToList(),
        };
        var betweenTheRingAndTheDial = DateTimeOffset.UtcNow;

        _fixture.SimulateDialBegin(CallerUid, DialedUid, DialedChannel);
        var afterTheDial = session.State;
        var dialingAt = session.DialingAt;
        var ringingAt = session.RingingAt;

        _fixture.SimulateAnswer(DialedUid);
        var afterTheAnswer = session.State;
        _fixture.SimulateBridgeCreated("rbd-bridge-001");
        _fixture.SimulateBridgeEnter("rbd-bridge-001", CallerUid);
        _fixture.SimulateBridgeEnter("rbd-bridge-001", DialedUid);
        _fixture.SimulateHangup(DialedUid);
        _fixture.SimulateHangup(CallerUid);

        using var scope = new AssertionScope();
        scope.AddReportable("session", Describe(session));
        afterTheRing.Should().BeEquivalentTo(
            new { State = CallSessionState.Created, RingingAtIsSet = true, RingEntries = new List<string?> { DialedChannel } },
            "the dialed leg's far end alerted while the call was in its initial state: the ring is recorded, naming "
            + "that leg, and the call does not move");
        afterTheDial.Should().Be(CallSessionState.Dialing, "the dial moves the call to dialing as it does when no ring came first");
        dialingAt.Should().NotBeNull("the dial was observed")
            .And.BeOnOrAfter(ringingAt ?? DateTimeOffset.MaxValue, "the dial was observed at or after the ring")
            .And.BeOnOrAfter(betweenTheRingAndTheDial, "the dialing time is when the dial was observed, never back-dated");
        afterTheAnswer.Should().Be(CallSessionState.Connected, "the dialed leg's answer connects the call as today");
        session.State.Should().Be(CallSessionState.Completed, "the call talked and hung up normally");
        RingEntries(session).Should().ContainSingle("only the one ring was observed");
    }

    // --- the recorded ring reaches the store ----------------------------------------------------

    [Fact]
    public void Store_ShouldBeHandedTheCallWithItsRingTime_WhenARingIsRecordedInTheInitialState()
    {
        var session = GivenACallingLegAndADialedLeg();
        var savesBefore = _store.Saves.Count;

        _fixture.SimulateStateChange(DialedUid, ChannelState.Ringing);

        var saves = _store.Saves.Skip(savesBefore).Where(s => s.SessionId == session.SessionId).ToList();
        saves.Should().Contain(s => s.RingingAt.HasValue && s.State == CallSessionState.Created,
            "the recorded ring is persisted: the store is handed the call, still in its initial state, with its ring "
            + $"time set. Saves: [{string.Join(",", saves.Select(s => $"{s.State}/{(s.RingingAt.HasValue ? "rang" : "-")}"))}]; "
            + Describe(session));
    }

    // --- a second ring before the dial ----------------------------------------------------------

    [Fact]
    public void SecondRing_ShouldChangeNothing_WhenTheRingWasAlreadyRecordedInTheInitialState()
    {
        var session = GivenACallingLegAndADialedLeg();
        _fixture.SimulateNewChannel(SecondUid, SecondChannel, ChannelState.Down, linkedId: LinkedId);

        _fixture.SimulateStateChange(DialedUid, ChannelState.Ringing);
        var firstRing = session.RingingAt;
        _fixture.SimulateStateChange(SecondUid, ChannelState.Ringing);

        using var scope = new AssertionScope();
        scope.AddReportable("session", Describe(session));
        session.State.Should().Be(CallSessionState.Created, "neither ring moves the call");
        session.RingingAt.Should().NotBeNull("the first ring was recorded")
            .And.Be(firstRing, "the ring time keeps the first observation");
        RingEntries(session).Should().ContainSingle("the second ring adds no ringing entry")
            .Which.SourceChannel.Should().Be(DialedChannel, "the one entry names the leg that rang first");
    }

    // --- a leg arriving is not a ring -----------------------------------------------------------

    /// <summary>
    /// The state a leg carries while Asterisk is receiving it (<c>Ring</c>) is its arrival, not a ring: a leg of a call
    /// in its initial state that changes to it records nothing and does not move the call.
    /// </summary>
    [Fact]
    public void ReceivingState_ShouldRecordNoRingAndNotMoveTheCall_WhenALegOfACallInItsInitialStateChangesToIt()
    {
        var session = GivenACallingLegAndADialedLeg();
        var savesBefore = _store.SavesOf(session.SessionId);

        _fixture.SimulateStateChange(DialedUid, ChannelState.Ring);

        new
        {
            session.State,
            RingingAtIsSet = session.RingingAt.HasValue,
            RingEntries = RingEntries(session).Count,
            Saves = _store.SavesOf(session.SessionId) - savesBefore,
        }.Should().BeEquivalentTo(
            new { State = CallSessionState.Created, RingingAtIsSet = false, RingEntries = 0, Saves = 0 },
            $"Ring is the receiving state, not a far end alerting. Measured: {Describe(session)}");
    }

    /// <summary>
    /// A live leg born in the receiving state (<c>Ring</c>) opens its call in the initial state with no ring recorded,
    /// and a dial observed next moves it to dialing. The live leg born answered is bound by
    /// <see cref="ReloadedSessionStateTests.NewChannel_ShouldOpenTheCallAsCreatedAndProgressAsItAlwaysHas_WhenTheChannelArrivesLive"/>.
    /// </summary>
    [Fact]
    public void LiveLeg_ShouldOpenTheCallInItsInitialStateWithNoRing_WhenItArrivesInTheReceivingState()
    {
        _fixture.SimulateNewChannel(CallerUid, CallerChannel, ChannelState.Ring,
            linkedId: LinkedId, callerIdNum: "5551234", context: "from-trunk", exten: "200");
        var session = Session();
        var atOpen = new
        {
            session.State,
            RingingAtIsSet = session.RingingAt.HasValue,
            RingEntries = RingEntries(session).Count,
        };

        _fixture.SimulateNewChannel(DialedUid, DialedChannel, ChannelState.Down, linkedId: LinkedId);
        _fixture.SimulateDialBegin(CallerUid, DialedUid, DialedChannel);

        new { AtOpen = atOpen, AfterTheDial = session.State }.Should().BeEquivalentTo(
            new
            {
                AtOpen = new { State = CallSessionState.Created, RingingAtIsSet = false, RingEntries = 0 },
                AfterTheDial = CallSessionState.Dialing,
            },
            "a leg the SDK watched arrive opens its call in the initial state whatever state it arrives in, and its "
            + $"arrival state is not a ring. Measured: {Describe(session)}");
    }

    // --- a ring after the dial ------------------------------------------------------------------

    [Fact]
    public void RingAfterTheDial_ShouldMoveTheCallToRingingAndRecordTheRing_WhenTheCallIsDialing()
    {
        var session = GivenACallingLegAndADialedLeg();
        _fixture.SimulateDialBegin(CallerUid, DialedUid, DialedChannel);
        session.State.Should().Be(CallSessionState.Dialing, "premise: the dial moved the call");

        _fixture.SimulateStateChange(DialedUid, ChannelState.Ringing);

        using var scope = new AssertionScope();
        scope.AddReportable("session", Describe(session));
        session.State.Should().Be(CallSessionState.Ringing, "a ring while dialing moves the call to ringing, as today");
        session.RingingAt.Should().NotBeNull("the ring was observed")
            .And.BeOnOrAfter(session.DialingAt ?? DateTimeOffset.MaxValue, "the ring followed the dial");
        RingEntries(session).Should().ContainSingle("the ring is recorded once")
            .Which.SourceChannel.Should().Be(DialedChannel, "the ringing entry names the dialed leg");
    }
}
