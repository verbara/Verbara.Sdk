using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// A multi-server host built through the public registrations — <c>AddVerbaraMultiServer</c> and
/// <c>AddVerbaraSessionsMultiServer</c>, as a consumer writes them — over substituted AMI connections, one per server,
/// each server joined as a cluster joins it (added to the pool, then the session manager attached under the same id).
/// </summary>
/// <remarks>
/// <para><b>Finding the pool sweep.</b> The rig names no sweep type: it looks the pool sweep up among the hosted
/// services the registrations added, by the name of its type. On a build that registers none, <see cref="PoolSweep"/>
/// is <see langword="null"/>, a tick sends nothing and the calls stay as they were — the measurement a test reads.</para>
/// <para><b>Time.</b> The host registers a <see cref="PoolClock"/> as its <see cref="TimeProvider"/>; the pool sweep's
/// timer runs on it, so <see cref="TickAsync"/> moves the clock one interval and waits for the tick's own log line.
/// Nothing waits on elapsed time.</para>
/// <para><b>What is counted.</b> Per server: the AMI actions its substitute received. For the host: every
/// <see cref="CallEndedEvent"/> the manager published and every log entry.</para>
/// </remarks>
internal sealed class PoolSweepRig : IAsyncDisposable
{
    /// <summary>The type name of the pool sweep's hosted service.</summary>
    public const string PoolSweepTypeName = "PoolReconciliationService";

    /// <summary>The interval every rig host runs with: long enough that no wall-clock timer of the host ever fires.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly ServiceProvider _provider;
    private readonly ConcurrentQueue<CallEndedEvent> _endings = new();
    private readonly IDisposable _endingSubscription;
    private readonly List<IHostedService> _started = [];
    private readonly Dictionary<string, RigServer> _servers = new(StringComparer.Ordinal);
    private readonly List<RigServer> _outsidePool = [];

