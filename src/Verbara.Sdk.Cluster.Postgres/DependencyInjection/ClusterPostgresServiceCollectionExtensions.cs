using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Verbara.Sdk.Cluster.Primitives;

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

        // TimeProvider.System satisfies the ctor for production hosts; tests inject a fake
        // TimeProvider directly into PostgresDistributedLock without going through DI.
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IDistributedLock>(sp =>
        {
            var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(connectionStringName);
            var logger = sp.GetRequiredService<ILogger<PostgresDistributedLock>>();
            var timeProvider = sp.GetRequiredService<TimeProvider>();
            return new PostgresDistributedLock(dataSource, logger, timeProvider);
        });

        return services;
    }
}
