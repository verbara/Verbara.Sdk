using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Verbara.Sdk.Cluster.Primitives;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Sdk.Cluster.Postgres;

/// <summary>
/// Postgres-backed <see cref="IDistributedLock"/> implementation. Uses a single-row
/// <c>cluster_distributed_lock</c> table with an atomic upsert keyed on
/// <c>resource</c> (primary key); a row is owned by the supplied <em>owner</em> token
/// until either the caller releases it or <c>expires_at</c> elapses. Server-side
/// <c>NOW()</c> comparisons make pod clock skew irrelevant.
/// </summary>
/// <remarks>
/// <para>
/// The acquire SQL is a single statement: an <c>INSERT … ON CONFLICT (resource) DO UPDATE
/// … WHERE owner = EXCLUDED.owner OR expires_at &lt; NOW()</c> with a <c>RETURNING</c>
/// projection that yields a single boolean <c>won</c>. Postgres evaluates the WHERE on the
/// conflicting row atomically; a contender either wins (own row inserted, or the held row
/// was expired / belongs to the same owner) or loses (RETURNING yields no row).
/// </para>
/// <para>
/// Release is an unconditional <c>DELETE WHERE resource = … AND owner = …</c>; a no-op when
/// the lock is not held by the supplied owner, per the <see cref="IDistributedLock"/> contract.
/// </para>
/// </remarks>
internal sealed class PostgresDistributedLock : IDistributedLock
{
    private const string AcquireSql = """
        INSERT INTO cluster_distributed_lock (resource, owner, expires_at)
        VALUES (@resource, @owner, NOW() + @expiry_interval)
        ON CONFLICT (resource) DO UPDATE
        SET owner = EXCLUDED.owner, expires_at = EXCLUDED.expires_at
        WHERE cluster_distributed_lock.owner = @owner
           OR cluster_distributed_lock.expires_at < NOW()
        RETURNING owner = @owner AS won;
        """;

    private const string ReleaseSql = """
        DELETE FROM cluster_distributed_lock
        WHERE resource = @resource AND owner = @owner;
        """;

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
    public async ValueTask<bool> TryAcquireAsync(string resource, string owner, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resource);
        ArgumentException.ThrowIfNullOrEmpty(owner);

        // QueryFirstOrDefaultAsync<bool?> would return null when the WHERE clause rejects the
        // conflict update (no RETURNING row). A null result means "lock held by another active
        // owner" → caller did NOT win.
        var won = await _dataSource.QueryFirstOrDefaultAsync<bool?>(
            AcquireSql,
            p =>
            {
                p.Add(new NpgsqlParameter("resource", NpgsqlDbType.Text) { Value = resource });
                p.Add(new NpgsqlParameter("owner", NpgsqlDbType.Text) { Value = owner });
                p.Add(new NpgsqlParameter("expiry_interval", NpgsqlDbType.Interval) { Value = expiry });
            },
            r => r.GetBoolean(0),
            cancellationToken).ConfigureAwait(false);

        return won == true;
    }

    /// <inheritdoc />
    public async ValueTask ReleaseAsync(string resource, string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resource);
        ArgumentException.ThrowIfNullOrEmpty(owner);

        await _dataSource.ExecuteAsync(
            ReleaseSql,
            p =>
            {
                p.Add(new NpgsqlParameter("resource", NpgsqlDbType.Text) { Value = resource });
                p.Add(new NpgsqlParameter("owner", NpgsqlDbType.Text) { Value = owner });
            },
            cancellationToken).ConfigureAwait(false);
    }
}
