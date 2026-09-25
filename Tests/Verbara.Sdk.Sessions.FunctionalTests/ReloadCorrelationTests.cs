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
/// Binds "Correlation SHALL survive the reload" for the half of it that no other test reaches: the
/// channels a reload reports that the SDK has <b>never seen</b> (ADR-0062, design D4).
///
/// <para>Why this file exists next to <see cref="ReconnectReloadTests"/>. That file's surviving-call
/// measurement went green at task 2.2, because the reconciliation stopped re-admitting a
/// <c>UniqueId</c> it already holds — so the two spurious <c>Created</c> sessions it measured are
/// simply never opened, and the code path that builds a <c>ChannelSnapshotEntry</c>'s correlation is
/// never exercised by it. A call that <b>starts during the outage</b> is the shape that still runs
/// that path: both of its legs are new to the SDK, both are admitted out of the snapshot, and the
/// correlation they are admitted with is the only thing that decides whether one call stays one
/// call. Green there is not coverage here.</para>
///
/// <para>The same argument applies to a first load: <c>RequestInitialStateAsync</c> serves both, so a
/// process restarted during live traffic admits every channel it finds exactly the way this reload
/// does. <see cref="InitialLoadTests"/> deliberately binds neither the loaded channels'
/// <c>LinkedId</c> nor any session identity derived from correlated legs, precisely so this task
/// could move them.</para>
///
/// <para>The degradation direction is bound here too, and it is the reason the requirement is worth
/// stating: task 3.3 measured <c>Linkedid</c> present, non-empty and identical across every leg of
/// one call on all four supported Asterisk versions, so there is <b>no version that triggers the
/// fallback</b> — nothing but a test keeps it honest. A channel the snapshot reports without a
/// correlation identifier must degrade to <c>linkedId = uniqueId</c>, be admitted, and invent no
/// second call for a call already held.</para>
///
/// <para>Harness traps, inherited from <see cref="ReconnectReloadTests"/>: the status reply must be
/// armed before the reconnect is raised, a server that was only constructed has subscribed to
/// nothing (<c>StartAsync</c> is where <c>Reconnected</c> is attached), and <c>OnReconnected</c> is
/// <c>async void</c> — its log lines are the only completion signal it has.</para>
/// </summary>
public sealed class ReloadCorrelationTests : IAsyncDisposable
{
    private const string ServerId = "test-srv";

    /// <summary>The call the SDK already held before the outage.</summary>
    private const string HeldCallerUid = "caller-001";
    private const string HeldAgentUid = "agent-001";
    private const string HeldLinkedId = "linked-001";

    /// <summary>The call that started while the connection was down — both legs new to the SDK.</summary>
    private const string OutageCallerUid = "caller-900";
    private const string OutageAgentUid = "agent-900";
    private const string OutageLinkedId = "linked-900";

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _server;
    private readonly CallSessionManager _sessions;
    private readonly ReloadLogger _serverLog = new();

    private readonly List<SessionDomainEvent> _sessionEvents = [];
    private readonly List<AsteriskChannel> _removed = [];
    private readonly IDisposable _sessionSubscription;

    private IReadOnlyList<StatusEvent> _statusReply = [];

    public ReloadCorrelationTests()
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
    /// Answers the three actions the load sends. Only the channel leg carries data; the agent leg is
    /// last, so it is where a reload that completed says so.
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

