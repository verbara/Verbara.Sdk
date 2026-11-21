using System.Collections.Concurrent;
using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Tests.Connection;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// The queue counters of a host that puts a decorator in front of the SDK's engine, built through the public sessions
/// registrations as the Sessions README recipe writes them: the engine registered as <see cref="CallSessionManager"/>,
/// a pass-through <see cref="ICallSessionManager"/> over it. Such a host counts each way of leaving a queue as an
/// undecorated host does — a key exit is not abandoned, a timeout is counted, the wait ends at the leave — on the
/// single-server and on the multi-server registration. A decorator over an engine the container does not know still
/// counts from the domain events only.
/// </summary>
/// <remarks>
/// The whole path is real: an <see cref="AmiConnection"/> reading a piped peer that plays Asterisk, a
/// <see cref="VerbaraServer"/> on it, the host's manager attached to it (by the single-server registration's attach, or
/// by hand under a pool id), and the <see cref="IQueueSessionTracker"/> the host resolves. A snapshot is
/// <c>offered,answered,abandoned,timedOut,waiting</c>. No wall clock is read except <see cref="QueueRig.Bound"/>, a
/// hang bound every wait ends long before.
/// </remarks>
public sealed class DecoratedQueueTrackerHostTests
{
    /// <summary>How the host's <see cref="ICallSessionManager"/> is registered.</summary>
    public enum Shape
    {
        /// <summary>No decorator: the SDK's default registration.</summary>
        Undecorated,

        /// <summary>The README recipe after the sessions call: the engine as itself, a decorator over it as the interface.</summary>
        DecoratorAfter,

        /// <summary>The same two registrations made before the sessions call.</summary>
        DecoratorBefore,

        /// <summary>The interface replaced by a decorator over an engine built with <c>new</c>, unknown to the container.</summary>
        DecoratorOverNew,
    }

    /// <summary>How the caller's queue visit ends.</summary>
    public enum VisitExit
    {
        /// <summary>The caller presses the queue's exit key: a leave with no abandon report.</summary>
        Key,

        /// <summary><c>Queue()</c>'s own timeout: the abandon report, the leave, then <c>QUEUESTATUS=TIMEOUT</c>.</summary>
        Timeout,

        /// <summary>The caller hangs up while waiting: the abandon report, then the leave.</summary>
        Abandon,

        /// <summary>The queue connects the caller: the leave, then <c>AgentConnect</c>.</summary>
        Answered,
    }

    /// <summary>What an undecorated host counts after the exit, after the connection (when there is one) and at the end.</summary>
    private static (string AtExit, string AtConnect, string AtEnd) Undecorated(VisitExit exit) => exit switch
    {
        VisitExit.Key => ("1,0,0,0,0", "1,0,0,0,0", "1,0,0,0,0"),
        VisitExit.Timeout => ("1,0,1,1,0", "1,0,1,1,0", "1,0,1,1,0"),
        VisitExit.Abandon => ("1,0,1,0,0", "1,0,1,0,0", "1,0,1,0,0"),
        VisitExit.Answered => ("1,0,0,0,0", "1,1,0,0,0", "1,1,0,0,0"),
        _ => throw new ArgumentOutOfRangeException(nameof(exit)),
    };

    /// <summary>
    /// What a host counts from the domain events alone: the visit stays waiting until its connection or the hang-up,
    /// and ends abandoned unless connected; no timeout is counted.
    /// </summary>
    private static (string AtExit, string AtConnect, string AtEnd) EventsOnly(VisitExit exit) => exit switch
    {
        VisitExit.Answered => ("1,0,0,0,1", "1,1,0,0,0", "1,1,0,0,0"),
        _ => ("1,0,0,0,1", "1,0,0,0,1", "1,0,1,0,0"),
    };

    public static TheoryData<Shape, bool, VisitExit> RegisteredEngineDecorators() =>
        Cases([Shape.DecoratorAfter, Shape.DecoratorBefore]);

    public static TheoryData<Shape, bool, VisitExit> UndecoratedHosts() => Cases([Shape.Undecorated]);

    public static TheoryData<Shape, bool, VisitExit> UnknownEngineDecorators() => Cases([Shape.DecoratorOverNew]);

    private static TheoryData<Shape, bool, VisitExit> Cases(Shape[] shapes)
    {
        var data = new TheoryData<Shape, bool, VisitExit>();
        foreach (var shape in shapes)
            foreach (var multiServer in new[] { false, true })
                foreach (var exit in Enum.GetValues<VisitExit>())
                    data.Add(shape, multiServer, exit);
        return data;
    }

