using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Tests.Connection;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// The collection of the classes in this assembly that read the process-wide <c>ami.events.dropped</c> counter. Declared
/// with <c>DisableParallelization</c>, so no other class drops events while one of them counts.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AmiEventsDroppedGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "ami-events-dropped";
}

/// <summary>
/// A caller that leaves a queue by pressing a key is counted a key exit when the only events the connection's full
/// buffer dropped during the visit were copies of an answer an action had already received: one offered, none
/// answered, none abandoned. A dropped event no action received is a real loss, and the same leave is then counted
/// abandoned, because its abandon report may have been among the lost events.
/// </summary>
/// <remarks>
/// <para>
/// The whole path is real: a <see cref="AmiConnection"/> with an event buffer of <see cref="KeyExitRig.Capacity"/>, reading a piped
/// peer that plays Asterisk; a <see cref="VerbaraServer"/> on it, whose event observer stamps each queue report with the
/// event-loss epoch it reads during that event's dispatch; a <see cref="CallSessionManager"/> attached to the server,
/// which compares the leave's epoch with the join's; and a <see cref="QueueSessionTracker"/> counting the visit.
/// </para>
/// <para>
/// The test holds the dispatch of one <c>PeerStatus</c> in an <c>OnEvent</c> handler and fills the buffer behind it, so
/// every later event is refused (<see cref="KeyExitRig"/>). Whatever the peer writes is read before a Ping is answered, so the Ping is the fence.
/// No wall clock is read except <see cref="Bound"/>, a hang bound every wait ends long before.
/// </para>
/// </remarks>
[Collection(AmiEventsDroppedGroup.Name)]
public sealed class KeyExitAfterDroppedAnswerCopiesTests
{
    private static readonly TimeSpan Bound = KeyExitRig.Bound;
    private const string Queue = KeyExitRig.Queue;
    private const string CallerUid = KeyExitRig.CallerUid;
    private const string CallerChannel = KeyExitRig.CallerChannel;

    /// <summary>What the queue counted for the visit, and what the buffer dropped.</summary>
    private sealed record Counted(int Offered, int Answered, int Abandoned, int TimedOut, int Waiting, long Dropped);

    [Fact]
    public async Task KeyExit_ShouldCountNeitherAnsweredNorAbandoned_WhenTheFullBufferDroppedOnlyCopiesOfAnAnswerTheActionReceived()
    {
        using var drops = new BufferFullDrops();
        await using var rig = await KeyExitRig.StartAsync();
        using var tracker = new QueueSessionTracker(rig.Manager, Options.Create(new SessionOptions()));
        await rig.JoinQueueAsync();
        await rig.HoldAndFillAsync();

        var read = ReadAllAsync(rig.Connection.SendEventGeneratingActionAsync(new StatusAction()));
        var id = await rig.StatusAsked.Task.WaitAsync(Bound);
        await rig.Peer.RespondAsync("Success", id, [new("EventList", "start")]);
        await rig.Peer.WriteEventAsync("Status", StatusEntry(id, CallerChannel, CallerUid));
        await rig.Peer.WriteEventAsync("Status", StatusEntry(id, "PJSIP/agent-00000001", "1795000000.1"));
        await rig.Peer.WriteEventAsync("Status", StatusEntry(id, "PJSIP/agent-00000002", "1795000000.2"));
        await rig.FenceAsync();

        await rig.ReleaseAsync();
        await rig.Peer.WriteEventAsync("StatusComplete", [new("ActionID", id), new("EventList", "Complete"), new("ListItems", "3")]);
        var received = await read.WaitAsync(Bound);
        await rig.LeaveByKeyAndHangUpAsync();

        using (new AssertionScope())
        {
            received.Should().HaveCount(3, "premise: the action received its three entries, whatever the buffer did with their copies");
            CountedOf(tracker, drops).Should().Be(
                new Counted(Offered: 1, Answered: 0, Abandoned: 0, TimedOut: 0, Waiting: 0, Dropped: 3),
                "the buffer dropped only copies of the snapshot's answer, which the action had received: no event was " +
                "lost, so the leave with no abandon report is a key exit, while the drop metric still counts every copy");
        }
    }

    [Fact]
    public async Task KeyExit_ShouldCountTheVisitAbandoned_WhenTheFullBufferDroppedEventsNoActionReceived()
    {
        using var drops = new BufferFullDrops();
        await using var rig = await KeyExitRig.StartAsync();
        using var tracker = new QueueSessionTracker(rig.Manager, Options.Create(new SessionOptions()));
        await rig.JoinQueueAsync();
        await rig.HoldAndFillAsync();

        for (var i = 1; i <= 3; i++)
        {
            await rig.Peer.WriteEventAsync("Newstate",
            [
                new("Channel", $"PJSIP/agent-0000000{i}"), new("ChannelState", "5"), new("ChannelStateDesc", "Ringing"),
                new("Uniqueid", $"1795000000.{i}"), new("Linkedid", $"1795000000.{i}"),
            ]);
        }

        await rig.FenceAsync();
        await rig.ReleaseAsync();
        await rig.LeaveByKeyAndHangUpAsync();

        CountedOf(tracker, drops).Should().Be(
            new Counted(Offered: 1, Answered: 0, Abandoned: 1, TimedOut: 0, Waiting: 0, Dropped: 3),
            "the three dropped events were sent unsolicited and no action received them: they are lost, the visit's " +
            "abandon report may have been among them, and the leave is counted abandoned, not a key exit");
    }

