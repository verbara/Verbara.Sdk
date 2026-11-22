using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Verbara.Sdk.Cluster.Postgres.Migrations;
using Xunit;

namespace Verbara.Sdk.Cluster.Postgres.Tests;

/// <summary>
/// The lock is a row with an expiry, not a session-scoped advisory lock: it survives a restart of
/// Postgres itself, with its owner, its expiry and its <c>acquired_at</c>. This class owns its container
/// (it stops and starts it), so it never shares the collection fixture.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresRestartTests : IAsyncLifetime
{
    private const string DbUser = "postgres";
    private const string DbPassword = "postgres";
    private const string DbName = "verbara_cluster_restart";

    private IContainer? _container;

    private string ConnectionString =>
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
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private static PostgresDistributedLock CreateLock(NpgsqlDataSource dataSource) => new(
        dataSource,
        NullLogger<PostgresDistributedLock>.Instance,
        TimeProvider.System);

    [Fact]
    public async Task TryAcquireAsync_ShouldStillFindTheHeldLock_WhenPostgresRestartsBeforeItExpires()
    {
        const string resource = "leader";
        LockRow beforeRestart;
        await using (var first = await OpenAsync())
        {
            await MigrationRunner.EnsureSchemaAsync(first, NullLogger.Instance);
            (await CreateLock(first).TryAcquireAsync(resource, "node-1", TimeSpan.FromMinutes(2))).Should().BeTrue();
            beforeRestart = await LockRows.ReadAsync(first, resource);
        }

        await _container!.StopAsync();
        await _container.StartAsync(); // same data directory; the mapped port may change

        await using var second = await OpenAsync();
        (await LockRows.ReadAsync(second, resource)).Should().Be(beforeRestart, "the row is durable, not session-scoped");

        var lk = CreateLock(second);
        (await lk.TryAcquireAsync(resource, "node-2", TimeSpan.FromMinutes(2)))
            .Should().BeFalse("the lock is still held until its expiry, whatever happened to the server");
        (await lk.TryAcquireAsync(resource, "node-1", TimeSpan.FromMinutes(2))).Should().BeTrue();
        (await LockRows.ReadAsync(second, resource)).AcquiredAt
            .Should().Be(beforeRestart.AcquiredAt, "the same owner's tenure goes on across the restart");
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldLetAnotherOwnerTakeTheLock_WhenItExpiredWhilePostgresWasDown()
    {
        const string resource = "leader";
        await using (var first = await OpenAsync())
        {
            await MigrationRunner.EnsureSchemaAsync(first, NullLogger.Instance);
            (await CreateLock(first).TryAcquireAsync(resource, "node-1", TimeSpan.FromSeconds(1))).Should().BeTrue();
        }

        await _container!.StopAsync();
        await Task.Delay(TimeSpan.FromSeconds(1.5)); // fence-allow: SIMULATED-WORK — Postgres stays down past the 1 s expiry; the check after restart waits on the server clock
        await _container.StartAsync();

        await using var second = await OpenAsync();
        await LockRows.WaitPastExpiryAsync(second, resource);
        var before = await LockRows.ServerClockAsync(second);
        (await CreateLock(second).TryAcquireAsync(resource, "node-2", TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the expiry is server time in the row, and it passed");

        var taken = await LockRows.ReadAsync(second, resource);
        taken.Owner.Should().Be("node-2");
        taken.AcquiredAt.Should().NotBeNull();
        taken.AcquiredAt!.Value.Should().BeOnOrAfter(before);
    }

    private async Task<NpgsqlDataSource> OpenAsync()
    {
        var dataSource = NpgsqlDataSource.Create(ConnectionString);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var conn = await dataSource.OpenConnectionAsync();
                return dataSource;
            }
            catch (Exception e) when (attempt < 20 && e is NpgsqlException or System.Net.Sockets.SocketException)
            {
                await Task.Delay(500); // fence-allow: LOOP-DRIVER — reconnect retry after the restart; the exit is an open connection
            }
        }
    }
}