    /// <summary>Builds the host.</summary>
    /// <param name="pool">Whether <c>AddVerbaraMultiServer</c> registers the server pool.</param>
    /// <param name="multiServerRegistrations">How many times <c>AddVerbaraSessionsMultiServer</c> is called.</param>
    /// <param name="singleServer">
    /// Also registers the single-server sessions (<c>AddVerbaraSessions</c>) over a DI <see cref="VerbaraServer"/>,
    /// <see cref="DiServer"/>.
    /// </param>
    /// <param name="configure">Applied to the session options after the rig's own interval.</param>
    public PoolSweepRig(
        bool pool = true,
        int multiServerRegistrations = 1,
        bool singleServer = false,
        Action<SessionOptions>? configure = null)
    {
        Log.Written = Pulse.Fire;
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Log));
        services.AddSingleton<TimeProvider>(Clock);

        void Configure(SessionOptions options)
        {
            options.ReconciliationInterval = Interval;
            configure?.Invoke(options);
        }

        if (singleServer)
        {
            DiServer = new RigServer("default", Pulse);
            services.AddSingleton(DiServer.Server);
            services.AddVerbaraSessions(Configure);
        }

        if (pool)
            services.AddVerbaraMultiServer();

        for (var i = 0; i < multiServerRegistrations; i++)
            services.AddVerbaraSessionsMultiServer(Configure);

        _provider = services.BuildServiceProvider();
        Manager = (CallSessionManager)_provider.GetRequiredService<ICallSessionManager>();
        Pool = _provider.GetService<VerbaraServerPool>();
        _endingSubscription = Manager.Events.Subscribe(new EndingObserver(this));
    }

    /// <summary>The clock the pool sweep's timer and its age reading run on.</summary>
    public PoolClock Clock { get; } = new();

    /// <summary>Every log entry the host wrote.</summary>
    public CapturingLoggerProvider Log { get; } = new();

    /// <summary>Fired on every log entry, every action a substitute receives and every ending.</summary>
    public Pulse Pulse { get; } = new();

    public CallSessionManager Manager { get; }

    /// <summary>The session options the host resolved.</summary>
    public SessionOptions Options => _provider.GetRequiredService<IOptions<SessionOptions>>().Value;

    /// <summary>The pool, when the host registered one.</summary>
    public VerbaraServerPool? Pool { get; }

    /// <summary>The single DI server, when the host registered the single-server sessions.</summary>
    public RigServer? DiServer { get; }

    /// <summary>The hosted services the registrations added, resolved as a host resolves them.</summary>
    public IReadOnlyList<IHostedService> HostedServices => [.. _provider.GetServices<IHostedService>()];

    /// <summary>The pool sweep's hosted service, or <see langword="null"/> when the registrations added none.</summary>
    public IHostedService? PoolSweep => HostedServices.FirstOrDefault(s => s.GetType().Name == PoolSweepTypeName);

    /// <summary>How many pool sweeps the registrations added.</summary>
    public int PoolSweeps => HostedServices.Count(s => s.GetType().Name == PoolSweepTypeName);

    /// <summary>The <see cref="CallEndedEvent"/>s the manager published, in order.</summary>
    public IReadOnlyList<CallEndedEvent> Endings => [.. _endings];

    /// <summary>How many ticks of the pool sweep have completed: one log line each.</summary>
    public int Ticks => Log.Entries.Count(e => e.Category.EndsWith(PoolSweepTypeName, StringComparison.Ordinal)
        && e.Values.ContainsKey("Serverless"));

    /// <summary>The <c>serverless</c> count of the last completed tick, or <see langword="null"/> before the first.</summary>
    public int? LastServerless => Log.Entries
        .LastOrDefault(e => e.Category.EndsWith(PoolSweepTypeName, StringComparison.Ordinal)
            && e.Values.ContainsKey("Serverless"))
        ?.Values["Serverless"] as int?;

    /// <summary>The servers' ids in the order the pool enumerates them.</summary>
    public IReadOnlyList<string> PoolOrder => [.. (Pool?.Servers ?? []).Select(entry => entry.Key)];

    public RigServer this[string id] => _servers[id];

    /// <summary>
    /// A server joined as a cluster joins it: added to the pool when <paramref name="inPool"/>, then the session
    /// manager attached to it under <paramref name="id"/>.
    /// </summary>
    public RigServer AddServer(string id, bool inPool = true, bool attach = true)
    {
        var server = new RigServer(id, Pulse);
        _servers[id] = server;
        if (!inPool)
            _outsidePool.Add(server);
        if (inPool)
            (Pool ?? throw new InvalidOperationException("The rig was built without a pool.")).AddExistingServer(id, server.Server);
        if (attach)
            Manager.AttachToServer(server.Server, id);
        return server;
    }

    /// <summary>Starts every hosted service the registrations added, as a host starts them, with <paramref name="token"/>.</summary>
    public async Task StartAsync(CancellationToken token = default)
    {
        foreach (var service in HostedServices.Where(s => s.GetType().Name != "HealthCheckPublisherHostedService"))
        {
            if (service is IHostedLifecycleService lifecycle)
                await lifecycle.StartingAsync(token);
            await service.StartAsync(token);
            _started.Add(service);
        }
    }

    /// <summary>
    /// Moves the clock one interval and waits for the pool sweep's tick to complete. With no pool sweep registered,
    /// it moves the clock and returns: nothing would tick.
    /// </summary>
    /// <returns>Whether a tick completed.</returns>
    public async Task<bool> TickAsync()
    {
        var before = Ticks;
        Clock.Advance(Interval);
        if (PoolSweep is null)
            return false;

        return await Pulse.WaitUntilAsync(() => Ticks > before, SweepRig.Bound);
    }

    /// <summary>Waits until <paramref name="condition"/> holds, re-checked at each pulse; false at the bound.</summary>
    public Task<bool> WaitUntilAsync(Func<bool> condition) => Pulse.WaitUntilAsync(condition, SweepRig.Bound);

    public int EndingsOf(CallSession session) => _endings.Count(e => e.SessionId == session.SessionId);

    /// <summary>A short account of what the rig measured, for failure messages.</summary>
    public string Describe() =>
        $"pool sweep registered: {PoolSweep is not null}; ticks: {Ticks}; Status per server: [" +
        string.Join(", ", _servers.Values.Append(DiServer).OfType<RigServer>().Select(s => $"{s.Id}={s.StatusRequests}")) +
        $"]; CallEndedEvents: {_endings.Count}; warnings and errors: [" +
        string.Join(" | ", Log.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => $"{e.Level} {e.Message}")) + "]";

    public async ValueTask DisposeAsync()
    {
        for (var i = _started.Count - 1; i >= 0; i--)
            await _started[i].StopAsync(CancellationToken.None).WaitAsync(SweepRig.Bound);

        _endingSubscription.Dispose();
        // The pool disposes the servers it still holds with the provider; the rig disposes the ones it never added and
        // the DI server it registered as an instance.
        await _provider.DisposeAsync();
        foreach (var server in _outsidePool)
            await server.Server.DisposeAsync();
        if (DiServer is not null)
            await DiServer.Server.DisposeAsync();
    }

    private sealed class EndingObserver(PoolSweepRig rig) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value)
        {
            if (value is CallEndedEvent ended)
            {
                rig._endings.Enqueue(ended);
                rig.Pulse.Fire();
            }
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
}

