using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Binds the requirement that a call the reload proves is gone ends through the completion path
/// consumers already observe (ADR-0062) — specifically its second scenario, that the ending is
/// <b>not merely a state change</b>: it is delivered through the same event a hangup would have
/// produced, and a consumer that subscribes only to call endings observes it.
///
/// <para><b>What "a consumer" means here, and why it is a type and not a lambda.</b>
/// <see cref="CallEndingConsumer"/> is handed one thing — <c>IObservable&lt;SessionDomainEvent&gt;</c>
/// — and subscribes to <c>OfType&lt;CallEndedEvent&gt;()</c>. It holds no reference to
/// <see cref="CallSessionManager"/>, to <c>VerbaraServer</c>, or to the channel table, so it cannot
/// reach any of them even by accident. That is the whole point of the scenario: a session that
/// quietly changed state would satisfy every assertion written against the session object and still
/// be invisible to every consumer written against this SDK, because the reload is the last
/// notification such a call will ever produce. If the ending does not arrive here, the requirement
/// is not met however green the rest of the suite is.</para>
///
/// <para><b>What nothing else binds.</b> <see cref="ReconnectReloadTests"/> measures that the call
/// ends and the active set empties, and <see cref="ReloadEndingProvenanceTests"/> measures how the
/// ending is attributed. Neither binds the <em>shape</em> of the ending against a hangup's, and
/// neither binds the double-ending failure mode that task 2.4 made reachable: two legs of one call
/// are removed by one reload, so the code that raises the ending runs on a path that could raise it
/// twice. <see cref="Reconnect_ShouldRaiseExactlyOneEnding_WhenBothLegsOfOneCallAreRemovedByTheSameReload"/>
/// and the two "no second ending" tests below exist for that.</para>
///
/// <para>Harness traps inherited from <see cref="ReconnectReloadTests"/>: the status reply must be
/// armed before the reconnect is raised; a server that was only constructed has subscribed to
/// nothing, because <c>StartAsync</c> is where <c>Reconnected</c> is attached; and
/// <c>OnReconnected</c> is <c>async void</c>, so its log lines are the only completion signal it
/// produces.</para>
/// </summary>
public sealed class ReloadCompletionPathTests : IAsyncDisposable
{
    private const string ServerId = "test-srv";

    /// <summary>The call a completed reload proves gone. No hangup is ever observed for it.</summary>
    private const string LostCallerUid = "caller-lost";
    private const string LostAgentUid = "agent-lost";
    private const string LostLinkedId = "linked-lost";

    /// <summary>A second, independent call, lost to the same reload.</summary>
    private const string OtherLostCallerUid = "caller-lost-2";
    private const string OtherLostAgentUid = "agent-lost-2";
    private const string OtherLostLinkedId = "linked-lost-2";

    /// <summary>The call that ends the ordinary way, with a cause Asterisk really reported.</summary>
    private const string HungUpCallerUid = "caller-hungup";
    private const string HungUpAgentUid = "agent-hungup";
    private const string HungUpLinkedId = "linked-hungup";

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _server;
    private readonly CallSessionManager _sessions;
    private readonly ReloadLogger _serverLog = new();

    /// <summary>The whole subject of this file: everything a call-ending subscriber can see.</summary>
    private readonly CallEndingConsumer _consumer;

    private IReadOnlyList<StatusEvent> _statusReply = [];

    public ReloadCompletionPathTests()
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

        // The consumer is given the public event stream and nothing else. Every assertion about
        // what "a consumer observes" reads this object, never the manager or the channel table.
        _consumer = new CallEndingConsumer(_sessions.Events);
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

    /// <summary>Both legs hang up while the link is live, with the cause Asterisk reported.</summary>
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

    private string SessionIdOf(string linkedId) =>
        (_sessions.GetByLinkedId(linkedId)
         ?? throw new InvalidOperationException(
             $"no session for linkedId '{linkedId}'. Measured: {Describe()}")).SessionId;

