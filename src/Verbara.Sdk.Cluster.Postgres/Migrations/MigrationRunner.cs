using Microsoft.Extensions.Logging;
using Npgsql;

namespace Verbara.Sdk.Cluster.Postgres.Migrations;

/// <summary>
/// Idempotent schema migration runner for the <c>cluster_distributed_lock</c> table.
/// Reads the embedded <c>V001__DistributedLockSchema.sql</c> resource via
/// <c>typeof(MigrationRunner).Assembly.GetManifestResourceStream(...)</c> (AOT-safe;
/// no reflection-based discovery) and executes it against the supplied
/// <see cref="NpgsqlDataSource"/>. Safe to invoke repeatedly — every statement in the
/// migration is guarded by <c>IF NOT EXISTS</c>.
/// </summary>
public static class MigrationRunner
{
    /// <summary>
    /// Ensure the <c>cluster_distributed_lock</c> schema exists. Idempotent; consumers
    /// invoke once at startup.
    /// </summary>
    public static Task EnsureSchemaAsync(NpgsqlDataSource dataSource, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);

#pragma warning disable MA0025 // Phase B scaffolding — embedded-resource read + ExecuteAsync land in next session
        throw new NotImplementedException("Phase B");
#pragma warning restore MA0025
    }
}
