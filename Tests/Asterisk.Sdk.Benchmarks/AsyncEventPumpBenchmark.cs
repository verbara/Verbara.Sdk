using System.Globalization;
using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Internal;
using BenchmarkDotNet.Attributes;

namespace Asterisk.Sdk.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class AsyncEventPumpBenchmark
{
    [Benchmark]
    public async Task EnqueueAndConsume1000Events()
    {
        var consumed = 0;
        await using var pump = new AsyncEventPump();

        var tcs = new TaskCompletionSource();
        pump.Start(evt =>
        {
            if (Interlocked.Increment(ref consumed) >= 1000)
                tcs.TrySetResult();
            return ValueTask.CompletedTask;
        });

        for (int i = 0; i < 1000; i++)
        {
            pump.TryEnqueue(new ManagerEvent { EventType = "Test", UniqueId = i.ToString(CultureInfo.InvariantCulture) });
        }

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Benchmark]
    public async Task EnqueueOnly10000Events()
    {
        await using var pump = new AsyncEventPump();

        for (int i = 0; i < 10_000; i++)
        {
            pump.TryEnqueue(new ManagerEvent { EventType = "Test" });
        }
    }
}
