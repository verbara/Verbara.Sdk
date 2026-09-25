using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Binds the requirement that an ending produced by a reload is distinguishable from an observed
/// hangup (ADR-0062, design D3).
///
/// <para>The defect this guards against is not cosmetic. <c>AsteriskChannel.HangupCause</c> is
/// non-nullable and defaults to <c>NotDefined</c> — cause zero — so a reload-driven removal that
/// reads it hands the session a cause nobody observed. <c>NotDefined</c> is not neutral downstream:
/// a classifier that treats anything other than <c>NormalClearing</c> as an abnormal ending reads
/// every reconnect-lost call as an abnormal hangup, and at the end of this dependency chain that
/// path calls a customer back. Asserting <c>NormalClearing</c> would be the opposite lie. The marker
/// plus no cause is the only shape that claims no knowledge the SDK does not have.</para>
///
/// <para><b>What a consumer reads.</b> The session carries <c>Metadata["cause"] == "reload"</c> —
/// the same key <c>SessionReconciler</c> already writes <c>"orphaned"</c> into, so this opens no
/// second vocabulary — and <c>HangupCause</c> is <c>null</c> on the session, on every departing
/// participant and on <c>CallEndedEvent.Cause</c>. <c>CallEndedEvent</c> gains no field: it is a
/// positional record, so a new member would move the public API this change claims it does not
/// touch. A consumer reaches the marker from the event's <c>SessionId</c> via
/// <c>ICallSessionManager.GetById</c>, which is the path
/// <see cref="Classify_ShouldSeparateAReloadEndingFromAnAbnormalHangup_WhenTheConsumerReadsTheMarker"/>
/// actually walks.</para>
///
/// <para>Harness traps inherited from <see cref="ReconnectReloadTests"/> and
/// <see cref="ReloadFailureTests"/>: the status reply must be armed before the reconnect is raised;
/// a server that was only constructed has subscribed to nothing, because <c>StartAsync</c> is where
/// <c>Reconnected</c> is attached; and <c>OnReconnected</c> is <c>async void</c>, so its log lines
/// are the only completion signal it produces.</para>
/// </summary>
public sealed class ReloadEndingProvenanceTests : IAsyncDisposable
{
    private const string ServerId = "test-srv";

    /// <summary>The call the reload proves gone: no hangup is ever observed for it.</summary>
    private const string LostCallerUid = "caller-001";
    private const string LostAgentUid = "agent-001";
    private const string LostLinkedId = "linked-lost";

    /// <summary>The call that ends the ordinary way, with a cause Asterisk really reported.</summary>
    private const string HungUpCallerUid = "caller-002";
    private const string HungUpAgentUid = "agent-002";
    private const string HungUpLinkedId = "linked-hungup";

    /// <summary>
    /// An abnormal cause on purpose. If the comparison case were <c>NormalClearing</c> the two
    /// endings would separate on the cause alone and the marker would be proving nothing.
    /// </summary>
    private const HangupCause ObservedCause = HangupCause.NormalTemporaryFailure;

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _server;
    private readonly CallSessionManager _sessions;
    private readonly ReloadLogger _serverLog = new();

    private readonly List<SessionDomainEvent> _sessionEvents = [];
    private readonly IDisposable _sessionSubscription;

    private IReadOnlyList<StatusEvent> _statusReply = [];