    private static Counted CountedOf(QueueSessionTracker tracker, BufferFullDrops drops)
    {
        var queue = tracker.GetByQueueName(Queue);
        return new Counted(queue?.CallsOffered ?? -1, queue?.CallsAnswered ?? -1, queue?.CallsAbandoned ?? -1,
            queue?.CallsTimedOut ?? -1, queue?.CallsWaiting ?? -1, drops.Total);
    }

    private static KeyValuePair<string, string>[] StatusEntry(string actionId, string channel, string uniqueId) =>
    [
        new("ActionID", actionId), new("Channel", channel), new("ChannelState", "4"), new("ChannelStateDesc", "Ring"),
        new("CallerIDNum", "5552101"), new("Context", "from-pstn"), new("Exten", "4021"), new("Uniqueid", uniqueId),
        new("Linkedid", uniqueId),
    ];

    private static async Task<List<ManagerEvent>> ReadAllAsync(IAsyncEnumerable<ManagerEvent> events)
    {
        var received = new List<ManagerEvent>();
        await foreach (var evt in events)
            received.Add(evt);

        return received;
    }

    /// <summary>Every event dropped as a full buffer, process-wide, while this capture is alive.</summary>
    private sealed class BufferFullDrops : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        public BufferFullDrops()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Verbara.Sdk.Ami" && instrument.Name == "ami.events.dropped")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "reason" && tag.Value as string == "buffer_full")
                        Interlocked.Add(ref _total, value);
                }
            });
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }
}

