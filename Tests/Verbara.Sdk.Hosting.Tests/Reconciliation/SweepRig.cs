using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// The collection every class that reads a <c>Verbara.Sdk.Sessions</c> counter delta or counts reconciliation
/// spans runs in. Apply with <c>[Collection(SweepCounterGroup.Name)]</c>.
/// </summary>
/// <remarks>
/// The session counters are process-wide statics with no tags: every <see cref="CallSessionManager"/> in this
/// assembly adds to the same instruments, and a listener sees what a class running beside it adds. So the
/// collection is declared with <c>DisableParallelization</c>: xunit runs it only after every parallel collection
/// has finished, and its classes one at a time, so the tests can assert exact counts.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SweepCounterGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "session-reconciliation-sweep";
}

/// <summary>
/// The real Live + Sessions pipeline the reconciliation sweep runs over: a <see cref="VerbaraServer"/> over a
/// substituted <see cref="IAmiConnection"/>, a <see cref="CallSessionManager"/> attached to it, and the sweep.
/// Calls are driven through the channel, bridge and queue managers' own event entry points, the paths
/// Asterisk's events take; Asterisk's answer to <c>Status</c> is what the test says it is.
/// </summary>
/// <remarks>
/// <para><b>The one sweep helper.</b> Every test builds the sweep with <see cref="BuildSweep"/> and runs one
/// sweep with <see cref="SweepOnceAsync"/>, or runs the sweep's own loop with <see cref="StartLoopAsync"/>.
/// These three members are the only place a test touches the sweep's construction or its entry point, so a
/// change to either edits them and nothing in the tests' text.</para>
/// <para><b>Aging.</b> <see cref="Age"/> puts a session past every timeout on both axes the sweep may read:
/// the public <see cref="CallSession.DialingAt"/> / <see cref="CallSession.RingingAt"/> setters, and
/// <see cref="CallSession.CreatedAt"/> through reflection on its backing field (it is init-only).</para>
/// <para><b>What is counted.</b> AMI actions sent, by type, from the calls the substitute received; each
/// <see cref="CallEndedEvent"/> the manager published; each save the store was handed, as the session read at
/// that moment; release-queue entries through the manager's own test reader; counter deltas through
/// <see cref="SessionCounters"/>; and completed sweeps through their <c>session reconciliation</c> span.</para>
/// </remarks>
internal sealed class SweepRig : IAsyncDisposable
{
    public const string ServerId = "sweep-srv";

    /// <summary>How far past every timeout <see cref="Age"/> puts a session.</summary>
    private static readonly TimeSpan AgeMargin = TimeSpan.FromHours(1);

    /// <summary>Bounds every wait on a signal; never reached when the signal comes.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly ConcurrentQueue<CallEndedEvent> _endings = new();
    private readonly IDisposable _endingSubscription;
    private readonly Dictionary<string, List<Leg>> _legsByCall = new(StringComparer.Ordinal);
    private readonly Pulse _pulse = new();
    private readonly List<IAsyncDisposable> _owned = [];
    private int _statusRequests;
    private IReadOnlyList<string> _listedCalls = [];