/// <summary>
/// One server of a <see cref="PoolSweepRig"/>: a <see cref="VerbaraServer"/> over a substituted
/// <see cref="IAmiConnection"/>, whose answer to <c>Status</c> the test sets, with its own call builders.
/// </summary>
internal sealed class RigServer
{
    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly Dictionary<string, List<Leg>> _legsByCall = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _connectionDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Pulse _pulse;
    private int _statusRequests;
    private IReadOnlyList<string> _listedCalls = [];

    public RigServer(string id, Pulse pulse)
    {
        Id = id;
        _pulse = pulse;
        _connection.AsteriskVersion.Returns("22.9.0");
        _connection.State.Returns(_ => ConnectionState);
        _connection.ReportsEventActionOutcome.Returns(true);
        _connection
            .SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(ci => Reply(ci.ArgAt<ManagerAction>(0), ci.ArgAt<CancellationToken>(1)));
        _connection
            .SendEventGeneratingActionAsync(
                Arg.Any<ManagerAction>(), Arg.Any<EventActionOutcome?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Reply(ci.ArgAt<ManagerAction>(0), ci.ArgAt<CancellationToken>(2)));
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        _connection.DisposeAsync().Returns(_ =>
        {
            _connectionDisposed.TrySetResult();
            return ValueTask.CompletedTask;
        });
#pragma warning restore CA2012
        Server = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
    }

    public string Id { get; }

    public VerbaraServer Server { get; }

    /// <summary>What the substituted connection reports as its state. Connected unless a test says otherwise.</summary>
    public AmiConnectionState ConnectionState { get; set; } = AmiConnectionState.Connected;

    /// <summary>How the substitute answers each <c>Status</c>; <c>null</c> lists the calls <see cref="AsteriskLists"/> named.</summary>
    public Func<CancellationToken, IAsyncEnumerable<ManagerEvent>>? StatusReply { get; set; }

    /// <summary>How many <c>Status</c> requests this server's substitute received.</summary>
    public int StatusRequests => Volatile.Read(ref _statusRequests);

    /// <summary>Whether <paramref name="pool"/> holds this server now.</summary>
    public bool InPool(VerbaraServerPool? pool) => pool?.GetServer(Id) is { } held && ReferenceEquals(held, Server);

    /// <summary>Asterisk's answer to every later <c>Status</c>: the legs of the calls named, and no other channel.</summary>
    public void AsteriskLists(params string[] calls) => _listedCalls = calls;

    /// <summary>
    /// A two-leg call, dialed, answered and bridged on this server, whose hangup the host will never receive: its
    /// session is aged past every timeout. <paramref name="manager"/> must be attached to this server.
    /// </summary>
    public CallSession LostCall(CallSessionManager manager, string id)
    {
        var (a, b) = TwoLegs(id);
        Server.Channels.OnDialBegin(a.UniqueId, b.UniqueId, b.Channel, null);
        Server.Channels.OnNewState(b.UniqueId, ChannelState.Up);
        Server.Bridges.OnBridgeCreated($"{id}-bridge", "mixing", "simple_bridge", null, null);
        Server.Bridges.OnChannelEntered($"{id}-bridge", a.UniqueId);
        Server.Bridges.OnChannelEntered($"{id}-bridge", b.UniqueId);
        var session = manager.GetByLinkedId(LinkedIdOf(id))
            ?? throw new InvalidOperationException($"No session holds the call {id} on server {Id}.");
        if (session.State != CallSessionState.Connected)
            throw new InvalidOperationException($"Premise: the call {id} is {session.State}, not Connected.");
        Age(session);
        return session;
    }

