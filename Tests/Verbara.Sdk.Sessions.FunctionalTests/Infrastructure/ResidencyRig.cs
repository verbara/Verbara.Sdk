using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

/// <summary>
/// A clock that moves only when the test moves it. It stands in for the manager's clock seam: the
/// release cutoff, and the two instants of a queue visit (the join and app_queue's connect, which
/// stamp <see cref="CallQueuedEvent"/> and <see cref="CallConnectedEvent"/> and fix the
/// queue-wait sample). A session's own timestamps (<see cref="CallSession.CompletedAt"/> included)
/// still come from the wall clock.
///
/// <para>It also reports two things about how it is used. <see cref="TimersCreated"/> counts the
/// timers asked of it, which it never fires: release rides arrivals and endings, so the manager
/// schedules nothing (<c>ADR-0063</c>, D4). And <see cref="HoldNextRead"/> parks the next reader
/// inside its read — the release walk reads the clock under its own lock, so a walk parked there
/// is a walk in progress, and another reader arriving meanwhile is a second walk running at the
/// same time.</para>
/// </summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now = start;
    private ReadHold? _hold;
    private int _timersCreated;

    /// <summary>How many timers have been asked of this clock.</summary>
    public int TimersCreated => Volatile.Read(ref _timersCreated);

    public override DateTimeOffset GetUtcNow()
    {
        Volatile.Read(ref _hold)?.OnRead();

        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }
    }

    /// <summary>
    /// Sets the clock to <paramref name="instant"/>, as a capture replay does from each frame's own
    /// <c>Timestamp</c>. The clock never moves back: an instant earlier than the current one is a replay
    /// out of order, and throws.
    /// </summary>
    public void MoveTo(DateTimeOffset instant)
    {
        lock (_gate)
        {
            if (instant < _now)
                throw new InvalidOperationException($"The clock is at {_now:O} and cannot move back to {instant:O}.");

            _now = instant;
        }
    }

    /// <summary>Parks the next reader of this clock until the returned hold is released.</summary>
    public ReadHold HoldNextRead()
    {
        var hold = new ReadHold();
        Volatile.Write(ref _hold, hold);
        return hold;
    }

    /// <summary>Counts the timer and returns one that never fires.</summary>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Increment(ref _timersCreated);
        return new InertTimer();
    }

    private sealed class InertTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
            // Nothing was scheduled, so there is nothing to cancel.
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// One read of a <see cref="ManualClock"/> held in place: the first reader after
/// <see cref="ManualClock.HoldNextRead"/> signals <see cref="Reached"/> and stays inside its read
/// until <see cref="Release"/>; every other read made while it is parked is counted in
/// <see cref="ReadsWhileHeld"/>. Both waits end on the signal they are for; their bound only turns a
/// test that never sends it into a report instead of a hang.
/// </summary>
internal sealed class ReadHold
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _taken;
    private int _parked;
    private int _readsWhileHeld;

    /// <summary>Reads of the clock made by anyone else while the first reader was parked.</summary>
    public int ReadsWhileHeld => Volatile.Read(ref _readsWhileHeld);

    /// <summary>Waits until the first reader is parked; false if none arrived within the bound.</summary>
    public bool WaitUntilReached() => _reached.Task.Wait(Bound);

    /// <summary>Lets the parked reader finish its read.</summary>
    public void Release() => _released.TrySetResult();

    internal void OnRead()
    {
        if (Interlocked.CompareExchange(ref _taken, 1, 0) == 0)
        {
            Volatile.Write(ref _parked, 1);
            _reached.TrySetResult();
            _released.Task.Wait(Bound);
            Volatile.Write(ref _parked, 0);
            return;
        }

        if (Volatile.Read(ref _parked) == 1)
            Interlocked.Increment(ref _readsWhileHeld);
    }
}

