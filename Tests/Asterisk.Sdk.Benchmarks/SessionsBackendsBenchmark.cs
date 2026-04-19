using Asterisk.Sdk;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Postgres;
using Asterisk.Sdk.Sessions.Redis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Dapper;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace Asterisk.Sdk.Benchmarks;

/// <summary>
/// Measures <see cref="ISessionStore"/> throughput and latency for the Redis and Postgres
/// backends shipped in v1.11.0. Each backend has its own Testcontainers instance started in
/// <c>[GlobalSetup]</c> and disposed in <c>[GlobalCleanup]</c>. Not a production-tuning tool
/// — small sample (ShortRunJob); use for comparison/regression only.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 5)]
public class SessionsBackendsBenchmark
{
    private IContainer? _redisContainer;
    private IContainer? _postgresContainer;
    private ConnectionMultiplexer? _redisConn;
    private NpgsqlDataSource? _postgresDs;

    private RedisSessionStore _redisStore = null!;
    private PostgresSessionStore _postgresStore = null!;

    private CallSession _probeSession = null!;
    private string _probeSessionId = null!;
    private string _probeLinkedId = null!;
    private IReadOnlyList<CallSession> _batch = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        // --- Redis container ---
        _redisContainer = new ContainerBuilder()
            .WithImage("redis:7-alpine")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();
        await _redisContainer.StartAsync();
        var redisPort = _redisContainer.GetMappedPublicPort(6379);
        _redisConn = await ConnectionMultiplexer.ConnectAsync($"localhost:{redisPort}");
        var redisOptions = Options.Create(new RedisSessionStoreOptions
        {
            KeyPrefix = "bench:",
            CompletedRetention = TimeSpan.FromMinutes(5),
        });
        _redisStore = new RedisSessionStore(_redisConn, redisOptions);

        // --- Postgres container ---
        _postgresContainer = new ContainerBuilder()
            .WithImage("postgres:16-alpine")
            .WithPortBinding(5432, true)
            .WithEnvironment("POSTGRES_USER", "postgres")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "asterisk_bench")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-d", "asterisk_bench"))
            .Build();
        await _postgresContainer.StartAsync();
        var pgPort = _postgresContainer.GetMappedPublicPort(5432);
        var pgConn = $"Host=localhost;Port={pgPort};Database=asterisk_bench;Username=postgres;Password=postgres;SSL Mode=Disable";
        _postgresDs = NpgsqlDataSource.Create(pgConn);

        const string migrationSql = """
            CREATE TABLE IF NOT EXISTS asterisk_call_sessions (
                session_id   TEXT        PRIMARY KEY,
                linked_id    TEXT        NOT NULL,
                server_id    TEXT        NOT NULL,
                state        SMALLINT    NOT NULL,
                direction    SMALLINT    NOT NULL,
                created_at   TIMESTAMPTZ NOT NULL,
                updated_at   TIMESTAMPTZ NOT NULL,
                completed_at TIMESTAMPTZ NULL,
                snapshot     JSONB       NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_asterisk_sessions_linked_id ON asterisk_call_sessions (linked_id);
            CREATE INDEX IF NOT EXISTS ix_asterisk_sessions_active ON asterisk_call_sessions (state) WHERE completed_at IS NULL;
            """;
        await using (var conn = await _postgresDs.OpenConnectionAsync())
            await conn.ExecuteAsync(migrationSql);

        var pgOptions = Options.Create(new PostgresSessionStoreOptions
        {
            TableName = "asterisk_call_sessions",
            SchemaName = "public",
        });
        _postgresStore = new PostgresSessionStore(_postgresDs, pgOptions);

        // --- Seed probe + active sets ---
        _probeSessionId = "probe-000";
        _probeLinkedId = "probe-linked-000";
        _probeSession = BuildSession(_probeSessionId, _probeLinkedId);
        await _redisStore.SaveAsync(_probeSession, default);
        await _postgresStore.SaveAsync(_probeSession, default);

        _batch = Enumerable.Range(0, 100).Select(i => BuildSession($"batch-{i:000}", $"batch-linked-{i:000}")).ToList();

        // Seed 1000 active sessions so GetActive benchmarks have realistic work.
        for (int i = 0; i < 1000; i++)
        {
            var s = BuildSession($"active-{i:0000}", $"active-linked-{i:0000}");
            await _redisStore.SaveAsync(s, default);
            await _postgresStore.SaveAsync(s, default);
        }
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_postgresDs is not null) await _postgresDs.DisposeAsync();
        if (_postgresContainer is not null) await _postgresContainer.DisposeAsync();
        if (_redisConn is not null) await _redisConn.DisposeAsync();
        if (_redisContainer is not null) await _redisContainer.DisposeAsync();
    }

    private static CallSession BuildSession(string sessionId, string linkedId)
    {
        var s = new CallSession(sessionId, linkedId, "bench-srv", CallDirection.Inbound);
        s.SetMetadata("bench", "v1.11");
        return s;
    }

    // ---------------- Redis ----------------

    [Benchmark]
    [BenchmarkCategory("Save", "Redis")]
    public async ValueTask RedisSave() => await _redisStore.SaveAsync(_probeSession, default);

    [Benchmark]
    [BenchmarkCategory("Get", "Redis")]
    public async ValueTask<CallSession?> RedisGet() => await _redisStore.GetAsync(_probeSessionId, default);

    [Benchmark]
    [BenchmarkCategory("GetByLinkedId", "Redis")]
    public async ValueTask<CallSession?> RedisGetByLinkedId() => await _redisStore.GetByLinkedIdAsync(_probeLinkedId, default);

    [Benchmark]
    [BenchmarkCategory("GetActive_1000", "Redis")]
    public async ValueTask<int> RedisGetActive() => (await _redisStore.GetActiveAsync(default)).Count();

    [Benchmark]
    [BenchmarkCategory("SaveBatch_100", "Redis")]
    public async ValueTask RedisSaveBatch() => await _redisStore.SaveBatchAsync(_batch, default);

    // ---------------- Postgres ----------------

    [Benchmark]
    [BenchmarkCategory("Save", "Postgres")]
    public async ValueTask PostgresSave() => await _postgresStore.SaveAsync(_probeSession, default);

    [Benchmark]
    [BenchmarkCategory("Get", "Postgres")]
    public async ValueTask<CallSession?> PostgresGet() => await _postgresStore.GetAsync(_probeSessionId, default);

    [Benchmark]
    [BenchmarkCategory("GetByLinkedId", "Postgres")]
    public async ValueTask<CallSession?> PostgresGetByLinkedId() => await _postgresStore.GetByLinkedIdAsync(_probeLinkedId, default);

    [Benchmark]
    [BenchmarkCategory("GetActive_1000", "Postgres")]
    public async ValueTask<int> PostgresGetActive() => (await _postgresStore.GetActiveAsync(default)).Count();

    [Benchmark]
    [BenchmarkCategory("SaveBatch_100", "Postgres")]
    public async ValueTask PostgresSaveBatch() => await _postgresStore.SaveBatchAsync(_batch, default);
}
