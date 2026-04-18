# Session Store Backends

> Choosing and configuring a `SessionStoreBase` backend for `Asterisk.Sdk.Sessions`.

The Session Engine tracks live call sessions (`CallSession`) from AMI/ARI events and persists them through a pluggable store. Three backends ship as MIT packages on nuget.org:

| Package | Backend | Multi-instance | Crash recovery | Read latency | Recommended for |
|---------|---------|----------------|----------------|--------------|-----------------|
| **`Asterisk.Sdk.Sessions`** (default) | `InMemorySessionStore` | ❌ | ❌ | <0.1 ms | Single-process apps, tests, POCs |
| **`Asterisk.Sdk.Sessions.Redis`** | `RedisSessionStore` | ✅ | ✅ (with `completedRetention`) | <1 ms | HA deployments, low-latency SLAs |
| **`Asterisk.Sdk.Sessions.Postgres`** | `PostgresSessionStore` | ✅ | ✅ (durable) | 5-10 ms | Teams already running Postgres, regulatory/audit workloads |

All three implement the public **`ISessionStore`** interface and derive from **`SessionStoreBase`** — the `SessionReconciliationService` and `CallSessionManager` are agnostic to the backend choice. Switching is a one-line DI change; no code outside the registration needs to move.

---

## Decision guide

**Pick InMemory if:**
- Running a single SDK instance (no horizontal scale)
- Session loss on crash is acceptable (ops workflow handles resync)
- Latency budget is sub-millisecond and every hop counts
- Development / integration tests

**Pick Redis if:**
- Multiple SDK instances must share session state (N:1 or active-active)
- You want sub-millisecond reads on the hot path
- You already operate a Redis fleet (managed cache, in-cluster, or AWS ElastiCache / Azure Cache)
- Completed-session retention of minutes-to-hours is fine (Redis TTL handles GC)

**Pick Postgres if:**
- You already run Postgres (e.g. for Asterisk Realtime, CDR/CEL archive, application data)
- You need durable audit of every session lifecycle (sessions survive Redis flush, cluster rebuild)
- Query-by-column requirements beyond `session_id` / `linked_id` (e.g. analytics over `snapshot` JSONB)
- 5-10 ms read latency is acceptable

---

## `Asterisk.Sdk.Sessions.Redis`

```csharp
using Asterisk.Sdk.Sessions.Redis;

builder.Services
    .AddAsteriskSessionsBuilder()
    .UseRedis(opts =>
    {
        opts.ConfigurationString = "redis.internal:6379,abortConnect=false";
        opts.KeyPrefix = "ast:";                         // default
        opts.CompletedRetention = TimeSpan.FromMinutes(10); // default — Redis TTL
        opts.DatabaseIndex = 0;                          // default
    });
```

Three registration overloads cover common patterns:

```csharp
// 1. Connection string + opts callback
.UseRedis("redis.internal:6379", opts => opts.KeyPrefix = "tenant-a:")

// 2. Pre-built multiplexer (you own the lifecycle)
.UseRedis(existingMultiplexer, opts => opts.KeyPrefix = "tenant-a:")

// 3. Just the opts callback — connection string from opts.ConfigurationString
.UseRedis(opts => { opts.ConfigurationString = cs; })
```

**Data layout** — one JSON blob per session + three indexes:

| Key | Type | Purpose |
|-----|------|---------|
| `ast:session:{id}` | String (JSON) | Snapshot serialized via `SessionJsonContext` (source-gen, AOT-safe) |
| `ast:idx:linked:{linkedId}` | String | Maps `LinkedId → SessionId` for `GetByLinkedIdAsync` |
| `ast:sessions:active` | Set | Membership of non-terminal sessions for `GetActiveAsync` (cursor scan, `pageSize: 500`) |
| `ast:sessions:completed` | Sorted Set (score = completed Unix ms) | Completed sessions with TTL-driven eviction |

I/O is pipelined via `IDatabase.CreateBatch()` + `Task.WhenAll(...).WaitAsync(ct)` — cancellation is honored at entry and around all batch awaits. Completed-retention pruning runs lazily on terminal writes (no background timer).

---

## `Asterisk.Sdk.Sessions.Postgres`

