using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Tests.Connection;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Hosting.Tests.Reconciliation;

/// <summary>
/// The sweep and the session engine over a real <see cref="AmiConnection"/> whose event buffer holds
/// <see cref="Capacity"/> events, so a test can lose an event the way production does: the full buffer refuses an event
/// no action received. A <see cref="VerbaraServer"/> on the connection, a <see cref="CallSessionManager"/> attached to it
/// (or attached by the test), and the reconciliation sweep driven by hand, one tick at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>The peer plays Asterisk truthfully.</b> It keeps the channels Asterisk holds: a channel the test creates is held
/// from its <c>Newchannel</c>, delivered or refused, until its <c>Hangup</c>, delivered or refused. Every <c>Status</c>
/// after the first load is counted and answered from that set, unless the test refuses it, names what it lists, or
/// answers it itself.
/// </para>
/// <para>
/// <b>A drop.</b> An <c>OnEvent</c> handler holds the dispatch of a <c>PeerStatus</c>; the peer fills the buffer behind
/// it, and any later event no action asked for is refused: a loss behind the backlog. A Ping the peer answers is the
/// fence that says the reader has handed an event to the buffer. <see cref="DeliverAsync"/> releases the hold and waits
/// for a tail event written once the last filler is in dispatch: the connection has dispatched through the drop. Outside
/// a hold the rig writes an event only once the one before it was dispatched.
/// </para>
/// <para>
/// <b>Clocks.</b> The sweep and the manager read <see cref="Clock"/>; the server's barrier waits on
/// <see cref="LiveClock"/>. A <c>Status</c> sent while the dispatch is held makes its barrier wait for a pump that cannot
/// move: <see cref="RunAsync"/> sees the wait begin, advances <see cref="LiveClock"/> past the barrier's bound and lets
/// the operation end deferred, and reports that it had to. Nothing waits on the wall clock but <see cref="Bound"/>, a
/// hang bound every wait ends long before.
/// </para>
/// </remarks>
internal sealed class LossRig : IAsyncDisposable
{
    public const int Capacity = 2;
    public const string ServerId = "loss-srv";
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const string SweepSpan = "session reconciliation";
    private const string ReconcileSpan = "live channel-reconcile";

    private readonly PipedSocketFactory _sockets = new();
    private readonly CancellationTokenSource _peerCts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _seen = new(StringComparer.Ordinal);
    private readonly Lock _truthGate = new();
    private readonly List<(string Uid, string LinkedId)> _asterisk = [];
    private readonly ConcurrentQueue<string> _statusIds = new();
    private readonly ConcurrentQueue<Func<string, PipedSocket, Task>> _statusPlan = new();
    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly ConcurrentDictionary<string, int> _endings = new(StringComparer.Ordinal);
    private readonly ActivityListener _listener;
    private readonly IDisposable _endingSubscription;
    private readonly bool _refuseFirstLoad;
    private readonly List<Task> _served = [];
    private TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _loadDispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _serving = Task.CompletedTask;
    private volatile bool _loaded;
    private volatile bool _holding;
    private int _holds;
    private int _statusAsked;
    private int _sessions;

