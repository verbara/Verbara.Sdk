using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Benchmarks;

/// <summary>
/// What a <c>Hangup</c> costs the queue table, per event (<c>OperationsPerInvoke</c>), against a table shaped like one
/// after a load: <see cref="Queues"/> configured queues, <see cref="Held"/> callers waiting in the first four of them.
/// With <c>QUEUE_HANGUP_RULE</c> defined (the candidate), a hangup calls the manager's hangup entry; without it (the
/// reference, where a <c>Hangup</c> never reaches the queue table) the same loop calls nothing, so the difference is the
/// hangup's cost. Every row is steady: no per-iteration setup, so each measures code the JIT has finished optimizing.
/// A class of its own, not parameters on <see cref="QueueManagerBenchmark"/>, whose row names a baseline gates.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class QueueHangupBenchmark
{
    private const int N = 1000;
    private const int WaitingQueues = 4;
    private const string JoinQueue = "q-0";
    private static readonly EventLossEpoch Epoch = new(0, 1, 0);
    private string[] _hungUp = null!;
    private string[] _joinIds = null!;
    private string[] _joinChannels = null!;
    private QueueManager _nobodyHeld = null!;
    private QueueManager _hangupAfterJoin = null!;
    private QueueManager _leaveAfterJoin = null!;
#if !QUEUE_HANGUP_RULE
    private int _seen;
#endif

    /// <summary>How many queues the table holds, every one configured by a queue's parameters.</summary>
    [Params(4, 200)]
    public int Queues { get; set; }

    /// <summary>How many other callers wait, spread over the first four queues.</summary>
    [Params(10, 1000)]
    public int Held { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _hungUp = new string[N];
        _joinIds = new string[N];
        _joinChannels = new string[N];
        for (var i = 0; i < N; i++)
        {
            _hungUp[i] = "1792109999." + i.ToString(CultureInfo.InvariantCulture);
            _joinIds[i] = "1792103773." + i.ToString(CultureInfo.InvariantCulture);
            _joinChannels[i] = "PJSIP/caller-" + i.ToString("x8", CultureInfo.InvariantCulture);
        }

        _nobodyHeld = Populated();
        _hangupAfterJoin = Populated();
        _leaveAfterJoin = Populated();
    }

    /// <summary>A <c>Hangup</c> of an id no queue holds: almost every <c>Hangup</c> of a connection.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void Hangup_NobodyHeld()
    {
        for (var i = 0; i < N; i++)
            HangUp(_nobodyHeld, _hungUp[i]);
    }

    /// <summary>
    /// A caller joins <c>q-0</c> with its <c>Uniqueid</c> and hangs up while waiting: the candidate's hit path, which
    /// removes it (with no subscriber). The reference leaves it held, so its later joins find it by channel.
    /// </summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void JoinThenHangup_HeldCaller()
    {
        for (var i = 0; i < N; i++)
        {
            _hangupAfterJoin.OnCallerJoined(JoinQueue, _joinChannels[i], "100", 1, _joinIds[i], Epoch);
            HangUp(_hangupAfterJoin, _joinIds[i]);
        }
    }

    /// <summary>A caller joins <c>q-0</c> with its <c>Uniqueid</c> and leaves it: the same on both sides.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void JoinThenLeave_WithUniqueId()
    {
        for (var i = 0; i < N; i++)
        {
            _leaveAfterJoin.OnCallerJoined(JoinQueue, _joinChannels[i], "100", 1, _joinIds[i], Epoch);
            _leaveAfterJoin.OnCallerLeft(JoinQueue, _joinChannels[i]);
        }
    }

    private QueueManager Populated()
    {
        var manager = new QueueManager(NullLogger.Instance);
        for (var q = 0; q < Queues; q++)
            manager.OnQueueParams(QueueName(q), 0, "ringall", 0, 0, 0, 0, 0);
        for (var i = 0; i < Held; i++)
        {
            manager.OnCallerJoined(QueueName(i % WaitingQueues), "PJSIP/other-" + i.ToString(CultureInfo.InvariantCulture),
                "100", 1, "1792100000." + i.ToString(CultureInfo.InvariantCulture), Epoch);
        }

        return manager;
    }

    private static string QueueName(int q) => "q-" + q.ToString(CultureInfo.InvariantCulture);

#if QUEUE_HANGUP_RULE
    private static void HangUp(QueueManager queues, string uniqueId) => queues.OnChannelHungUp(uniqueId);
#else
    private void HangUp(QueueManager queues, string uniqueId)
    {
        _ = queues;
        _seen += uniqueId.Length & 0;
    }
#endif
}
