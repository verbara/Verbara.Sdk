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
/// Measures what an AMI reconnect does to sessions that the session manager is holding.
///
/// <para>The mechanism under measurement: <c>VerbaraServer.OnReconnected</c> calls
/// <c>Channels.Clear()</c>, and <c>ChannelManager.Clear()</c> empties its two dictionaries
/// without raising <c>ChannelRemoved</c>. The session manager therefore never learns that the
/// channels went away. The reload then re-adds whatever Asterisk still has via
/// <c>StatusAction</c> — and <c>RequestInitialStateAsync</c> calls <c>OnNewChannel</c> without
/// passing the status event's <c>LinkedId</c>.</para>
///
/// <para>These tests assert the behaviour a consumer needs, not the behaviour the code has, so a
/// failure here is the measurement. Each one states what the current code is expected to do.</para>
/// </summary>
public sealed class ReconnectReloadTests : IAsyncDisposable
{
    private const string ServerId = "test-srv";
    private const string CallerUid = "caller-001";
    private const string AgentUid = "agent-001";
    private const string LinkedId = "linked-001";

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _server;
    private readonly CallSessionManager _sessions;
    private readonly RecordingLogger _serverLog = new();
    private TaskCompletionSource _reloadDone =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<string> _actionsSeen = [];
    private IReadOnlyList<StatusEvent> _statusReply = [];

