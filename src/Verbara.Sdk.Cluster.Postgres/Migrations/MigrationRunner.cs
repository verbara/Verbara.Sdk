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
public static partial class MigrationRunner
{
    private const string MigrationResourceName =
        "Verbara.Sdk.Cluster.Postgres.Migrations.V001__DistributedLockSchema.sql";

    /// <summary>
    /// Ensure the <c>cluster_distributed_lock</c> schema exists. Idempotent; consumers
    /// invoke once at startup.
    /// </summary>
    public static async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);

        var sql = LoadEmbeddedMigrationSql();

        await using var cmd = dataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        LogSchemaEnsured(logger);
    }

    private static string LoadEmbeddedMigrationSql()
    {
        // Assembly access is required to load the embedded V001 SQL resource. The
        // GetManifestResourceStream call takes a constant resource name (no reflection
        // discovery) and is AOT-safe — mirrors the pattern used by OnnxSessionManager.
#pragma warning disable RS0030 // Banned API — no source-generator alternative for embedded resources
        var assembly = typeof(MigrationRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream(MigrationResourceName)
#pragma warning restore RS0030
            ?? throw new InvalidOperationException(
                $"Embedded migration resource not found: {MigrationResourceName}. " +
                "Verify the .csproj <EmbeddedResource> include for V001__DistributedLockSchema.sql.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "cluster_distributed_lock schema ensured (Verbara.Sdk.Cluster.Postgres V001 migration applied or already present).")]
    private static partial void LogSchemaEnsured(ILogger logger);
}
