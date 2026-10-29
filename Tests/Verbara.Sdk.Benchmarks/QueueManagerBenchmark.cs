using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Queues;

namespace Verbara.Sdk.Benchmarks;

/// <summary>
/// Per-event cost of the queue manager's frequent event paths, each with and without one subscriber. A change nobody
/// hears is applied and dropped; a change a handler hears is also queued and raised, so only the subscribed row shows
/// what an observed change costs. Every row reports the cost of ONE event (<c>OperationsPerInvoke</c>), so its
/// allocated bytes are per event and deterministic.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class QueueManagerBenchmark
{
    private const int N = 1000;
    private string[] _ifaces = null!;
    private string[] _uids = null!;
    private string[] _chans = null!;
    private QueueManager _unobserved = null!;
    private QueueManager _observed = null!;
    private int _seen;

    [GlobalSetup]
    public void Setup()
    {
        _ifaces = new string[N];
        _uids = new string[N];
        _chans = new string[N];
        for (var i = 0; i < N; i++)
        {
            _ifaces[i] = "PJSIP/agent-" + i.ToString(CultureInfo.InvariantCulture);
            _uids[i] = "1792103773." + i.ToString(CultureInfo.InvariantCulture);
            _chans[i] = "PJSIP/caller-" + i.ToString("x8", CultureInfo.InvariantCulture);
        }

        _unobserved = new QueueManager(NullLogger.Instance);
        _observed = new QueueManager(NullLogger.Instance);
        _observed.MemberStatusChanged += (_, _) => _seen++;
        _observed.CallerQueueStatus += (_, _, _) => _seen++;
        _observed.CallerAbandonReported += (_, _) => _seen++;
        _observed.CallerJoined += (_, _) => _seen++;
        _observed.CallerLeft += (_, _) => _seen++;
        for (var i = 0; i < N; i++)
        {
            _unobserved.OnMemberAdded("sales", _ifaces[i], _ifaces[i], 0, false, 1);
            _observed.OnMemberAdded("sales", _ifaces[i], _ifaces[i], 0, false, 1);
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = N)]
    public void MemberStatus_NoSubscriber()
    {
        for (var i = 0; i < N; i++)
            _unobserved.OnMemberStatusChanged("sales", _ifaces[i], 2);
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void MemberStatus_OneSubscriber()
    {
        for (var i = 0; i < N; i++)
            _observed.OnMemberStatusChanged("sales", _ifaces[i], 2);
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void DeviceState_NoSubscriber()
    {
        for (var i = 0; i < N; i++)
            _unobserved.OnDeviceStateChanged(_ifaces[i], "INUSE");
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void DeviceState_OneSubscriber()
    {
        for (var i = 0; i < N; i++)
            _observed.OnDeviceStateChanged(_ifaces[i], "INUSE");
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void QueueStatus_NoSubscriber()
    {
        for (var i = 0; i < N; i++)
            _unobserved.OnCallerQueueStatus(_uids[i], _chans[i], "TIMEOUT");
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void QueueStatus_OneSubscriber()
    {
        for (var i = 0; i < N; i++)
            _observed.OnCallerQueueStatus(_uids[i], _chans[i], "TIMEOUT");
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void Abandon_NoSubscriber()
    {
        for (var i = 0; i < N; i++)
            _unobserved.OnCallerAbandonReported(_uids[i], "sales");
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void Abandon_OneSubscriber()
    {
        for (var i = 0; i < N; i++)
            _observed.OnCallerAbandonReported(_uids[i], "sales");
    }

    // One operation is a join and its leave; each allocates its entry and records metrics by design.
    [Benchmark(OperationsPerInvoke = N)]
    public void JoinLeave_NoSubscriber()
    {
        for (var i = 0; i < N; i++)
        {
            _unobserved.OnCallerJoined("sales", _chans[i], null, 1);
            _unobserved.OnCallerLeft("sales", _chans[i]);
        }
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void JoinLeave_OneSubscriber()
    {
        for (var i = 0; i < N; i++)
        {
            _observed.OnCallerJoined("sales", _chans[i], null, 1);
            _observed.OnCallerLeft("sales", _chans[i]);
        }
    }
}
