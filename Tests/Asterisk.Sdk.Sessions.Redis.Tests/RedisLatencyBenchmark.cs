using System.Diagnostics;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions;
using Xunit;
using Xunit.Abstractions;

namespace Asterisk.Sdk.Sessions.Redis.Tests;

/// <summary>
/// Spot-benchmark of <see cref="RedisSessionStore"/> latency and batch throughput against a
/// real Redis instance managed by <see cref="RedisFixture"/>. Prints percentiles via
/// <see cref="ITestOutputHelper"/>. Not a BenchmarkDotNet run - intended as a smoke test for
/// performance regressions, not a formal benchmark.
/// </summary>
[Collection("Redis")]
[Trait("Category", "Integration")]
[Trait("Category", "Benchmark")]
public class RedisLatencyBenchmark : IAsyncLifetime
{
    private readonly RedisFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly RedisSessionStore _store;

    public RedisLatencyBenchmark(RedisFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _store = new RedisSessionStore(fixture.Redis, new RedisSessionStoreOptions());
    }

    public Task InitializeAsync() => _fixture.FlushAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Benchmark_SaveLatency()
    {
        const int iterations = 1000;
        const int warmup = 10;

        for (var i = 0; i < warmup; i++)
        {
            var session = CreateRealisticSession($"warmup-save-{i}");
            await _store.SaveAsync(session, CancellationToken.None);
        }

        var latencies = new double[iterations];
        var sw = new Stopwatch();

        for (var i = 0; i < iterations; i++)
        {
            var session = CreateRealisticSession($"bench-save-{i}");
            sw.Restart();
            await _store.SaveAsync(session, CancellationToken.None);
            sw.Stop();
            latencies[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(latencies);
        LogPercentiles("SaveAsync", latencies, iterations);
    }

    [Fact]
    public async Task Benchmark_GetLatency()
    {
        const int iterations = 1000;
        const int warmup = 10;

        for (var i = 0; i < iterations; i++)
        {
            var session = CreateRealisticSession($"bench-get-{i}");
            await _store.SaveAsync(session, CancellationToken.None);
        }

        for (var i = 0; i < warmup; i++)
        {
            await _store.GetAsync($"bench-get-{i}", CancellationToken.None);
        }

        var latencies = new double[iterations];
        var sw = new Stopwatch();

        for (var i = 0; i < iterations; i++)
        {
            sw.Restart();
            await _store.GetAsync($"bench-get-{i}", CancellationToken.None);
            sw.Stop();
            latencies[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(latencies);
        LogPercentiles("GetAsync", latencies, iterations);
    }

    [Fact]
    public async Task Benchmark_BatchThroughput()
    {
        const int batchCount = 10;
        const int batchSize = 500;
        const int warmup = 10;

        for (var w = 0; w < warmup; w++)
        {
            var warmupBatch = Enumerable.Range(0, 10)
                .Select(i => CreateRealisticSession($"warmup-batch-{w}-{i}"))
                .ToList();
            await _store.SaveBatchAsync(warmupBatch, CancellationToken.None);
        }

        await _fixture.FlushAsync();

        var latencies = new double[batchCount];
        var sw = new Stopwatch();
        var totalSessions = 0;

        for (var b = 0; b < batchCount; b++)
        {
            var batch = Enumerable.Range(0, batchSize)
                .Select(i => CreateRealisticSession($"bench-batch-{b}-{i}"))
                .ToList();

            sw.Restart();
            await _store.SaveBatchAsync(batch, CancellationToken.None);
            sw.Stop();

            latencies[b] = sw.Elapsed.TotalMilliseconds;
            totalSessions += batchSize;
        }

        Array.Sort(latencies);
        var totalTimeMs = latencies.Sum();
        var sessionsPerSec = totalSessions / (totalTimeMs / 1000.0);

        _output.WriteLine("--- SaveBatchAsync ({0} batches x {1} sessions) ---", batchCount, batchSize);
        _output.WriteLine("  p50: {0:F3} ms", latencies[batchCount / 2]);
        _output.WriteLine("  max: {0:F3} ms", latencies[^1]);
        _output.WriteLine("  total: {0:F1} ms for {1} sessions", totalTimeMs, totalSessions);
        _output.WriteLine("  throughput: {0:F0} sessions/sec", sessionsPerSec);
    }

    private static CallSession CreateRealisticSession(string id)
    {
        var session = new CallSession(id, $"linked-{id}", "server-1", CallDirection.Inbound)
        {
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.QueueName = "support";
        session.AgentId = "agent-42";
        session.AgentInterface = "SIP/1001";
        session.BridgeId = $"bridge-{id}";
        session.Context = "from-external";
        session.Extension = "100";

        session.AddParticipant(new SessionParticipant
        {
            UniqueId = $"uid-{id}",
            Channel = $"SIP/trunk-{id}",
            Technology = "SIP",
            Role = ParticipantRole.Caller,
            CallerIdNum = "5551234567",
            CallerIdName = "John Doe",
            JoinedAt = DateTimeOffset.UtcNow,
        });

        session.AddEvent(new CallSessionEvent(
            DateTimeOffset.UtcNow,
            CallSessionEventType.Created,
            $"SIP/trunk-{id}",
            null,
            "Call created"));

        session.AddEvent(new CallSessionEvent(
            DateTimeOffset.UtcNow,
            CallSessionEventType.QueueJoined,
            $"SIP/trunk-{id}",
            null,
            "Joined queue: support"));

        session.SetMetadata("campaign", "spring-sale");
        session.SetMetadata("priority", "high");
        session.SetMetadata("source", "web-callback");

        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Ringing);
        session.Transition(CallSessionState.Queued);
        session.Transition(CallSessionState.Connected);

        return session;
    }

    private void LogPercentiles(string operation, double[] sortedLatencies, int count)
    {
        _output.WriteLine("--- {0} ({1} iterations) ---", operation, count);
        _output.WriteLine("  p50: {0:F3} ms", sortedLatencies[count / 2]);
        _output.WriteLine("  p95: {0:F3} ms", sortedLatencies[(int)(count * 0.95)]);
        _output.WriteLine("  p99: {0:F3} ms", sortedLatencies[(int)(count * 0.99)]);
        _output.WriteLine("  max: {0:F3} ms", sortedLatencies[^1]);
    }
}
