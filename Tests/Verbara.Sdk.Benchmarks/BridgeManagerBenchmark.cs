using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Bridges;

namespace Verbara.Sdk.Benchmarks;

/// <summary>
/// Per-event cost of the bridge manager's channel-membership path (a channel entering, then leaving, a bridge held from
/// setup: one <c>ChannelEntered</c> and one <c>ChannelLeft</c>) and of a blind transfer (one <c>TransferOccurred</c>),
/// with and without one subscriber. Each row reports the cost of ONE event pair or transfer (<c>OperationsPerInvoke</c>);
/// the bridge is never destroyed, so no retention queue grows across iterations.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class BridgeManagerBenchmark
{
    private const int N = 1000;
    private const string Bridge = "11111111-1111-1111-1111-000000000001";
    private string[] _chans = null!;
    private BridgeManager _unobserved = null!;
    private BridgeManager _observed = null!;
    private int _seen;

    [GlobalSetup]
    public void Setup()
    {
        _chans = new string[N];
        for (var i = 0; i < N; i++)
            _chans[i] = "1792274553." + i.ToString(CultureInfo.InvariantCulture);

        _unobserved = new BridgeManager(NullLogger.Instance);
        _observed = new BridgeManager(NullLogger.Instance);
        _observed.ChannelEntered += (_, _) => _seen++;
        _observed.ChannelLeft += (_, _) => _seen++;
        _observed.TransferOccurred += _ => _seen++;
        _unobserved.OnBridgeCreated(Bridge, "basic", "simple_bridge", null, null);
        _observed.OnBridgeCreated(Bridge, "basic", "simple_bridge", null, null);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = N)]
    public void EnterLeave_NoSubscriber()
    {
        for (var i = 0; i < N; i++)
        {
            _unobserved.OnChannelEntered(Bridge, _chans[i]);
            _unobserved.OnChannelLeft(Bridge, _chans[i]);
        }
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void EnterLeave_OneSubscriber()
    {
        for (var i = 0; i < N; i++)
        {
            _observed.OnChannelEntered(Bridge, _chans[i]);
            _observed.OnChannelLeft(Bridge, _chans[i]);
        }
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void BlindTransfer_NoSubscriber()
    {
        for (var i = 0; i < N; i++)
            _unobserved.OnBlindTransfer(Bridge, _chans[i], "100", "default");
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void BlindTransfer_OneSubscriber()
    {
        for (var i = 0; i < N; i++)
            _observed.OnBlindTransfer(Bridge, _chans[i], "100", "default");
    }
}
