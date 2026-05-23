using Microsoft.Extensions.Logging;
using Npgsql;
using Verbara.Sdk.Cluster.Primitives;

namespace Verbara.Sdk.Cluster.Postgres;

/// <summary>
/// Postgres-backed <see cref="IDistributedLock"/> implementation. Uses a single-row
/// <c>cluster_distributed_lock</c> table with an atomic upsert keyed on
/// <c>resource</c> (primary key); a row is owned by the supplied <em>owner</em> token
/// until either the caller releases it or <c>expires_at</c> elapses. Server-side
/// <c>NOW()</c> comparisons make pod clock skew irrelevant.
/// </summary>
/// <remarks>
/// Phase B (ADR-0022 Phase A.5) supplies the SQL bodies. Phase A scaffolds the type
/// shape so consumer wiring (Pro.Cluster leader election + Realtime relay gating) can
/// compile against a stable surface while the implementation is TDD-driven against a
/// real Postgres 18 fixture in the next session.
/// </remarks>
internal sealed class PostgresDistributedLock : IDistributedLock
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresDistributedLock> _logger;
    private readonly TimeProvider _timeProvider;

    public PostgresDistributedLock(
        NpgsqlDataSource dataSource,
        ILogger<PostgresDistributedLock> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dataSource = dataSource;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
#pragma warning disable MA0025 // Phase B scaffolding — TDD implementation lands in next session
    public ValueTask<bool> TryAcquireAsync(string resource, string owner, TimeSpan expiry, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Phase B");

    /// <inheritdoc />
    public ValueTask ReleaseAsync(string resource, string owner, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Phase B");
#pragma warning restore MA0025
}