    [Theory]
    [MemberData(nameof(RegisteredEngineDecorators))]
    public async Task QueueSessionTracker_ShouldCountEachExitAsAnUndecoratedHost_WhenTheManagerIsADecoratorOverARegisteredEngine(
        Shape shape, bool multiServer, VisitExit exit)
    {
        var counted = await RunVisitAsync(shape, multiServer, exit);

        counted.Should().Be(Undecorated(exit), "a decorator over a registered engine counts as an undecorated host");
    }

    // Control: green before and after the fix. Killed by (m1) the tracker built with no visit source, and by (m2) the
    // factory looking the engine up with GetRequiredService<CallSessionManager>() instead of SessionEngine.Resolve.
    [Theory]
    [MemberData(nameof(UndecoratedHosts))]
    public async Task QueueSessionTracker_ShouldCountEachExitFromAsteriskReports_WhenTheManagerIsUndecorated(
        Shape shape, bool multiServer, VisitExit exit)
    {
        var counted = await RunVisitAsync(shape, multiServer, exit);

        counted.Should().Be(Undecorated(exit));
    }

    // Control: green before and after the fix. Killed by (m2) GetRequiredService<CallSessionManager>() in the factory,
    // which fails the tracker's resolve on a host that registered no engine as itself.
    [Theory]
    [MemberData(nameof(UnknownEngineDecorators))]
    public async Task QueueSessionTracker_ShouldCountFromTheDomainEventsOnly_WhenTheDecoratedEngineIsUnknownToTheContainer(
        Shape shape, bool multiServer, VisitExit exit)
    {
        var counted = await RunVisitAsync(shape, multiServer, exit);

        counted.Should().Be(EventsOnly(exit));
    }

    /// <summary>
    /// Builds and starts the host, plays one queue visit that ends by <paramref name="exit"/> and the caller's hang-up,
    /// and returns the queue's counters after the exit, after the connection (the exit's snapshot again when there is
    /// none) and once the call ended.
    /// </summary>
    private static async Task<(string AtExit, string AtConnect, string AtEnd)> RunVisitAsync(
        Shape shape, bool multiServer, VisitExit exit)
    {
        await using var rig = new QueueRig();
        using var host = Build(rig.Server, shape, multiServer);
        await host.StartAsync(CancellationToken.None).WaitAsync(QueueRig.Bound);
        try
        {
            var manager = host.Services.GetRequiredService<ICallSessionManager>();
            var tracker = host.Services.GetRequiredService<IQueueSessionTracker>();
            if (multiServer)
                manager.AttachToServer(rig.Server, "node-1");

            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var endings = manager.Events.Subscribe(new EndingObserver(ended));

            await rig.StartAsync();
            await rig.JoinQueueAsync();
            manager.GetByChannelId(QueueRig.CallerUid).Should().NotBeNull("the host opened a session for the caller");

            switch (exit)
            {
                case VisitExit.Key:
                    await rig.SendAsync("QueueCallerLeave", QueueRig.QueueCaller(1, 0));
                    break;
                case VisitExit.Timeout:
                    await rig.SendAsync("QueueCallerAbandon", QueueRig.AbandonFields());
                    await rig.SendAsync("QueueCallerLeave", QueueRig.QueueCaller(1, 0));
                    await rig.SendAsync("VarSet", QueueRig.QueueStatusTimeout());
                    break;
                case VisitExit.Abandon:
                    await rig.SendAsync("QueueCallerAbandon", QueueRig.AbandonFields());
                    await rig.SendAsync("QueueCallerLeave", QueueRig.QueueCaller(1, 0));
                    break;
                case VisitExit.Answered:
                    await rig.SendAsync("QueueCallerLeave", QueueRig.QueueCaller(1, 0));
                    break;
            }

            await rig.MarkAsync("exit");
            var atExit = Snap(tracker);
            var atConnect = atExit;
            if (exit == VisitExit.Answered)
            {
                await rig.SendAsync("AgentConnect", QueueRig.AgentConnectFields());
                await rig.MarkAsync("connect");
                atConnect = Snap(tracker);
            }

            await rig.SendAsync("Hangup", QueueRig.HangupFields());
            await rig.MarkAsync("end");
            await ended.Task.WaitAsync(QueueRig.Bound);
            var atEnd = Snap(tracker);

            if (multiServer)
                manager.DetachFromServer("node-1");

            return (atExit, atConnect, atEnd);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(QueueRig.Bound);
        }
    }

    private static IHost Build(VerbaraServer server, Shape shape, bool multiServer) =>
        new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton(server);
                if (shape == Shape.DecoratorBefore)
                    AddEngineAndDecorator(services);

                if (multiServer)
                    services.AddVerbaraSessionsMultiServer(SweepOff);
                else
                    services.AddVerbaraSessions(SweepOff);

