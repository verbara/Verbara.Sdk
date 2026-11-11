using System.Collections.Concurrent;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Live.Tests.Harness;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// The loss mark the session engine stamps on every notification (<see cref="SessionDomainEvent.Epoch"/>): over the
/// SDK's <see cref="Verbara.Sdk.Ami.Connection.AmiConnection"/> every kind of notification carries one, a call with no
/// loss carries equal marks, each server carries its own connection's, and a call queued from a reload's queue snapshot
/// carries the mark its row was sampled with; over a connection that is not the SDK's, and for a wrap-up whose server
/// was detached, the mark is <see langword="null"/> and never one that reads as complete.
/// </summary>
/// <remarks>
/// The tests run here, not in <c>Verbara.Sdk.Sessions.Tests</c>: the real connection over the in-memory harness
/// (<see cref="Run"/>, <see cref="BootingAsterisk"/>) lives in this project, which sees Live's internals (the server's
/// clock seam the harness sets, a queue entry's sampled epoch) and the session engine's (its clock, for the wrap-up
/// timer). Every frame goes over the wire and through the connection's pump, so a notification raised by a handler is
/// raised inside its event's dispatch. Every wait is bounded by <see cref="Run.Bound"/> and ends on its signal.
/// </remarks>
public sealed class SessionEventEpochStampTests
{
    private const string ServerId = "srv-epoch";
    private const string Caller = "1700000100.1";
    private const string CallerChannel = "PJSIP/pstn-00000001";
    private const string Member = "PJSIP/agent-2001";
    private const string MemberLeg = "1700000100.2";
    private const string MemberChannel = "PJSIP/agent-2001-00000002";
    private const string Queue = BootingAsterisk.QueueName;

    [Fact]
    public async Task Events_ShouldCarryANonNullEpoch_WhenEveryNotificationKindIsRaisedOverAmiConnection()
    {
        await using var rig = await EpochRig.StartAsync();
        var second = rig.Subscribe();

        await rig.SendAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        await rig.First.WaitAsync<CallStartedEvent>();
        await rig.SendAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(Caller, CallerChannel, Queue, 1, 1));
        await rig.First.WaitAsync<CallQueuedEvent>();
        await rig.SendAsync("AgentConnect", Frames.AgentConnect());
        await rig.First.WaitAsync<CallConnectedEvent>();
        await rig.SendAsync("Hold", Frames.Hold());
        await rig.First.WaitAsync<CallHeldEvent>();
        await rig.SendAsync("Unhold", Frames.Hold());
        await rig.First.WaitAsync<CallResumedEvent>();
        await rig.SendAsync("BridgeCreate", AsteriskFrames.BridgeCreate("bridge-1"));
        await rig.SendAsync("BridgeEnter", AsteriskFrames.BridgeEnter("bridge-1", 1, Caller, CallerChannel, Caller));
        await rig.SendAsync("BlindTransfer", Frames.BlindTransfer("bridge-1"));
        await rig.First.WaitAsync<CallTransferredEvent>();
        await rig.SendAsync("AgentComplete", Frames.AgentComplete());
        await rig.SendAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
        await rig.First.WaitAsync<CallEndedEvent>();
        rig.WrapUpClock.Advance(new SessionOptions().WrapUpDuration);
        await rig.First.WaitAsync<CallWrapUpEvent>();
        await rig.SendAsync("Newchannel", AsteriskFrames.Newchannel("1700000100.5", "PJSIP/pstn-00000005", "1700000100.5"));
        await rig.First.WaitAsync<CallStartedEvent>(e => e.SessionId != rig.First.First<CallStartedEvent>().SessionId);
        rig.Manager.DetachFromServer(ServerId, DetachedCalls.Release);
        await rig.First.WaitAsync<CallReleasedEvent>();