    public ReconnectReloadTests()
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
    }

    /// <summary>
    /// Answers the three actions <c>RequestInitialStateAsync</c> sends. Only the channel leg
    /// carries data; <c>AgentsAction</c> is the last one, so it is where the reload is signalled
    /// as finished — <c>OnReconnected</c> is <c>async void</c> and cannot be awaited directly.
    /// </summary>
    private async IAsyncEnumerable<ManagerEvent> Reply(ManagerAction action)
    {
        await Task.Yield();
        lock (_actionsSeen)
        {
            _actionsSeen.Add(action.GetType().Name);
        }

        if (action is StatusAction)
        {
            foreach (var status in _statusReply)
            {
                yield return status;
            }
        }

        if (action is AgentsAction)
        {
            _reloadDone.TrySetResult();
        }
    }

    /// <summary>
    /// Starts the server, which is where <c>Reconnected</c> is subscribed — a server that was only
    /// constructed never hears a reconnect at all. <c>StartAsync</c> ends with its own
    /// <c>RequestInitialStateAsync</c>, so the completion signal is armed after it, not before.
    /// </summary>
    private async Task GivenAStartedServer()
    {
        await _server.StartAsync();
        lock (_actionsSeen)
        {
            _actionsSeen.Clear();
        }
    }

    /// <summary>A two-leg inbound call, answered and bridged: one session, state Connected.</summary>
    private void GivenAnAnsweredCall()
    {
        _server.Channels.OnNewChannel(CallerUid, "PJSIP/trunk-001", ChannelState.Ring,
            callerIdNum: "5551234", context: "from-trunk", linkedId: LinkedId);
        _server.Channels.OnNewChannel(AgentUid, "PJSIP/100-001", ChannelState.Ring,
            linkedId: LinkedId);
        _server.Channels.OnDialBegin(CallerUid, AgentUid, "PJSIP/100-001", null);
        _server.Channels.OnNewState(AgentUid, ChannelState.Up);
        _server.Bridges.OnBridgeCreated("bridge-001", "mixing", "simple_bridge", null, null);
        _server.Bridges.OnChannelEntered("bridge-001", CallerUid);
        _server.Bridges.OnChannelEntered("bridge-001", AgentUid);
    }

    private async Task WhenTheConnectionReconnects(params StatusEvent[] channelsAsteriskStillHas)
    {
        _statusReply = channelsAsteriskStillHas;
        _reloadDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.Reconnected += Raise.Event<Action>();

        try
        {
            await _reloadDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // The reload never finished. OnReconnected swallows every exception into a log line,
            // so say what the server actually did instead of reporting a bare timeout.
            string actions;
            lock (_actionsSeen)
            {
                actions = _actionsSeen.Count == 0 ? "(none)" : string.Join(", ", _actionsSeen);
            }

            throw new InvalidOperationException(
                $"The reconnect reload never completed. Actions the server sent: {actions}. "
                + $"Server log:{Environment.NewLine}{_serverLog}");
        }
    }

    /// <summary>The measurement itself, in one line, so a failure reports numbers and not a dump.</summary>
    private string Describe() =>
        $"{_sessions.ActiveSessions.Count()} active session(s) [" +
        string.Join(" | ", _sessions.ActiveSessions.Select(s => $"linked={s.LinkedId} state={s.State} participants={s.Participants.Count}")) +
        "]";

    private static StatusEvent Leg(string uniqueId, string channel, string linkedId) => new()
    {
        UniqueId = uniqueId,
        Channel = channel,
        State = nameof(ChannelState.Up),
        LinkedId = linkedId,
    };

    [Fact]
    public async Task Reconnect_ShouldEndTheSession_WhenTheCallHungUpDuringTheOutage()
    {
        var events = new List<SessionDomainEvent>();
        using var subscription = _sessions.Events.Subscribe(events.Add);

        await GivenAStartedServer();
        GivenAnAnsweredCall();
        _sessions.ActiveSessions.Should().HaveCount(1, "the call is up before the outage");

        // Asterisk answers the reload with nothing: the call ended while the socket was down, so
        // its Hangup was never delivered and never will be.
        await WhenTheConnectionReconnects();

        _sessions.ActiveSessions.Should().BeEmpty(
            "a call Asterisk no longer has cannot still be in progress; the reload is the only "
            + $"notification the consumer will ever get that it ended. Measured: {Describe()}");
        events.OfType<CallEndedEvent>().Should().ContainSingle(
            "the consumer closes its own records on CallEndedEvent, and nothing else will arrive. "
            + $"Measured: {events.Count} domain event(s), "
            + $"{events.OfType<CallEndedEvent>().Count()} of them CallEndedEvent");
    }

    [Fact]
    public async Task Reconnect_ShouldKeepOneSession_WhenBothLegsSurviveTheOutage()
    {
        await GivenAStartedServer();
        GivenAnAnsweredCall();
        var sessionIdBefore = _sessions.ActiveSessions.Single().SessionId;

        // Asterisk still has both legs, and its Status carries the Linkedid that ties them.
        await WhenTheConnectionReconnects(
            Leg(CallerUid, "PJSIP/trunk-001", LinkedId),
            Leg(AgentUid, "PJSIP/100-001", LinkedId));

        _sessions.ActiveSessions.Should().ContainSingle(
            "one call is one session across a reconnect; the reload carries the LinkedId that says "
            + $"so. Measured: {Describe()}")
            .Which.SessionId.Should().Be(sessionIdBefore,
                "the surviving call keeps its identity, or every downstream record keyed on it is orphaned");
    }

    public async ValueTask DisposeAsync()
    {
        await _sessions.DisposeAsync();
        await _server.DisposeAsync();
    }

    /// <summary>
    /// Keeps every line the server logs, including the one <c>OnReconnected</c>'s catch-all writes
    /// — without it a failed reload is indistinguishable from a reload that never started.
    /// </summary>
    private sealed class RecordingLogger
    {
        private readonly List<string> _lines = [];

        public ILogger<T> For<T>() => new Sink<T>(this);

        private void Add(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }

        public override string ToString()
        {
            lock (_lines)
            {
                return _lines.Count == 0 ? "  (the server logged nothing)" : "  " + string.Join($"{Environment.NewLine}  ", _lines);
            }
        }

        private sealed class Sink<T>(RecordingLogger owner) : ILogger<T>
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
