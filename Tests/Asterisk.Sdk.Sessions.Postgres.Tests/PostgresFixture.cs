using Dapper;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Xunit;

namespace Asterisk.Sdk.Sessions.Postgres.Tests;

/// <summary>
/// Testcontainers-backed Postgres fixture. Spins up <c>postgres:16-alpine</c> in a disposable
/// container, runs the package migration, and exposes an <see cref="NpgsqlDataSource"/>
/// ready for tests. Uses <c>pg_isready</c> as the wait strategy (bundled in postgres:16-alpine)
/// to avoid the <c>UntilPortIsAvailable</c> hang seen on GitHub Actions /proc/net/tcp.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string DbUser = "postgres";
    private const string DbPassword = "postgres";
    private const string DbName = "asterisk_sessions_test";

    private IContainer? _container;
    private NpgsqlDataSource? _dataSource;

    public NpgsqlDataSource DataSource =>
        _dataSource ?? throw new InvalidOperationException("Postgres fixture not initialized.");

    public string ConnectionString =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        $"Database={DbName};Username={DbUser};Password={DbPassword};SSL Mode=Disable";

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder()
            .WithImage("postgres:16-alpine")
            .WithPortBinding(5432, true)
            .WithEnvironment("POSTGRES_USER", DbUser)
            .WithEnvironment("POSTGRES_PASSWORD", DbPassword)
            .WithEnvironment("POSTGRES_DB", DbName)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilCommandIsCompleted("pg_isready", "-U", DbUser))
            .Build();

        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(ConnectionString);

        // Run migration shipped with the package (copied to bin/.../Migrations/).
        var migrationPath = Path.Combine(AppContext.BaseDirectory, "Migrations", "001_create_sessions_table.sql");
        var migrationSql = await File.ReadAllTextAsync(migrationPath);

        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.ExecuteAsync(migrationSql);
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
            await _dataSource.DisposeAsync();
        if (_container is not null)
            await _container.DisposeAsync();
    }

    /// <summary>Truncate the session table between tests for isolation.</summary>
    public async Task FlushAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("TRUNCATE asterisk_call_sessions");
    }
}

#pragma warning disable CA1711 // Identifiers should not have incorrect suffix - xunit convention
[CollectionDefinition("Postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture>;
#pragma warning restore CA1711