    /// <summary>
    /// Everything about an ending except which call it ended and why: the event's own type, the
    /// server it came from, and whether it carries the identity, the instant and the two durations a
    /// consumer reads off it. Two endings with the same shape were produced by the same code path,
    /// and a consumer written for one needs no change to handle the other — which is exactly what
    /// "delivered through the same event a hangup would have produced" means.
    /// <para>
    /// <c>Cause</c> is deliberately absent: it is the one field the two endings are required to
    /// differ on (ADR-0062, design D3), so including it would make the comparison fail for the very
    /// reason the design says it must.
    /// </para>
    /// </summary>
    private static string Shape(CallEndedEvent ending) =>
        ending.GetType().Name
        + $" serverId={ending.ServerId}"
        + $" sessionId={(string.IsNullOrEmpty(ending.SessionId) ? "<empty>" : "present")}"
        + $" timestamp={(ending.Timestamp == default ? "<default>" : "present")}"
        + $" duration={(ending.Duration < TimeSpan.Zero ? "negative" : "measured")}"
        + $" talkTime={(ending.TalkTime.HasValue ? "present" : "<null>")}";

    /// <summary>The measurement in one line, so a failure reports numbers and not a dump.</summary>
    private string Describe() =>
        $"{_consumer.Endings.Length} ending(s) reached the consumer ["
        + string.Join(" | ", _consumer.Endings.Select(
            e => $"session={e.SessionId} cause={e.Cause?.ToString() ?? "<null>"} {Shape(e)}"))
        + $"]; {_sessions.ActiveSessions.Count()} active session(s) ["
        + string.Join(" | ", _sessions.ActiveSessions.Select(
            s => $"linked={s.LinkedId} state={s.State} participants={s.Participants.Count}"))
        + $"]; {_server.Channels.ChannelCount} channel(s) held ["
        + string.Join(", ", _server.Channels.ActiveChannels.Select(c => c.UniqueId))
        + "]";

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

    // --- scenario: a consumer that subscribes only to call endings observes it --------------------

    [Fact]
    public async Task Reconnect_ShouldReachAConsumerSubscribedOnlyToCallEndings_WhenTheReloadProvesTheCallGone()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall(LostCallerUid, LostAgentUid, LostLinkedId, "bridge-lost");
        var sessionId = SessionIdOf(LostLinkedId);

        // Asterisk answers the reload in full and lists nothing: the call ended during the outage,
        // its Hangup was never delivered and never will be.
        await WhenTheConnectionReconnects();

        var ending = _consumer.Endings.Should().ContainSingle(
            "a consumer that subscribes to nothing but call endings is the whole audience for this "
            + "reload — an ending that only changed session state would be invisible to it, and the "
            + $"reload is the last notification this call will ever produce. Measured: {Describe()}")
            .Which;

        ending.SessionId.Should().Be(sessionId,
            $"the ending names the call it ended. Measured: {Describe()}");
        ending.ServerId.Should().Be(ServerId,
            $"and the server it was held on. Measured: {Describe()}");

