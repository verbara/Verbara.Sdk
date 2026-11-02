using System.Reflection;
using Verbara.Sdk.Ari.Client;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Benchmarks;

/// <summary>
/// One event through <c>AriClient.DispatchAsync</c> to one observer: the path every ARI event takes after the pump, which
/// <see cref="AriParseEventBenchmark"/> does not reach. The private method is bound once, in setup, to an open-instance
/// delegate, so this file builds unchanged on a tag that predates any change to the dispatch, and the observer goes in through the public
/// <c>Subscribe</c>, so what stands between the two is what a consumer gets.
/// </summary>
#pragma warning disable CA1001 // GlobalCleanup releases the client: BenchmarkDotNet owns this type's lifetime.
[MemoryDiagnoser]
[ShortRunJob]
public class AriDispatchBenchmark
{
    private AriClient _client = null!;
    private AriEvent _event = null!;
    private Func<AriClient, AriEvent, ValueTask> _dispatch = null!;

    [GlobalSetup]
    public void Setup()
    {
        var options = Options.Create(new AriClientOptions
        {
            BaseUrl = "http://127.0.0.1:8088",
            Username = "user",
            Password = "password",
            Application = "bench",
        });
        _client = new AriClient(options, NullLogger<AriClient>.Instance);
        _client.Subscribe(new CountingObserver());
        _event = new AriEvent { Type = "StasisStart" };
        _dispatch = typeof(AriClient)
            .GetMethod("DispatchAsync", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(AriEvent)])!
            .CreateDelegate<Func<AriClient, AriEvent, ValueTask>>();
    }

    [GlobalCleanup]
    public void Cleanup() => _client.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark]
    public ValueTask OneEventToOneObserver() => _dispatch(_client, _event);

    private sealed class CountingObserver : IObserver<AriEvent>
    {
        private long _received;

        public void OnNext(AriEvent value) => _received++;

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
#pragma warning restore CA1001
