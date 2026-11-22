# Session Store Backends

> Choosing and configuring a `SessionStoreBase` backend for `Verbara.Sdk.Sessions`.

The Session Engine tracks live call sessions (`CallSession`) from AMI/ARI events and persists them through a pluggable store. Three backends ship as MIT packages on nuget.org:

| Package | Backend | Multi-instance | Crash recovery | Read latency | Recommended for |
|---------|---------|----------------|----------------|--------------|-----------------|
| **`Verbara.Sdk.Sessions`** (default) | `InMemorySessionStore` | ❌ | ❌ | In-process: no I/O, no serialization | Single-process apps, tests, POCs |
| **`Verbara.Sdk.Sessions.Redis`** | `RedisSessionStore` | ✅ | ✅ (with `completedRetention`) | Network round trip: one `GET` for `GetAsync`, two pipelined round trips for `GetByLinkedIdAsync` | HA deployments, low-latency SLAs |
| **`Verbara.Sdk.Sessions.Postgres`** | `PostgresSessionStore` | ✅ | ✅ (durable) | Network I/O: one `SELECT` per read; by default each single save also waits for a WAL flush | Teams already running Postgres, regulatory/audit workloads |

All three implement the public **`ISessionStore`** interface and derive from **`SessionStoreBase`** — the session manager (`CallSessionManager`) and the reconciliation sweep are agnostic to the backend choice. Switching is a one-line DI change; no code outside the registration needs to move.

**Lookup by `linkedid` with several servers.** Two Asterisk servers can report the same `linkedid` for two unrelated calls. Every store keeps each server's session apart and offers two lookups, the same pair the session manager has:

- `GetByLinkedIdAsync(serverId, linkedId, ct)` returns the session the named server holds under that `linkedid`, or `null`.
- `GetByLinkedIdAsync(linkedId, ct)` returns the session when exactly **one** server holds that `linkedid`, and `null` when none or two or more do. Treat its `null` as "absent or not unique". With one server and one session under the id the answer is the same as before; when one server holds two sessions under the id, see the newest rule below.

A server "holds" the id while the store keeps its session: Redis until its `CompletedRetention` runs out, InMemory until the session manager releases it, Postgres for as long as the row is kept — so in Postgres a `linkedid` two servers ever used stays ambiguous to the unscoped lookup; use the scoped one. Within one server each store answers with the newest session (Postgres and InMemory by `CreatedAt`, Redis the last saved). A store of your own that derives from `SessionStoreBase` or implements `ISessionStore` gets a default scoped lookup that keeps the unscoped answer only when it belongs to the named server, so it can answer `null` for an id two servers hold until you override it.

---

## Decision guide

**Pick InMemory if:**
- Running a single SDK instance (no horizontal scale)
- Session loss on crash is acceptable (ops workflow handles resync)
- Store calls must stay in-process: no network hop, no serialization
- Development / integration tests