    public ReloadEndingProvenanceTests()
    {
        _connection.AsteriskVersion.Returns("21.0.0");
        _connection
            .SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(ci => Reply(ci.ArgAt<ManagerAction>(0)));

        _server = new VerbaraServer(_connection, _serverLog.For<VerbaraServer>());
        _sessions = new CallSessionManager(
            Options.Create(new SessionOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore());

        _sessions.AttachToServer(_server, ServerId);
        _sessionSubscription = _sessions.Events.Subscribe(_sessionEvents.Add);
    }

    /// <summary>Answers the three actions the load sends; only <c>Status</c> carries channels.</summary>
    private async IAsyncEnumerable<ManagerEvent> Reply(ManagerAction action)
    {
        await Task.Yield();

        if (action is AgentsAction)
        {
            _serverLog.ReloadFinished.TrySetResult();
            yield break;
        }

        if (action is not StatusAction)
            yield break;

        foreach (var status in _statusReply)
            yield return status;
    }

    private async Task GivenAStartedServer() => await _server.StartAsync();

    /// <summary>A two-leg inbound call, answered and bridged: one session, state Connected.</summary>
    private void GivenAnAnsweredCall(string callerUid, string agentUid, string linkedId, string bridgeId)
    {
        _server.Channels.OnNewChannel(callerUid, $"PJSIP/trunk-{callerUid}", ChannelState.Ring,
            callerIdNum: "5551234", context: "from-trunk", linkedId: linkedId);
        _server.Channels.OnNewChannel(agentUid, $"PJSIP/100-{agentUid}", ChannelState.Ring,
            linkedId: linkedId);
        _server.Channels.OnDialBegin(callerUid, agentUid, $"PJSIP/100-{agentUid}", null);
        _server.Channels.OnNewState(agentUid, ChannelState.Up);
        _server.Bridges.OnBridgeCreated(bridgeId, "mixing", "simple_bridge", null, null);
        _server.Bridges.OnChannelEntered(bridgeId, callerUid);
        _server.Bridges.OnChannelEntered(bridgeId, agentUid);
    }

    /// <summary>Both legs hang up, with a cause Asterisk reported on the wire.</summary>
    private void WhenBothLegsHangUp(string callerUid, string agentUid, HangupCause cause)
    {
        _server.Channels.OnHangup(agentUid, cause);
        _server.Channels.OnHangup(callerUid, cause);
    }

    /// <summary>
    /// Raises <c>Reconnected</c> and waits for the reload to end — at its last action when it
    /// completed, at the catch-all's log line when it failed.
    /// </summary>
    private async Task WhenTheConnectionReconnects(params StatusEvent[] channelsAsteriskStillHas)
    {
        _statusReply = channelsAsteriskStillHas;
        _serverLog.RearmReloadSignals();
        _connection.Reconnected += Raise.Event<Action>();

        // async void OnReconnected cannot be awaited, so the wait is on its two log-line signals.
        // fence-allow: GUARD-TIMEOUT — bounds that wait so a reload that never ran reports the log
        var timeout = Task.Delay(TimeSpan.FromSeconds(5));
        var ended = await Task.WhenAny(
            _serverLog.ReloadFinished.Task, _serverLog.ReloadFailed.Task, timeout);

        ReferenceEquals(ended, timeout).Should().BeFalse(
            "the reconnect reload neither finished nor logged a failure. Server log:"
            + $"{Environment.NewLine}{_serverLog}");
    }

    private CallSession SessionFor(string linkedId) =>
        _sessions.GetByLinkedId(linkedId)
        ?? throw new InvalidOperationException(
            $"no session for linkedId '{linkedId}'. Measured: {Describe()}");

    /// <summary>The measurement in one line, so a failure reports numbers and not a dump.</summary>
    private string Describe() =>
        "sessions [" + string.Join(" | ", new[] { LostLinkedId, HungUpLinkedId }
            .Select(_sessions.GetByLinkedId)
            .Where(s => s is not null)
            .Select(s => $"linked={s!.LinkedId} state={s.State} cause={Show(s.HangupCause)}"
                         + $" marker={Show(s.Metadata.GetValueOrDefault("cause"))}"
                         + $" participantCauses=[{string.Join(",", s.Participants.Select(p => Show(p.HangupCause)))}]"))
        + "]; CallEndedEvent causes ["
        + string.Join(", ", _sessionEvents.OfType<CallEndedEvent>().Select(e => Show(e.Cause)))
        + "]";

    private static string Show(object? value) => value?.ToString() ?? "<null>";

    /// <summary>
    /// A channel as <c>Status</c> really reports it: the state arrives as the numeric
    /// <c>ChannelState</c> header in <c>RawFields</c>, never as <c>StatusEvent.State</c>, which no
    /// supported Asterisk version populates (ADR-0062, design D5).
    /// </summary>
    private static StatusEvent Leg(string uniqueId, string channel, string linkedId) => new()
    {
        UniqueId = uniqueId,
        Channel = channel,
        LinkedId = linkedId,
        RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ChannelState"] = "6",       // AST_STATE_UP, exactly as the frame carries it
            ["ChannelStateDesc"] = "Up",
        },
    };

