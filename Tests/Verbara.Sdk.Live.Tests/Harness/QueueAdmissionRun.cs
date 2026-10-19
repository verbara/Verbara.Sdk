using System.Collections.Concurrent;
using System.Reactive.Disposables;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Tests.Queues;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Live.Tests.Harness;

/// <summary>
/// An existing call and real session tracker over the piped AMI connection. The queue answer is withheld until all
/// earlier list barriers have completed. Each held queue fence gets its own manual barrier clock; visit time is a
/// separate clock, advanced without expiring that fence. Cleanup releases both dispatch holds before awaiting owners.
/// </summary>
internal sealed class QueueAdmissionRun : IAsyncDisposable
{
    public const string Uid = "1790633830.31";
    public const string Caller = "PJSIP/pstn-00000031";
    public const string Queue = BootingAsterisk.QueueName;
    public const string StateLoaded = "[LIVE] State loaded";
    private const string SecondGateName = "held-queue-second";

    private readonly TaskCompletionSource _queueAnswer = NewSignal();
    private readonly TaskCompletionSource _statusAnswer = NewSignal();
    private readonly DispatchGate _firstGate = new();
    private readonly DispatchGate _secondGate = new(SecondGateName);
    private readonly CompositeDisposable _subscriptions = new();
    private readonly ConcurrentQueue<AsteriskQueueEntry> _reports = new();
    private readonly ConcurrentQueue<CallQueuedEvent> _queued = new();
    private readonly TaskCompletionSource _channelDelivered = NewSignal();
    private readonly TaskCompletionSource _joinDelivered = NewSignal();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _tails = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _snapshotWaiters = new();
    private int _snapshots;

    private QueueAdmissionRun(Run run, BootingAsterisk first, BootingAsterisk peer, bool reconnect)
    {
        Run = run;
        First = first;
        Peer = peer;
        Reconnect = reconnect;
        Manager = new CallSessionManager(Options.Create(new SessionOptions()),
            NullLogger<CallSessionManager>.Instance, new NullStore(), run.Clock);
        Tracker = new QueueSessionTracker(Manager, Options.Create(new SessionOptions()));
        _subscriptions.Add(Manager.Events.Subscribe(new SessionObserver(e =>
        {
            if (e is CallQueuedEvent queued)
                _queued.Enqueue(queued);
        })));
        Manager.AttachToServer(run.Server, "srv-queue-order");
        run.Server.Queues.CallerJoined += OnReported;
        _subscriptions.Add(run.Connection.Subscribe(_firstGate));
        _subscriptions.Add(run.Connection.Subscribe(_secondGate));
    }

    public Run Run { get; }
    public BootingAsterisk First { get; }
    public BootingAsterisk Peer { get; }
    public bool Reconnect { get; }
    public CallSessionManager Manager { get; }
    public QueueSessionTracker Tracker { get; }
    public LiveCounters Counters { get; private set; } = null!;
    public Task ActiveLoad { get; private set; } = Task.CompletedTask;
    public FakeTimeProvider BarrierClock { get; private set; } = new();
    public DateTimeOffset RowReadAt { get; private set; }
    public IReadOnlyList<CallQueuedEvent> Queued => _queued.ToArray();
    public IReadOnlyList<AsteriskQueueEntry> Reports => _reports.ToArray();
    public bool GateTimedOut => _firstGate.TimedOut || _secondGate.TimedOut;
    public int LoadCount => Reconnect ? 2 : 1;

    public static async Task<QueueAdmissionRun> PrepareAsync(bool reconnect = true, bool holdStatus = false,
        CancellationToken ct = default)
    {
        var first = new BootingAsterisk { BootedAtLogin = true };
        var peer = reconnect ? new BootingAsterisk { BootedAtLogin = true } : first;
        var run = await Run.ConnectAsync(first, reconnect ? peer : null, autoReconnect: reconnect);
        var rig = new QueueAdmissionRun(run, first, peer, reconnect);
        try
        {
            if (reconnect)
            {
                await run.StartServerAsync();
                rig.SubscribeTail();
                await first.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Uid, Caller, Uid));
                await rig._channelDelivered.Task.WaitAsync(Run.Bound, CancellationToken.None);
            }
            else
            {
                // A first load can already hold a channel and a session through these public entry points.
                run.Server.Channels.OnNewChannel(Uid, Caller, ChannelState.Up, context: "from-pstn", linkedId: Uid);
            }

