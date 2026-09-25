using System.Globalization;
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
/// Binds the eighth requirement: a call the reload reports as live is not described as newly created
/// (ADR-0062, design D5).
///
/// <para>Task 2.6 made the CHANNEL carry the state Asterisk reported. The session did not follow it:
/// <c>CallSessionManager.OnChannelAdded</c> never read <c>channel.State</c>, and only
/// <c>OnChannelStateChanged</c> moves a session — which never fires for a channel that was just
/// admitted, because the state change already happened while the socket was down. So a reload that
/// admitted an answered call opened it in <c>Created</c>, which is the exact condition
/// <c>proposal.md</c> names as the reason a clock-based sweep would mark healthy calls dead.</para>
///
/// <para>It cannot be fixed with a transition: <c>CallSessionStateTransitions</c> lets <c>Created</c>
/// reach only <c>Dialing</c>, <c>Queued</c> and <c>Failed</c>, so <c>TryTransition(Connected)</c>
/// returns false and silently does nothing. The state is therefore chosen when the session is
/// constructed — which also means <c>CallSession.UpdateTimestamp</c> never runs, and that is the
/// second half of what these tests bind: the state is carried, the history is not invented.</para>
///
/// <para>The last test is the non-regression one. <c>OnChannelAdded</c> serves the ordinary live
/// <c>NewChannel</c> path as well, and a regression there breaks every call rather than only
/// reloaded ones, so it asserts that a channel arriving live opens and progresses exactly as it
/// does today even when it arrives already <c>Up</c>.</para>
/// </summary>
public sealed class ReloadedSessionStateTests : IAsyncDisposable
{
    private const string ServerId = "test-srv";

    /// <summary>A call that started during the outage: never held, only ever reported by a reload.</summary>
    private const string OutageUid = "outage-001";
    private const string OutageName = "PJSIP/trunk-outage";
    private const string OutageLinkedId = "linked-outage";

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _server;
    private readonly CallSessionManager _sessions;
    private readonly ReloadLogger _serverLog = new();

    private readonly List<SessionDomainEvent> _sessionEvents = [];
    private readonly IDisposable _sessionSubscription;

    private IReadOnlyList<StatusEvent> _statusReply = [];

    public ReloadedSessionStateTests()
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

    /// <summary>Answers the three actions the load sends; only the channel leg carries data.</summary>
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

    /// <summary>A first load against an idle estate, so the reload below starts from nothing held.</summary>
    private async Task GivenAStartedServer() => await _server.StartAsync();

    /// <summary>
    /// Raises <c>Reconnected</c> and waits for the reload to end — at its last action when it
    /// completed, at the catch-all's log line when it failed.
    /// </summary>
    private async Task WhenTheConnectionReconnects(params StatusEvent[] channelsAsteriskHas)
    {
        _statusReply = channelsAsteriskHas;
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

    /// <summary>The measurement in one line, so a failure reports numbers and not a dump.</summary>
    private string Describe() =>
        $"{_sessions.ActiveSessions.Count()} active session(s) [" +
        string.Join(" | ", _sessions.ActiveSessions.Select(
            s => $"linked={s.LinkedId} state={s.State} connectedAt={s.ConnectedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "null"}"
                 + $" ringingAt={s.RingingAt?.ToString("O", CultureInfo.InvariantCulture) ?? "null"}"
                 + $" origin={s.Metadata.GetValueOrDefault("origin") ?? "(none)"}")) +
        $"]; {_server.Channels.ChannelCount} channel(s) held [" +
        string.Join(", ", _server.Channels.ActiveChannels.Select(c => $"{c.UniqueId} state={c.State}")) +
        "]";

    /// <summary>
    /// A channel as <c>Status</c> really reports it: the state arrives as the numeric
    /// <c>ChannelState</c> header in <c>RawFields</c>, never as <c>StatusEvent.State</c>, which no
    /// supported Asterisk version populates (ADR-0062, design D5). Passing <c>state: null</c> omits
    /// the header entirely, which is the frame the third scenario is about.
    /// </summary>
    private static StatusEvent Leg(string uniqueId, string channel, string linkedId, ChannelState? state)
    {
        var rawFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Context"] = "from-trunk",
            ["CallerIDNum"] = "5551234",
        };

        if (state is { } reported)
        {
            rawFields["ChannelState"] = ((int)reported).ToString(CultureInfo.InvariantCulture);
            rawFields["ChannelStateDesc"] = reported.ToString();
        }

        return new StatusEvent
        {
            UniqueId = uniqueId,
            Channel = channel,
            LinkedId = linkedId,
            RawFields = rawFields,
        };
    }

