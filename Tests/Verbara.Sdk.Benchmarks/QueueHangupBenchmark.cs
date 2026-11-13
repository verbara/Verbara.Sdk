using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Queues;
using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Benchmarks;

/// <summary>
/// What a <c>Hangup</c> costs the queue table, per event (<c>OperationsPerInvoke</c>). With <c>QUEUE_HANGUP_RULE</c>
/// defined (the candidate), each row calls the manager's hangup entry, which looks for a caller held on the
/// <c>Uniqueid</c> in every queue and removes it; without it (the reference, where a <c>Hangup</c> never reaches the
/// queue table) each row runs the same loop over the same ids and calls nothing, so the difference is the hangup's cost.
/// A class of its own, not parameters on <see cref="QueueManagerBenchmark"/>, whose row names a baseline gates.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class QueueHangupBenchmark
{
    private const int N = 1000;
    private const int Queues = 4;
    private static readonly EventLossEpoch Epoch = new(0, 1, 0);
    private string[] _hungUp = null!;
    private string[] _heldIds = null!;
    private string[] _heldChannels = null!;
    private QueueManager _empty = null!;
    private QueueManager _others = null!;
    private QueueManager _held = null!;
#if !QUEUE_HANGUP_RULE
    private int _seen;
#endif

    /// <summary>How many other callers wait in the table while the hung-up ids are looked for.</summary>
    [Params(10, 1000)]
    public int Held { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _hungUp = new string[N];
        _heldIds = new string[N];
        _heldChannels = new string[N];
        for (var i = 0; i < N; i++)
        {
            _hungUp[i] = "1792109999." + i.ToString(CultureInfo.InvariantCulture);
            _heldIds[i] = "1792103773." + i.ToString(CultureInfo.InvariantCulture);
            _heldChannels[i] = "PJSIP/caller-" + i.ToString("x8", CultureInfo.InvariantCulture);
        }

        _empty = new QueueManager(NullLogger.Instance);
        _others = new QueueManager(NullLogger.Instance);
        for (var i = 0; i < Held; i++)
        {
            _others.OnCallerJoined(QueueOf(i), "PJSIP/other-" + i.ToString(CultureInfo.InvariantCulture), "100", 1,
                "1792100000." + i.ToString(CultureInfo.InvariantCulture), Epoch);
        }

        _held = new QueueManager(NullLogger.Instance);
    }

    /// <summary>Before each iteration of <see cref="Hangup_HeldCaller"/>: the N callers it hangs up wait again.</summary>
    [IterationSetup(Target = nameof(Hangup_HeldCaller))]
    public void JoinHeld()
    {
        for (var i = 0; i < N; i++)
            _held.OnCallerJoined(QueueOf(i), _heldChannels[i], "100", 1, _heldIds[i], Epoch);
    }

    /// <summary>A <c>Hangup</c> when the table holds no queue at all.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void Hangup_NoQueuePopulated()
    {
        for (var i = 0; i < N; i++)
            HangUp(_empty, _hungUp[i]);
    }

    /// <summary>A <c>Hangup</c> of an id no queue holds, while <see cref="Held"/> other callers wait in four queues.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void Hangup_NobodyHeld()
    {
        for (var i = 0; i < N; i++)
            HangUp(_others, _hungUp[i]);
    }

    /// <summary>
    /// A <c>Hangup</c> of a caller the table holds, which the candidate removes (with no subscriber). The reference
    /// leaves it held; its next iteration's joins then replace it.
    /// </summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void Hangup_HeldCaller()
    {
        for (var i = 0; i < N; i++)
            HangUp(_held, _heldIds[i]);
    }

    private static string QueueOf(int i) => "q-" + (i % Queues).ToString(CultureInfo.InvariantCulture);

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
