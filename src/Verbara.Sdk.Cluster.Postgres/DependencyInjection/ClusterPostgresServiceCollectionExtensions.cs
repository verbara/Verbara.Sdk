using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Sdk.Cluster.Postgres.DependencyInjection;

/// <summary>
/// DI registration entry point for <see cref="PostgresDistributedLock"/>.
/// </summary>
public static class ClusterPostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers <c>PostgresDistributedLock</c> as the singleton
    /// <see cref="Verbara.Sdk.Cluster.Primitives.IDistributedLock"/>, resolving its
    /// <see cref="Npgsql.NpgsqlDataSource"/> from a keyed singleton named
    /// <paramref name="connectionStringName"/> (default <c>"Cluster"</c>); the data
    /// source must be registered by the host before this call.
    /// </summary>
    public static IServiceCollection AddPostgresDistributedLock(
        this IServiceCollection services,
        string connectionStringName = "Cluster")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

#pragma warning disable MA0025 // Phase B scaffolding — DI wiring lands in next session
        throw new NotImplementedException("Phase B");
#pragma warning restore MA0025
    }
}
