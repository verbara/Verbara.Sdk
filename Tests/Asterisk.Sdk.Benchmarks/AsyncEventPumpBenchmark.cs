using System.Globalization;
using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Internal;
using BenchmarkDotNet.Attributes;

namespace Asterisk.Sdk.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class AsyncEventPumpBenchmark
{
    private ManagerEvent[] _events = null!;

    [GlobalSetup]
    public void Setup()
    {
        _events = new ManagerEvent[10_000];
        for (int i = 0; i < _events.Length; i++)
            _events[i] = new ManagerEvent { EventType = "Test", UniqueId = i.ToString(CultureInfo.InvariantCulture) };
    }

    [Benchmark(Baseline = true)]
    public async Task EnqueueAndConsume1000Events()
    {
        var consumed = 0;
        await using var pump = new AsyncEventPump();

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.Start(evt =>
        {
            if (Interlocked.Increment(ref consumed) >= 1000)
                tcs.TrySetResult();
            return ValueTask.CompletedTask;
        });

        for (int i = 0; i < 1000; i++)
            pump.TryEnqueue(_events[i]);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Benchmark]
    public async Task EnqueueOnly10000Events()
    {
        await using var pump = new AsyncEventPump();

        for (int i = 0; i < 10_000; i++)
            pump.TryEnqueue(_events[i]);
    }
}
