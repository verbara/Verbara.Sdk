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
/// Binds the failure direction of the post-reconnect reload, at the level where it matters: a
/// reload that could not be shown to have completed MUST end nothing — no session closed, no
/// <c>CallEndedEvent</c>, every participant still in the call (ADR-0062, design D1).
///
/// <para>Why this is a requirement and not carefulness: <c>ChannelManager.ReconcileWithSnapshot</c>
/// removes exactly the channels the snapshot omits, and a snapshot that died halfway through omits
/// every channel it never reached. Streaming into the table while reading it, or reconciling a
/// truncated buffer, would therefore end calls that are still live — worse than the ghost sessions
/// this change removes. <c>VerbaraServer</c> reads the snapshot into a buffer and reconciles only
/// after the read completed, so a failure discards the buffer having mutated nothing.</para>
///
/// <para>Two harness traps, inherited from <see cref="ReconnectReloadTests"/>: the status reply must
/// be armed before the reconnect is raised, and a server that was only constructed has subscribed to
/// nothing — <c>StartAsync</c> is where <c>Reconnected</c> is attached. A third is specific to a
/// failing reload: <c>OnReconnected</c> is <c>async void</c> and swallows every exception into a log
/// line, so that line is the only completion signal a failed reload produces.</para>
/// </summary>
public sealed class ReloadFailureTests : IAsyncDisposable
{
    private const string ServerId = "test-srv";
    private const string CallerUid = "caller-001";
    private const string AgentUid = "agent-001";
    private const string LinkedId = "linked-001";

    // A second, independent call. The spec's premise for this requirement is plural — "GIVEN two
    // connected calls … THEN neither call is ended AND both remain active" — and the plural is not
    // decoration: a snapshot that died partway is truncated, and a reload that reconciled the
    // truncated buffer would find the calls it never reached missing. With one held call the
    // reconciliation would drop legs out of that one call and stop short of ending it, because a
    // session ends only once every participant has left; with two, the call the snapshot never
    // reached loses both its legs and ends outright, which is the failure this file exists to
    // catch. The second call's legs are also the ones delivered LAST, so the truncation falls
    // between the two calls rather than inside one.
    private const string CallerUid2 = "caller-002";
    private const string AgentUid2 = "agent-002";
    private const string LinkedId2 = "linked-002";

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _server;
    private readonly CallSessionManager _sessions;
    private readonly ReloadLogger _serverLog = new();

    private readonly List<SessionDomainEvent> _sessionEvents = [];
    private readonly List<AsteriskChannel> _removed = [];
    private readonly IDisposable _sessionSubscription;

    private readonly CancellationTokenSource _abandonedReload = new();

    private IReadOnlyList<StatusEvent> _statusReply = [];
    private bool _snapshotDiesAfterFirstEntry;