    /// <summary>A channel this server's table holds, announced before any manager was attached: it has no session.</summary>
    public void ChannelWithoutSession(string uniqueId) =>
        Server.Channels.OnNewChannel(uniqueId, $"PJSIP/300-{uniqueId}", ChannelState.Up, linkedId: $"{uniqueId}-linked");

    /// <summary>The unique ids of the call <paramref name="id"/>'s legs.</summary>
    public IReadOnlyList<string> UniqueIdsOf(string id) => [.. _legsByCall[id].Select(leg => leg.UniqueId)];

    public static string LinkedIdOf(string id) => $"{id}-linked";

    /// <summary>A <c>Status</c> answer that never completes: it ends when its token is cancelled or the connection is disposed.</summary>
    public async IAsyncEnumerable<ManagerEvent> Hang([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken));
        var finished = await Task.WhenAny(cancelled.Task, _connectionDisposed.Task);
        if (finished == _connectionDisposed.Task)
            throw new ObjectDisposedException(nameof(IAmiConnection), "The AMI connection was disposed under the Status.");

        await cancelled.Task;
        yield break;
    }

    /// <summary>A <c>Status</c> answer that throws <paramref name="error"/>.</summary>
    public static async IAsyncEnumerable<ManagerEvent> Throw(Exception error)
    {
        await Task.Yield();
        throw error;
#pragma warning disable CS0162 // An iterator needs a yield to be one.
        yield break;
#pragma warning restore CS0162
    }

    private static void Age(CallSession session)
    {
        var then = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
        if (session.DialingAt.HasValue)
            session.DialingAt = then;
        if (session.RingingAt.HasValue)
            session.RingingAt = then;
        SweepRig.CreatedAtField.SetValue(session, then);
    }

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

    private IAsyncEnumerable<ManagerEvent> Reply(ManagerAction action, CancellationToken cancellationToken)
    {
        if (action is not StatusAction)
        {
            _pulse.Fire();
            return Nothing();
        }

        Interlocked.Increment(ref _statusRequests);
        _pulse.Fire();
        return StatusReply?.Invoke(cancellationToken) ?? Listed(_listedCalls);
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
}

/// <summary>
/// A <see cref="TimeProvider"/> whose clock moves only on <see cref="Advance"/> and whose timers — one-shot and periodic,
/// as <see cref="PeriodicTimer"/> creates them — fire only when it does.
/// </summary>
internal sealed class PoolClock : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ClockTimer> _timers = [];
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private int _created;

    /// <summary>How many timers were created on this clock.</summary>
    public int TimersCreated => Volatile.Read(ref _created);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        Interlocked.Increment(ref _created);
        return timer;
    }

    /// <summary>Moves the clock and fires, on the calling thread, every timer that came due, once each.</summary>
    public void Advance(TimeSpan by)
    {
        List<ClockTimer> due;
        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(t => t.DueAt is { } at && at <= _now)];
            foreach (var timer in due)
                timer.DueAt = timer.Period > TimeSpan.Zero && timer.Period != Timeout.InfiniteTimeSpan ? _now + timer.Period : null;
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private void Remove(ClockTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ClockTimer(PoolClock owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Period = period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>One log entry as the host wrote it: its category, level, rendered message, structured values and exception.</summary>
internal sealed record LogEntry(
    string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values, Exception? Exception);

/// <summary>Keeps every log entry of every level, with its structured values.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    /// <summary>Fired after each entry; set by the rig.</summary>
    public Action? Written { get; set; }

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
        // Nothing to release.
    }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            owner._entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), values, exception));
            owner.Written?.Invoke();
        }
    }
}