    /// <summary>Builds the pipeline over a substituted connection that is connected and lists no channel.</summary>
    /// <param name="options">The session options; the loop's interval is set to 5 ms on them.</param>
    /// <param name="defaultStore">Whether the manager saves to the default in-memory store instead of a recording one.</param>
    /// <param name="reportsOutcome">
    /// Whether the substituted connection reports how an action ended, as an <c>AmiConnection</c> does. When
    /// <see langword="false"/> it is left as a substitute answers unconfigured: it reports nothing, and only the
    /// two-argument send is answered.
    /// </param>
    public SweepRig(SessionOptions? options = null, bool defaultStore = false, bool reportsOutcome = true)
    {
        Options = options ?? new SessionOptions();

        // A loop the tests run spins through its ticks at this interval; a tick only matters through what it
        // sends and the span it closes, both of which are counted, never timed.
        Options.ReconciliationInterval = TimeSpan.FromMilliseconds(5);

        Clock = new ManualClock(DateTimeOffset.UtcNow);
        RecordingStore = defaultStore ? null : new RecordingStore();
        DefaultStore = defaultStore ? new InMemorySessionStore() : null;

        _connection.AsteriskVersion.Returns("22.9.0");
        _connection.State.Returns(_ => ConnectionState);
        _connection
            .SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(ci => Reply(ci.ArgAt<ManagerAction>(0), ci.ArgAt<CancellationToken>(1)));
        if (reportsOutcome)
        {
            // Reports how an action ended, so the sweep verifies over it; the outcome itself is never set (its setters
            // are internal to the SDK), which a reader takes as an answer that completed.
            _connection.ReportsEventActionOutcome.Returns(true);
            _connection
                .SendEventGeneratingActionAsync(
                    Arg.Any<ManagerAction>(), Arg.Any<EventActionOutcome?>(), Arg.Any<CancellationToken>())
                .Returns(ci => Reply(ci.ArgAt<ManagerAction>(0), ci.ArgAt<CancellationToken>(2)));
        }

        Server = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
        Manager = new CallSessionManager(
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<CallSessionManager>.Instance,
            (SessionStoreBase?)RecordingStore ?? DefaultStore!,
            Clock);
        Manager.AttachToServer(Server, ServerId);
        _endingSubscription = Manager.Events.Subscribe(new EndingObserver(this));
    }

    public SessionOptions Options { get; }

    public VerbaraServer Server { get; }

    public CallSessionManager Manager { get; }

    /// <summary>
    /// The manager's clock (its release cutoff) and the time the sweep measures a call's age at. A session's own
    /// timestamps come from the wall clock, so a call this rig opens is younger than this clock's time until aged.
    /// </summary>
    public ManualClock Clock { get; }

    /// <summary>The store the manager saves to, unless the rig was built on the default store.</summary>
    public RecordingStore? RecordingStore { get; }

    /// <summary>The default in-memory store, when the rig was built on it.</summary>
    public InMemorySessionStore? DefaultStore { get; }

    /// <summary>What the substituted connection reports as its state. Connected unless a test says otherwise.</summary>
    public AmiConnectionState ConnectionState { get; set; } = AmiConnectionState.Connected;

    /// <summary>
    /// How the substitute answers each <c>Status</c>, given its 1-based number and the token it was sent with.
    /// <c>null</c> answers with the legs of the calls <see cref="AsteriskLists"/> named last.
    /// </summary>
    public Func<int, CancellationToken, IAsyncEnumerable<ManagerEvent>>? StatusReply { get; set; }

    /// <summary>The <see cref="CallEndedEvent"/>s the manager published, in order.</summary>
    public IReadOnlyList<CallEndedEvent> Endings => [.. _endings];

    /// <summary>How many <c>Status</c> requests the substitute received.</summary>
    public int StatusRequests => Volatile.Read(ref _statusRequests);

    /// <summary>Fired on every action the substitute receives and every completed sweep; what bounded waits wait on.</summary>
    public Pulse Pulse => _pulse;

    // --- Calls -------------------------------------------------------------------------------------------

    /// <summary>A two-leg call whose dial began and nothing answered: state <see cref="CallSessionState.Dialing"/>.</summary>
    public CallSession DialingCall(string id)
    {
        var (a, b) = TwoLegs(id);
        Server.Channels.OnDialBegin(a.UniqueId, b.UniqueId, b.Channel, null);
        return Session(id, CallSessionState.Dialing);
    }

    /// <summary>A dialed call whose callee rings: state <see cref="CallSessionState.Ringing"/>.</summary>
    public CallSession RingingCall(string id)
    {
        var (a, b) = TwoLegs(id);
        Server.Channels.OnDialBegin(a.UniqueId, b.UniqueId, b.Channel, null);
        Server.Channels.OnNewState(b.UniqueId, ChannelState.Ringing);
        return Session(id, CallSessionState.Ringing);
    }

    /// <summary>One inbound leg nothing answered: state <see cref="CallSessionState.Created"/>.</summary>
    public CallSession CreatedCall(string id)
    {
        OneLeg(id);
        return Session(id, CallSessionState.Created);
    }