/// <summary>
/// The real Live + Sessions pipeline for the tests of what the SDK holds for a call after it ends:
/// a <see cref="VerbaraServer"/> over a substituted connection (and, through
/// <see cref="AttachServer"/>, more of them), a <see cref="CallSessionManager"/> on the default
/// <see cref="InMemorySessionStore"/>, and a <see cref="ManualClock"/> for the manager's release
/// cutoff. Everything is driven through the channel and bridge managers' own
/// event entry points and the reconnect reload — the paths Asterisk's events take — and read back
/// through the manager's and the store's members, never through private fields.
///
/// <para><b>Time.</b> A test never waits for the retention period to pass: it calls
/// <see cref="MovePastRetention"/>, which moves the cutoff clock past it. Because a session's
/// timestamps are stamped from the wall clock while the test runs, every call the test has ended
/// — and every call it ends afterwards — then lies before the cutoff.</para>
///
/// <para><b>The reload.</b> <c>OnReconnected</c> is <c>async void</c>, so its only completion
/// signals are its last action (<c>Agents</c>) being requested and its failure being logged.
/// <see cref="ReconnectAsync"/> waits for whichever arrives; the bound only turns a reload that
/// never ran into a report instead of a hang.</para>
/// </summary>
internal sealed class ResidencyRig : IAsyncDisposable
{
    public const string ServerId = "residency-srv";

    /// <summary>
    /// How far past the retention period <see cref="MovePastRetention"/> moves the cutoff clock.
    /// Every wall-clock timestamp the test's calls carry is taken during the test's own run, so an
    /// hour puts all of them before the cutoff.
    /// </summary>
    private static readonly TimeSpan PastRetentionMargin = TimeSpan.FromHours(1);

    /// <summary>Bounds the wait on the reload's own completion signal; never reached when it runs.</summary>
    private static readonly TimeSpan ReloadBound = TimeSpan.FromSeconds(10);

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly List<VerbaraServer> _otherServers = [];
    private readonly ReloadSignals _reload = new();
    private readonly List<CallEndedEvent> _endings = [];
    private readonly IDisposable _endingSubscription;
    private IReadOnlyList<StatusEvent> _statusReply = [];

    public ResidencyRig(SessionOptions? options = null)
    {
        Options = options ?? new SessionOptions();
        Clock = new ManualClock(DateTimeOffset.UtcNow);
        Store = new InMemorySessionStore();

        _connection.AsteriskVersion.Returns("21.0.0");
        _connection
            .SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(ci => Reply(ci.ArgAt<ManagerAction>(0)));

        Server = new VerbaraServer(_connection, _reload.For<VerbaraServer>());
        Manager = new CallSessionManager(
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<CallSessionManager>.Instance,
            Store,
            Clock);
        Manager.AttachToServer(Server, ServerId);

        _endingSubscription = Manager.Events.OfType<CallEndedEvent>().Subscribe(Record);
    }

    public VerbaraServer Server { get; }

    public CallSessionManager Manager { get; }

    public SessionOptions Options { get; }

    public ManualClock Clock { get; }

    /// <summary>The store the SDK resolves when a consumer registers none.</summary>
    public InMemorySessionStore Store { get; }

    /// <summary>The instant an ended call must have completed before to be past retention.</summary>
    public DateTimeOffset Cutoff => Clock.GetUtcNow() - Options.CompletedRetention;

    // --- driving calls ------------------------------------------------------------------------

    /// <summary>Starts the server; required before <see cref="ReconnectAsync"/>, which it subscribes.</summary>
    public Task StartAsync() => Server.StartAsync();

    /// <summary>
    /// Attaches one more server to the same manager, over a connection of its own — as the
    /// multi-server registration attaches one manager to every server. Its calls are driven through
    /// its own channel manager: pass it as <c>on</c> to the helpers below.
    /// </summary>
    public VerbaraServer AttachServer(string serverId)
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("21.0.0");

