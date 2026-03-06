using Asterisk.Sdk;
using BenchmarkDotNet.Attributes;

namespace Asterisk.Sdk.Benchmarks;

/// <summary>
/// Benchmarks the lock-free copy-on-write volatile array observer dispatch pattern.
/// This is the AMI hot path: called for every event (100K+ events/sec at scale).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ObserverDispatchBenchmark
{
    private ManagerEvent _event = null!;

    // Simulates the volatile snapshot + foreach loop from AmiConnection.DispatchEventAsync
    private IObserver<ManagerEvent>[] _observers1 = null!;
    private IObserver<ManagerEvent>[] _observers10 = null!;
    private IObserver<ManagerEvent>[] _observers100 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _event = new ManagerEvent
        {
            EventType = "Newchannel",
            UniqueId = "1234567890.1",
        };

        _observers1 = CreateObservers(1);
        _observers10 = CreateObservers(10);
        _observers100 = CreateObservers(100);
    }

    private static IObserver<ManagerEvent>[] CreateObservers(int count)
    {
        var observers = new IObserver<ManagerEvent>[count];
        for (int i = 0; i < count; i++)
            observers[i] = new NoopObserver();
        return observers;
    }

    [Benchmark(Baseline = true)]
    public void Dispatch_1Observer()
    {
        var snapshot = Volatile.Read(ref _observers1);
        foreach (var observer in snapshot)
            observer.OnNext(_event);
    }

    [Benchmark]
    public void Dispatch_10Observers()
    {
        var snapshot = Volatile.Read(ref _observers10);
        foreach (var observer in snapshot)
            observer.OnNext(_event);
    }

    [Benchmark]
    public void Dispatch_100Observers()
    {
        var snapshot = Volatile.Read(ref _observers100);
        foreach (var observer in snapshot)
            observer.OnNext(_event);
    }

    private sealed class NoopObserver : IObserver<ManagerEvent>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(ManagerEvent value) { /* hot path — intentionally empty */ }
    }
}
