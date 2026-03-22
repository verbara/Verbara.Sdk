namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;

using Asterisk.Sdk.TestInfrastructure.Stacks;
using Dapper;
using Npgsql;

/// <summary>
/// Collection fixture for realtime DB tests. Wraps the TestInfrastructure
/// RealtimeFixture (Postgres + Asterisk containers) and adds a NpgsqlDataSource
/// plus DB-level helper methods needed by the test classes.
/// </summary>
public sealed class RealtimeDbFixture : IAsyncLifetime
{
    private readonly TestInfrastructure.Stacks.RealtimeFixture _stack = new();
    private NpgsqlDataSource? _dataSource;

    public string AmiHost => _stack.Asterisk.Host;
    public int AmiPort => _stack.Asterisk.AmiPort;
    public static string AmiUsername =>
        Environment.GetEnvironmentVariable("REALTIME_AMI_USERNAME") ?? "dashboard";
    public static string AmiPassword =>
        Environment.GetEnvironmentVariable("REALTIME_AMI_PASSWORD") ?? "dashboard";

    public NpgsqlDataSource DataSource => _dataSource
        ?? throw new InvalidOperationException("RealtimeDbFixture not initialized");

    public async Task InitializeAsync()
    {
        await _stack.InitializeAsync().ConfigureAwait(false);

        _dataSource = NpgsqlDataSource.Create(_stack.Postgres.ConnectionString);
        await using var conn = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
        await conn.ExecuteAsync("SELECT 1").ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
            await _dataSource.DisposeAsync().ConfigureAwait(false);

        await _stack.DisposeAsync().ConfigureAwait(false);
    }

    public async Task CleanupTestEndpointAsync(string endpointId)
    {
        await using var conn = await DataSource.OpenConnectionAsync().ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM ps_endpoints WHERE id = @Id", new { Id = endpointId }).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM ps_auths WHERE id = @Id", new { Id = endpointId }).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM ps_aors WHERE id = @Id", new { Id = endpointId }).ConfigureAwait(false);
    }

    public async Task CleanupTestQueueAsync(string queueName)
    {
        await using var conn = await DataSource.OpenConnectionAsync().ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM queue_members WHERE queue_name = @Name", new { Name = queueName }).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM queue_table WHERE name = @Name", new { Name = queueName }).ConfigureAwait(false);
    }
}