        _sessions.GetById(ending.SessionId).Should().NotBeNull(
            "the id on the event is the one a consumer looks the session up by; if it resolved to "
            + $"nothing the ending would carry no usable handle. Measured: {Describe()}");
    }

    [Fact]
    public async Task Reconnect_ShouldDeliverAnEndingShapedExactlyLikeAHangups_WhenTheReloadProvesTheCallGone()
    {
        await GivenAStartedServer();

        // One call ends the ordinary way, through Asterisk's own Hangup.
        GivenAnAnsweredCall(HungUpCallerUid, HungUpAgentUid, HungUpLinkedId, "bridge-hungup");
        var hungUpSessionId = SessionIdOf(HungUpLinkedId);
        WhenBothLegsHangUp(HungUpCallerUid, HungUpAgentUid, HangupCause.NormalClearing);

        // The other is proved gone by a completed reload that observed no hangup at all.
        GivenAnAnsweredCall(LostCallerUid, LostAgentUid, LostLinkedId, "bridge-lost");
        var lostSessionId = SessionIdOf(LostLinkedId);
        await WhenTheConnectionReconnects();

        var bySession = _consumer.Endings.ToDictionary(e => e.SessionId, StringComparer.Ordinal);
        bySession.Should().HaveCount(2,
            $"both calls ended, and both reached the consumer. Measured: {Describe()}");

        var hangupEnding = bySession[hungUpSessionId];
        var reloadEnding = bySession[lostSessionId];

        Shape(reloadEnding).Should().Be(Shape(hangupEnding),
            "the reload's ending must be the same event a hangup produces, not a second kind of "
            + "notification a consumer would have to learn: same type, same server, same populated "
            + $"fields. Measured: {Describe()}");

        // Stated positively as well, so the comparison cannot pass by both sides being empty.
        reloadEnding.TalkTime.Should().NotBeNull(
            "this call was answered, so the ending carries a talk time exactly as the hangup's does. "
            + $"Measured: {Describe()}");
        reloadEnding.Duration.Should().Be(_sessions.GetById(lostSessionId)!.Duration,
            "and the duration on the event is the session's own, computed at the same completion "
            + $"the hangup path computes it at. Measured: {Describe()}");

        // The one field they are required to differ on — the point of D3, restated here so a change
        // that made the two endings identical could not hide behind the shape comparison above.
        hangupEnding.Cause.Should().Be(HangupCause.NormalClearing,
            $"the hangup was observed and Asterisk reported its cause. Measured: {Describe()}");
        reloadEnding.Cause.Should().BeNull(
            $"no hangup was observed, so no cause is known. Measured: {Describe()}");
    }

    // --- exactly one ending per ended call --------------------------------------------------------

    [Fact]
    public async Task Reconnect_ShouldRaiseExactlyOneEnding_WhenBothLegsOfOneCallAreRemovedByTheSameReload()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall(LostCallerUid, LostAgentUid, LostLinkedId, "bridge-lost");
        _sessions.GetByLinkedId(LostLinkedId)!.Participants.Should().HaveCount(2,
            "premise: the reconciliation will remove two channels, and both removals reach the "
            + "same session");

        await WhenTheConnectionReconnects();

        _consumer.Endings.Should().HaveCount(1,
            "one call ended, so the consumer is told once. Two channel removals reach the session "
            + "manager for this call, and a second CallEndedEvent would make every consumer that "
            + "closes a record on the ending close it twice — double billing, a duplicate CDR, a "
            + $"second wrap-up. Measured: {Describe()}");
    }

    [Fact]
    public async Task Reconnect_ShouldRaiseOneEndingPerLostCall_WhenTheReloadProvesTwoCallsGone()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall(LostCallerUid, LostAgentUid, LostLinkedId, "bridge-lost");
        GivenAnAnsweredCall(OtherLostCallerUid, OtherLostAgentUid, OtherLostLinkedId, "bridge-lost-2");
        var expected = new[] { SessionIdOf(LostLinkedId), SessionIdOf(OtherLostLinkedId) };

        await WhenTheConnectionReconnects();

        _consumer.Endings.Select(e => e.SessionId).Should().BeEquivalentTo(expected,
            "two calls were proved gone, so exactly two endings arrive — one each, in any order, "
            + $"with no call ended twice and none left unannounced. Measured: {Describe()}");
    }

    [Fact]
    public async Task Reconnect_ShouldEndOnlyTheCallTheSnapshotOmits_WhenAnotherCallSurvivesTheSameReload()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall(LostCallerUid, LostAgentUid, LostLinkedId, "bridge-lost");
        GivenAnAnsweredCall(OtherLostCallerUid, OtherLostAgentUid, OtherLostLinkedId, "bridge-lost-2");
        var lostSessionId = SessionIdOf(LostLinkedId);
        var survivingSessionId = SessionIdOf(OtherLostLinkedId);

        // Asterisk still has one of the two calls, and says so with both its legs.
        await WhenTheConnectionReconnects(
            Leg(OtherLostCallerUid, $"PJSIP/trunk-{OtherLostCallerUid}", OtherLostLinkedId),
            Leg(OtherLostAgentUid, $"PJSIP/100-{OtherLostAgentUid}", OtherLostLinkedId));

        _consumer.Endings.Should().ContainSingle(
            "the ending is driven by the difference the snapshot describes, not by the reload "
            + $"happening. Measured: {Describe()}")
            .Which.SessionId.Should().Be(lostSessionId,
            $"and it names the call the snapshot omitted. Measured: {Describe()}");

        _sessions.ActiveSessions.Select(s => s.SessionId).Should().Contain(survivingSessionId,
            "the call the snapshot still contains was never ended, and its consumer was never told "
            + $"anything. Measured: {Describe()}");
    }

    // --- the call is gone from the active set afterwards -------------------------------------------

    [Fact]
    public async Task Reconnect_ShouldRemoveTheCallFromTheActiveSet_WhenTheEndingReachesTheConsumer()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall(LostCallerUid, LostAgentUid, LostLinkedId, "bridge-lost");
        var sessionId = SessionIdOf(LostLinkedId);
        _sessions.ActiveSessions.Should().ContainSingle("premise: the call is up before the outage");

        await WhenTheConnectionReconnects();

        _consumer.Endings.Should().ContainSingle($"premise for this test. Measured: {Describe()}");

        _sessions.ActiveSessions.Should().BeEmpty(
            "a call Asterisk no longer has cannot still be in progress, and a consumer that polls "
            + $"the active set must not keep finding it there. Measured: {Describe()}");
        _sessions.GetById(sessionId).Should().NotBeNull(
            "it is ended, not erased — the record the ending pointed at is still readable. "
            + $"Measured: {Describe()}");
        _sessions.GetRecentCompleted().Select(s => s.SessionId).Should().Contain(sessionId,
            $"and it moved to where finished calls are read from. Measured: {Describe()}");
    }

    // --- the ending happens once, and stays happened ----------------------------------------------

    [Fact]
    public async Task Reconnect_ShouldRaiseNoSecondEnding_WhenAFurtherReloadFindsTheSameCallStillAbsent()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall(LostCallerUid, LostAgentUid, LostLinkedId, "bridge-lost");

        await WhenTheConnectionReconnects();
        _consumer.Endings.Should().ContainSingle($"premise for this test. Measured: {Describe()}");

        // A second outage, and Asterisk still lists nothing. The call is already over: its absence
        // is no longer news, and the channels it was ended from are no longer held.
        await WhenTheConnectionReconnects();

        _consumer.Endings.Should().HaveCount(1,
            "an already-ended call cannot end again; a reconnect loop would otherwise close the "
            + $"consumer's record once per reconnect. Measured: {Describe()}");
    }

    [Fact]
    public async Task Hangup_ShouldRaiseNoSecondEnding_WhenItArrivesForAChannelAReloadAlreadyRemoved()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall(LostCallerUid, LostAgentUid, LostLinkedId, "bridge-lost");
        var sessionId = SessionIdOf(LostLinkedId);

        await WhenTheConnectionReconnects();
        _consumer.Endings.Should().ContainSingle($"premise for this test. Measured: {Describe()}");

        // A late Hangup for a channel the reload already removed. Measured on 18.26.4, 20.20.1,
        // 22.9.0 and 23.4.1, Asterisk replays nothing after a reconnect — but a consumer's record
        // must not depend on that measurement holding for every version and every transport.
        WhenBothLegsHangUp(LostCallerUid, LostAgentUid, HangupCause.NormalClearing);

        _consumer.Endings.Should().HaveCount(1,
            "the call ended once, on the reload. A hangup for a channel that is no longer held is "
            + $"about a call that is already over. Measured: {Describe()}");

        var session = _sessions.GetById(sessionId)!;
        session.HangupCause.Should().BeNull(
            "and the late hangup does not retroactively give the ending a cause nobody observed at "
            + $"the time it was recorded. Measured: {Describe()}");
    }

    public async ValueTask DisposeAsync()
    {
        _consumer.Dispose();
        await _sessions.DisposeAsync();
        await _server.DisposeAsync();
    }

    /// <summary>
    /// A consumer of call endings and nothing else: it is constructed from
    /// <c>ICallSessionManager.Events</c>, filters it to <see cref="CallEndedEvent"/>, and keeps no
    /// reference to anything that could tell it about a call by another route. Everything this file
    /// claims "a consumer observes" is read from here.
    /// </summary>
    private sealed class CallEndingConsumer : IDisposable
    {
        private readonly List<CallEndedEvent> _endings = [];
        private readonly IDisposable _subscription;

        public CallEndingConsumer(IObservable<SessionDomainEvent> events) =>
            _subscription = events.OfType<CallEndedEvent>().Subscribe(Record);

        /// <summary>
        /// A copy, because the reload raises its endings on the thread reading the snapshot while
        /// the test asserts on its own.
        /// </summary>
        public CallEndedEvent[] Endings
        {
            get
            {
                lock (_endings)
                {
                    return _endings.ToArray();
                }
            }
        }

        private void Record(CallEndedEvent ending)
        {
            lock (_endings)
            {
                _endings.Add(ending);
            }
        }

        public void Dispose() => _subscription.Dispose();
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