                switch (shape)
                {
                    case Shape.DecoratorAfter:
                        AddEngineAndDecorator(services);
                        break;
                    case Shape.DecoratorOverNew:
                        services.Replace(ServiceDescriptor.Singleton<ICallSessionManager>(sp =>
                            new ForwardingManager(
                                new CallSessionManager(
                                    sp.GetRequiredService<IOptions<SessionOptions>>(),
                                    NullLogger<CallSessionManager>.Instance,
                                    sp.GetRequiredService<SessionStoreBase>()),
                                ownsInner: true)));
                        break;
                }
            })
            .Build();

    private static void SweepOff(SessionOptions options) => options.ReconciliationInterval = Timeout.InfiniteTimeSpan;

    /// <summary>The README recipe: the engine registered as itself, a pass-through decorator over it as the interface.</summary>
    private static void AddEngineAndDecorator(IServiceCollection services)
    {
        services.AddSingleton<CallSessionManager>();
        services.AddSingleton<ICallSessionManager>(sp =>
            new ForwardingManager(sp.GetRequiredService<CallSessionManager>(), ownsInner: false));
    }

    /// <summary><c>offered,answered,abandoned,timedOut,waiting</c> of the queue, or <c>-</c> when it was never counted.</summary>
    private static string Snap(IQueueSessionTracker tracker)
    {
        var queue = tracker.GetByQueueName(QueueRig.Queue);
        return queue is null
            ? "-"
            : string.Create(CultureInfo.InvariantCulture,
                $"{queue.CallsOffered},{queue.CallsAnswered},{queue.CallsAbandoned},{queue.CallsTimedOut},{queue.CallsWaiting}");
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

    /// <summary>
    /// A real <see cref="AmiConnection"/> over a piped peer that plays Asterisk, and a <see cref="VerbaraServer"/> on it.
    /// The peer answers the server's load with an empty snapshot holding one queue; the test writes each event and
    /// waits until the connection dispatched it.
    /// </summary>
    private sealed class QueueRig : IAsyncDisposable
    {
        public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
        public const string Queue = "support";
        public const string CallerUid = "1795000000.7";
        public const string CallerChannel = "PJSIP/trunk-00000007";

        private readonly PipedSocketFactory _sockets = new();
        private readonly CancellationTokenSource _peerCts = new(Bound * 6);
        private readonly TaskCompletionSource _loadDispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _seen = new(StringComparer.Ordinal);
        private volatile bool _loaded;
        private Task _serving = Task.CompletedTask;
        private int _marks;
        private PipedSocket? _peer;

        public QueueRig()
        {
            Connection = new AmiConnection(Options.Create(new AmiConnectionOptions
            {
                Hostname = "localhost",
                Username = "admin",
                Password = "secret",
                EnableHeartbeat = false,
                AutoReconnect = false,
                DefaultEventTimeout = TimeSpan.Zero,
            }), _sockets, NullLogger<AmiConnection>.Instance);
            Connection.OnEvent += OnEventAsync;
            Server = new VerbaraServer(Connection, NullLogger<VerbaraServer>.Instance);
        }

        public AmiConnection Connection { get; }

        public VerbaraServer Server { get; }

        /// <summary>Connects, starts the server and returns once its first load was dispatched.</summary>
        public async Task StartAsync()
        {
            var socket = new TaskCompletionSource<PipedSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            _serving = Task.Run(() => ServeAsync(socket), CancellationToken.None);
            await Connection.ConnectAsync().AsTask().WaitAsync(Bound);
            _peer = await socket.Task.WaitAsync(Bound);
            await Server.StartAsync().WaitAsync(Bound);
            await _loadDispatched.Task.WaitAsync(Bound);
            _loaded = true;
        }

        /// <summary>The caller's channel, then its join of <see cref="Queue"/>.</summary>
        public async Task JoinQueueAsync()
        {
            await SendAsync("Newchannel",
            [
                new("Channel", CallerChannel), new("ChannelState", "4"), new("ChannelStateDesc", "Ring"),
                new("CallerIDNum", "5552101"), new("Context", "from-pstn"), new("Exten", "4021"), new("Priority", "1"),
                new("Uniqueid", CallerUid), new("Linkedid", CallerUid),
            ]);
            await SendAsync("QueueCallerJoin", QueueCaller(1, 1));
            await MarkAsync("joined");
        }

        /// <summary>Writes one event and returns once the connection dispatched it.</summary>
        public async Task SendAsync(string eventType, KeyValuePair<string, string>[] fields)
        {
            var peer = _peer ?? throw new InvalidOperationException("The rig is not started.");
            var key = fields.FirstOrDefault(f => f.Key == "Peer").Value
                ?? fields.FirstOrDefault(f => f.Key == "Uniqueid").Value ?? "";
            var seen = Seen(eventType, key);
            if (!await peer.WriteEventAsync(eventType, fields))
                throw new InvalidOperationException("The peer refused the write of " + eventType + ".");
            await seen.WaitAsync(Bound);
        }

        /// <summary>A marker event: once it is dispatched, every event written before it was fully dispatched.</summary>
        public Task MarkAsync(string label) =>
            SendAsync("PeerStatus",
                [new("Peer", $"MARK/{label}/{Interlocked.Increment(ref _marks)}"), new("PeerStatus", "Reachable")]);

        public static KeyValuePair<string, string>[] QueueCaller(int position, int count) =>
        [
            new("Channel", CallerChannel), new("ChannelState", "4"), new("ChannelStateDesc", "Ring"),
            new("CallerIDNum", "5552101"), new("Context", "from-pstn"), new("Exten", "4021"), new("Priority", "1"),
            new("Uniqueid", CallerUid), new("Linkedid", CallerUid), new("Queue", Queue),
            new("Position", position.ToString(CultureInfo.InvariantCulture)),
            new("Count", count.ToString(CultureInfo.InvariantCulture)),
        ];

        public static KeyValuePair<string, string>[] AbandonFields() =>
        [
            new("Channel", CallerChannel), new("Uniqueid", CallerUid), new("Linkedid", CallerUid), new("Queue", Queue),
            new("HoldTime", "3"), new("Position", "1"), new("OriginalPosition", "1"),
        ];

        public static KeyValuePair<string, string>[] QueueStatusTimeout() =>
        [
            new("Channel", CallerChannel), new("Uniqueid", CallerUid), new("Linkedid", CallerUid),
            new("Variable", "QUEUESTATUS"), new("Value", "TIMEOUT"),
        ];

        public static KeyValuePair<string, string>[] AgentConnectFields() =>
        [
            new("Privilege", "agent,all"), new("Channel", CallerChannel), new("Uniqueid", CallerUid),
            new("Linkedid", CallerUid), new("DestChannel", "PJSIP/agent-1001-00000008"),
            new("DestUniqueid", "1795000000.8"), new("DestLinkedid", CallerUid), new("Queue", Queue),
            new("Interface", "PJSIP/agent-1001"), new("MemberName", "Agent 1001"), new("Agent", "1001"),
            new("HoldTime", "3"), new("RingTime", "1"),
        ];

        public static KeyValuePair<string, string>[] HangupFields() =>
        [
            new("Channel", CallerChannel), new("ChannelState", "4"), new("ChannelStateDesc", "Ring"),
            new("Uniqueid", CallerUid), new("Linkedid", CallerUid), new("Cause", "16"), new("Cause-txt", "Normal Clearing"),
        ];

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync().AsTask().WaitAsync(Bound);
            await Connection.DisposeAsync().AsTask().WaitAsync(Bound);
            await _peerCts.CancelAsync();
            await _serving.WaitAsync(Bound);
            _peerCts.Dispose();
        }

        private Task Seen(string eventType, string key) =>
            _seen.GetOrAdd(eventType + "|" + key,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        private ValueTask OnEventAsync(ManagerEvent evt)
        {
            var fields = evt.RawFields;
            var key = fields?.GetValueOrDefault("Peer") ?? fields?.GetValueOrDefault("Uniqueid")
                ?? fields?.GetValueOrDefault("ActionID") ?? "";
            _seen.GetOrAdd((evt.EventType ?? "") + "|" + key,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            if (!_loaded && evt.EventType == "AgentsComplete")
                _loadDispatched.TrySetResult();
            return ValueTask.CompletedTask;
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
                served.TrySetException(ex);
            }
        }

        private static async Task AnswerAsync(PipedSocket socket, string action)
        {
            var end = action.IndexOf("\r\n", StringComparison.Ordinal);
            var name = (end >= 0 ? action[..end] : action).Replace("Action:", "", StringComparison.OrdinalIgnoreCase).Trim();
            var id = PipedSocket.ActionIdOf(action);
            switch (name.ToUpperInvariant())
            {
                case "QUEUESTATUS":
                    await socket.RespondAsync("Success", id, [new("EventList", "start")]);
                    await socket.WriteEventAsync("QueueParams",
                    [
                        new("ActionID", id), new("Queue", Queue), new("Max", "0"), new("Strategy", "ringall"), new("Calls", "0"),
                        new("Holdtime", "0"), new("TalkTime", "0"), new("Completed", "0"), new("Abandoned", "0"),
                    ]);
                    await socket.WriteEventAsync("QueueStatusComplete",
                        [new("ActionID", id), new("EventList", "Complete"), new("ListItems", "1")]);
                    break;
                case "STATUS":
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
    }
}
