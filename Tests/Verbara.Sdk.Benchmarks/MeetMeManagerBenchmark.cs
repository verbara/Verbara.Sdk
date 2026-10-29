using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.MeetMe;

namespace Verbara.Sdk.Benchmarks;

/// <summary>
/// Per-event cost of the conference manager's most frequent event path (a participant's mute state flipping), with
/// and without one subscriber. Each row reports the cost of ONE event (<c>OperationsPerInvoke</c>), so its allocated
/// bytes are per event and deterministic.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class MeetMeManagerBenchmark
{
    private const int N = 1000;
    private string[] _chans = null!;
    private MeetMeManager _unobserved = null!;
    private MeetMeManager _observed = null!;
    private bool _flip;
    private int _seen;

    [GlobalSetup]
    public void Setup()
    {
        _chans = new string[N];
        for (var i = 0; i < N; i++)
            _chans[i] = "PJSIP/caller-" + i.ToString("x8", CultureInfo.InvariantCulture);

        _unobserved = new MeetMeManager(NullLogger.Instance);
        _observed = new MeetMeManager(NullLogger.Instance);
        _observed.UserStateChanged += (_, _) => _seen++;
        for (var i = 0; i < N; i++)
        {
            _unobserved.OnUserJoined("100", 0, _chans[i]);
            _observed.OnUserJoined("100", 0, _chans[i]);
        }
    }

    // The flag flips on every invocation so every call is a real change, never a repeat the manager ignores.
    [Benchmark(Baseline = true, OperationsPerInvoke = N)]
    public void Mute_NoSubscriber()
    {
        _flip = !_flip;
        for (var i = 0; i < N; i++)
            _unobserved.OnUserMuted("100", _chans[i], _flip);
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void Mute_OneSubscriber()
    {
        _flip = !_flip;
        for (var i = 0; i < N; i++)
            _observed.OnUserMuted("100", _chans[i], _flip);
    }
}