    private CallSession TheCallOpenedByTheReload() =>
        _sessions.ActiveSessions.Should().ContainSingle(
            $"the reload opens exactly one call for the leg it reported. Measured: {Describe()}")
            .Subject;

    // --- scenario: the reload reports an answered call --------------------------------------------

    [Fact]
    public async Task Reconnect_ShouldOpenTheCallAsConnected_WhenTheReloadReportsTheChannelAnswered()
    {
        await GivenAStartedServer();

        await WhenTheConnectionReconnects(
            Leg(OutageUid, OutageName, OutageLinkedId, ChannelState.Up));

        var session = TheCallOpenedByTheReload();

        session.State.Should().Be(CallSessionState.Connected,
            "Asterisk reported this channel up, so the call it opened is a conversation in "
            + $"progress. Measured: {Describe()}");
        session.State.Should().NotBe(CallSessionState.Created,
            "a live conversation reported as newly created is what SessionReconciler's orphan "
            + $"branch fails past a dialing timeout. Measured: {Describe()}");
    }

    // --- scenario: the reload reports a ringing call ----------------------------------------------

    [Fact]
    public async Task Reconnect_ShouldOpenTheCallAsRinging_WhenTheReloadReportsTheChannelRinging()
    {
        await GivenAStartedServer();

        await WhenTheConnectionReconnects(
            Leg(OutageUid, OutageName, OutageLinkedId, ChannelState.Ringing));

        TheCallOpenedByTheReload().State.Should().Be(CallSessionState.Ringing,
            "the call is ringing, which is the state Asterisk reported and the same one a live "
            + $"NewState carrying Ringing would have produced. Measured: {Describe()}");
    }

    [Fact]
    public async Task Reconnect_ShouldOpenTheCallAsRinging_WhenTheReloadReportsTheChannelInTheRingState()
    {
        await GivenAStartedServer();

        // AST_STATE_RING (4) and AST_STATE_RINGING (5) are two different headers that the session
        // manager has always folded into one session state; the reload uses the same folding.
        await WhenTheConnectionReconnects(
            Leg(OutageUid, OutageName, OutageLinkedId, ChannelState.Ring));

        TheCallOpenedByTheReload().State.Should().Be(CallSessionState.Ringing,
            $"OnChannelStateChanged maps Ring and Ringing alike. Measured: {Describe()}");
    }

    // --- scenario: a state the reload cannot determine --------------------------------------------

    [Fact]
    public async Task Reconnect_ShouldOpenTheCallAsCreated_WhenTheReloadReportsNoChannelState()
    {
        await GivenAStartedServer();

        // No ChannelState header at all — the frame ReadChannelState defaults to Unknown.
        await WhenTheConnectionReconnects(
            Leg(OutageUid, OutageName, OutageLinkedId, state: null));

        var session = TheCallOpenedByTheReload();

        session.State.Should().Be(CallSessionState.Created,
            "nothing was reported, so nothing is asserted and the session opens exactly as it "
            + $"always has. Measured: {Describe()}");
        session.Participants.Select(p => p.UniqueId).Should().Equal([OutageUid],
            "and the call is still opened — a state the snapshot could not determine is not a "
            + $"reason to drop the call. Measured: {Describe()}");
        _server.Channels.GetByUniqueId(OutageUid).Should().NotBeNull(
            $"the channel is still admitted too. Measured: {Describe()}");
    }

    // --- scenario: an unobserved answer time is not invented ---------------------------------------