    /// <summary>
    /// One inbound leg the dialplan answered with no dial or queue reaching it (an IVR): it stays
    /// <see cref="CallSessionState.Created"/>, with its answer recorded.
    /// </summary>
    public CallSession AnsweredByDialplanCall(string id)
    {
        var a = OneLeg(id);
        Server.Channels.OnNewState(a.UniqueId, ChannelState.Up);
        return Session(id, CallSessionState.Created);
    }

    /// <summary>A two-leg call, dialed, answered and bridged: state <see cref="CallSessionState.Connected"/>.</summary>
    public CallSession ConnectedCall(string id)
    {
        var (a, b) = TwoLegs(id);
        Server.Channels.OnDialBegin(a.UniqueId, b.UniqueId, b.Channel, null);
        Server.Channels.OnNewState(b.UniqueId, ChannelState.Up);
        Server.Bridges.OnBridgeCreated($"{id}-bridge", "mixing", "simple_bridge", null, null);
        Server.Bridges.OnChannelEntered($"{id}-bridge", a.UniqueId);
        Server.Bridges.OnChannelEntered($"{id}-bridge", b.UniqueId);
        return Session(id, CallSessionState.Connected);
    }

    /// <summary>One inbound leg that joined <paramref name="queue"/>: state <see cref="CallSessionState.Queued"/>.</summary>
    public CallSession QueuedCall(string id, string queue)
    {
        var a = OneLeg(id);
        Server.Queues.OnCallerJoined(queue, a.Channel, "5551234", 1);
        return Session(id, CallSessionState.Queued);
    }

    /// <summary>
    /// A session registered as reconstructed with no participant, so the channel table holds none of it:
    /// state <see cref="CallSessionState.Created"/>.
    /// </summary>
    public CallSession ZeroChannelSession(string id)
    {
        var session = new CallSession($"{id}-session", LinkedIdOf(id), ServerId, CallDirection.Inbound);
        if (!Manager.RegisterReconstructedSession(session))
            throw new InvalidOperationException($"The manager refused the reconstructed session {id}.");

        _legsByCall[id] = [];
        return session;
    }

    /// <summary>
    /// The connection announces that it reconnected, as it does once a new AMI session is logged in: the server
    /// reloads its state from Asterisk.
    /// </summary>
    public void Reconnect() => _connection.Reconnected += Raise.Event<Action>();

    /// <summary>Every leg of the call <paramref name="id"/> hangs up, with <paramref name="cause"/>.</summary>
    public void HangUp(string id, HangupCause cause)
    {
        foreach (var leg in _legsByCall[id])
            Server.Channels.OnHangup(leg.UniqueId, cause);
    }

    public static string LinkedIdOf(string id) => $"{id}-linked";

    /// <summary>
    /// Puts <paramref name="session"/> past every timeout the sweep may read: <c>DialingAt</c> and
    /// <c>RingingAt</c> when set, and <c>CreatedAt</c>.
    /// </summary>
    public void Age(CallSession session)
    {
        var age = (Options.DialingTimeout > Options.RingingTimeout ? Options.DialingTimeout : Options.RingingTimeout)
            + AgeMargin;
        var then = DateTimeOffset.UtcNow - age;

        if (session.DialingAt.HasValue)
            session.DialingAt = then;
        if (session.RingingAt.HasValue)
            session.RingingAt = then;

        CreatedAtField.SetValue(session, then);
    }