        var seen = rig.First.Seen;
        var connection = rig.Run.Server.ConnectionIdentity;
        using (new AssertionScope())
        {
            seen.Select(s => s.Notification.GetType().Name).Distinct().Should().BeEquivalentTo(
                [
                    nameof(CallStartedEvent), nameof(CallQueuedEvent), nameof(CallConnectedEvent), nameof(CallHeldEvent),
                    nameof(CallResumedEvent), nameof(CallTransferredEvent), nameof(CallEndedEvent),
                    nameof(CallWrapUpEvent), nameof(CallReleasedEvent),
                ], "premise: the call and the released call raised every kind of notification");
            seen.Should().OnlyContain(s => s.Epoch != null,
                "every notification raised over the SDK's connection carries its server's loss mark");
            seen.Where(s => s.Epoch != null).Should().OnlyContain(s => s.Epoch!.Value.ConnectionId == connection,
                "every mark names the connection of the notification's own server");
            second.Seen.Select(s => (s.Notification, s.Epoch)).Should().Equal(seen.Select(s => (s.Notification, s.Epoch)),
                "the mark is set before the first subscriber sees the notification, so every subscriber reads the same one");
        }
    }

    [Fact]
    public async Task Events_ShouldCarryANullEpoch_WhenTheConnectionIsNotAmiConnection()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer);
        await using var server = new VerbaraServer(new PlainAmiConnectionWrapper(run.Connection),
            NullLogger<VerbaraServer>.Instance) { TimeProvider = run.Clock };
        await server.StartAsync().WaitAsync(Run.Bound);
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(server, ServerId);

        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        await recorder.WaitAsync<CallStartedEvent>();
        await peer.SendEventAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
        await recorder.WaitAsync<CallEndedEvent>();

        using (new AssertionScope())
        {
            recorder.Seen.Should().HaveCount(2, "premise: the call started and ended");
            recorder.Seen.Should().OnlyContain(s => s.Epoch == null,
                "a connection that is not the SDK's reports no drops and no ordered session: the engine cannot tell");
        }
    }

    [Fact]
    public async Task CallWrapUp_ShouldCarryANullEpoch_WhenItsServerWasDetached()
    {
        await using var rig = await EpochRig.StartAsync();
        await rig.SendAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        await rig.SendAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(Caller, CallerChannel, Queue, 1, 1));
        await rig.SendAsync("AgentConnect", Frames.AgentConnect());
        await rig.SendAsync("AgentComplete", Frames.AgentComplete());
        await rig.SendAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
        var ended = await rig.First.WaitAsync<CallEndedEvent>();

        rig.Manager.DetachFromServer(ServerId);
        rig.WrapUpClock.Advance(new SessionOptions().WrapUpDuration);
        var wrapUp = await rig.First.WaitAsync<CallWrapUpEvent>();

        using (new AssertionScope())
        {
            ended.Epoch.Should().NotBeNull("premise: the call ended while its server was attached over the SDK's connection");
            wrapUp.Epoch.Should().BeNull(
                "the wrap-up's server is no longer attached and no attachment of it raised the wrap-up: the engine cannot tell");
        }
    }

    [Fact]
    public async Task Events_ShouldCarryEqualEpochs_WhenACallHasNoLoss()
    {
        await using var rig = await EpochRig.StartAsync();

        await rig.SendAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var started = await rig.First.WaitAsync<CallStartedEvent>();
        await rig.SendAsync("QueueCallerJoin", AsteriskFrames.QueueCallerJoin(Caller, CallerChannel, Queue, 1, 1));
        await rig.SendAsync("AgentConnect", Frames.AgentConnect());
        var connected = await rig.First.WaitAsync<CallConnectedEvent>();
        await rig.SendAsync("Hangup", AsteriskFrames.Hangup(Caller, CallerChannel, Caller));
        var ended = await rig.First.WaitAsync<CallEndedEvent>();

        using (new AssertionScope())
        {
            started.Epoch.Should().NotBeNull("the call started over the SDK's connection");
            SessionEventEpoch.LossBetween(started.Epoch, connected.Epoch).Should().Be(SessionEventLoss.NoneKnown,
                "the connection neither reconnected nor dropped an event between the call's start and its connect");
            SessionEventEpoch.LossBetween(connected.Epoch, ended.Epoch).Should().Be(SessionEventLoss.NoneKnown,
                "nor between its connect and its end");
            started.Epoch.Should().Be(ended.Epoch, "the three notifications carry one mark");
        }
    }

    [Fact]
    public async Task Events_ShouldCarryTheirOwnServersEpoch_WhenTwoServersAreAttached()
    {
        var peerA = new BootingAsterisk { BootedAtLogin = true };
        var peerB = new BootingAsterisk { BootedAtLogin = true };
        await using var runA = await Run.ConnectAsync(peerA, eventPumpCapacity: 1);
        await runA.StartServerAsync();
        await using var runB = await Run.StartAsync(peerB);
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(runA.Server, "srv-a");
        manager.AttachToServer(runB.Server, "srv-b");
        await EpochRig.DropOneEventAsync(runA, peerA);

        await peerA.SendEventAsync("Newchannel", AsteriskFrames.Newchannel("1700000200.1", "PJSIP/a-00000001", "1700000200.1"));
        await peerB.SendEventAsync("Newchannel", AsteriskFrames.Newchannel("1700000300.1", "PJSIP/b-00000001", "1700000300.1"));
        var onA = await recorder.WaitAsync<CallStartedEvent>(e => e.ServerId == "srv-a");
        var onB = await recorder.WaitAsync<CallStartedEvent>(e => e.ServerId == "srv-b");

        using (new AssertionScope())
        {
            onA.Epoch.Should().NotBeNull("server A runs over the SDK's connection");
            onB.Epoch.Should().NotBeNull("server B runs over the SDK's connection");
            onA.Epoch?.ConnectionId.Should().Be(runA.Server.ConnectionIdentity!.Value, "A's notification names A's connection");
            onB.Epoch?.ConnectionId.Should().Be(runB.Server.ConnectionIdentity!.Value, "B's notification names B's connection");
            onA.Epoch?.ConnectionId.Should().NotBe(onB.Epoch?.ConnectionId ?? Guid.Empty, "the two connections are two instances");
            onA.Epoch?.DroppedBefore.Should().Be(1, "A's call arrived after A's connection dropped one event");
            onB.Epoch?.DroppedBefore.Should().Be(0, "B's connection dropped nothing: A's drop is not B's");
        }
    }

    [Fact]
    public async Task CallQueued_ShouldCarryTheRowsSampledMark_WhenAdmittedFromAQueueSnapshot()
    {
        // The call starts live; a reload then lists it still up and waiting in the queue. After the queue row has been
        // read (and its epoch sampled on the reload's thread) and before the listing completes, the connection drops
        // one event. The completion is delivered after the drop, so a read at the raise, which follows the completion's
        // delivery, counts the drop the row's sample does not: the mark the notification carries tells the two apart.
        const int capacity = 8;
        var peer = new BootingAsterisk { BootedAtLogin = true };
        await using var run = await Run.ConnectAsync(peer, eventPumpCapacity: capacity);
        await run.StartServerAsync();
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        var rows = new ConcurrentQueue<AsteriskQueueEntry>();
        run.Server.Queues.CallerJoined += (_, entry) => { if (entry.FromSnapshot) rows.Enqueue(entry); };
        manager.AttachToServer(run.Server, ServerId);
        await peer.SendEventAsync("Newchannel", AsteriskFrames.Newchannel(Caller, CallerChannel, Caller));
        var started = await recorder.WaitAsync<CallStartedEvent>();

        var buffered = 0;
        var entryBuffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Server.QueueRowBuffered = () =>
        {
            // QueueParams, QueueMember, then the caller's QueueEntry.
            if (Interlocked.Increment(ref buffered) == 3)
                entryBuffered.TrySetResult();
        };
        peer.StatusChannels = [new StatusChannel(Caller, CallerChannel, Caller)];
        peer.QueueEntries = [new QueueCaller(CallerChannel, Caller, Position: 1, Wait: 5)];
        peer.BeforeQueueStatusComplete = async () =>
        {
            await entryBuffered.Task.WaitAsync(Run.Bound);
            await EpochRig.DropOneEventWhilePeerIsBusyAsync(run, peer, capacity);
        };
        await run.Server.RequestInitialStateAsync().AsTask().WaitAsync(Run.Bound);
        var queued = await recorder.WaitAsync<CallQueuedEvent>();
        var startedSide = run.Server.ReadSessionLossMark();

        using (new AssertionScope())
        {
            rows.Should().ContainSingle("premise: the reload's queue snapshot listed the caller once");
            var row = rows.Single().LossEpoch;
            row.Should().NotBeNull("premise: a snapshot row carries the epoch sampled when it was read");
            row!.DroppedBefore.Should().Be(0, "premise: the row was read before the drop");
            startedSide!.Value.DroppedBefore.Should().Be(1,
                "premise: the listing's completion, delivered after the drop, started its delivery before the raise");
            queued.Epoch.Should().Be(new SessionEventEpoch(run.Server.ConnectionIdentity!.Value, row.AmiSession,
                    row.DroppedBefore, started.Epoch?.Attachment ?? -1),
                "a call queued from a snapshot carries the mark its row was sampled with, composed with its connection "
                + "and its attachment");
        }
    }

    [Fact]
    public async Task SnapshotAdmittedCallStarted_ShouldCarryANullEpochAndKeepItsReloadProvenance_WhenTheConnectionIsNotAmiConnection()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true, StatusChannels = [new StatusChannel(Caller, CallerChannel, Caller)] };
        await using var run = await Run.ConnectAsync(peer);
        await using var server = new VerbaraServer(new PlainAmiConnectionWrapper(run.Connection),
            NullLogger<VerbaraServer>.Instance) { TimeProvider = run.Clock };
        await using var manager = EpochRig.NewManager(new FakeTimeProvider());
        var recorder = EpochRecorder.On(manager);
        manager.AttachToServer(server, ServerId);

        await server.StartAsync().WaitAsync(Run.Bound);
        var started = await recorder.WaitAsync<CallStartedEvent>();
        var call = manager.GetById(started.SessionId);

        using (new AssertionScope())
        {
            started.Epoch.Should().BeNull(
                "a connection that is not the SDK's yields no request mark, and no default mark stands in for one");
            call.Should().NotBeNull("premise: the snapshot admitted the call");
            call?.State.Should().Be(CallSessionState.Connected, "the call keeps the state the snapshot reported");
            call?.Metadata.GetValueOrDefault("origin").Should().Be("reload", "the call keeps its reload provenance");
        }
    }

    /// <summary>The frames of a queue call that the shared <see cref="AsteriskFrames"/> does not carry.</summary>
    private static class Frames
    {
        public static KeyValuePair<string, string>[] AgentConnect() =>
        [
            new("Privilege", "agent,all"), new("Channel", CallerChannel), new("Uniqueid", Caller), new("Linkedid", Caller),
            new("DestChannel", MemberChannel), new("DestUniqueid", MemberLeg), new("DestLinkedid", Caller),
            new("Queue", Queue), new("Interface", Member), new("MemberName", Member), new("HoldTime", "0"),
            new("RingTime", "1"),
        ];

        public static KeyValuePair<string, string>[] AgentComplete() =>
        [
            new("Privilege", "agent,all"), new("Channel", CallerChannel), new("Uniqueid", Caller), new("Linkedid", Caller),
            new("DestChannel", MemberChannel), new("DestUniqueid", MemberLeg), new("DestLinkedid", Caller),
            new("Queue", Queue), new("Interface", Member), new("MemberName", Member), new("HoldTime", "0"),
            new("TalkTime", "12"), new("Reason", "agent"),
        ];

        public static KeyValuePair<string, string>[] Hold() =>
        [
            new("Privilege", "call,all"), new("Channel", CallerChannel), new("ChannelState", "6"),
            new("ChannelStateDesc", "Up"), new("Uniqueid", Caller), new("Linkedid", Caller), new("MusicClass", "default"),
        ];

        public static KeyValuePair<string, string>[] BlindTransfer(string bridgeId) =>
        [
            new("Privilege", "call,all"), new("Result", "Success"), new("TransfererChannel", MemberChannel),
            new("TransfererUniqueid", MemberLeg), new("TransfereeChannel", CallerChannel),
            new("TransfereeUniqueid", Caller), new("BridgeUniqueid", bridgeId), new("BridgeType", "basic"),
            new("IsExternal", "No"), new("Context", "from-internal"), new("Extension", "4022"),
        ];
    }
}