        foreach (var status in _statusReply)
            yield return status;
    }

    private async Task GivenAStartedServer() => await _server.StartAsync();

    /// <summary>A two-leg inbound call, answered and bridged: one session, state Connected.</summary>
    private void GivenAnAnsweredCall()
    {
        _server.Channels.OnNewChannel(HeldCallerUid, "PJSIP/trunk-001", ChannelState.Ring,
            callerIdNum: "5551234", context: "from-trunk", linkedId: HeldLinkedId);
        _server.Channels.OnNewChannel(HeldAgentUid, "PJSIP/100-001", ChannelState.Ring,
            linkedId: HeldLinkedId);
        _server.Channels.OnDialBegin(HeldCallerUid, HeldAgentUid, "PJSIP/100-001", null);
        _server.Channels.OnNewState(HeldAgentUid, ChannelState.Up);
        _server.Bridges.OnBridgeCreated("bridge-001", "mixing", "simple_bridge", null, null);
        _server.Bridges.OnChannelEntered("bridge-001", HeldCallerUid);
        _server.Bridges.OnChannelEntered("bridge-001", HeldAgentUid);
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
            s => $"linked={s.LinkedId} state={s.State} participants={s.Participants.Count}")) +
        $"]; {_server.Channels.ChannelCount} channel(s) held [" +
        string.Join(", ", _server.Channels.ActiveChannels.Select(
            c => $"uid={c.UniqueId} linked={c.LinkedId ?? "(null)"}")) +
        $"]; ChannelRemoved raised for [{string.Join(", ", _removed.Select(c => c.UniqueId))}]" +
        $"; domain events [{string.Join(", ", _sessionEvents.Select(e => e.GetType().Name))}]";

    /// <summary>
    /// A channel as <c>Status</c> really reports it: the state arrives as the numeric
    /// <c>ChannelState</c> header in <c>RawFields</c>, never as <c>StatusEvent.State</c>, which no
    /// supported Asterisk version populates (ADR-0062, design D5).
    /// </summary>
    private static StatusEvent Leg(string uniqueId, string channel, string? linkedId) => new()
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

    // --- scenario: both legs of one call survive the outage, and neither was ever seen ------------

    [Fact]
    public async Task Reconnect_ShouldOpenOneSession_WhenTheReloadReportsTwoUnseenLegsSharingOneLinkedId()
    {
        await GivenAStartedServer();
        _sessions.ActiveSessions.Should().BeEmpty("the SDK held nothing before the outage");

        // A call that began while the socket was down. The SDK never saw either NewChannel, so the
        // reload is the first and only thing that will ever tell it these two legs are one call —
        // and the Linkedid that says so is on the wire, measured on 18/20/22/23.
        await WhenTheConnectionReconnects(
            Leg(OutageCallerUid, "PJSIP/trunk-900", OutageLinkedId),
            Leg(OutageAgentUid, "PJSIP/100-900", OutageLinkedId));

        var session = _sessions.ActiveSessions.Should().ContainSingle(
            "two legs Asterisk correlates under one Linkedid are one call; admitting them without "
            + $"that correlation splits one call into two records. Measured: {Describe()}").Subject;

        session.LinkedId.Should().Be(OutageLinkedId,
            "the call is keyed on the correlation Asterisk reported, not on whichever leg happened "
            + $"to be admitted first. Measured: {Describe()}");
        session.Participants.Select(p => p.UniqueId).Should().BeEquivalentTo(
            [OutageCallerUid, OutageAgentUid],
            $"both legs belong to it. Measured: {Describe()}");

        _sessionEvents.OfType<CallStartedEvent>().Should().ContainSingle(
            "the consumer opens exactly one record for one call. Measured: " + Describe());
    }

    [Fact]
    public async Task Reconnect_ShouldCarryTheReportedCorrelationOntoTheChannel_WhenTheReloadAdmitsAnUnseenLeg()
    {
        await GivenAStartedServer();

        await WhenTheConnectionReconnects(
            Leg(OutageCallerUid, "PJSIP/trunk-900", OutageLinkedId));

        var admitted = _server.Channels.GetByUniqueId(OutageCallerUid);
        admitted.Should().NotBeNull($"the reload admits what it reports. Measured: {Describe()}");
        admitted!.LinkedId.Should().Be(OutageLinkedId,
            "the snapshot's Linkedid reaches the channel table; a channel admitted without it hands "
            + $"every subscriber a call identity of one leg. Measured: {Describe()}");
    }

    // --- scenario: a reload without correlation does not invent calls ------------------------------

    [Fact]
    public async Task Reconnect_ShouldNotCreateAnAdditionalCall_WhenTheReloadReportsAHeldChannelWithNoCorrelation()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall();
        var sessionIdBefore = _sessions.ActiveSessions.Single().SessionId;

        // Asterisk still has both legs but supplies no Linkedid for either. The channels are held,
        // so the reconciliation keeps them as they are and correlation never has to be re-derived.
        await WhenTheConnectionReconnects(
            Leg(HeldCallerUid, "PJSIP/trunk-001", linkedId: null),
            Leg(HeldAgentUid, "PJSIP/100-001", linkedId: ""));

        _sessions.ActiveSessions.Should().ContainSingle(
            "a reload that reports no correlation for a channel already held invents no second "
            + $"call for it. Measured: {Describe()}")
            .Which.SessionId.Should().Be(sessionIdBefore,
                $"and it is the same call under the identity it had. Measured: {Describe()}");

        _sessions.ActiveSessions.Single().LinkedId.Should().Be(HeldLinkedId,
            $"the correlation it was admitted with is not overwritten. Measured: {Describe()}");
        _sessionEvents.OfType<CallStartedEvent>().Should().ContainSingle(
            $"only the original call was ever started. Measured: {Describe()}");
        _sessionEvents.OfType<CallEndedEvent>().Should().BeEmpty(
            $"the snapshot contains both legs, so nothing is gone. Measured: {Describe()}");
    }

    [Fact]
    public async Task Reconnect_ShouldAdmitTheChannelUnderItsOwnId_WhenTheReloadReportsAnUnseenLegWithNoCorrelation()
    {
        await GivenAStartedServer();

        // Two never-seen legs, one with the header absent and one with it empty. No supported
        // Asterisk version does this, which is exactly why only a test keeps the path honest: it
        // must degrade to today's linkedId = uniqueId, not throw and not drop the channel.
        await WhenTheConnectionReconnects(
            Leg(OutageCallerUid, "PJSIP/trunk-900", linkedId: null),
            Leg(OutageAgentUid, "PJSIP/100-900", linkedId: ""));

        _server.Channels.ActiveChannels.Select(c => c.UniqueId).Should().BeEquivalentTo(
            [OutageCallerUid, OutageAgentUid],
            "an absent or empty correlation identifier is not a reason to reject a channel. "
            + $"Measured: {Describe()}");

        _sessions.ActiveSessions.Select(s => s.LinkedId).Should().BeEquivalentTo(
            [OutageCallerUid, OutageAgentUid],
            "with nothing to correlate them, each leg is its own call keyed on its own id — "
            + $"today's behaviour, unchanged. Measured: {Describe()}");
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
