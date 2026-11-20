using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Live.Agents;

namespace Verbara.Sdk.Benchmarks;

/// <summary>
/// Per-event cost of the agent manager's two most frequent event paths, with and without one subscriber of each event
/// they raise: a pause change (one <c>AgentStateChanged</c>) and a queue call answered (<c>AgentStateChanged</c>, then
/// <c>AgentConnected</c>). Each row reports the cost of ONE event (<c>OperationsPerInvoke</c>), on agents logged in
/// during setup, so its allocated bytes are per event and deterministic.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class AgentManagerBenchmark
{
    private const int N = 1000;
    private string[] _ids = null!;
    private AgentManager _unobserved = null!;
    private AgentManager _observed = null!;
    private bool _flip;
    private int _seen;

    [GlobalSetup]
    public void Setup()
    {
        _ids = new string[N];
        for (var i = 0; i < N; i++)
            _ids[i] = (1000 + i).ToString(CultureInfo.InvariantCulture);

        _unobserved = new AgentManager(NullLogger.Instance);
        _observed = new AgentManager(NullLogger.Instance);
        _observed.AgentStateChanged += _ => _seen++;
        _observed.AgentConnected += (_, _, _) => _seen++;
        for (var i = 0; i < N; i++)
        {
            _unobserved.OnAgentLogin(_ids[i], "PJSIP/" + _ids[i]);
            _observed.OnAgentLogin(_ids[i], "PJSIP/" + _ids[i]);
        }
    }

    // The flag flips on every invocation so every call is a real change.
    [Benchmark(Baseline = true, OperationsPerInvoke = N)]
    public void Pause_NoSubscriber()
    {
        _flip = !_flip;
        for (var i = 0; i < N; i++)
            _unobserved.OnAgentPaused(_ids[i], _flip);
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void Pause_OneSubscriber()
    {
        _flip = !_flip;
        for (var i = 0; i < N; i++)
            _observed.OnAgentPaused(_ids[i], _flip);
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void Connect_NoSubscriber()
    {
        for (var i = 0; i < N; i++)
            _unobserved.OnAgentConnect(_ids[i], "PJSIP/caller", "linked", "Agent/1000");
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void Connect_OneSubscriber()
    {
        for (var i = 0; i < N; i++)
            _observed.OnAgentConnect(_ids[i], "PJSIP/caller", "linked", "Agent/1000");
    }
}