/// <summary>
/// A session engine over one server on the SDK's connection, served by a <see cref="BootingAsterisk"/>; the engine's own
/// clock (<see cref="WrapUpClock"/>) runs its wrap-up timers, and a recorder subscribed before the attach sees every
/// notification.
/// </summary>
internal sealed class EpochRig : IAsyncDisposable
{
    private EpochRig(Run run, BootingAsterisk peer)
    {
        Run = run;
        Peer = peer;
        Manager = NewManager(WrapUpClock);
        First = EpochRecorder.On(Manager);
    }

    public Run Run { get; }
    public BootingAsterisk Peer { get; }
    public FakeTimeProvider WrapUpClock { get; } = new();
    public CallSessionManager Manager { get; }

    /// <summary>The first subscriber.</summary>
    public EpochRecorder First { get; }

    public static CallSessionManager NewManager(TimeProvider clock) =>
        new(Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new NullStore(), clock);

    public static async Task<EpochRig> StartAsync()
    {
        var peer = new BootingAsterisk { BootedAtLogin = true };
        var run = await Run.StartAsync(peer);
        var rig = new EpochRig(run, peer);
        rig.Manager.AttachToServer(run.Server, "srv-epoch");
        return rig;
    }

    /// <summary>Another subscriber, after the first.</summary>
    public EpochRecorder Subscribe() => EpochRecorder.On(Manager);

