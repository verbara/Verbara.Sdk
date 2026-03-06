using System.Collections.Concurrent;
using Asterisk.Sdk.Ami.Internal;
using BenchmarkDotNet.Attributes;

namespace Asterisk.Sdk.Benchmarks;

/// <summary>
/// Benchmarks the request/response correlation pattern used in AmiConnection.
/// ConcurrentDictionary&lt;string, TaskCompletionSource&lt;AmiMessage&gt;&gt; TryAdd/TryRemove.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ActionCorrelationBenchmark
{
    private ConcurrentDictionary<string, TaskCompletionSource<AmiMessage>> _pending = null!;
    private string[] _actionIds = null!;
    private AmiMessage _response = null!;

    [GlobalSetup]
    public void Setup()
    {
        _pending = new ConcurrentDictionary<string, TaskCompletionSource<AmiMessage>>();
        _actionIds = new string[1000];
        for (int i = 0; i < 1000; i++)
            _actionIds[i] = $"action-{i}";

        _response = new AmiMessage(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Response"] = "Success",
            ["Message"] = "Pong"
        });
    }

    [Benchmark(Baseline = true)]
    public void AddAndCorrelate1000Actions()
    {
        _pending.Clear();

        // Register 1000 pending actions
        for (int i = 0; i < 1000; i++)
        {
            _pending[_actionIds[i]] = new TaskCompletionSource<AmiMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Correlate all 1000 responses
        for (int i = 0; i < 1000; i++)
        {
            if (_pending.TryRemove(_actionIds[i], out var tcs))
                tcs.TrySetResult(_response);
        }
    }

    [Benchmark]
    public void TryAdd1000Actions()
    {
        _pending.Clear();
        for (int i = 0; i < 1000; i++)
        {
            _pending[_actionIds[i]] = new TaskCompletionSource<AmiMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    [Benchmark]
    public void TryRemove1000Actions()
    {
        // Pre-fill
        _pending.Clear();
        for (int i = 0; i < 1000; i++)
            _pending[_actionIds[i]] = new TaskCompletionSource<AmiMessage>();

        // Benchmark remove + complete
        for (int i = 0; i < 1000; i++)
        {
            if (_pending.TryRemove(_actionIds[i], out var tcs))
                tcs.TrySetResult(_response);
        }
    }
}
