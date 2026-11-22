using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Verbara.Sdk.Cluster.Postgres.Migrations;
using Xunit;

namespace Verbara.Sdk.Cluster.Postgres.Tests;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public sealed class MigrationRunnerTests
{
    /// <summary>The V001 statement as 2.10.1 and every earlier release shipped it.</summary>
    private const string PublishedV001Sql = """
        CREATE TABLE IF NOT EXISTS cluster_distributed_lock (
            resource TEXT PRIMARY KEY,
            owner    TEXT NOT NULL,
            expires_at TIMESTAMPTZ NOT NULL
        );
        """;

    private readonly PostgresFixture _fixture;

    public MigrationRunnerTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EnsureSchemaAsync_ShouldBeIdempotent_WhenInvokedTwice()
    {
        // The fixture already ran the migration once in InitializeAsync. A second
        // invocation against the same database must succeed without throwing because
        // every statement is guarded by IF NOT EXISTS.
        var act = async () =>
            await MigrationRunner.EnsureSchemaAsync(_fixture.DataSource, NullLogger.Instance);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureSchemaAsync_ShouldAddANullableAcquiredAtAndLeaveExistingRowsUnknown_WhenTheTableCameFromAnEarlierRelease()
    {
        await using var dataSource = NpgsqlDataSource.Create(await _fixture.CreateFreshDatabaseAsync());
        await ExecuteAsync(dataSource, PublishedV001Sql);
        await ExecuteAsync(
            dataSource,
            "INSERT INTO cluster_distributed_lock (resource, owner, expires_at) VALUES ('held', 'node-1', NOW() + interval '1 minute')");

        await MigrationRunner.EnsureSchemaAsync(dataSource, NullLogger.Instance);

        await using (var cmd = dataSource.CreateCommand("""
            SELECT data_type, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_name = 'cluster_distributed_lock' AND column_name = 'acquired_at'
            """))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).Should().BeTrue("V002 adds the acquired_at column");
            reader.GetString(0).Should().Be("timestamp with time zone");
            reader.GetString(1).Should().Be("YES");
            reader.IsDBNull(2).Should().BeTrue("no default: a row the SDK did not see acquired stays unknown");
        }

        await using var read = dataSource.CreateCommand(
            "SELECT acquired_at IS NULL FROM cluster_distributed_lock WHERE resource = @r");
        read.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Text) { Value = "held" });
        ((bool)(await read.ExecuteScalarAsync())!).Should().BeTrue("an existing row gets no invented time");
    }

    [Fact]
    public async Task EnsureSchemaAsync_ShouldSucceedOnEveryHost_WhenEightHostsRunItAtOnceOnAFreshDatabase()
    {
        // Two or more hosts run EnsureSchemaAsync at startup against the same database.
        var connectionString = await _fixture.CreateFreshDatabaseAsync();
        var sources = Enumerable.Range(0, 8).Select(_ => NpgsqlDataSource.Create(connectionString)).ToArray();
        try
        {
            using var start = new SemaphoreSlim(0);
            var hosts = sources
                .Select(ds => Task.Run(async () =>
                {
                    await using (var warm = await ds.OpenConnectionAsync()) { } // pool warm, so the start is close
                    await start.WaitAsync();
                    await MigrationRunner.EnsureSchemaAsync(ds, NullLogger.Instance);
                }))
                .ToArray();
            start.Release(sources.Length);

            var act = async () => await Task.WhenAll(hosts);

            await act.Should().NotThrowAsync("every host's schema run is safe against the others");
        }
        finally
        {
            foreach (var ds in sources)
                await ds.DisposeAsync();
        }
    }

    [Fact]
    public async Task EnsureSchemaAsync_ShouldNotWaitForAnOpenReaderOfTheTable_WhenTheSchemaIsAlreadyCurrent()
    {
        // Every host runs the list on every start. On a current schema it must not queue behind a transaction
        // that reads the lock table (nor make every later acquire queue behind itself): with a two-second
        // lock_timeout it completes while a reader holds the table.
        var connectionString = await _fixture.CreateFreshDatabaseAsync();
        await using (var first = NpgsqlDataSource.Create(connectionString))
            await MigrationRunner.EnsureSchemaAsync(first, NullLogger.Instance);

        await using var holder = NpgsqlDataSource.Create(connectionString);
        await using var reader = await holder.OpenConnectionAsync();
        await using var open = await reader.BeginTransactionAsync();
        await using (var read = new NpgsqlCommand("SELECT count(*) FROM cluster_distributed_lock", reader, open))
            await read.ExecuteScalarAsync();

        var bounded = new NpgsqlConnectionStringBuilder(connectionString) { Options = "-c lock_timeout=2000" };
        await using var host = NpgsqlDataSource.Create(bounded.ConnectionString);

        var act = async () => await MigrationRunner.EnsureSchemaAsync(host, NullLogger.Instance);

        await act.Should().NotThrowAsync("a current schema takes no lock that conflicts with a reader");
        await open.RollbackAsync();
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }
}
