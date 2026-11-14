using System.Globalization;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Benchmarks;

/// <summary>
/// Per-event cost of a queue event delivered through the live server's own event handler, as the connection delivers
/// it, rather than through the queue table directly as <see cref="QueueManagerBenchmark"/> does: everything the server
/// does on the dispatch for a queue event, including any check of whether the queue it names was listed. The table
/// holds the queue from a completed load, the steady state of every event of a running server. Every row reports the
/// cost of ONE event (<c>OperationsPerInvoke</c>), so its allocated bytes are per event and deterministic.
/// </summary>
/// <remarks>
/// The server runs over an in-memory connection that captures the server's event handler and answers the load's
/// listings: <c>QueueStatus</c> with one queue and its members, every other listing empty. Only public API, so the row
/// builds on a reference tree that predates it.
/// </remarks>
[MemoryDiagnoser]
[ShortRunJob]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "BenchmarkDotNet owns the lifetime: GlobalCleanup disposes the server, which owns nothing of the connection.")]
public class QueueEventDispatchBenchmark
{
    private const int N = 1000;
    private const string Queue = "sales";
    private QueueMemberStatusEvent[] _statusEvents = null!;
    private QueueCallerJoinEvent[] _joins = null!;
    private QueueCallerLeaveEvent[] _leaves = null!;
    private VerbaraServer _server = null!;
    private ListingConnection _connection = null!;

    [GlobalSetup]
    public void Setup()
    {
        var members = new string[N];
        _statusEvents = new QueueMemberStatusEvent[N];
        _joins = new QueueCallerJoinEvent[N];
        _leaves = new QueueCallerLeaveEvent[N];
        for (var i = 0; i < N; i++)
        {
            var iface = "PJSIP/agent-" + i.ToString(CultureInfo.InvariantCulture);
            var uid = "1792103773." + i.ToString(CultureInfo.InvariantCulture);
            var channel = "PJSIP/caller-" + i.ToString("x8", CultureInfo.InvariantCulture);
            members[i] = iface;
            _statusEvents[i] = new QueueMemberStatusEvent
            {
                EventType = "QueueMemberStatus", Queue = Queue, Interface = iface, Location = iface, Status = 2,
            };
            var callerFields = new Dictionary<string, string>
            {
                ["Queue"] = Queue, ["Channel"] = channel, ["CallerIDNum"] = "5550100", ["Uniqueid"] = uid,
            };
            _joins[i] = new QueueCallerJoinEvent
            {
                EventType = "QueueCallerJoin", UniqueId = uid, Position = 1, RawFields = callerFields,
            };
            _leaves[i] = new QueueCallerLeaveEvent { EventType = "QueueCallerLeave", UniqueId = uid, RawFields = callerFields };
        }

        _connection = new ListingConnection(Queue, members);
        _server = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
        _server.StartAsync().GetAwaiter().GetResult();
        if (_server.Queues.GetByName(Queue)?.Members.Count != N)
            throw new InvalidOperationException("The load did not hold the listed queue and its members.");
    }

    [GlobalCleanup]
    public void Cleanup() => _server.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>A member's device state, for a queue the load listed: the most frequent queue event.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void MemberStatus_ListedQueue()
    {
        var handler = _connection.Handler;
        for (var i = 0; i < N; i++)
            handler.OnNext(_statusEvents[i]);
    }

    /// <summary>A caller's join and its leave, for a queue the load listed: two events per operation.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void CallerJoinThenLeave_ListedQueue()
    {
        var handler = _connection.Handler;
        for (var i = 0; i < N; i++)
        {
            handler.OnNext(_joins[i]);
            handler.OnNext(_leaves[i]);
        }
    }

    /// <summary>
    /// A connection that only captures the server's handler and answers the load: one queue with its members on
    /// <c>QueueStatus</c>, nothing on any other listing. Not an <see cref="AmiConnection"/>, so no list waits for a pump.
    /// </summary>
    private sealed class ListingConnection(string queue, string[] members) : IAmiConnection
    {
        public IObserver<ManagerEvent> Handler { get; private set; } = null!;

        public AmiConnectionState State => AmiConnectionState.Connected;

        public string? AsteriskVersion => "22.0.0";

        public event Func<ManagerEvent, ValueTask>? OnEvent
        {
            add { }
            remove { }
        }

        public event Action? Reconnected
        {
            add { }
            remove { }
        }

        public IDisposable Subscribe(IObserver<ManagerEvent> observer)
        {
            Handler = observer;
            return new Unsubscriber();
        }

        public async IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(ManagerAction action,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (action is not QueueStatusAction)
                yield break;

            yield return new QueueParamsEvent { EventType = "QueueParams", Queue = queue, Strategy = "ringall" };
            foreach (var member in members)
            {
                yield return new QueueMemberEvent
                {
                    EventType = "QueueMember", Queue = queue, Interface = member, Location = member, Status = 1,
                };
            }
        }

        public ValueTask<ManagerResponse> SendActionAsync(ManagerAction action, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ManagerResponse { Response = "Success" });

        public ValueTask<TResponse> SendActionAsync<TResponse>(ManagerAction action,
            CancellationToken cancellationToken = default) where TResponse : ManagerResponse =>
            throw new NotSupportedException();

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Unsubscriber : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