            peer.StatusChannels = [new StatusChannel(Uid, Caller, Uid)];
            if (holdStatus)
                peer.StatusAnsweredAfter = rig._statusAnswer.Task;
            peer.QueueEntries = [new QueueCaller(Caller, Uid, Position: 1, Wait: 42)];
            peer.AnsweredAfter = new Dictionary<string, Task> { ["QueueStatus"] = rig._queueAnswer.Task };
            rig.Counters = new LiveCounters();
            if (reconnect)
            {
                first.CloseSession();
                await run.SecondSocketCreated.WaitAsync(Run.Bound, CancellationToken.None);
                run.ReleaseSecondPeer();
                rig.ActiveLoad = run.ServerLog.Logged(StateLoaded, 2);
            }
            else
            {
                rig.ActiveLoad = run.Server.StartAsync(ct);
                rig.SubscribeTail();
            }

            // The load asks QueueStatus only after Status, bridges and conferences reconciled behind their fences.
            await peer.AskedAtLeast(holdStatus ? "Status" : "QueueStatus", 1).WaitAsync(Run.Bound, CancellationToken.None);
            rig.NewBarrierClock();
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    public void NewBarrierClock()
    {
        BarrierClock = new FakeTimeProvider(Run.Clock.GetUtcNow());
        Run.Server.BarrierClock = BarrierClock;
    }

