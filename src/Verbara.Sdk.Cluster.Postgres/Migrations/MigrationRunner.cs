using Microsoft.Extensions.Logging;
using Npgsql;

namespace Verbara.Sdk.Cluster.Postgres.Migrations;

/// <summary>
/// Idempotent schema migration runner for the <c>cluster_distributed_lock</c> table.
/// Runs a constant, ordered list of embedded SQL resources — <c>V001__DistributedLockSchema.sql</c>
/// (the table, byte for byte as earlier releases shipped it) then <c>V002__DistributedLockAcquiredAt.sql</c>
/// (the nullable <c>acquired_at</c> column) — read via
/// <c>typeof(MigrationRunner).Assembly.GetManifestResourceStream(...)</c> with constant names (AOT-safe;
/// no reflection-based discovery), against the supplied <see cref="NpgsqlDataSource"/>. There is no
/// version table: every statement is guarded by <c>IF NOT EXISTS</c>, so the whole list runs on every
/// call and is safe to invoke repeatedly.
/// </summary>
/// <remarks>
/// <para>
/// <c>acquired_at</c> is the time the current <c>owner</c> name took the lock, by the server clock. It
/// measures the tenure of the owner <em>name</em> (typically the machine or pod name), not of the
/// process: a renewal by the same owner before <c>expires_at</c> keeps it, so a process restarted under
/// the same name inherits the time of the one that died; an acquisition of a free or expired row stamps
/// <c>NOW()</c>. It is <c>NULL</c> ("unknown") on rows that existed before V002 and on rows written by
/// an earlier SDK, and an earlier SDK taking an expired row leaves the previous tenure's value in
/// place. A reader that needs its own tenure uses
/// <c>max(acquired_at, the time it first observed this owner)</c>, and treats <c>NULL</c> as unknown.
/// </para>
/// </remarks>
public static partial class MigrationRunner
{
    /// <summary>The migrations in the order they run. Never reorder; append only.</summary>
    private static readonly string[] MigrationResourceNames =
    [
        "Verbara.Sdk.Cluster.Postgres.Migrations.V001__DistributedLockSchema.sql",
        "Verbara.Sdk.Cluster.Postgres.Migrations.V002__DistributedLockAcquiredAt.sql",
    ];

    /// <summary>
    /// Ensure the <c>cluster_distributed_lock</c> schema exists and is current. Idempotent; consumers
    /// invoke once at startup.
    /// </summary>
    public static async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var resourceName in MigrationResourceNames)
        {
            await using var cmd = dataSource.CreateCommand(LoadEmbeddedMigrationSql(resourceName));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        LogSchemaEnsured(logger);
    }

    private static string LoadEmbeddedMigrationSql(string resourceName)
    {
        // Assembly access is required to load the embedded SQL resources. The
        // GetManifestResourceStream call takes a constant resource name (no reflection
        // discovery) and is AOT-safe — mirrors the pattern used by OnnxSessionManager.
#pragma warning disable RS0030 // Banned API — no source-generator alternative for embedded resources
        var assembly = typeof(MigrationRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
#pragma warning restore RS0030
            ?? throw new InvalidOperationException(
                $"Embedded migration resource not found: {resourceName}. " +
                "Verify the .csproj <EmbeddedResource> includes under Migrations\\.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "cluster_distributed_lock schema ensured (Verbara.Sdk.Cluster.Postgres V001-V002 migrations applied or already present).")]
    private static partial void LogSchemaEnsured(ILogger logger);
}
