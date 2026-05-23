using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Verbara.Sdk.Cluster.Postgres.Migrations;
using Xunit;

namespace Verbara.Sdk.Cluster.Postgres.Tests;

/// <summary>
/// Testcontainers-backed Postgres 18 fixture. Spins up <c>postgres:18-alpine</c> in a disposable
/// container, runs <see cref="MigrationRunner.EnsureSchemaAsync"/> to create the
/// <c>cluster_distributed_lock</c> table, and exposes a ready <see cref="NpgsqlDataSource"/>.
/// Mirrors the canonical <c>Verbara.Sdk.Sessions.Postgres.Tests.PostgresFixture</c>.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string DbUser = "postgres";
    private const string DbPassword = "postgres";
    private const string DbName = "verbara_cluster_test";

    private IContainer? _container;
    private NpgsqlDataSource? _dataSource;

    public NpgsqlDataSource DataSource =>
        _dataSource ?? throw new InvalidOperationException("Postgres fixture not initialized.");

    public string ConnectionString =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        $"Database={DbName};Username={DbUser};Password={DbPassword};SSL Mode=Disable";

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("postgres:18-alpine")
            .WithPortBinding(5432, true)
            .WithEnvironment("POSTGRES_USER", DbUser)
            .WithEnvironment("POSTGRES_PASSWORD", DbPassword)
            .WithEnvironment("POSTGRES_DB", DbName)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilCommandIsCompleted("pg_isready", "-U", DbUser, "-d", DbName))
            .Build();

        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(ConnectionString);

        // CI runners sometimes reset the TCP stream within ~100ms of pg_isready succeeding.
        await OpenWithRetryAsync(_dataSource, attempts: 10, delay: TimeSpan.FromMilliseconds(500));

        await MigrationRunner.EnsureSchemaAsync(_dataSource, NullLogger.Instance);
    }

    private static async Task OpenWithRetryAsync(NpgsqlDataSource dataSource, int attempts, TimeSpan delay)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                await using var conn = await dataSource.OpenConnectionAsync();
                return;
            }
            catch (NpgsqlException) when (i < attempts - 1)
            {
                await Task.Delay(delay);
            }
            catch (System.Net.Sockets.SocketException) when (i < attempts - 1)
            {
                await Task.Delay(delay);
            }
        }
        throw new InvalidOperationException("Could not open Postgres connection after retries.");
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
            await _dataSource.DisposeAsync();
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

#pragma warning disable CA1711 // Identifiers should not have incorrect suffix - xunit convention
[CollectionDefinition("Postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture>;
#pragma warning restore CA1711