    public Task<bool> SendAsync(string eventType, IEnumerable<KeyValuePair<string, string>> fields) =>
        Peer.SendEventAsync(eventType, fields);

    /// <summary>
    /// On a connection whose pump holds one event: holds the dispatch inside an observer, queues one event behind it,
    /// sends one more, which the full buffer refuses, waits until the connection has read it (a Ping answered after it),
    /// then releases the dispatch and waits until the queued event has been delivered.
    /// </summary>
    public static async Task DropOneEventAsync(Run run, BootingAsterisk peer)
    {
        using var gate = new DispatchGate();
        var queuedDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var held = run.Connection.Subscribe(gate);
        using var tail = run.Connection.Subscribe(new AmiObserver(e =>
        {
            if (e is UserEventEvent { UserEvent: "queued-before-drop" })
                queuedDelivered.TrySetResult();
        }));
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame());
        await gate.Held.WaitAsync(Run.Bound);
        await peer.SendEventAsync("UserEvent", [new("Privilege", "user,all"), new("UserEvent", "queued-before-drop")]);
        await peer.SendEventAsync("UserEvent", [new("Privilege", "user,all"), new("UserEvent", "dropped")]);
        await run.Connection.SendActionAsync(new PingAction()).AsTask().WaitAsync(Run.Bound);
        gate.Release();
        await queuedDelivered.Task.WaitAsync(Run.Bound);
    }

    /// <summary>
    /// <see cref="DropOneEventAsync"/> while the peer is inside an answer and cannot answer a Ping: holds the dispatch,
    /// fills the pump's <paramref name="capacity"/> behind it, sends one more event, which the full buffer refuses, and
    /// waits until the connection has logged that refusal, which it does once it has counted it; then releases the
    /// dispatch and waits until the buffer has been delivered.
    /// </summary>
    public static async Task DropOneEventWhilePeerIsBusyAsync(Run run, BootingAsterisk peer, int capacity)
    {
        using var gate = new DispatchGate();
        var lastDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var last = "fill-" + capacity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var held = run.Connection.Subscribe(gate);
        using var tail = run.Connection.Subscribe(new AmiObserver(e =>
        {
            if (e is UserEventEvent user && user.UserEvent == last)
                lastDelivered.TrySetResult();
        }));
        const string refused = "[AMI_EVENT] Dropped: event_type=UserEvent";
        var refusedBefore = run.ConnectionLog.Entries.Count(e => e.Line.Contains(refused, StringComparison.Ordinal));
        await peer.SendEventAsync("UserEvent", DispatchGate.Frame());
        await gate.Held.WaitAsync(Run.Bound);
        for (var i = 1; i <= capacity; i++)
            await peer.SendEventAsync("UserEvent",
                [new("Privilege", "user,all"), new("UserEvent", "fill-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
        await peer.SendEventAsync("UserEvent", [new("Privilege", "user,all"), new("UserEvent", "dropped")]);
        // The connection logs the refusal of the first event of a full-buffer episode once it has counted it.
        await run.ConnectionLog.Logged(refused, refusedBefore + 1).WaitAsync(Run.Bound);
        gate.Release();
        await lastDelivered.Task.WaitAsync(Run.Bound);
    }

    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        await Run.DisposeAsync();
    }

    private sealed class AmiObserver(Action<ManagerEvent> next) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class NullStore : SessionStoreBase
    {
        public override ValueTask SaveAsync(CallSession session, CancellationToken ct) => ValueTask.CompletedTask;
        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult<CallSession?>(null);
    }
}

/// <summary>
/// Records every notification a subscriber receives, with the mark it read at that moment, and lets a test wait for the
/// first one of a kind that matches.
/// </summary>
internal sealed class EpochRecorder : IObserver<SessionDomainEvent>
{
    private readonly Lock _gate = new();
    private readonly List<(SessionDomainEvent Notification, SessionEventEpoch? Epoch)> _seen = [];
    private readonly List<(Func<SessionDomainEvent, bool> Match, TaskCompletionSource<SessionDomainEvent> Signal)> _waiters = [];

    public static EpochRecorder On(CallSessionManager manager)
    {
        var recorder = new EpochRecorder();
        manager.Events.Subscribe(recorder);
        return recorder;
    }

    public IReadOnlyList<(SessionDomainEvent Notification, SessionEventEpoch? Epoch)> Seen
    {
        get
        {
            lock (_gate)
                return [.. _seen];
        }
    }

    public T First<T>() where T : SessionDomainEvent => Seen.Select(s => s.Notification).OfType<T>().First();

    public async Task<T> WaitAsync<T>(Func<T, bool>? match = null) where T : SessionDomainEvent
    {
        bool Matches(SessionDomainEvent e) => e is T typed && (match is null || match(typed));
        TaskCompletionSource<SessionDomainEvent> signal;
        lock (_gate)
        {
            foreach (var (notification, _) in _seen)
                if (Matches(notification))
                    return (T)notification;
            signal = new TaskCompletionSource<SessionDomainEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((Matches, signal));
        }
        return (T)await signal.Task.WaitAsync(Run.Bound);
    }

    public void OnNext(SessionDomainEvent value)
    {
        lock (_gate)
        {
            _seen.Add((value, value.Epoch));
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (!_waiters[i].Match(value))
                    continue;
                _waiters[i].Signal.TrySetResult(value);
                _waiters.RemoveAt(i);
            }
        }
    }

    public void OnError(Exception error) { }

    public void OnCompleted() { }
}
