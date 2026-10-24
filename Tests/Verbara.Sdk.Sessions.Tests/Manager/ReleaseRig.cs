using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Diagnostics;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// The real <see cref="CallSessionManager"/> attached to one or more <see cref="VerbaraServer"/>s under server ids the
/// test names, for the tests of a detach that releases a server's calls. Each server runs over a substitute
/// <see cref="IAmiConnection"/>; the test drives a server through its live tables (<see cref="RigServer.Server"/>) or
/// writes AMI frames to the observer the server subscribed (<see cref="RigServer.Deliver"/>). A server id can be
/// attached again with a new instance (<see cref="AttachAsync"/>), as a cluster does when a node returns.
/// </summary>
/// <remarks>
/// <para>Every notification the manager raises is recorded with a sequence number taken inside the subscriber
/// (<see cref="Events"/>), and every log entry the manager writes is kept (<see cref="Log"/>). The store is the SDK's
/// default in-memory store unless the test gives one.</para>
/// <para>Nothing here sleeps. A step that must run while another is parked runs on a thread the test starts
/// (<see cref="OnThread{T}"/>), and every wait the test makes is bounded by <see cref="Bound"/>.</para>
/// </remarks>
internal sealed class ReleaseRig : IAsyncDisposable
{
    /// <summary>The bound on the test's own waits: far above the release's limits (5 s drain, 1 s per call lock).</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>How long a test waits to see that an operation has not returned while a handler is parked.</summary>
    public static readonly TimeSpan NotReturnedWithin = TimeSpan.FromMilliseconds(750);

    private readonly ConcurrentDictionary<string, RigServer> _current = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RigServer> _all = new();
    private readonly ConcurrentQueue<Recorded> _events = new();
    private readonly ConcurrentQueue<CallSession> _released = new();
    private readonly IDisposable _subscription;
    private long _sequence;

    private ReleaseRig(CallSessionManager manager, SessionStoreBase store, TimeProvider clock, RecordingLog log)
    {
        Manager = manager;
        Store = store;
        Clock = clock;
        Log = log;
        _subscription = manager.Events.Subscribe(new Recorder(this));
    }

    public CallSessionManager Manager { get; }

    public SessionStoreBase Store { get; }

    public TimeProvider Clock { get; }

    public RecordingLog Log { get; }

    /// <summary>Every notification raised, in the order the recorder received them.</summary>
    public IReadOnlyList<Recorded> Events => [.. _events.OrderBy(e => e.Sequence)];

    /// <summary>The sequence number of the last notification recorded so far.</summary>
    public long Sequence => Interlocked.Read(ref _sequence);

    /// <summary>The <see cref="CallReleasedEvent"/>s raised, in order.</summary>
    public IReadOnlyList<CallReleasedEvent> Released => [.. Events.Select(e => e.Event).OfType<CallReleasedEvent>()];

    /// <summary>The kinds of notification raised for <paramref name="sessionId"/>, in order.</summary>
    public string[] KindsOf(string sessionId) =>
        [.. Events.Where(e => e.Event.SessionId == sessionId).Select(e => e.Event.GetType().Name)];

    /// <summary>The current instance attached under <paramref name="serverId"/>.</summary>
    public RigServer this[string serverId] => _current[serverId];