    /// <summary>The backing field of the init-only <see cref="CallSession.CreatedAt"/>.</summary>
    internal static readonly FieldInfo CreatedAtField =
        typeof(CallSession).GetField("<CreatedAt>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("CallSession.CreatedAt has no auto-property backing field.");

    /// <summary>
    /// Asterisk's answer to every later <c>Status</c>: the legs of the calls named, and no other channel.
    /// </summary>
    public void AsteriskLists(params string[] calls) => _listedCalls = calls;

    // --- The sweep -----------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the sweep over this rig's manager and server, measuring a call's age at <see cref="Clock"/>'s time.
    /// Disposed with the rig.
    /// </summary>
    public SessionReconciliationService BuildSweep()
    {
        var sweep = new SessionReconciliationService(
            Manager,
            Server,
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<SessionReconciliationService>.Instance,
            Clock);
        _owned.Add(new DisposeSweep(sweep));
        return sweep;
    }

    /// <summary>Runs one sweep of <paramref name="sweep"/> to its end.</summary>
    public static Task SweepOnceAsync(SessionReconciliationService sweep) => sweep.SweepAsync(CancellationToken.None);

    /// <summary>
    /// Starts the sweep's own loop, as the host does, and returns a handle that counts the sweeps it completes
    /// and stops it. Disposed (stopped) with the rig if the test does not stop it.
    /// </summary>
    public async Task<SweepLoop> StartLoopAsync(SessionReconciliationService sweep)
    {
        var loop = new SweepLoop(sweep, _pulse);
        _owned.Add(loop);
        await loop.StartAsync();
        return loop;
    }

    // --- Measurement ---------------------------------------------------------------------------------------

    /// <summary>The AMI actions the substitute received, by action type name.</summary>
    public IReadOnlyDictionary<string, int> ActionsSent() =>
        _connection.ReceivedCalls()
            .SelectMany(call => call.GetArguments().OfType<ManagerAction>())
            .GroupBy(action => action.GetType().Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    /// <summary>How many actions of type <typeparamref name="T"/> the substitute received.</summary>
    public int Sent<T>() where T : ManagerAction =>
        ActionsSent().GetValueOrDefault(typeof(T).Name);

    /// <summary>The call's session as a test reads it before and after a sweep.</summary>
    public static SessionLook Look(CallSession session) => new(
        session.State,
        session.Metadata.GetValueOrDefault("cause"),
        session.HangupCause,
        session.Events.Count);

    public int EndingsOf(CallSession session) => _endings.Count(e => e.SessionId == session.SessionId);

    /// <summary>The unique ids of the call <paramref name="id"/>'s legs.</summary>
    public IReadOnlyList<string> UniqueIdsOf(string id) => [.. _legsByCall[id].Select(leg => leg.UniqueId)];

    /// <summary>Waits until <paramref name="condition"/> holds, re-checked at each pulse; false at the bound.</summary>
    public Task<bool> WaitUntilAsync(Func<bool> condition) => _pulse.WaitUntilAsync(condition, Bound);

    /// <summary>A short account of what the rig measured, for failure messages.</summary>
    public string Describe() =>
        $"actions sent: [{string.Join(", ", ActionsSent().Select(kv => $"{kv.Key}={kv.Value}"))}], " +
        $"CallEndedEvents: {_endings.Count}";

    // --- Plumbing ------------------------------------------------------------------------------------------

    private sealed record Leg(string UniqueId, string Channel, string LinkedId);

    private (Leg A, Leg B) TwoLegs(string id)
    {
        var a = new Leg($"{id}-a", $"PJSIP/100-{id}-a", LinkedIdOf(id));
        var b = new Leg($"{id}-b", $"PJSIP/200-{id}-b", LinkedIdOf(id));
        Server.Channels.OnNewChannel(a.UniqueId, a.Channel, ChannelState.Ring,
            callerIdNum: "100", context: "from-internal", linkedId: a.LinkedId);
        Server.Channels.OnNewChannel(b.UniqueId, b.Channel, ChannelState.Down, linkedId: b.LinkedId);
        _legsByCall[id] = [a, b];
        return (a, b);
    }

    private Leg OneLeg(string id)
    {
        var a = new Leg($"{id}-a", $"PJSIP/trunk-{id}-a", LinkedIdOf(id));
        Server.Channels.OnNewChannel(a.UniqueId, a.Channel, ChannelState.Ring,
            callerIdNum: "5551234", context: "from-trunk", linkedId: a.LinkedId);
        _legsByCall[id] = [a];
        return a;
    }

    private CallSession Session(string id, CallSessionState expected)
    {
        var session = Manager.GetByLinkedId(LinkedIdOf(id))
            ?? throw new InvalidOperationException($"No session holds the call {id}.");
        if (session.State != expected)
            throw new InvalidOperationException($"Premise: the call {id} is {session.State}, not {expected}.");

        return session;
    }

    private IAsyncEnumerable<ManagerEvent> Reply(ManagerAction action, CancellationToken cancellationToken)
    {
        if (action is not StatusAction)
        {
            _pulse.Fire();
            return Nothing();
        }

        var number = Interlocked.Increment(ref _statusRequests);
        _pulse.Fire();
        return StatusReply?.Invoke(number, cancellationToken) ?? Listed(_listedCalls);
    }

    private static async IAsyncEnumerable<ManagerEvent> Nothing()
    {
        await Task.Yield();
        yield break;
    }

    private async IAsyncEnumerable<ManagerEvent> Listed(IReadOnlyList<string> calls)
    {
        await Task.Yield();
        foreach (var call in calls)
            foreach (var leg in _legsByCall[call])
                yield return Status(leg);
    }

    /// <summary>The legs of the calls named, as Asterisk's answer to <c>Status</c>.</summary>
    public async IAsyncEnumerable<ManagerEvent> ListedNow(
        IReadOnlyList<string> calls, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        foreach (var call in calls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var leg in _legsByCall[call])
                yield return Status(leg);
        }
    }

    /// <summary>
    /// One channel of a <c>Status</c> answer, shaped as Asterisk sends it: the numeric <c>ChannelState</c> in
    /// <c>RawFields</c> (6, up), and the linked id the leg carries.
    /// </summary>
    private static StatusEvent Status(Leg leg) => new()
    {
        EventType = "Status",
        UniqueId = leg.UniqueId,
        Channel = leg.Channel,
        LinkedId = leg.LinkedId,
        RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Channel"] = leg.Channel,
            ["Uniqueid"] = leg.UniqueId,
            ["Linkedid"] = leg.LinkedId,
            ["ChannelState"] = "6",
            ["ChannelStateDesc"] = "Up",
        },
    };