/// <summary>
/// A logged-in <see cref="AmiConnection"/> over a piped peer, a started <see cref="VerbaraServer"/> on it and a
/// <see cref="CallSessionManager"/> attached to the server. The peer answers the server's first load with empty
/// listings and every Ping with <c>Success</c>, and hands each later <c>Status</c> to the test through
/// <see cref="StatusAsked"/>. An <c>OnEvent</c> handler records each dispatched event and holds the dispatch of
/// <see cref="HeldPeer"/>'s <c>PeerStatus</c> until <see cref="ReleaseAsync"/>.
/// </summary>
internal sealed class KeyExitRig : IAsyncDisposable
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>The connection's event buffer capacity.</summary>
    public const int Capacity = 2;

    public const string Queue = "support";
    public const string CallerUid = "1795000000.7";
    public const string CallerChannel = "PJSIP/trunk-00000007";
    public const string HeldPeer = "HOLD/key-exit";

    private const string ServerId = "key-exit-srv";

    private readonly PipedSocketFactory _sockets = new();
    private readonly CancellationTokenSource _peerCts = new(Bound * 6);
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _seen = new(StringComparer.Ordinal);
    private readonly IDisposable _endings;
    private volatile bool _loaded;
    private Task _serving = Task.CompletedTask;

    private KeyExitRig()
    {
        Connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = false,
            DefaultEventTimeout = TimeSpan.Zero,
            EventPumpCapacity = Capacity,
        }), _sockets, NullLogger<AmiConnection>.Instance);
        Connection.OnEvent += OnEventAsync;
        Server = new VerbaraServer(Connection, NullLogger<VerbaraServer>.Instance);
        Manager = new CallSessionManager(Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance,
            new InMemorySessionStore());
        Manager.AttachToServer(Server, ServerId);
        _endings = Manager.Events.Subscribe(new EndingObserver(_ended));
    }

    public AmiConnection Connection { get; }

    public VerbaraServer Server { get; }

    public CallSessionManager Manager { get; }

    public PipedSocket Peer { get; private set; } = null!;

    /// <summary>The ActionID of the first <c>Status</c> sent after the server's first load.</summary>
    public TaskCompletionSource<string> StatusAsked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task<KeyExitRig> StartAsync()
    {
        var rig = new KeyExitRig();
        var socket = new TaskCompletionSource<PipedSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig._serving = Task.Run(() => rig.ServeAsync(socket), CancellationToken.None);
        try
        {
            await rig.Connection.ConnectAsync().AsTask().WaitAsync(Bound);
            rig.Peer = await socket.Task.WaitAsync(Bound);
            await rig.Server.StartAsync().WaitAsync(Bound);
            rig._loaded = true;
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    /// <summary>The caller's channel appears and joins <see cref="Queue"/>; returns once both were dispatched.</summary>
    public async Task JoinQueueAsync()
    {
        var joined = Seen("QueueCallerJoin", CallerUid);
        await Peer.WriteEventAsync("Newchannel",
        [
            new("Channel", CallerChannel), new("ChannelState", "4"), new("ChannelStateDesc", "Ring"),
            new("CallerIDNum", "5552101"), new("Context", "from-pstn"), new("Exten", "4021"), new("Priority", "1"),
            new("Uniqueid", CallerUid), new("Linkedid", CallerUid),
        ]);
        await Peer.WriteEventAsync("QueueCallerJoin", QueueCaller(position: 1, count: 1));
        await joined.WaitAsync(Bound);
    }

    /// <summary>The peer sends the held <c>PeerStatus</c>, then fills the buffer behind it.</summary>
    public async Task HoldAndFillAsync()
    {
        (await Peer.WriteEventAsync("PeerStatus", [new("Peer", HeldPeer), new("PeerStatus", "Reachable")])).Should().BeTrue();
        await _held.Task.WaitAsync(Bound);
        for (var i = 0; i < Capacity; i++)
            (await Peer.WriteEventAsync("PeerStatus", [new("Peer", $"FILL/{i}"), new("PeerStatus", "Reachable")])).Should().BeTrue();
        await FenceAsync();
    }

    /// <summary>Opens the held dispatch and returns once the buffer behind it has been dispatched.</summary>
    public async Task ReleaseAsync()
    {
        var drained = Seen("PeerStatus", $"FILL/{Capacity - 1}");
        _gate.TrySetResult();
        await drained.WaitAsync(Bound);
    }

    /// <summary>
    /// Asterisk reports the caller's leave with no abandon (a key exit), then the caller hangs up in the dialplan;
    /// returns once the call's ending was published and the <c>Hangup</c> dispatched.
    /// </summary>
    public async Task LeaveByKeyAndHangUpAsync()
    {
        var hungUp = Seen("Hangup", CallerUid);
        await Peer.WriteEventAsync("QueueCallerLeave", QueueCaller(position: 1, count: 0));
        await Peer.WriteEventAsync("Hangup",
        [
            new("Channel", CallerChannel), new("ChannelState", "4"), new("ChannelStateDesc", "Ring"),
            new("Uniqueid", CallerUid), new("Linkedid", CallerUid), new("Cause", "16"), new("Cause-txt", "Normal Clearing"),
        ]);
        await hungUp.WaitAsync(Bound);
        await _ended.Task.WaitAsync(Bound);
    }

    /// <summary>A Ping answered by the peer: every event written before it has been read once it returns.</summary>
    public async Task FenceAsync() => await Connection.SendActionAsync(new PingAction()).AsTask().WaitAsync(Bound);

    public async ValueTask DisposeAsync()
    {
        _gate.TrySetResult();
        await Server.DisposeAsync().AsTask().WaitAsync(Bound);
        await Connection.DisposeAsync().AsTask().WaitAsync(Bound);
        await _peerCts.CancelAsync();
        await _serving.WaitAsync(Bound);
        _endings.Dispose();
        await Manager.DisposeAsync();
        _peerCts.Dispose();
    }

    private static KeyValuePair<string, string>[] QueueCaller(int position, int count) =>
    [
        new("Channel", CallerChannel), new("ChannelState", "4"), new("ChannelStateDesc", "Ring"),
        new("CallerIDNum", "5552101"), new("Context", "from-pstn"), new("Exten", "4021"), new("Priority", "1"),
        new("Uniqueid", CallerUid), new("Linkedid", CallerUid), new("Queue", Queue),
        new("Position", position.ToString(CultureInfo.InvariantCulture)),
        new("Count", count.ToString(CultureInfo.InvariantCulture)),
    ];

    private Task Seen(string eventType, string key) =>
        _seen.GetOrAdd(eventType + "|" + key,
            static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    private async ValueTask OnEventAsync(ManagerEvent evt)
    {
        var fields = evt.RawFields;
        var key = fields?.GetValueOrDefault("Peer") ?? fields?.GetValueOrDefault("Uniqueid") ?? "";
        _seen.GetOrAdd((evt.EventType ?? "") + "|" + key,
            static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        if (key == HeldPeer)
        {
            _held.TrySetResult();
            await _gate.Task;
        }
    }

    private async Task ServeAsync(TaskCompletionSource<PipedSocket> served)
    {
        try
        {
            var socket = await _sockets.NextAsync(_peerCts.Token);
            await socket.CompleteLoginAsync(_peerCts.Token);
            served.TrySetResult(socket);
            while (await socket.ReadActionAsync(_peerCts.Token) is { } action)
                await AnswerAsync(socket, action);
        }
        catch (OperationCanceledException) when (_peerCts.IsCancellationRequested)
        {
            served.TrySetCanceled();
        }
        catch (InvalidOperationException ex)
        {
            // The connection closed the socket before the login finished.
            served.TrySetException(ex);
        }
    }

    private async Task AnswerAsync(PipedSocket socket, string action)
    {
        var end = action.IndexOf("\r\n", StringComparison.Ordinal);
        var name = (end >= 0 ? action[..end] : action).Replace("Action:", "", StringComparison.OrdinalIgnoreCase).Trim();
        var id = PipedSocket.ActionIdOf(action);
        switch (name.ToUpperInvariant())
        {
            case "STATUS" when _loaded:
                StatusAsked.TrySetResult(id);
                break;
            case "STATUS":
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

    private sealed class EndingObserver(TaskCompletionSource ended) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value)
        {
            if (value is CallEndedEvent)
                ended.TrySetResult();
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