    // --- scenario: the ending carries its provenance ---------------------------------------------

    [Fact]
    public async Task Reconnect_ShouldMarkTheEndingAsComingFromAReload_WhenTheCompletedSnapshotProvesTheCallGone()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall(LostCallerUid, LostAgentUid, LostLinkedId, "bridge-lost");
        _sessions.ActiveSessions.Should().ContainSingle("the call is up before the outage");

        // Asterisk answers the reload in full and lists nothing: the call is gone, and this is the
        // last notification it will ever produce. No Hangup was seen, so no cause exists.
        await WhenTheConnectionReconnects();

        var session = SessionFor(LostLinkedId);

        session.Metadata.Should().ContainKey("cause",
            "a consumer must be able to read the provenance of the ending as a present value, not "
            + $"deduce it from something missing. Measured: {Describe()}")
            .WhoseValue.Should().Be("reload",
            "the SDK already writes 'orphaned' under this key for an ending it did not observe; "
            + $"'reload' is the sibling value, not a second vocabulary. Measured: {Describe()}");

        session.HangupCause.Should().BeNull(
            "no hangup was observed, so no cause is known. HangupCause.NotDefined would be a cause "
            + $"the SDK invented, and downstream it reads as an abnormal ending. Measured: {Describe()}");
    }

    [Fact]
    public async Task Reconnect_ShouldLeaveEveryDepartingParticipantWithoutACause_WhenTheReloadProvesTheCallGone()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall(LostCallerUid, LostAgentUid, LostLinkedId, "bridge-lost");

        await WhenTheConnectionReconnects();

        var session = SessionFor(LostLinkedId);

        session.Participants.Should().HaveCount(2, $"both legs were in the call. Measured: {Describe()}");
        session.Participants.Should().OnlyContain(p => p.LeftAt.HasValue,
            $"the reload proved both legs gone. Measured: {Describe()}");
        session.Participants.Should().OnlyContain(p => p.HangupCause == null,
            "a departing participant of a reload-produced ending carries no cause — the reload "
            + $"observed none. Measured: {Describe()}");

        session.Events.Where(e => e.Type == CallSessionEventType.ParticipantLeft)
            .Should().OnlyContain(e => e.Detail == "reload",
                "the audit trail records each departure's provenance too, rather than the string "
                + $"'NotDefined' that reading the default cause would have produced. Measured: {Describe()}");
    }

    [Fact]
    public async Task Reconnect_ShouldRaiseACallEndedEventWithNoCause_WhenTheReloadProvesTheCallGone()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall(LostCallerUid, LostAgentUid, LostLinkedId, "bridge-lost");

        await WhenTheConnectionReconnects();

        _sessionEvents.OfType<CallEndedEvent>().Should().ContainSingle(
            $"the call ended exactly once. Measured: {Describe()}")
            .Which.Cause.Should().BeNull(
            "CallEndedEvent.Cause is already HangupCause?, so 'no cause' costs no public API and "
            + $"reaches every consumer on the event they already handle. Measured: {Describe()}");
    }

    [Fact]
    public async Task Reconnect_ShouldEndTheCallAsCompleted_WhenTheSessionWasConnectedBeforeTheReload()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall(LostCallerUid, LostAgentUid, LostLinkedId, "bridge-lost");
        SessionFor(LostLinkedId).State.Should().Be(CallSessionState.Connected, "premise");

        await WhenTheConnectionReconnects();

        SessionFor(LostLinkedId).State.Should().Be(CallSessionState.Completed,
            "with no cause to read, the outcome follows from what the session already was: this "
            + $"call was up, so it really took place and is now over. Measured: {Describe()}");
    }

    [Fact]
    public async Task Reconnect_ShouldEndTheCallAsFailed_WhenTheSessionNeverConnectedBeforeTheReload()
    {
        await GivenAStartedServer();

        // One leg, still ringing: the session is Created and never became a call.
        _server.Channels.OnNewChannel(LostCallerUid, "PJSIP/trunk-001", ChannelState.Ring,
            callerIdNum: "5551234", context: "from-trunk", linkedId: LostLinkedId);
        SessionFor(LostLinkedId).State.Should().Be(CallSessionState.Created, "premise");

        await WhenTheConnectionReconnects();

        SessionFor(LostLinkedId).State.Should().Be(CallSessionState.Failed,
            "the outcome follows from the prior state, and a call that never connected never "
            + $"became one. Measured: {Describe()}");
        SessionFor(LostLinkedId).HangupCause.Should().BeNull(
            $"the state followed from the session, not from an invented cause. Measured: {Describe()}");
    }

    // --- scenario: an observed hangup is unchanged -----------------------------------------------

    [Fact]
    public async Task Hangup_ShouldCarryAsteriskCauseAndNoReloadMarker_WhenTheHangupWasObserved()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall(HungUpCallerUid, HungUpAgentUid, HungUpLinkedId, "bridge-hungup");

        WhenBothLegsHangUp(HungUpCallerUid, HungUpAgentUid, ObservedCause);

        var session = SessionFor(HungUpLinkedId);

        session.HangupCause.Should().Be(ObservedCause,
            $"the hangup was observed and Asterisk reported its cause. Measured: {Describe()}");
        session.Participants.Should().OnlyContain(p => p.HangupCause == ObservedCause,
            $"each leg carries the cause its Hangup reported. Measured: {Describe()}");
        session.Metadata.Should().NotContainKey("cause",
            "nothing about this ending came from a reload, so nothing marks it as having done so. "
            + $"Measured: {Describe()}");
        session.State.Should().Be(CallSessionState.Failed,
            $"an abnormal cause still drives the state it always did. Measured: {Describe()}");

        _sessionEvents.OfType<CallEndedEvent>().Should().ContainSingle(
            $"Measured: {Describe()}")
            .Which.Cause.Should().Be(ObservedCause,
            $"the observed path is untouched by this change. Measured: {Describe()}");

        session.Events.Where(e => e.Type == CallSessionEventType.ParticipantLeft)
            .Should().OnlyContain(e => e.Detail == "NormalTemporaryFailure",
                $"the audit trail still names the observed cause. Measured: {Describe()}");
    }

    [Fact]
    public async Task Hangup_ShouldStillCompleteNormally_WhenAsteriskReportedNormalClearing()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall(HungUpCallerUid, HungUpAgentUid, HungUpLinkedId, "bridge-hungup");

        WhenBothLegsHangUp(HungUpCallerUid, HungUpAgentUid, HangupCause.NormalClearing);

        var session = SessionFor(HungUpLinkedId);
        session.State.Should().Be(CallSessionState.Completed, $"Measured: {Describe()}");
        session.HangupCause.Should().Be(HangupCause.NormalClearing, $"Measured: {Describe()}");
        session.Metadata.Should().NotContainKey("cause", $"Measured: {Describe()}");
    }

    // --- scenario: a consumer can separate the two ------------------------------------------------

    /// <summary>
    /// A consumer's classifier, written the way one would be written after this change: it reaches
    /// the marker from the ending event's session id and reads a value that is <b>present</b>. Its
    /// only null-sensitive comparison is <c>== NormalClearing</c>, which is false for a null cause
    /// and false for an abnormal one alike — so nothing here is inferred from a value's absence.
    /// </summary>
    private static string ClassifyLikeAConsumer(CallSession session) =>
        session.Metadata.TryGetValue("cause", out var provenance) && provenance == "reload"
            ? "ending-not-observed"
            : session.HangupCause == HangupCause.NormalClearing
                ? "normal-hangup"
                : "abnormal-hangup";

    /// <summary>
    /// The classifier a consumer can write today, with only the cause to go on. It is here to prove
    /// the requirement is about something real: it cannot tell the two endings apart, and the one
    /// bucket it puts them both in is the one that triggers a callback downstream.
    /// </summary>
    private static string ClassifyFromTheCauseAlone(CallSession session) =>
        session.HangupCause == HangupCause.NormalClearing ? "normal-hangup" : "abnormal-hangup";

    [Fact]
    public async Task Classify_ShouldSeparateAReloadEndingFromAnAbnormalHangup_WhenTheConsumerReadsTheMarker()
    {
        await GivenAStartedServer();

        // One call ends the ordinary way, with an abnormal cause Asterisk really reported.
        GivenAnAnsweredCall(HungUpCallerUid, HungUpAgentUid, HungUpLinkedId, "bridge-hungup");
        WhenBothLegsHangUp(HungUpCallerUid, HungUpAgentUid, ObservedCause);

        // The other is proved gone by a completed reload that observed no hangup at all.
        GivenAnAnsweredCall(LostCallerUid, LostAgentUid, LostLinkedId, "bridge-lost");
        await WhenTheConnectionReconnects();

        // A consumer that subscribes only to call endings, and looks each session up by the id the
        // event carries — the whole path, not a peek at internal state.
        var classified = _sessionEvents.OfType<CallEndedEvent>()
            .Select(e => _sessions.GetById(e.SessionId))
            .Where(s => s is not null)
            .ToDictionary(s => s!.LinkedId, s => ClassifyLikeAConsumer(s!), StringComparer.Ordinal);

        classified.Should().HaveCount(2,
            $"both calls ended, and both reached the consumer as CallEndedEvent. Measured: {Describe()}");

        classified[HungUpLinkedId].Should().Be("abnormal-hangup",
            "the hangup was observed and its cause was abnormal; that classification is the one "
            + $"this change must not disturb. Measured: {Describe()}");
        classified[LostLinkedId].Should().Be("ending-not-observed",
            "the marker is what says so — a present value the consumer read, not a null it noticed. "
            + $"Measured: {Describe()}");

        // The point of the requirement, stated as a measurement: without the marker the two endings
        // are the same ending. If this assertion ever fails because the cause-only classifier starts
        // separating them, the SDK has begun inventing a cause again.
        var causeOnly = _sessionEvents.OfType<CallEndedEvent>()
            .Select(e => _sessions.GetById(e.SessionId))
            .Where(s => s is not null)
            .ToDictionary(s => s!.LinkedId, s => ClassifyFromTheCauseAlone(s!), StringComparer.Ordinal);

        causeOnly[HungUpLinkedId].Should().Be("abnormal-hangup", $"Measured: {Describe()}");
        causeOnly[LostLinkedId].Should().Be("abnormal-hangup",
            "a classifier with only the cause to go on lumps an unobserved ending in with a genuine "
            + "abnormal hangup — which is exactly the spurious-callback path D3 exists to close. "
            + $"Measured: {Describe()}");
        causeOnly[LostLinkedId].Should().Be(causeOnly[HungUpLinkedId],
            "so the two are indistinguishable on the cause alone, and the marker is doing the whole "
            + $"job of separating them. Measured: {Describe()}");
    }

    public async ValueTask DisposeAsync()
    {
        _sessionSubscription.Dispose();
        await _sessions.DisposeAsync();
        await _server.DisposeAsync();
    }

    /// <summary>
    /// Records every line the server logs and completes one of two signals when a reload ends: the
    /// last action for a reload that finished, the catch-all's line for one that failed. Both are
    /// rearmed per reload, because <c>StartAsync</c>'s own load trips the first one.
    /// </summary>
    private sealed class ReloadLogger
    {
        private readonly List<string> _lines = [];

        public TaskCompletionSource ReloadFinished { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReloadFailed { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RearmReloadSignals()
        {
            ReloadFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ReloadFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public ILogger<T> For<T>() => new Sink<T>(this);

        private void Add(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }

            if (line.Contains("Reconnect reload failed", StringComparison.Ordinal))
                ReloadFailed.TrySetResult();
        }

        public override string ToString()
        {
            lock (_lines)
            {
                return _lines.Count == 0
                    ? "  (the server logged nothing)"
                    : "  " + string.Join($"{Environment.NewLine}  ", _lines);
            }
        }

        private sealed class Sink<T>(ReloadLogger owner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                owner.Add(exception is null
                    ? $"[{logLevel}] {message}"
                    : $"[{logLevel}] {message} -> {exception.GetType().Name}: {exception.Message}");
            }
        }
    }
}