**Pick Redis if:**
- Multiple SDK instances must share session state (N:1 or active-active)
- Saves are on the hot path: of the two networked backends, Redis measures the faster single save (see [Benchmarks](#benchmarks))
- You already operate a Redis fleet (managed cache, in-cluster, or AWS ElastiCache / Azure Cache)
- Completed-session retention of minutes-to-hours is fine (Redis TTL handles GC)

**Pick Postgres if:**
- You already run Postgres (e.g. for Asterisk Realtime, CDR/CEL archive, application data)
- You need durable audit of every session lifecycle (sessions survive Redis flush, cluster rebuild)
- Query-by-column requirements beyond `session_id` / `linked_id` (e.g. analytics over `snapshot` JSONB)
- Each single save can wait for a durable commit (one WAL flush, under the default `synchronous_commit=on`); a `SaveBatchAsync` shares one, and reads are not the slow path

---

## `Verbara.Sdk.Sessions.Redis`

<!-- skip-doc-snippet -->
```csharp
using Verbara.Sdk.Sessions.Redis;

builder.Services
    .AddVerbaraSessionsBuilder()
    .UseRedis(opts =>
    {
        opts.ConfigurationString = "redis.internal:6379,abortConnect=false";
        opts.KeyPrefix = "ast:";                         // default
        opts.CompletedRetention = TimeSpan.FromMinutes(10); // default — Redis TTL
        opts.DatabaseIndex = 0;                          // default
    });
```

Three registration overloads cover common patterns:

<!-- skip-doc-snippet -->
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
| `ast:idx:linked-by-server:{linkedId}` | Hash | One field per server id, value `<expiryUnixMs>\|<sessionId>` (`0` = live), for both `GetByLinkedIdAsync` lookups |
| `ast:sessions:active` | Set | Membership of non-terminal sessions for `GetActiveAsync` (cursor scan, `pageSize: 500`) |
| `ast:sessions:completed` | Sorted Set (score = completed Unix ms) | Completed sessions with TTL-driven eviction |

I/O is pipelined via `IDatabase.CreateBatch()` + `Task.WhenAll(...).WaitAsync(ct)` — cancellation is honored at entry and around all batch awaits. Completed-retention pruning runs lazily on terminal writes (no background timer).

**The `linkedid` index.** Each server writes, removes and expires only its own field of the hash. A live save writes its field with `HSET` and then clears the key's expiry with `PERSIST`, in the same batch as the session key. Ending or deleting a session runs a single-key Lua script (atomic): a delete removes the field only while it still names that session, and the key has no expiry while any field is live and otherwise expires with the latest ended one. Deleting or expiring one server's session therefore never hides another server's. Every script names one key, so none is refused with `CROSSSLOT` on Redis Cluster; there a session key and its index entry are in different slots and are not written in order, so a lookup that races a session's first save can miss it once. The suite runs against a single node. A lookup reads the hash (and the key of earlier versions, below), then the candidate sessions — two pipelined round trips — counts a candidate only while its session key exists, and writes nothing: an entry whose session is gone is ignored, not removed.

**Requirements.** Redis **5.0 or later** (the script reads the server clock with `TIME`), and a connection whose user may run scripts: `EVALSHA`/`EVAL` and `SCRIPT LOAD` (ACL category `@scripting`) plus the commands the scripts call (`TIME`, `HSET`, `HGET`, `HDEL`, `HGETALL`, `PERSIST`, `PEXPIREAT`, `GET`, `DEL`, `PEXPIRE`). A deployment or proxy that refuses scripting is not supported: live saves succeed there, but ending or deleting a session fails.

**Cost.** A live save writes two index commands (`HSET` + `PERSIST`) where 2.10.x wrote one `SET`; ending or deleting a session runs one script per session, sent after the save's batch.

**Concurrent saves of one session.** The store does not order two saves of the same session against each other: two concurrent saves of one session (for example a live and an ended snapshot from two places) can leave the session key and its index entry from different saves. Save a session from one place at a time, as the session manager does.

**Upgrading from 2.10.x.** Earlier versions kept one string per `linkedid`, `ast:idx:linked:{linkedId}`, which the last writer overwrote. It is not migrated: this store still reads it, counting the session it names as that session's server unless that server has a field in the hash, and never writes it again — it only gives it its retention when the session it names ends, and removes it when that session is deleted. Nodes of both versions can run side by side during a rolling upgrade (the keys differ in name and type, so neither gets `WRONGTYPE`), but a 2.10.x node does not find by `linkedid` a session a newer node created. Tools of your own that read `idx:linked:` must read the hash instead.

---

## `Verbara.Sdk.Sessions.Postgres`

<!-- skip-doc-snippet -->
```csharp
using Verbara.Sdk.Sessions.Postgres;

builder.Services
    .AddVerbaraSessionsBuilder()
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

**Read path:** `GetActiveAsync` uses the `ix_asterisk_sessions_active` partial index (predicate `completed_at IS NULL`). `GetByLinkedIdAsync(linkedId)` reads `DISTINCT ON (server_id) … ORDER BY server_id, created_at DESC LIMIT 2` through `ix_asterisk_sessions_linked_id` and answers only when one row comes back (one server); `GetByLinkedIdAsync(serverId, linkedId)` adds `AND server_id = @server` and returns that server's newest row. No migration or new index is needed.

**Identifier safety.** `TableName` and `SchemaName` are interpolated into SQL (not parameterizable), so they're validated at resolve time against `^[A-Za-z_][A-Za-z0-9_]*$` via `AddOptions<T>().Validate()`. Values are then double-quoted (`"public"."asterisk_call_sessions"`).

**External data source.** If you already build an `NpgsqlDataSource` elsewhere in the host (connection pooling, `DataSourceBuilder` for logical replication, etc.), pass it directly:

<!-- skip-doc-snippet -->
```csharp
.UsePostgres(myExistingDataSource, opts => opts.TableName = "asterisk_sessions")
```

---

## Switching backends

`UseRedis` / `UsePostgres` use `IServiceCollection.Replace(...)` so the call always overrides the `InMemorySessionStore` registered by `AddVerbaraSessions` / `AddVerbaraSessionsBuilder`. Order of registration in `Program.cs` doesn't matter; the last `Use*` wins:

<!-- skip-doc-snippet -->
```csharp
// Both InMemory (default) and Redis get registered. Redis wins.
builder.Services
    .AddVerbaraSessionsBuilder()
    .UseRedis(opts => opts.ConfigurationString = cs);
```

`ISessionStore` is registered via a factory forwarding to `SessionStoreBase`, so consumers resolving either type get the same singleton instance:

<!-- skip-doc-snippet -->
```csharp
var store1 = sp.GetRequiredService<SessionStoreBase>();
var store2 = sp.GetRequiredService<ISessionStore>();
object.ReferenceEquals(store1, store2); // true
```

---

## Custom backends

Implement `SessionStoreBase` (inherit the abstract base or write `: ISessionStore` directly) and register your store as the replacement:

<!-- skip-doc-snippet -->
```csharp
public sealed class MyMongoSessionStore : SessionStoreBase { /* … */ }

builder.Services
    .AddVerbaraSessionsBuilder()
    .Services // the underlying IServiceCollection
    .Replace(ServiceDescriptor.Singleton<SessionStoreBase, MyMongoSessionStore>());
```

The serialization DTO `CallSessionSnapshot` is `internal` to `Verbara.Sdk.Sessions` — reach it from a third-party store by adding your assembly to the `InternalsVisibleTo` grant list (or fork the package).

---

## Benchmarks

Two figures per networked backend, re-measured 2026-09-12 on .NET 10.0.12, AMD Ryzen 9 9900X, with an xunit `Fact` + `Stopwatch` (`RedisLatencyBenchmark` / `PostgresLatencyBenchmark`) against loopback Docker with no TLS — Redis 7.4.8, PostgreSQL 18.4 — median of five runs. They are bound to `Tests/Verbara.Sdk.Benchmarks/performance-record.json`, as `README.md`'s Performance table is.

| Backend | `SaveAsync` (single session) | `SaveBatchAsync` (500 sessions) |
|---------|------------------------------|---------------------------------|
| Redis | p50 30 µs | batch 91,021 sess/sec |
| Postgres | p50 1.97 ms | batch 13,489 sess/sec |

Postgres single-save latency is the measuring machine's WAL flush — one per commit — not SDK code; a batch commits once. `GetAsync`, the p95/p99/max percentiles, each of the five runs and the reproduce commands are in the addendum. Nothing records a measurement of InMemory, `GetActiveAsync` or batches of other sizes, so no figure is published for them. Loopback without TLS is a best-case baseline, not production sizing: **if latency matters, benchmark on your topology.**