    /// <summary>
    /// Builds a manager on <paramref name="clock"/> (the system clock when null) and attaches one started server per
    /// id in <paramref name="serverIds"/>.
    /// </summary>
    public static async Task<ReleaseRig> StartAsync(TimeProvider? clock = null, SessionOptions? options = null,
        IWrapUpDurationProvider? wrapUpDurations = null, SessionStoreBase? store = null, params string[] serverIds)
    {
        var log = new RecordingLog();
        var theStore = store ?? new InMemorySessionStore();
        var theClock = clock ?? TimeProvider.System;
        var manager = new CallSessionManager(Options.Create(options ?? new SessionOptions()), log, theStore, theClock,
            wrapUpDurations);
        var rig = new ReleaseRig(manager, theStore, theClock, log);
        try
        {
            foreach (var id in serverIds)
                await rig.AttachAsync(id);
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    /// <summary>The rig on the system clock with the servers named.</summary>
    public static Task<ReleaseRig> StartAsync(params string[] serverIds) =>
        StartAsync(clock: null, options: null, wrapUpDurations: null, store: null, serverIds);

    /// <summary>Starts a new server instance and attaches the manager to it under <paramref name="serverId"/>.</summary>
    public async Task<RigServer> AttachAsync(string serverId)
    {
        var server = await RigServer.StartAsync(serverId);
        _all.Enqueue(server);
        Manager.AttachToServer(server.Server, serverId);
        _current[serverId] = server;
        return server;
    }

    /// <summary>Starts a new server instance that is not attached; the test attaches it itself.</summary>
    public async Task<RigServer> NewInstanceAsync(string serverId)
    {
        var server = await RigServer.StartAsync(serverId);
        _all.Enqueue(server);
        return server;
    }

    /// <summary>Records <paramref name="server"/> as the instance now attached under its id.</summary>
    public void Current(RigServer server) => _current[server.Id] = server;

    /// <summary>
    /// Detaches <paramref name="serverId"/> naming "release", and records the calls returned for the post-condition
    /// the rig checks when it is disposed.
    /// </summary>
    public IReadOnlyList<CallSession> Release(string serverId)
    {
        var released = Manager.DetachFromServer(serverId, DetachedCalls.Release);
        foreach (var call in released)
            _released.Enqueue(call);
        return released;
    }

    /// <summary>
    /// A connected two-leg call on <paramref name="serverId"/>: legs <c>{id}-a</c> (caller) and <c>{id}-b</c>, linkedid
    /// <c>{id}-L</c>, dialed, answered and bridged in <c>{id}-bridge</c>.
    /// </summary>
    public CallSession ConnectedCall(string serverId, string id, string? tenantId = null)
    {
        var channels = this[serverId].Server.Channels;
        channels.OnNewChannel(CallerLeg(id), $"PJSIP/100-{id}-a", ChannelState.Ring, callerIdNum: "100",
            context: "from-internal", linkedId: LinkedIdOf(id));
        channels.OnNewChannel(CalleeLeg(id), $"PJSIP/200-{id}-b", ChannelState.Down, linkedId: LinkedIdOf(id));
        channels.OnDialBegin(CallerLeg(id), CalleeLeg(id), $"PJSIP/200-{id}-b", null);
        channels.OnNewState(CalleeLeg(id), ChannelState.Up);
        var bridges = this[serverId].Server.Bridges;
        bridges.OnBridgeCreated(BridgeOf(id), "mixing", "simple_bridge", null, null);
        bridges.OnChannelEntered(BridgeOf(id), CallerLeg(id));
        bridges.OnChannelEntered(BridgeOf(id), CalleeLeg(id));
        var session = Manager.GetByLinkedId(LinkedIdOf(id))
            ?? throw new InvalidOperationException($"premise: no session holds call {id} on {serverId}");
        if (session.State != CallSessionState.Connected)
            throw new InvalidOperationException($"premise: call {id} is {session.State}, not Connected");
        if (tenantId is not null)
            session.TenantId = tenantId;
        return session;
    }

    /// <summary>A one-leg inbound call on <paramref name="serverId"/>: leg <c>{id}-a</c>, linkedid <c>{id}-L</c>.</summary>
    public CallSession Arrival(string serverId, string id)
    {
        this[serverId].Server.Channels.OnNewChannel(CallerLeg(id), $"PJSIP/trunk-{id}-a", ChannelState.Up,
            context: "from-trunk", linkedId: LinkedIdOf(id));
        return Manager.GetByLinkedId(LinkedIdOf(id))
            ?? throw new InvalidOperationException($"premise: no session holds call {id} on {serverId}");
    }

    /// <summary>Hangs up leg <paramref name="uniqueId"/> on <paramref name="serverId"/> with normal clearing.</summary>
    public void HangUp(string serverId, string uniqueId) =>
        this[serverId].Server.Channels.OnHangup(uniqueId, HangupCause.NormalClearing);

    /// <summary>Hangs up both legs of a call <see cref="ConnectedCall"/> opened.</summary>
    public void HangUpCall(string serverId, string id)
    {
        HangUp(serverId, CalleeLeg(id));
        HangUp(serverId, CallerLeg(id));
    }

    public static string CallerLeg(string id) => $"{id}-a";

    public static string CalleeLeg(string id) => $"{id}-b";

    public static string LinkedIdOf(string id) => $"{id}-L";

    public static string BridgeOf(string id) => $"{id}-bridge";

    /// <summary>Runs <paramref name="work"/> on a thread of its own, never the test's.</summary>
    public static Task<T> OnThread<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Runs <paramref name="work"/> on a thread of its own, never the test's.</summary>
    public static Task OnThread(Action work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Whether <paramref name="task"/> completes within <paramref name="bound"/> (<see cref="Bound"/> when null).</summary>
    public static bool Returns(Task task, TimeSpan? bound = null)
    {
        try
        {
            return task.Wait(bound ?? Bound);
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    /// <summary>The result of <paramref name="task"/> when it completed successfully, else the default.</summary>
    public static T? ResultOf<T>(Task<T> task) => task.IsCompletedSuccessfully ? task.Result : default;

    /// <summary>What <paramref name="task"/> threw, when it faulted.</summary>
    public static Exception? FaultOf(Task task) => task.IsFaulted ? task.Exception!.InnerException : null;

    /// <summary>A short account of what the rig measured, for failure messages.</summary>
    public string Describe() =>
        "notifications: [" + string.Join(", ", Events.Select(e => $"#{e.Sequence} {e.Event.GetType().Name}:{e.Event.SessionId[..6]}"))
        + "]; log: [" + string.Join(" | ", Log.Entries.Where(e => e.Level >= LogLevel.Information)
            .Select(e => $"{e.Level} {e.Message}")) + "]";

    /// <summary>
    /// Disposes the manager and the servers, then checks the post-condition of every release made through
    /// <see cref="Release"/>: no index entry names a released call, and no connected-leg count still includes a
    /// released call's legs (<see cref="CallSessionManager.ReleaseLeftovers"/>). Read before the manager is disposed,
    /// once the test has stopped driving it; checked after the clean-up, so a breach never leaks the servers. A
    /// breach fails the test even when its own assertions passed. When the test's own assertion has just failed on
    /// this thread — the disposal then runs while that failure unwinds, and a throw here would replace its message —
    /// the breach is not thrown; the test is red already.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var ownAssertionFailed = t_assertionFailed;
        t_assertionFailed = false;
        var leftovers = Manager.ReleaseLeftovers(_released);
        _subscription.Dispose();
        await Manager.DisposeAsync();
        while (_all.TryDequeue(out var server))
            await server.Server.DisposeAsync();

        if (!ownAssertionFailed)
        {
            leftovers.Should().BeEmpty("a release leaves no index entry naming a released call and no connected-leg "
                + "count including its legs. " + Describe());
        }
    }

    /// <summary>
    /// Whether an assertion failed on this thread since the last disposal read it. An <c>await using</c> disposes on
    /// the thread that threw, synchronously, before the failure leaves the test.
    /// </summary>
    [ThreadStatic]
    private static bool t_assertionFailed;

    static ReleaseRig() =>
        AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
        {
            if (args.Exception is Xunit.Sdk.XunitException)
                t_assertionFailed = true;
        };

    /// <summary>One notification and the sequence number the recorder gave it.</summary>
    internal sealed record Recorded(long Sequence, SessionDomainEvent Event, int ThreadId);

    private sealed class Recorder(ReleaseRig rig) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value) =>
            rig._events.Enqueue(new Recorded(Interlocked.Increment(ref rig._sequence), value,
                Environment.CurrentManagedThreadId));

        public void OnError(Exception error)
        {
            // The manager's stream never faults; nothing to record.
        }

        public void OnCompleted()
        {
            // Completed when the manager is disposed; nothing to record.
        }
    }
}

/// <summary>One server instance of a <see cref="ReleaseRig"/>.</summary>
internal sealed class RigServer
{
    private RigServer(string id, VerbaraServer server, IObserver<ManagerEvent> observer)
    {
        Id = id;
        Server = server;
        Observer = observer;
    }

    public string Id { get; }

    public VerbaraServer Server { get; }

    public IObserver<ManagerEvent> Observer { get; }

    /// <summary>Writes <paramref name="frames"/>, in order, to the observer the server subscribed.</summary>
    public void Deliver(params ManagerEvent[] frames)
    {
        foreach (var frame in frames)
            Observer.OnNext(frame);
    }

    public static async Task<RigServer> StartAsync(string id)
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("22.10.1");
        connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(_ => NoEvents());
        IObserver<ManagerEvent>? observer = null;
        connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(o => observer = o)).Returns(Substitute.For<IDisposable>());
        var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        await server.StartAsync();
        return new RigServer(id, server,
            observer ?? throw new InvalidOperationException("VerbaraServer.StartAsync subscribed no event observer."));
    }