    public async ValueTask DisposeAsync()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
            await _owned[i].DisposeAsync();

        _endingSubscription.Dispose();
        await Manager.DisposeAsync();
        await Server.DisposeAsync();
    }

    private sealed class EndingObserver(SweepRig rig) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value)
        {
            if (value is CallEndedEvent ended)
                rig._endings.Enqueue(ended);
        }

        public void OnError(Exception error)
        {
            // The manager's subject never faults; nothing to record.
        }

        public void OnCompleted()
        {
            // Completed when the manager is disposed; nothing to record.
        }
    }

    private sealed class DisposeSweep(SessionReconciliationService sweep) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            sweep.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A session as a test compares it before and after a sweep.</summary>
internal sealed record SessionLook(CallSessionState State, string? Cause, HangupCause? HangupCause, int AuditEntries);

/// <summary>
/// The sweep's own loop, started as the host starts it, with every sweep it completes counted through its
/// <c>session reconciliation</c> span.
/// </summary>
/// <remarks>
/// The loop runs on the execution context <see cref="StartAsync"/> was called on, so a parent activity current
/// there is the parent of every sweep span the loop opens: the count is of this loop's sweeps, whatever else
/// opens a reconciliation span in the process. Each start opens a parent of its own, so a loop stopped and
/// started again keeps counting.
/// </remarks>
internal sealed class SweepLoop : IAsyncDisposable
{
    private const string SessionsSource = "Verbara.Sdk.Sessions";
    private const string SweepSpan = "session reconciliation";

    private readonly SessionReconciliationService _sweep;
    private readonly Pulse _pulse;
    private readonly ActivityListener _listener;
    private readonly ConcurrentDictionary<ActivityTraceId, Activity> _parents = new();
    private int _sweeps;
    private int _running;

    public SweepLoop(SessionReconciliationService sweep, Pulse pulse)
    {
        _sweep = sweep;
        _pulse = pulse;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SessionsSource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnStopped,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>How many sweeps this loop has completed.</summary>
    public int Sweeps => Volatile.Read(ref _sweeps);

    /// <summary>Starts the loop as the host does.</summary>
    public async Task StartAsync()
    {
        var previous = Activity.Current;
        var parent = new Activity("sweep-loop-under-test");
        parent.SetIdFormat(ActivityIdFormat.W3C);
        parent.Start();
        _parents[parent.TraceId] = parent;
        try
        {
            await _sweep.StartAsync(CancellationToken.None);
            Volatile.Write(ref _running, 1);
        }
        finally
        {
            parent.Stop();
            Activity.Current = previous;
        }
    }

    /// <summary>Stops the loop as the host does; returns once the sweep's stop has returned.</summary>
    public async Task StopAsync()
    {
        Volatile.Write(ref _running, 0);
        await _sweep.StopAsync(CancellationToken.None).WaitAsync(SweepRig.Bound);
    }

    private void OnStopped(Activity activity)
    {
        if (activity.OperationName != SweepSpan || !_parents.ContainsKey(activity.TraceId))
            return;

        Interlocked.Increment(ref _sweeps);
        _pulse.Fire();
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _running) == 1)
            await StopAsync();

