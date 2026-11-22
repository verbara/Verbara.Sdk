# Verbara.Sdk.Cluster.Postgres

MIT-licensed Postgres-backed implementation of `IDistributedLock` from `Verbara.Sdk.Cluster.Primitives`. AOT-safe (raw Npgsql via the `Verbara.Sdk.Data.Npgsql` facade; no reflection).

## What it does

Implements cluster-wide mutual exclusion against a single Postgres table (`cluster_distributed_lock`) using atomic upserts with TTL. Ships an idempotent migration that creates the table on demand.

| Type | Role |
|---|---|
| `PostgresDistributedLock` (internal) | `IDistributedLock` implementation. Registered via the DI extension below. |
| `ClusterPostgresServiceCollectionExtensions.AddPostgresDistributedLock(...)` | DI registration entry point. |
| `Migrations.MigrationRunner.EnsureSchemaAsync(...)` | One-shot idempotent migration runner. |

The lock semantics match `IDistributedLock`: TryAcquire returns true if the caller now holds the lock; re-acquisition by the same owner refreshes the expiry; Release is a no-op when not currently owned by the supplied owner.

### Since when the owner holds the lock (`acquired_at`)

Each row carries `acquired_at`: the server time at which the current `owner` name took the lock.

- It measures the tenure of the **owner name** (typically the machine or pod name), not of the process. A renewal by the same owner before `expires_at` keeps it, so a process restarted under the same name before expiry inherits the time of the one that died.
- Taking a free row, or winning an expired one (whoever held it, the same owner included), stamps `NOW()`.
- Rows that existed before the column was added stay `NULL` ("unknown"); no time is invented for them, and a renewal by the same owner keeps them `NULL` until the lock expires and is taken again.
- During a staggered deploy, a node still on an earlier SDK sets only `owner` and `expires_at`: its rows are `NULL`, and its takeover of an expired row leaves the previous tenure's `acquired_at` in place.

A reader that needs the tenure of what it is watching therefore uses `max(acquired_at, the time it first observed this owner)` and treats `NULL` as unknown. There is no API for it: read the table.

## Why a separate package

The lock is a generic primitive — both `Verbara.Sdk.Pro.Cluster` (leader election) and any future SDK consumer can depend on it independently of the Pro commercial license. Placing the implementation in the MIT SDK avoids fragmenting cluster-wide locking by deployment tier.

## Install

```sh
dotnet add package Verbara.Sdk.Cluster.Postgres
```

## Quick start

```csharp
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Verbara.Sdk.Cluster.Postgres.DependencyInjection;
using Verbara.Sdk.Cluster.Postgres.Migrations;

services.AddKeyedSingleton<NpgsqlDataSource>("Cluster", (_, _) =>
    NpgsqlDataSource.Create(configuration.GetConnectionString("Cluster")!));

services.AddPostgresDistributedLock(connectionStringName: "Cluster");

// At startup, once:
await MigrationRunner.EnsureSchemaAsync(dataSource, logger, ct);
```

## Schema

```sql
-- V001
CREATE TABLE IF NOT EXISTS cluster_distributed_lock (
    resource TEXT PRIMARY KEY,
    owner    TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL
);
-- V002
ALTER TABLE cluster_distributed_lock ADD COLUMN IF NOT EXISTS acquired_at TIMESTAMPTZ;
```

`EnsureSchemaAsync` runs both, in order, on every call; there is no version table. Each statement is idempotent, and several hosts may run it at once against the same database.

## AOT

Trimming/AOT-safe. No reflection, no dynamic code generation. The migration runner reads its embedded `V001__DistributedLockSchema.sql` and `V002__DistributedLockAcquiredAt.sql` resources, a constant ordered list, via `typeof(MigrationRunner).Assembly.GetManifestResourceStream(...)` — the constant-name lookups are AOT-friendly.

## License

MIT. See repository root.
