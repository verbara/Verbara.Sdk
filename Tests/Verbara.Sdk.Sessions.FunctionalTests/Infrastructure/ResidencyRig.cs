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
/// A clock that moves only when the test moves it. It stands in for the manager's release cutoff
/// and nothing else: a session's own timestamps (<see cref="CallSession.CompletedAt"/> included)
/// still come from the wall clock.
/// </summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
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
}

/// <summary>
/// The real Live + Sessions pipeline for the tests of what the SDK holds for a call after it ends:
/// a <see cref="VerbaraServer"/> over a substituted connection, a <see cref="CallSessionManager"/>
/// on the default <see cref="InMemorySessionStore"/>, and a <see cref="ManualClock"/> for the
/// manager's release cutoff. Everything is driven through the channel and bridge managers' own
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
    /// Two legs sharing <c>L-{tag}</c>, dialled and answered, no bridge: one session, state
    /// Connected. Returns that session.
    /// </summary>
    public CallSession OpenAnsweredCall(string tag)
    {
        var linkedId = LinkedIdOf(tag);
        Server.Channels.OnNewChannel($"c-{tag}", $"PJSIP/trunk-c-{tag}", ChannelState.Ring,
            callerIdNum: "5551234", context: "from-trunk", linkedId: linkedId);
        Server.Channels.OnNewChannel($"a-{tag}", $"PJSIP/100-a-{tag}", ChannelState.Ring,
            linkedId: linkedId);
        Server.Channels.OnDialBegin($"c-{tag}", $"a-{tag}", $"PJSIP/100-a-{tag}", null);
        Server.Channels.OnNewState($"a-{tag}", ChannelState.Up);

        return Manager.GetByLinkedId(linkedId)
            ?? throw new InvalidOperationException($"no session for '{linkedId}'. Measured: {Describe()}");
    }

    /// <summary>Both legs of <paramref name="tag"/>'s call hang up, with NormalClearing.</summary>
    public void HangUp(string tag)
    {
        Server.Channels.OnHangup($"a-{tag}", HangupCause.NormalClearing);
        Server.Channels.OnHangup($"c-{tag}", HangupCause.NormalClearing);
    }

    /// <summary>One ordinary answered call, from its first leg to both hangups.</summary>
    public CallSession Call(string tag)
    {
        var session = OpenAnsweredCall(tag);
        HangUp(tag);
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
    /// whatever state that call is in.
    /// </summary>
    public void LegJoins(string uniqueId, string tag) =>
        Server.Channels.OnNewChannel(uniqueId, $"PJSIP/300-{uniqueId}", ChannelState.Ring,
            linkedId: LinkedIdOf(tag));

    public void LegLeaves(string uniqueId) =>
        Server.Channels.OnHangup(uniqueId, HangupCause.NormalClearing);

    /// <summary>Moves the release cutoff past <see cref="SessionOptions.CompletedRetention"/>.</summary>
    public void MovePastRetention() => Clock.Advance(Options.CompletedRetention + PastRetentionMargin);

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