        _listener.Dispose();
        foreach (var parent in _parents.Values)
            parent.Dispose();
    }
}

/// <summary>
/// A signal that fires many times: a waiter re-checks its condition at each firing. The bound only turns a
/// signal that never comes into a <see langword="false"/> instead of a hang.
/// </summary>
internal sealed class Pulse
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Fire()
    {
        TaskCompletionSource fired;
        lock (_gate)
        {
            fired = _next;
            _next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        fired.TrySetResult();
    }

    public async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan bound)
    {
        using var hang = new CancellationTokenSource(bound);
        while (true)
        {
            Task next;
            lock (_gate)
            {
                next = _next.Task;
            }

            if (condition())
                return true;

            try
            {
                await next.WaitAsync(hang.Token);
            }
            catch (OperationCanceledException) when (hang.IsCancellationRequested)
            {
                return condition();
            }
        }
    }
}

/// <summary>
/// Sums what the session engine adds to <c>sessions.completed</c>, <c>sessions.failed</c>,
/// <c>sessions.timed_out</c> and <c>sessions.orphaned</c> from the moment it is created until it is disposed,
/// as an exporter subscribed to the instruments would receive it. Matched by meter and instrument name, the
/// contract an exporter subscribes by.
/// </summary>
internal sealed class SessionCounters : IDisposable
{
    public const string MeterName = "Verbara.Sdk.Sessions";

    private static readonly string[] Instruments =
        ["sessions.completed", "sessions.failed", "sessions.timed_out", "sessions.orphaned"];

    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, long> _sums = new(StringComparer.Ordinal);

    public SessionCounters()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == MeterName && Instruments.Contains(instrument.Name))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>(
            (instrument, value, _, _) => _sums.AddOrUpdate(instrument.Name, value, (_, sum) => sum + value));
        _listener.Start();
    }

    /// <summary>What the four counters have recorded since this reader started.</summary>
    public CounterDeltas Deltas => new(
        _sums.GetValueOrDefault("sessions.completed"),
        _sums.GetValueOrDefault("sessions.failed"),
        _sums.GetValueOrDefault("sessions.timed_out"),
        _sums.GetValueOrDefault("sessions.orphaned"));

    public void Dispose() => _listener.Dispose();
}

/// <summary>The four session-ending counters' deltas.</summary>
internal sealed record CounterDeltas(long Completed, long Failed, long TimedOut, long Orphaned)
{
    public static readonly CounterDeltas None = new(0, 0, 0, 0);

    /// <summary>What was counted since <paramref name="earlier"/> was read.</summary>
    public CounterDeltas Since(CounterDeltas earlier) => new(
        Completed - earlier.Completed, Failed - earlier.Failed, TimedOut - earlier.TimedOut, Orphaned - earlier.Orphaned);
}

/// <summary>
/// A store that keeps nothing and records every save as the session read at that moment. The manager hands a
/// save to the store on the thread that changed the session and this store completes it at once, so a save is
/// recorded before the call that caused it returns.
/// </summary>
internal sealed class RecordingStore : SessionStoreBase
{
    private readonly ConcurrentQueue<(string SessionId, CallSessionState State)> _saves = new();

    /// <summary>The states the session <paramref name="sessionId"/> was saved in, in order.</summary>
    public IReadOnlyList<CallSessionState> SavesOf(string sessionId) =>
        [.. _saves.Where(s => s.SessionId == sessionId).Select(s => s.State)];

    public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
    {
        _saves.Enqueue((session.SessionId, session.State));
        return ValueTask.CompletedTask;
    }

    public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
        ValueTask.FromResult<CallSession?>(null);
}

/// <summary>A clock that moves only when the test moves it; the manager's release cutoff.</summary>
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