    public async Task<FakeTimeProvider.FakeTimer> HoldQueueAsync(bool liveJoin = true, int livePosition = 7)
    {
        await Peer.SendEventAsync("UserEvent", DispatchGate.Frame());
        await _firstGate.Held.WaitAsync(Run.Bound);
        if (liveJoin)
            await Peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(Uid, Caller, Queue, livePosition, 1));
        RowReadAt = Run.Clock.GetUtcNow();
        _queueAnswer.TrySetResult();
        return await BarrierClock.TimersCreated.ReadAsync().AsTask().WaitAsync(Run.Bound);
    }

    public async Task ExpireStatusThenHoldTheLaterQueueFenceAsync()
    {
        await Peer.SendEventAsync("UserEvent", DispatchGate.Frame());
        await _firstGate.Held.WaitAsync(Run.Bound);
        _statusAnswer.TrySetResult();
        var statusTimer = await BarrierClock.TimersCreated.ReadAsync().AsTask().WaitAsync(Run.Bound);
        BarrierClock.Advance(statusTimer.DueTime);
        await Peer.AskedAtLeast("QueueStatus", 1).WaitAsync(Run.Bound);
        // These ordinals are later than StatusComplete. Releasing the first gate can reach Status's mark while the
        // later named gate and live join still hold the queue mark: the two fences are observably different.
        await Peer.SendEventAsync("UserEvent", DispatchGate.Frame(SecondGateName));
        await Peer.SendEventAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(Uid, Caller, Queue, 7, 1));
        _queueAnswer.TrySetResult();
        await ActiveLoad.WaitAsync(Run.Bound);
    }

    public void Release() => _firstGate.Release();
    public void ReleaseSecond() => _secondGate.Release();
    public void AnswerQueue() => _queueAnswer.TrySetResult();

    public async Task FinishAsync(bool liveJoin = true)
    {
        Release();
        await ActiveLoad.WaitAsync(Run.Bound);
        if (liveJoin)
            await _joinDelivered.Task.WaitAsync(Run.Bound);
    }

    public async Task ExpireAsync(FakeTimeProvider.FakeTimer timer)
    {
        BarrierClock.Advance(timer.DueTime);
        await ActiveLoad.WaitAsync(Run.Bound);
        await Run.ServerLog.Logged("[LIVE] A load's wait for the event pump ran out", 1).WaitAsync(Run.Bound);
    }

    public Task SnapshotReportsAsync(int count)
    {
        var signal = _snapshotWaiters.GetOrAdd(count, static _ => NewSignal());
        if (Volatile.Read(ref _snapshots) >= count)
            signal.TrySetResult();
        return signal.Task.WaitAsync(Run.Bound);
    }

    public Task TailAsync(string userEvent)
    {
        var signal = _tails.GetOrAdd(userEvent, static _ => NewSignal());
        return signal.Task.WaitAsync(Run.Bound);
    }

    public Task<bool> SendTailAsync(string name) => Peer.SendEventAsync("UserEvent",
        [new("Privilege", "user,all"), new("UserEvent", name)]);

    public async Task<FakeTimeProvider.FakeTimer> HoldSecondQueueAsync(TaskCompletionSource answer)
    {
        await Peer.AskedAtLeast("QueueStatus", 2).WaitAsync(Run.Bound);
        NewBarrierClock();
        await Peer.SendEventAsync("UserEvent", DispatchGate.Frame(SecondGateName));
        await _secondGate.Held.WaitAsync(Run.Bound);
        answer.TrySetResult();
        return await BarrierClock.TimersCreated.ReadAsync().AsTask().WaitAsync(Run.Bound);
    }

    public object Visit() => new
    {
        CallQueuedEvents = Queued.Count,
        Offered = Tracker.GetByQueueName(Queue)?.CallsOffered ?? 0,
        Waiting = Tracker.GetByQueueName(Queue)?.CallsWaiting ?? 0,
        Abandoned = Tracker.GetByQueueName(Queue)?.CallsAbandoned ?? 0,
        Held = Run.Server.Queues.GetByName(Queue)?.EntryCount ?? 0,
        Counters.Joined,
        Counters.Left,
        GateTimedOut,
    };

    public static object OneVisit => new
    {
        CallQueuedEvents = 1, Offered = 1, Waiting = 1, Abandoned = 0, Held = 1,
        Joined = 1L, Left = 0L, GateTimedOut = false,
    };

    public async ValueTask DisposeAsync()
    {
        _queueAnswer.TrySetResult();
        _statusAnswer.TrySetResult();
        _firstGate.Release();
        _secondGate.Release();
        using (_secondGate)
        using (_firstGate)
        {
            try
            {
                using (_subscriptions)
                {
                    await Run.DisposeAsync();
                    await Run.Server.ScheduledReload.WaitAsync(Run.Bound);
                }
            }
            finally
            {
                Tracker.Dispose();
                await Manager.DisposeAsync();
                Counters?.Dispose();
            }
        }
        // This assertion intentionally propagates cleanup failures to xunit.
        new { First = First.Fault?.Message, Peer = Peer.Fault?.Message }.Should().BeEquivalentTo(
            new { First = default(string), Peer = default(string) }, "the peer must also finish without a hidden failure");
        Run.Server.Queues.ActiveSnapshotWindowCount.Should().Be(0,
            "teardown leaves no retained queue read window after the deferred owner ended");
    }

    private void OnReported(string queue, AsteriskQueueEntry entry)
    {
        if (queue != Queue)
            return;
        _reports.Enqueue(entry);
        if (!entry.FromSnapshot)
            return;
        var count = Interlocked.Increment(ref _snapshots);
        foreach (var (target, signal) in _snapshotWaiters)
        {
            if (count >= target)
                signal.TrySetResult();
        }
    }

    private void SubscribeTail() => _subscriptions.Add(Run.Connection.Subscribe(new AmiObserver(e =>
    {
        switch (e)
        {
            case NewChannelEvent channel when channel.UniqueId == Uid:
                _channelDelivered.TrySetResult();
                break;
            case QueueCallerJoinEvent join when join.UniqueId == Uid:
                _joinDelivered.TrySetResult();
                break;
            case UserEventEvent user when user.UserEvent is { } name:
                _tails.GetOrAdd(name, static _ => NewSignal()).TrySetResult();
                break;
        }
    })));

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class AmiObserver(Action<ManagerEvent> next) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class SessionObserver(Action<SessionDomainEvent> next) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class NullStore : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) => ValueTask.CompletedTask;
        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) => ValueTask.FromResult<CallSession?>(null);
    }
}