    public ReloadFailureTests()
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
        _server.Channels.ChannelRemoved += _removed.Add;
    }

    /// <summary>
    /// Answers the three actions the load sends. The channel leg can be made to die partway — one
    /// entry delivered, then the socket gone — which is the whole subject of this file.
    /// </summary>
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

        var delivered = 0;
        foreach (var status in _statusReply)
        {
            yield return status;

            if (++delivered == 1 && _snapshotDiesAfterFirstEntry)
                throw new IOException("the AMI socket died halfway through the Status snapshot");
        }
    }

    private async Task GivenAStartedServer() => await _server.StartAsync();

    /// <summary>A two-leg inbound call, answered and bridged: one session, state Connected.</summary>
    private void GivenAnAnsweredCall() =>
        GivenAnAnsweredCall(CallerUid, AgentUid, LinkedId, "PJSIP/trunk-001", "PJSIP/100-001",
            "bridge-001", "5551234");

    /// <summary>
    /// The second of the two calls the spec's failure-direction premise requires. Same shape as
    /// <see cref="GivenAnAnsweredCall()"/>, under its own correlation identifier, so the two
    /// sessions are independent and either can be ended without the other.
    /// </summary>
    private void GivenASecondAnsweredCall() =>
        GivenAnAnsweredCall(CallerUid2, AgentUid2, LinkedId2, "PJSIP/trunk-002", "PJSIP/200-002",
            "bridge-002", "5559876");

    private void GivenAnAnsweredCall(string callerUid, string agentUid, string linkedId,
        string trunkChannel, string agentChannel, string bridgeId, string callerIdNum)
    {
        _server.Channels.OnNewChannel(callerUid, trunkChannel, ChannelState.Ring,
            callerIdNum: callerIdNum, context: "from-trunk", linkedId: linkedId);
        _server.Channels.OnNewChannel(agentUid, agentChannel, ChannelState.Ring,
            linkedId: linkedId);
        _server.Channels.OnDialBegin(callerUid, agentUid, agentChannel, null);
        _server.Channels.OnNewState(agentUid, ChannelState.Up);
        _server.Bridges.OnBridgeCreated(bridgeId, "mixing", "simple_bridge", null, null);
        _server.Bridges.OnChannelEntered(bridgeId, callerUid);
        _server.Bridges.OnChannelEntered(bridgeId, agentUid);
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

    /// <summary>The measurement in one line, so a failure reports numbers and not a dump.</summary>
    private string Describe() =>
        $"{_sessions.ActiveSessions.Count()} active session(s) [" +
        string.Join(" | ", _sessions.ActiveSessions.Select(
            s => $"linked={s.LinkedId} state={s.State} participants={s.Participants.Count}"
                 + $" left={s.Participants.Count(p => p.LeftAt.HasValue)}")) +
        $"]; {_server.Channels.ChannelCount} channel(s) held" +
        $"; ChannelRemoved raised for [{string.Join(", ", _removed.Select(c => c.UniqueId))}]" +
        $"; domain events [{string.Join(", ", _sessionEvents.Select(e => e.GetType().Name))}]";

    /// <summary>
    /// A channel as <c>Status</c> really reports it: the state arrives as the numeric
    /// <c>ChannelState</c> header in <c>RawFields</c>, never as <c>StatusEvent.State</c>, which no
    /// supported Asterisk version populates (ADR-0062, design D5).
    /// </summary>
    private static StatusEvent Leg(string uniqueId, string channel, string linkedId = LinkedId) => new()
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

    [Fact]
    public async Task Reconnect_ShouldEndNothing_WhenTheReloadSnapshotFailsPartway()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall();
        GivenASecondAnsweredCall();
        _sessions.ActiveSessions.Should().HaveCount(2, "two calls are up before the outage");
        var idsBefore = _sessions.ActiveSessions
            .ToDictionary(s => s.LinkedId, s => s.SessionId, StringComparer.Ordinal);

        // Asterisk starts answering the reload — one leg of the FIRST call arrives — and then the
        // socket dies. The snapshot never reached the other three legs, and that silence is not
        // evidence of a hangup. The second call is the one a truncated buffer would end outright:
        // neither of its legs was ever delivered.
        _snapshotDiesAfterFirstEntry = true;
        await WhenTheConnectionReconnects(
            Leg(CallerUid, "PJSIP/trunk-001"),
            Leg(AgentUid, "PJSIP/100-001"),
            Leg(CallerUid2, "PJSIP/trunk-002", LinkedId2),
            Leg(AgentUid2, "PJSIP/200-002", LinkedId2));

        _sessionEvents.OfType<CallEndedEvent>().Should().BeEmpty(
            "a reload that cannot be trusted ends nothing; ending a live call in error is worse "
            + $"than the defect this change removes. Measured: {Describe()}");
        _removed.Should().BeEmpty(
            "the reload is buffered and reconciled only once the read completed, so a failed read "
            + $"announces no removal at all. Measured: {Describe()}");

        _sessions.ActiveSessions.Select(s => s.LinkedId).Should().BeEquivalentTo(
            [LinkedId, LinkedId2],
            "both calls were live before the reload and both are still live after it — including "
            + $"the one the truncated snapshot never mentioned. Measured: {Describe()}");

        foreach (var session in _sessions.ActiveSessions)
        {
            session.SessionId.Should().Be(idsBefore[session.LinkedId],
                "it is the same call, under the same identity");
            session.State.Should().Be(CallSessionState.Connected,
                $"a failed reload changes no session state either. Measured: {Describe()}");
            session.Participants.Should().HaveCount(2,
                $"both legs are still in the call. Measured: {Describe()}");
            session.Participants.Should().OnlyContain(p => !p.LeftAt.HasValue,
                "a participant marked as having left is how a consumer sees a leg drop out; the "
                + $"reload observed no leg leaving. Measured: {Describe()}");
            session.Participants.Should().OnlyContain(p => p.HangupCause == null,
                $"no hangup was observed for either leg. Measured: {Describe()}");
        }
    }

    [Fact]
    public async Task Reconnect_ShouldKeepEveryHeldChannel_WhenTheReloadSnapshotFailsPartway()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall();
        GivenASecondAnsweredCall();

        _snapshotDiesAfterFirstEntry = true;
        await WhenTheConnectionReconnects(
            Leg(CallerUid, "PJSIP/trunk-001"),
            Leg(AgentUid, "PJSIP/100-001"),
            Leg(CallerUid2, "PJSIP/trunk-002", LinkedId2),
            Leg(AgentUid2, "PJSIP/200-002", LinkedId2));

        _server.Channels.ActiveChannels.Select(c => c.UniqueId).Should().BeEquivalentTo(
            [CallerUid, AgentUid, CallerUid2, AgentUid2],
            "the buffer is discarded with the exception, so the table is exactly as it was — "
            + $"including the three legs the snapshot never reached. Measured: {Describe()}");
        _server.Channels.GetByUniqueId(CallerUid)!.LinkedId.Should().Be(LinkedId,
            "the held instances survive untouched, correlation included");
    }

    [Fact]
    public async Task Reconnect_ShouldEndNothing_WhenAsteriskNeverAnswersTheStateRequest()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall();
        var sessionIdBefore = _sessions.ActiveSessions.Single().SessionId;

        // Asterisk accepts the Status action and then says nothing at all. The reload never
        // completes, so it never reconciles, so it ends nothing — the hang is the safe outcome.
        _statusReply = [];
        _serverLog.RearmReloadSignals();
        _connection
            .SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(_ => NeverAnswers());
        _connection.Reconnected += Raise.Event<Action>();

        // The premise is a non-event — the reload still waiting — and only a clock bounds that.
        // fence-allow: GUARD-TIMEOUT — the assertion below is that this bound is what won the race
        var timeout = Task.Delay(TimeSpan.FromSeconds(1));
        var ended = await Task.WhenAny(
            _serverLog.ReloadFinished.Task, _serverLog.ReloadFailed.Task, timeout);

        ReferenceEquals(ended, timeout).Should().BeTrue(
            "an unanswered state request is the premise of this test: the reload must still be "
            + $"waiting, neither finished nor failed. Server log:{Environment.NewLine}{_serverLog}");
        _sessionEvents.OfType<CallEndedEvent>().Should().BeEmpty(
            $"absence of an answer is not evidence that the call is gone. Measured: {Describe()}");
        _sessions.ActiveSessions.Should().ContainSingle($"Measured: {Describe()}")
            .Which.SessionId.Should().Be(sessionIdBefore);
        _server.Channels.ChannelCount.Should().Be(2, $"Measured: {Describe()}");
    }

    /// <summary>
    /// A reply that is accepted and then never produces anything and never ends — Asterisk taking
    /// the action and saying nothing. Released only when the test is torn down, so the abandoned
    /// reload does not outlive it.
    /// </summary>
    private async IAsyncEnumerable<ManagerEvent> NeverAnswers()
    {
        // This IS the fake: Asterisk accepting the action and then never answering at all.
        // fence-allow: SIMULATED-WORK — stands in for silence; released only by teardown's cancel
        await Task.Delay(Timeout.InfiniteTimeSpan, _abandonedReload.Token);
        yield break;
    }

    public async ValueTask DisposeAsync()
    {
        await _abandonedReload.CancelAsync();
        _sessionSubscription.Dispose();
        await _sessions.DisposeAsync();
        await _server.DisposeAsync();
        _abandonedReload.Dispose();
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