        var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        Manager.AttachToServer(server, serverId);
        _otherServers.Add(server);
        return server;
    }

    /// <summary>
    /// Two legs sharing <c>L-{tag}</c>, dialled and answered, no bridge: one session, state
    /// Connected, on <paramref name="on"/> (the first server by default). Returns that session.
    /// </summary>
    public CallSession OpenAnsweredCall(string tag, VerbaraServer? on = null)
    {
        var channels = (on ?? Server).Channels;
        var linkedId = LinkedIdOf(tag);
        channels.OnNewChannel($"c-{tag}", $"PJSIP/trunk-c-{tag}", ChannelState.Ring,
            callerIdNum: "5551234", context: "from-trunk", linkedId: linkedId);
        channels.OnNewChannel($"a-{tag}", $"PJSIP/100-a-{tag}", ChannelState.Ring,
            linkedId: linkedId);
        channels.OnDialBegin($"c-{tag}", $"a-{tag}", $"PJSIP/100-a-{tag}", null);
        channels.OnNewState($"a-{tag}", ChannelState.Up);

        return Manager.GetByLinkedId(linkedId)
            ?? throw new InvalidOperationException($"no session for '{linkedId}'. Measured: {Describe()}");
    }

    /// <summary>Both legs of <paramref name="tag"/>'s call hang up, with NormalClearing.</summary>
    public void HangUp(string tag, VerbaraServer? on = null)
    {
        var channels = (on ?? Server).Channels;
        channels.OnHangup($"a-{tag}", HangupCause.NormalClearing);
        channels.OnHangup($"c-{tag}", HangupCause.NormalClearing);
    }

    /// <summary>One ordinary answered call, from its first leg to both hangups.</summary>
    public CallSession Call(string tag, VerbaraServer? on = null)
    {
        var session = OpenAnsweredCall(tag, on);
        HangUp(tag, on);
        return session;
    }

    /// <summary><paramref name="count"/> ordinary calls, each ended before the next starts.</summary>
    public IReadOnlyList<CallSession> Calls(string prefix, int count)
    {
        var sessions = new List<CallSession>(count);
        for (var i = 0; i < count; i++)
            sessions.Add(Call($"{prefix}{i}"));
        return sessions;
    }

    /// <summary>
    /// A new leg arrives carrying <c>L-{tag}</c> — the correlation of <paramref name="tag"/>'s call,
    /// whatever state that call is in, or of no call at all — on <paramref name="on"/> (the first
    /// server by default).
    /// </summary>
    public void LegJoins(string uniqueId, string tag, VerbaraServer? on = null) =>
        (on ?? Server).Channels.OnNewChannel(uniqueId, $"PJSIP/300-{uniqueId}", ChannelState.Ring,
            linkedId: LinkedIdOf(tag));

    public void LegLeaves(string uniqueId, VerbaraServer? on = null) =>
        (on ?? Server).Channels.OnHangup(uniqueId, HangupCause.NormalClearing);

    /// <summary>Moves the release cutoff past <see cref="SessionOptions.CompletedRetention"/>.</summary>
    public void MovePastRetention() => Clock.Advance(Options.CompletedRetention + PastRetentionMargin);

    /// <summary>
    /// Moves the release clock forward until <see cref="Cutoff"/> is exactly <paramref name="cutoff"/>:
    /// a call that completed at that instant then ended exactly <see cref="SessionOptions.CompletedRetention"/>
    /// ago. Tick-exact, because the clock moves only by what it is told.
    /// </summary>
    public void PlaceCutoffAt(DateTimeOffset cutoff)
    {
        var by = cutoff - Cutoff;
        if (by < TimeSpan.Zero)
            throw new InvalidOperationException($"the cutoff is already past {cutoff:O}; the release clock only moves forward");

        Clock.Advance(by);
    }

    /// <summary>
    /// Raises <c>Reconnected</c> with <paramref name="channelsAsteriskStillHas"/> as the
    /// <c>Status</c> reply, and returns once the reload has requested its last action.
    /// </summary>
    public async Task ReconnectAsync(params StatusEvent[] channelsAsteriskStillHas)
    {
        _statusReply = channelsAsteriskStillHas;
        _reload.Rearm();
        _connection.Reconnected += Raise.Event<Action>();

        var ended = await Task.WhenAny(_reload.Finished.Task, _reload.Failed.Task).WaitAsync(ReloadBound);
        if (ReferenceEquals(ended, _reload.Failed.Task))
            throw new InvalidOperationException($"the reconnect reload failed. Server log:{Environment.NewLine}{_reload}");
    }

    /// <summary>
    /// A channel as <c>Status</c> reports it: the state arrives as the numeric <c>ChannelState</c>
    /// header in <c>RawFields</c>, never as <c>StatusEvent.State</c>.
    /// </summary>
    public static StatusEvent StatusLeg(string uniqueId, string tag) => new()
    {
        UniqueId = uniqueId,
        Channel = $"PJSIP/200-{uniqueId}",
        LinkedId = LinkedIdOf(tag),
        RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ChannelState"] = "6",
            ["ChannelStateDesc"] = "Up",
        },
    };

    public static string LinkedIdOf(string tag) => $"L-{tag}";

    // --- reading what is held -----------------------------------------------------------------

    /// <summary>How many <see cref="CallEndedEvent"/>s a subscriber has seen for this session.</summary>
    public int EndingsFor(string sessionId)
    {
        lock (_endings)
        {
            return _endings.Count(e => e.SessionId == sessionId);
        }
    }

    /// <summary>How many times this session is queued for release.</summary>
    public int QueueEntriesFor(string sessionId) => Manager.ReleaseQueueEntriesFor(sessionId);

    /// <summary>Ended calls the manager still holds whose completion lies before the cutoff.</summary>
    public int EndedHeldPastRetention()
    {
        var cutoff = Cutoff;
        return Manager.GetRecentCompleted(int.MaxValue).Count(s => s.CompletedAt < cutoff);
    }

    /// <summary>The measurement in one line, so a failure reports numbers rather than a dump.</summary>
    public string Describe()
    {
        int endings;
        lock (_endings)
        {
            endings = _endings.Count;
        }

        var ended = Manager.GetRecentCompleted(int.MaxValue).ToList();
        var cutoff = Cutoff;
        return $"manager holds {ended.Count} ended call(s), {ended.Count(s => s.CompletedAt < cutoff)} of them "
            + $"past retention and {ended.Count(s => s.CompletedAt is null)} with no completion time; "
            + $"{Manager.ActiveSessions.Count()} active; {endings} CallEndedEvent(s) raised in all";
    }

    public async ValueTask DisposeAsync()
    {
        _endingSubscription.Dispose();
        await Manager.DisposeAsync();
        await Server.DisposeAsync();
        foreach (var server in _otherServers)
            await server.DisposeAsync();
    }

    private void Record(CallEndedEvent ending)
    {
        lock (_endings)
        {
            _endings.Add(ending);
        }
    }

    /// <summary>Answers the three actions the load sends; only <c>Status</c> carries channels.</summary>
    private async IAsyncEnumerable<ManagerEvent> Reply(ManagerAction action)
    {
        await Task.Yield();

        if (action is AgentsAction)
        {
            _reload.Finished.TrySetResult();
            yield break;
        }

        if (action is not StatusAction)
            yield break;

        foreach (var status in _statusReply)
            yield return status;
    }

    /// <summary>
    /// The reload's two completion signals, rearmed per reload because <c>StartAsync</c>'s own load
    /// trips the first. Keeps only reload-related log lines, for the failure report.
    /// </summary>
    private sealed class ReloadSignals
    {
        private readonly List<string> _lines = [];

        public TaskCompletionSource Finished { get; private set; } = New();

        public TaskCompletionSource Failed { get; private set; } = New();

        public void Rearm()
        {
            Finished = New();
            Failed = New();
        }

        public ILogger<T> For<T>() => new Sink<T>(this);

        public override string ToString()
        {
            lock (_lines)
            {
                return _lines.Count == 0 ? "  (nothing logged)" : "  " + string.Join($"{Environment.NewLine}  ", _lines);
            }
        }

        private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void Add(string line)
        {
            if (line.Contains("eload", StringComparison.Ordinal) || line.Contains("econnect", StringComparison.Ordinal))
            {
                lock (_lines)
                {
                    _lines.Add(line);
                }
            }

            if (line.Contains("Reconnect reload failed", StringComparison.Ordinal))
                Failed.TrySetResult();
        }

        private sealed class Sink<T>(ReloadSignals owner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner.Add($"[{logLevel}] {formatter(state, exception)}");
        }
    }
}