```csharp
using Asterisk.Sdk.Sessions.Postgres;

builder.Services
    .AddAsteriskSessionsBuilder()
    .UsePostgres(opts =>
    {
        opts.ConnectionString = "Host=pg.internal;Database=sessions;Username=asterisk;Password=…;SSL Mode=Require";
        opts.SchemaName = "public";                    // default
        opts.TableName = "asterisk_call_sessions";     // default
    });
```

**Schema setup.** The migration SQL ships inside the NuGet package at `contentFiles/any/any/Migrations/001_create_sessions_table.sql`. Run it once against your database before the app starts — the SDK does **not** auto-migrate:

```sql
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
CREATE INDEX IF NOT EXISTS ix_asterisk_sessions_active    ON asterisk_call_sessions (state) WHERE completed_at IS NULL;
```

**Write path:** UPSERT via `INSERT … ON CONFLICT (session_id) DO UPDATE …`. `SaveBatchAsync` wraps the loop in a transaction with rollback on throw.

**Read path:** `GetActiveAsync` uses the `ix_asterisk_sessions_active` partial index (predicate `completed_at IS NULL`). `GetByLinkedIdAsync` orders by `created_at DESC LIMIT 1` (matches Redis single-session-per-linked semantics).

**Identifier safety.** `TableName` and `SchemaName` are interpolated into SQL (not parameterizable), so they're validated at resolve time against `^[A-Za-z_][A-Za-z0-9_]*$` via `AddOptions<T>().Validate()`. Values are then double-quoted (`"public"."asterisk_call_sessions"`).

**External data source.** If you already build an `NpgsqlDataSource` elsewhere in the host (connection pooling, `DataSourceBuilder` for logical replication, etc.), pass it directly:

```csharp
.UsePostgres(myExistingDataSource, opts => opts.TableName = "asterisk_sessions")
```

---

## Switching backends

`UseRedis` / `UsePostgres` use `IServiceCollection.Replace(...)` so the call always overrides the `InMemorySessionStore` registered by `AddAsteriskSessions` / `AddAsteriskSessionsBuilder`. Order of registration in `Program.cs` doesn't matter; the last `Use*` wins:

```csharp
// Both InMemory (default) and Redis get registered. Redis wins.
builder.Services
    .AddAsteriskSessionsBuilder()
    .UseRedis(opts => opts.ConfigurationString = cs);
```

`ISessionStore` is registered via a factory forwarding to `SessionStoreBase`, so consumers resolving either type get the same singleton instance:

```csharp
var store1 = sp.GetRequiredService<SessionStoreBase>();
var store2 = sp.GetRequiredService<ISessionStore>();
object.ReferenceEquals(store1, store2); // true
```

---

## Custom backends

Implement `SessionStoreBase` (inherit the abstract base or write `: ISessionStore` directly) and register your store as the replacement:

```csharp
public sealed class MyMongoSessionStore : SessionStoreBase { /* … */ }

builder.Services
    .AddAsteriskSessionsBuilder()
    .Services // the underlying IServiceCollection
    .Replace(ServiceDescriptor.Singleton<SessionStoreBase, MyMongoSessionStore>());
```

The serialization DTO `CallSessionSnapshot` is `internal` to `Asterisk.Sdk.Sessions` — reach it from a third-party store by adding your assembly to the `InternalsVisibleTo` grant list (or fork the package).

---

## Benchmarks

See [`docs/analysis/benchmark-analysis.md`](../analysis/benchmark-analysis.md) for the full methodology. Quick reference on AMD Ryzen 9 9900X, Postgres 16 local, Redis 7 local:

| Operation | InMemory | Redis | Postgres |
|-----------|----------|-------|----------|
| `SaveAsync` (single session) | ~50 ns | ~250 μs | ~1.5 ms |
| `GetAsync` (single) | ~30 ns | ~200 μs | ~1.2 ms |
| `GetActiveAsync` (1,000 active) | ~10 μs | ~8 ms | ~12 ms |
| `SaveBatchAsync` (100 sessions) | ~5 μs | ~3 ms | ~15 ms (single tx) |

Numbers are order-of-magnitude indicators from the `RedisLatencyBenchmark` / `PostgresLatencyBenchmark` smoke tests and will vary by network, cluster configuration, and hardware. **If latency matters, benchmark on your topology.**