    private LossRig(bool attach, bool autoReconnect, bool refuseFirstLoad, IObserver<ManagerEvent>? beforeServer)
    {
        _refuseFirstLoad = refuseFirstLoad;
        Connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = autoReconnect,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(1),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(1),
            DefaultEventTimeout = TimeSpan.Zero,
            EventPumpCapacity = Capacity,
        }), _sockets, ConnectionLog);
        Connection.OnEvent += OnEventAsync;
        if (beforeServer is not null)
            BeforeServer = Connection.Subscribe(beforeServer);
        Server = new VerbaraServer(Connection, NullLogger<VerbaraServer>.Instance)
        {
            TimeProvider = LiveClock,
        };
        Settings = new SessionOptions();
        Clock = new ManualClock(DateTimeOffset.UtcNow);
        Manager = new CallSessionManager(Options.Create(Settings), NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore(), Clock);
        if (attach)
            Manager.AttachToServer(Server, ServerId);
        _endingSubscription = Manager.Events.Subscribe(new EndingObserver(this));
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Verbara.Sdk.Sessions" or "Verbara.Sdk.Live",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName is SweepSpan or ReconcileSpan)
                    _spans.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public AmiConnection Connection { get; }

    public VerbaraServer Server { get; }

    public CallSessionManager Manager { get; }

    public SessionOptions Settings { get; }

    /// <summary>The sweep's and the manager's clock.</summary>
    public ManualClock Clock { get; }

    /// <summary>The clock the server's barrier waits on.</summary>
    public FakeTimeProvider LiveClock { get; } = new();

    public CapturingLoggerProvider Log { get; } = new();

    /// <summary>The connection's log: <c>[AMI] Connected</c> is written once a session is logged in.</summary>
    public SignalingLogger<AmiConnection> ConnectionLog { get; } = new();

    private readonly Pulse _statusPulse = new();

    /// <summary>The peer of the current AMI session.</summary>
    public PipedSocket Peer { get; private set; } = null!;

    /// <summary>An observer subscribed on the connection before the server, when the test gave one.</summary>
    public IDisposable? BeforeServer { get; }

    /// <summary>How many <c>Status</c> the peers were sent after the first load.</summary>
    public int StatusAsked => Volatile.Read(ref _statusAsked);

    /// <summary>Whether an <c>OnEvent</c> handler holds the dispatch now.</summary>
    public bool Holding => _holding;

    /// <summary>
    /// Connects, starts the server (its first load lists <paramref name="listed"/>, or is refused) and returns once the
    /// first load's last list was dispatched.
    /// </summary>
    public static async Task<LossRig> StartAsync(bool attach = true, bool autoReconnect = false,
        bool refuseFirstLoad = false, IObserver<ManagerEvent>? beforeServer = null,
        params (string Uid, string LinkedId)[] listed)
    {
        var rig = new LossRig(attach, autoReconnect, refuseFirstLoad, beforeServer);
        foreach (var channel in listed)
            rig.Hold(channel.Uid, channel.LinkedId);
        rig._serving = rig.ServeAsync(rig._peerCts.Token);
        try
        {
            await rig.Connection.ConnectAsync().AsTask().WaitAsync(Bound);
            await rig.Server.StartAsync().WaitAsync(Bound);
            await rig._loadDispatched.Task.WaitAsync(Bound);
            rig._loaded = true;
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    public void Attach() => Manager.AttachToServer(Server, ServerId);

    // --- Asterisk's channels ------------------------------------------------------------------------------

    /// <summary>The channels Asterisk holds now, in creation order.</summary>
    public IReadOnlyList<(string Uid, string LinkedId)> AsteriskHolds()
    {
        lock (_truthGate)
            return [.. _asterisk];
    }

    private void Hold(string uid, string linkedId)
    {
        lock (_truthGate)
            _asterisk.Add((uid, linkedId));
    }

    private string Forget(string uid)
    {
        lock (_truthGate)
        {
            var index = _asterisk.FindIndex(c => c.Uid == uid);
            var linkedId = index >= 0 ? _asterisk[index].LinkedId : uid;
            if (index >= 0) _asterisk.RemoveAt(index);
            return linkedId;
        }
    }

    public static string ChannelOf(string uid) => $"PJSIP/{uid}";

    private static KeyValuePair<string, string>[] NewChannelFields(string uid, string linkedId) =>
    [
        new("Channel", ChannelOf(uid)), new("ChannelState", "6"), new("ChannelStateDesc", "Up"),
        new("CallerIDNum", "5552101"), new("Context", "from-pstn"), new("Exten", "4021"), new("Priority", "1"),
        new("Uniqueid", uid), new("Linkedid", linkedId),
    ];

    private static KeyValuePair<string, string>[] HangupFields(string uid, string linkedId) =>
    [
        new("Channel", ChannelOf(uid)), new("ChannelState", "6"), new("ChannelStateDesc", "Up"),
        new("Uniqueid", uid), new("Linkedid", linkedId), new("Cause", "16"), new("Cause-txt", "Normal Clearing"),
    ];

    /// <summary>A channel appears in Asterisk; returns once its <c>Newchannel</c> was dispatched.</summary>
    public async Task NewChannelAsync(string uid, string linkedId)
    {
        Hold(uid, linkedId);
        var seen = Seen("Newchannel", uid);
        await Peer.WriteEventAsync("Newchannel", NewChannelFields(uid, linkedId));
        await seen.WaitAsync(Bound);
    }

    /// <summary>A channel hangs up (cause 16); returns once its <c>Hangup</c> was dispatched.</summary>
    public async Task HangUpAsync(string uid)
    {
        var linkedId = Forget(uid);
        var seen = Seen("Hangup", uid);
        await Peer.WriteEventAsync("Hangup", HangupFields(uid, linkedId));
        await seen.WaitAsync(Bound);
    }

    /// <summary>A channel appears in Asterisk while the buffer is full: its <c>Newchannel</c> is refused.</summary>
    public async Task DropNewChannelAsync(string uid, string linkedId)
    {
        Hold(uid, linkedId);
        await Peer.WriteEventAsync("Newchannel", NewChannelFields(uid, linkedId));
        await FenceAsync();
    }

    /// <summary>A channel hangs up while the buffer is full: its <c>Hangup</c> is refused.</summary>
    public async Task DropHangupAsync(string uid)
    {
        var linkedId = Forget(uid);
        await Peer.WriteEventAsync("Hangup", HangupFields(uid, linkedId));
        await FenceAsync();
    }

    /// <summary>A channel hangs up; its <c>Hangup</c> is written and not waited for (a test holds its dispatch).</summary>
    public async Task WriteHangupAsync(string uid)
    {
        var linkedId = Forget(uid);
        await Peer.WriteEventAsync("Hangup", HangupFields(uid, linkedId));
    }

    /// <summary>Asterisk no longer holds the channel, and the SDK is told some other way, or not at all.</summary>
    public void ForgetChannel(string uid) => Forget(uid);

    /// <summary>Completes once the event of this type and key has been dispatched to the <c>OnEvent</c> handlers.</summary>
    public Task DispatchedAsync(string eventType, string key) => Seen(eventType, key).WaitAsync(Bound);

    /// <summary>
    /// Fills the buffer behind a dispatch the test holds some other way: the next event no action asked for is refused.
    /// </summary>
    public async Task FillAsync()
    {
        var n = Interlocked.Increment(ref _holds);
        for (var i = 0; i < Capacity; i++)
            await BufferAsync("PeerStatus", [new("Peer", $"FILL/{n}/{i}"), new("PeerStatus", "Reachable")], $"FILL/{n}/{i}");
        await FenceAsync();
    }

    /// <summary>Runs <paramref name="handler"/> inside the <c>OnEvent</c> dispatch of the event keyed <paramref name="key"/>.</summary>
    public Task InHandlerOf(string key, Func<Task> handler)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inHandler[key] = async () =>
        {
            try
            {
                await handler();
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        };
        return done.Task;
    }

    /// <summary>An event no channel reads is refused by the full buffer: a loss that leaves no ghost.</summary>
    public async Task DropUnrelatedAsync()
    {
        await Peer.WriteEventAsync("PeerStatus", [new("Peer", "LOST/" + Interlocked.Increment(ref _holds)), new("PeerStatus", "Reachable")]);
        await FenceAsync();
    }

    // --- Holding the dispatch ------------------------------------------------------------------------------

    /// <summary>The peer sends a <c>PeerStatus</c> whose <c>OnEvent</c> handler holds the dispatch.</summary>
    public async Task HoldAsync()
    {
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Peer.WriteEventAsync("PeerStatus", [new("Peer", "HOLD/" + Interlocked.Increment(ref _holds)), new("PeerStatus", "Reachable")]);
        await _held.Task.WaitAsync(Bound);
    }

    /// <summary>Holds the dispatch and fills the buffer behind it: the next event no action asked for is refused.</summary>
    public async Task HoldAndFillAsync()
    {
        await HoldAsync();
        for (var i = 0; i < Capacity; i++)
            await BufferAsync("PeerStatus", [new("Peer", $"FILL/{_holds}/{i}"), new("PeerStatus", "Reachable")], $"FILL/{_holds}/{i}");
        await FenceAsync();
    }

    /// <summary>Writes an event that waits in the buffer behind the hold; <see cref="DeliverAsync"/> waits for it.</summary>
    public async Task BufferAsync(string eventType, KeyValuePair<string, string>[] fields, string key)
    {
        _lastBuffered = eventType + "|" + key;
        await Peer.WriteEventAsync(eventType, fields);
    }

    /// <summary>A channel's <c>Newchannel</c> waits in the buffer behind the hold.</summary>
    public Task BufferNewChannelAsync(string uid, string linkedId)
    {
        Hold(uid, linkedId);
        return BufferAsync("Newchannel", NewChannelFields(uid, linkedId), uid);
    }

    private string? _lastBuffered;

    /// <summary>
    /// Opens the held dispatch and returns once a tail event written after the buffer emptied was dispatched: the
    /// connection has dispatched through every event refused before.
    /// </summary>
    public async Task DeliverAsync()
    {
        var tail = "TAIL/" + Interlocked.Increment(ref _holds);
        var seen = Seen("PeerStatus", tail);
        var last = _lastBuffered is { } buffered
            ? _seen.GetOrAdd(buffered, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task
            : Task.CompletedTask;
        _lastBuffered = null;
        _gate.TrySetResult();
        _holding = false;
        await last.WaitAsync(Bound);
        await Peer.WriteEventAsync("PeerStatus", [new("Peer", tail), new("PeerStatus", "Reachable")]);
        await seen.WaitAsync(Bound);

        // The tail's handler is signalled before it returns, and the consumer passes the drop only once it has returned.
        // It reads the next event only after that, so the next event's dispatch says the tail's has been counted.
        var after = Seen("PeerStatus", tail + "/after");
        await Peer.WriteEventAsync("PeerStatus", [new("Peer", tail + "/after"), new("PeerStatus", "Reachable")]);
        await after.WaitAsync(Bound);
    }

    /// <summary>Opens the held dispatch without waiting for anything.</summary>
    public void Release()
    {
        _gate.TrySetResult();
        _holding = false;
    }

    /// <summary>A Ping answered by the peer: every event written before it has been read once it returns.</summary>
    public async Task FenceAsync() => await Connection.SendActionAsync(new PingAction()).AsTask().WaitAsync(Bound);

    /// <summary>Runs <paramref name="handler"/> inside the <c>OnEvent</c> dispatch of a <c>PeerStatus</c> written now.</summary>
    public async Task InHandlerAsync(Func<Task> handler)
    {
        var name = "IN/" + Interlocked.Increment(ref _holds);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inHandler[name] = async () =>
        {
            try
            {
                await handler();
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        };
        await Peer.WriteEventAsync("PeerStatus", [new("Peer", name), new("PeerStatus", "Reachable")]);
        await done.Task.WaitAsync(Bound);
    }

    private readonly ConcurrentDictionary<string, Func<Task>> _inHandler = new(StringComparer.Ordinal);

    // --- Status ---------------------------------------------------------------------------------------------

    /// <summary>The next <c>Status</c> is refused by Asterisk.</summary>
    public void RefuseNextStatus() =>
        _statusPlan.Enqueue((id, socket) => socket.RespondAsync("Error", id, [new("Message", "Permission denied")]));

    /// <summary>The next <c>Status</c> lists <paramref name="uids"/>, whatever Asterisk holds.</summary>
    public void NextStatusLists(params string[] uids) =>
        _statusPlan.Enqueue((id, socket) => AnswerAsync(socket, id, uids));

    /// <summary>The next <c>Status</c> is left unanswered; the test answers it with <see cref="AnswerStatusAsync"/>.</summary>
    public void AnswerNextStatusByHand() => _statusPlan.Enqueue((_, _) => Task.CompletedTask);

    /// <summary>The ActionID of the <paramref name="n"/>-th <c>Status</c> asked after the first load (1-based).</summary>
    public async Task<string> StatusIdAsync(int n)
    {
        if (!await _statusPulse.WaitUntilAsync(() => _statusIds.Count >= n, Bound))
            throw new TimeoutException($"Status number {n} was not asked.");
        return _statusIds.ElementAt(n - 1);
    }

    /// <summary>Answers the <c>Status</c> <paramref name="id"/> by hand, listing <paramref name="uids"/>.</summary>
    public Task AnswerStatusAsync(string id, params string[] uids) => AnswerAsync(Peer, id, uids);

    private async Task AnswerAsync(PipedSocket socket, string id, string[] uids)
    {
        var listed = AsteriskHoldsAll();
        await socket.RespondAsync("Success", id, [new("EventList", "start")]);
        foreach (var uid in uids)
        {
            var linkedId = listed.TryGetValue(uid, out var l) ? l : uid;
            await socket.WriteEventAsync("Status",
            [
                new("ActionID", id), new("Channel", ChannelOf(uid)), new("ChannelState", "6"), new("ChannelStateDesc", "Up"),
                new("CallerIDNum", "5552101"), new("Context", "from-pstn"), new("Exten", "4021"), new("Uniqueid", uid),
                new("Linkedid", linkedId),
            ]);
        }

        await socket.WriteEventAsync("StatusComplete",
            [new("ActionID", id), new("EventList", "Complete"), new("ListItems", uids.Length.ToString(CultureInfo.InvariantCulture))]);
    }

    // Every linkedid the test ever named, so a hand-written list may name a channel Asterisk no longer holds.
    private readonly ConcurrentDictionary<string, string> _everHeld = new(StringComparer.Ordinal);

    private Dictionary<string, string> AsteriskHoldsAll()
    {
        lock (_truthGate)
        {
            foreach (var (uid, linkedId) in _asterisk)
                _everHeld[uid] = linkedId;
        }

        return new Dictionary<string, string>(_everHeld, StringComparer.Ordinal);
    }

    // --- Operations under test ---------------------------------------------------------------------------

    /// <summary>What one sweep tick did.</summary>
    internal sealed record Tick(int Status, bool Waited, string? Verification, string? Trigger, int Candidates,
        int Unverifiable, string Line);

    /// <summary>
    /// Runs <paramref name="operation"/> to its end. When it sends a <c>Status</c> whose barrier waits on a held
    /// dispatch, the barrier's bound is run out on <see cref="LiveClock"/>; returns whether that was needed.
    /// </summary>
    public async Task<bool> RunAsync(Func<Task> operation)
    {
        while (LiveClock.TimersCreated.TryRead(out _))
        {
            // Drop timers created before this operation: only the barrier's own is advanced past.
        }

        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Server.FenceWaitObserved = (_, _) =>
        {
            if (_holding || Server.IsReconnectPending) waiting.TrySetResult();
        };
        try
        {
            var task = operation();
            var first = await Task.WhenAny(task, waiting.Task).WaitAsync(Bound);
            if (ReferenceEquals(first, task))
            {
                await task;
                return false;
            }

            await LiveClock.TimersCreated.ReadAsync().AsTask().WaitAsync(Bound);
            LiveClock.Advance(VerbaraServer.BarrierBound + TimeSpan.FromSeconds(1));
            await task.WaitAsync(Bound);
            return true;
        }
        finally
        {
            Server.FenceWaitObserved = null;
        }
    }

    /// <summary>
    /// One sweep tick, single-server (<paramref name="pool"/> false) or pool, as the host runs it; returns what its span
    /// and its result line say, and the <c>Status</c> it sent.
    /// </summary>
    public async Task<Tick> TickAsync(bool pool)
    {
        var asked = StatusAsked;
        var spans = _spans.Count;
        var lines = Log.Entries.Count;
        var sweep = new ServerSweep(Manager, Settings, Log.CreateLogger("LossRig.Sweep"), Clock);
        var waited = await RunAsync(() => sweep.SweepAsync(Server, pool ? ServerId : null, CancellationToken.None));
        var span = _spans.Skip(spans).Last(a => a.OperationName == SweepSpan);
        var line = Log.Entries.Skip(lines).LastOrDefault(e => e.Values.ContainsKey("Verification"));
        return new Tick(StatusAsked - asked, waited, span.GetTagItem("verification") as string,
            span.GetTagItem("sessions.trigger") as string, Convert.ToInt32(span.GetTagItem("sessions.candidates"), CultureInfo.InvariantCulture),
            Convert.ToInt32(span.GetTagItem("sessions.unverifiable"), CultureInfo.InvariantCulture), line?.Message ?? "");
    }

    /// <summary>A caller's own channel reconciliation from the test thread; returns its span's tags.</summary>
    public async Task<IReadOnlyDictionary<string, string?>> ReconcileAsync()
    {
        var spans = _spans.Count;
        await RunAsync(() => Server.ReconcileChannelsAsync().AsTask());
        return TagsOf(_spans.Skip(spans).Last(a => a.OperationName == ReconcileSpan));
    }

    /// <summary>The tags of every <c>live channel-reconcile</c> span stopped since <paramref name="mark"/>.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string?>> ReconcileSpansSince(int mark) =>
        [.. _spans.Skip(mark).Where(a => a.OperationName == ReconcileSpan).Select(TagsOf)];

    public int SpanMark => _spans.Count;

    private static Dictionary<string, string?> TagsOf(Activity span) =>
        span.TagObjects.ToDictionary(t => t.Key, t => Convert.ToString(t.Value, CultureInfo.InvariantCulture),
            StringComparer.Ordinal);

    /// <summary>The admission of the attached server's held channels, run from the test thread.</summary>
    public Task<bool> AdmitAsync() => RunAsync(() => Manager.AdmitExistingCalls(ServerId).AsTask());

    /// <summary>A reconstructed call of the attached server, linkedid <paramref name="linkedId"/>, with these legs.</summary>
    public static CallSession Reconstructed(string linkedId, params string[] legs)
    {
        var call = new CallSession($"{linkedId}-restored", linkedId, ServerId, CallDirection.Inbound);
        foreach (var leg in legs)
            call.AddParticipant(new SessionParticipant { UniqueId = leg, Channel = ChannelOf(leg), Technology = "PJSIP" });
        return call;
    }

    public int EndingsOf(CallSession call) => _endings.GetValueOrDefault(call.SessionId);

    /// <summary>The call as a test reads it.</summary>
    public string Describe(CallSession call) =>
        $"state={call.State}; ended={EndingsOf(call)}; unverified={call.Unverified}; cause={call.Metadata.GetValueOrDefault("cause") ?? "null"}; " +
        $"hangup={call.HangupCause?.ToString() ?? "null"}; legs=[{string.Join(",", call.Participants.Select(p => p.UniqueId + (p.LeftAt.HasValue ? "(left)" : "")))}]; " +
        $"table=[{string.Join(",", Server.Channels.ActiveChannels.Select(c => c.UniqueId))}]; status asked={StatusAsked}";

    /// <summary>Closes the current AMI session from the peer's side.</summary>
    public void CloseSession() => Peer.CloseFromPeer();

    /// <summary>Completes once the connection has logged in <paramref name="sessions"/> sessions.</summary>
    public async Task LoggedInAsync(int sessions)
    {
        await ConnectionLog.Logged("[AMI] Connected", sessions).WaitAsync(Bound);
    }

    /// <summary>Completes once no reconnect is pending and no load runs: the reload after the reconnect has returned.</summary>
    public async Task ReconnectHandledAsync()
    {
        var bound = DateTime.UtcNow + Bound;
        while (Server.IsReconnectPending || Server.IsLoadInFlight)
        {
            if (DateTime.UtcNow > bound) throw new TimeoutException("The reconnect was not handled.");
            await Task.Yield();
        }
    }

    // --- Plumbing ------------------------------------------------------------------------------------------

    private Task Seen(string eventType, string key) =>
        _seen.GetOrAdd(eventType + "|" + key,
            static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    private async ValueTask OnEventAsync(ManagerEvent evt)
    {
        var fields = evt.RawFields;
        var key = fields?.GetValueOrDefault("Peer") ?? fields?.GetValueOrDefault("Uniqueid")
            ?? fields?.GetValueOrDefault("ActionID") ?? "";
        _seen.GetOrAdd((evt.EventType ?? "") + "|" + key,
            static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        if (!_loaded && evt.EventType == "AgentsComplete")
            _loadDispatched.TrySetResult();
        if (key.StartsWith("HOLD/", StringComparison.Ordinal))
        {
            _holding = true;
            var gate = _gate;
            _held.TrySetResult();
            await gate.Task;
        }
        else if (_inHandler.TryRemove(key, out var handler))
        {
            await handler();
        }
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var socket = await _sockets.NextAsync(cancellationToken);
                await socket.CompleteLoginAsync(cancellationToken);
                Peer = socket;
                Interlocked.Increment(ref _sessions);
                _served.Add(Task.Run(() => AnswerLoopAsync(socket, cancellationToken), CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run is over.
        }
        catch (InvalidOperationException)
        {
            // The connection closed a socket before its login finished.
        }

        await Task.WhenAll(_served);
    }

    private async Task AnswerLoopAsync(PipedSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            while (await socket.ReadActionAsync(cancellationToken) is { } action)
                await AnswerActionAsync(socket, action);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run is over.
        }
    }

    private async Task AnswerActionAsync(PipedSocket socket, string action)
    {
        var end = action.IndexOf("\r\n", StringComparison.Ordinal);
        var name = (end >= 0 ? action[..end] : action).Replace("Action:", "", StringComparison.OrdinalIgnoreCase).Trim();
        var id = PipedSocket.ActionIdOf(action);
        switch (name.ToUpperInvariant())
        {
            case "STATUS" when _loaded:
                _statusIds.Enqueue(id);
                Interlocked.Increment(ref _statusAsked);
                _statusPulse.Fire();
                if (_statusPlan.TryDequeue(out var plan))
                    await plan(id, socket);
                else
                    await AnswerAsync(socket, id, [.. AsteriskHolds().Select(c => c.Uid)]);
                break;
            case "STATUS" when _refuseFirstLoad:
                await socket.RespondAsync("Error", id, [new("Message", "Permission denied")]);
                break;
            case "STATUS":
                await AnswerAsync(socket, id, [.. AsteriskHolds().Select(c => c.Uid)]);
                break;
            case "PRESENCESTATELIST":
                _presenceAsked.TrySetResult(id);
                break;
            case "QUEUESTATUS":
            case "AGENTS":
            case "BRIDGELIST":
                await socket.RespondAsync("Success", id, [new("EventList", "start")]);
                await socket.WriteEventAsync(name + "Complete",
                    [new("ActionID", id), new("EventList", "Complete"), new("ListItems", "0")]);
                break;
            case "CONFBRIDGELISTROOMS":
            case "CONFBRIDGELIST":
                await socket.RespondAsync("Error", id, [new("Message", "No active conferences.")]);
                break;
            default:
                await socket.RespondAsync("Success", id);
                break;
        }
    }

    private readonly TaskCompletionSource<string> _presenceAsked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The ActionID of the first presence-state listing the peer was sent.</summary>
    public Task<string> PresenceListAsked => _presenceAsked.Task;

    public async ValueTask DisposeAsync()
    {
        _gate.TrySetResult();
        await Server.DisposeAsync().AsTask().WaitAsync(Bound);
        await Connection.DisposeAsync().AsTask().WaitAsync(Bound);
        await _peerCts.CancelAsync();
        await _serving.WaitAsync(Bound);
        _endingSubscription.Dispose();
        BeforeServer?.Dispose();
        await Manager.DisposeAsync();
        _listener.Dispose();
        _peerCts.Dispose();
    }

    private sealed class EndingObserver(LossRig rig) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value)
        {
            if (value is CallEndedEvent ended)
                rig._endings.AddOrUpdate(ended.SessionId, 1, static (_, n) => n + 1);
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
/// An observer subscribed on the connection before the server: it holds the dispatch of the event
/// <paramref name="holds"/> selects, before the server sees it, until the test releases it.
/// </summary>
internal sealed class FrontHold(Func<ManagerEvent, bool> holds) : IObserver<ManagerEvent>, IDisposable
{
    public void Dispose()
    {
        _released.Set();
        _released.Dispose();
    }

    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _released = new(false);

    public Task Held => _held.Task;

    public void Release() => _released.Set();

    public void OnNext(ManagerEvent value)
    {
        if (_held.Task.IsCompleted || !holds(value)) return;
        _held.TrySetResult();
        _released.Wait(LossRig.Bound * 3);
    }

    public void OnError(Exception error)
    {
        // The connection never faults its observers in these tests; nothing to do.
    }

    public void OnCompleted()
    {
        // Completed when the connection is disposed; nothing to do.
    }
}