    private static async IAsyncEnumerable<ManagerEvent> NoEvents()
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>One log entry: its level, its event name, the formatted message, its named values and its exception.</summary>
internal sealed record LogRecord(LogLevel Level, string? EventName, string Message,
    IReadOnlyDictionary<string, object?> Values, Exception? Exception);

/// <summary>Keeps every entry the manager writes, at every level.</summary>
internal sealed class RecordingLog : ILogger<CallSessionManager>
{
    private readonly ConcurrentQueue<LogRecord> _entries = new();

    public IReadOnlyList<LogRecord> Entries => [.. _entries];

    /// <summary>The entries at <paramref name="level"/>.</summary>
    public IReadOnlyList<LogRecord> At(LogLevel level) => [.. _entries.Where(e => e.Level == level)];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
                values[pair.Key] = pair.Value;
        }

        _entries.Enqueue(new LogRecord(logLevel, eventId.Name, formatter(state, exception), values, exception));
    }
}

/// <summary>
/// Sums, per value of one tag, what is recorded on one instrument of the <c>Verbara.Sdk.Sessions</c> meter while it
/// listens. An instrument that does not exist is never published, and reads zero.
/// </summary>
internal sealed class InstrumentTally : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, double> _byTag = new(StringComparer.Ordinal);
    private readonly string _tag;
    private double _total;
    private long _measurements;

    public InstrumentTally(string instrumentName, string tag = "server.id")
    {
        _tag = tag;
        // Touch the static meter first, so its instruments are published before the listener starts.
        _ = SessionMetrics.SessionsCreated;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == SessionMetrics.Meter.Name && instrument.Name == instrumentName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) => Add(value, tags));
        _listener.SetMeasurementEventCallback<double>((_, value, tags, _) => Add(value, tags));
        _listener.SetMeasurementEventCallback<int>((_, value, tags, _) => Add(value, tags));
        _listener.Start();
    }

    /// <summary>What was recorded with the tag set to <paramref name="value"/>.</summary>
    public double For(string value) => _byTag.GetValueOrDefault(value);

    /// <summary>What was recorded, all tags together.</summary>
    public double Total
    {
        get
        {
            lock (_byTag)
            {
                return _total;
            }
        }
    }

    /// <summary>How many measurements were recorded.</summary>
    public long Measurements => Interlocked.Read(ref _measurements);

    public void Dispose() => _listener.Dispose();

    private void Add(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Interlocked.Increment(ref _measurements);
        lock (_byTag)
        {
            _total += value;
        }

        foreach (var tag in tags)
        {
            if (tag.Key == _tag && tag.Value is string id)
                _byTag.AddOrUpdate(id, value, (_, sum) => sum + value);
        }
    }
}
