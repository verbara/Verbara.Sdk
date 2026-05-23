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
CREATE TABLE IF NOT EXISTS cluster_distributed_lock (
    resource TEXT PRIMARY KEY,
    owner    TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL
);
```

## AOT

Trimming/AOT-safe. No reflection, no dynamic code generation. The migration runner reads its embedded `V001__DistributedLockSchema.sql` resource via `typeof(MigrationRunner).Assembly.GetManifestResourceStream(...)` — the constant-name lookup is AOT-friendly.

## License

MIT. See repository root.