    [Fact]
    public async Task Reconnect_ShouldLeaveTheAnswerTimeUnknown_WhenTheReloadReportsTheChannelAnswered()
    {
        await GivenAStartedServer();
        var beforeTheReload = DateTimeOffset.UtcNow;

        await WhenTheConnectionReconnects(
            Leg(OutageUid, OutageName, OutageLinkedId, ChannelState.Up));

        var session = TheCallOpenedByTheReload();

        session.ConnectedAt.Should().BeNull(
            "the reload reports that the channel is up now, never when it answered; a Status frame "
            + "carries Seconds, which is channel age and not time since answer, and the two differ "
            + $"by the ring time. Measured: {Describe()}");
        session.RingingAt.Should().BeNull($"nor when it rang. Measured: {Describe()}");
        session.WaitTime.Should().BeNull(
            $"WaitTime is computed from ConnectedAt, so it reads unknown. Measured: {Describe()}");

        session.Metadata.Should().Contain("origin", "reload",
            "and the consumer can TELL it is unknown rather than inferring it from a null: the "
            + "session is marked as having been opened from a reload, the same way a "
            + $"reload-produced ending is marked. Measured: {Describe()}");

        session.CreatedAt.Should().BeOnOrAfter(beforeTheReload,
            "Duration runs from CreatedAt, which for a reloaded call is when the SDK learned of it "
            + "and not when the call began — so Duration is an undercount of the real call age, "
            + $"and the origin marker is what says so. Measured: {Describe()}");

        // Ending it the ordinary way stamps CompletedAt, which is the moment a consumer would ask
        // for TalkTime. It still reads unknown, because the answer it would be measured from was
        // never observed — rather than reading as a talk time measured from the reload.
        _server.Channels.OnHangup(OutageUid, HangupCause.NormalClearing);

        var ended = _sessions.GetByLinkedId(OutageLinkedId);
        ended.Should().NotBeNull();
        ended!.State.Should().Be(CallSessionState.Completed,
            "the call was up, so an observed normal hangup completes it — proof the reloaded state "
            + $"is a real session state and not a label. Measured: {Describe()}");
        ended.CompletedAt.Should().NotBeNull($"Measured: {Describe()}");
        ended.TalkTime.Should().BeNull(
            "TalkTime is CompletedAt minus ConnectedAt, and ConnectedAt was never observed. A "
            + "consumer reads 'unknown', not a duration measured from the moment of the reload. "
            + $"Measured: {Describe()}");
    }

    [Fact]
    public async Task Reconnect_ShouldRecordWhereTheStateCameFrom_WhenTheReloadReportsTheChannelAnswered()
    {
        await GivenAStartedServer();

        await WhenTheConnectionReconnects(
            Leg(OutageUid, OutageName, OutageLinkedId, ChannelState.Up));

        var session = TheCallOpenedByTheReload();

        session.Events.Should().Contain(
            e => e.Type == CallSessionEventType.Connected && e.Detail == "reload",
            "the audit trail says the connected state was reported by a reload, so a reader cannot "
            + "mistake it for an answer this SDK watched happen. Measured: "
            + string.Join(", ", session.Events.Select(e => $"{e.Type}/{e.Detail ?? "null"}")));
    }

    // --- non-regression: the ordinary live path is untouched ---------------------------------------

    [Fact]
    public async Task NewChannel_ShouldOpenTheCallAsCreatedAndProgressAsItAlwaysHas_WhenTheChannelArrivesLive()
    {
        await GivenAStartedServer();

        // Deliberately Up, the state that would move a reloaded session: a live NewChannel is a
        // channel the SDK is watching from its first instant, so its session opens in Created and
        // is moved only by the events that follow. OnChannelAdded serves both paths, and a
        // regression here breaks every call rather than only reloaded ones.
        _server.Channels.OnNewChannel("live-001", "PJSIP/trunk-live", ChannelState.Up,
            callerIdNum: "5559999", context: "from-trunk", linkedId: "linked-live");

        var session = _sessions.GetByLinkedId("linked-live");
        session.Should().NotBeNull($"Measured: {Describe()}");
        session!.State.Should().Be(CallSessionState.Created,
            $"exactly as it does today. Measured: {Describe()}");
        session.Metadata.Should().NotContainKey("origin",
            "nothing about this call came from a reload, so nothing marks it as having done so. "
            + $"Measured: {Describe()}");
        session.Events.Should().OnlyContain(e => e.Type == CallSessionEventType.Created,
            "the audit trail carries the one event today's live admission writes. Measured: "
            + string.Join(", ", session.Events.Select(e => $"{e.Type}/{e.Detail ?? "null"}")));

        // And the transitions that depend on starting from Created still run. Created -> Ringing is
        // not a legal transition, so a live admission that had adopted Ringing would silently swallow
        // the Dialing that DialBegin produces.
        _server.Channels.OnDialBegin("live-001", "live-002", "PJSIP/100-live", null);
        session.State.Should().Be(CallSessionState.Dialing,
            $"Created -> Dialing, the first step of every live call. Measured: {Describe()}");

        _server.Channels.OnNewState("live-001", ChannelState.Up);
        session.State.Should().Be(CallSessionState.Connected,
            $"Dialing -> Connected on the observed answer. Measured: {Describe()}");
        session.ConnectedAt.Should().NotBeNull(
            "and the live path DOES stamp the answer time, because it observed the answer. That is "
            + $"the difference the reloaded path must not erase. Measured: {Describe()}");
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
