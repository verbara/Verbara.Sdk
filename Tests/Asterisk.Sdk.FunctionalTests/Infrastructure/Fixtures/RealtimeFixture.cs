namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;

using Asterisk.Sdk.TestInfrastructure.Stacks;
using Dapper;
using Npgsql;

/// <summary>
/// Collection fixture for realtime DB tests. Supports two modes:
/// 1. Testcontainers (default) — starts PostgreSQL + Asterisk Realtime automatically.
/// 2. External (docker-compose) — set REALTIME_DB_HOST to use pre-started containers.
/// </summary>
public sealed class RealtimeDbFixture : IAsyncLifetime
{
    private readonly RealtimeFixture? _stack;
    private readonly bool _useExternal;
    private NpgsqlDataSource? _dataSource;

    public string AmiHost { get; private set; }
    public int AmiPort { get; private set; }
    public static string AmiUsername =>
        Environment.GetEnvironmentVariable("REALTIME_AMI_USERNAME") ?? "dashboard";
    public static string AmiPassword =>
        Environment.GetEnvironmentVariable("REALTIME_AMI_PASSWORD") ?? "dashboard";

    public NpgsqlDataSource DataSource => _dataSource
        ?? throw new InvalidOperationException("RealtimeDbFixture not initialized");

    public RealtimeDbFixture()
    {
        var externalDbHost = Environment.GetEnvironmentVariable("REALTIME_DB_HOST");
        _useExternal = !string.IsNullOrEmpty(externalDbHost);

        if (_useExternal)
        {
            AmiHost = Environment.GetEnvironmentVariable("REALTIME_AMI_HOST") ?? "localhost";
            AmiPort = int.Parse(
                Environment.GetEnvironmentVariable("REALTIME_AMI_PORT") ?? "15039",
                System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            _stack = new RealtimeFixture();
            AmiHost = "localhost";
            AmiPort = 5038;
        }
    }

    public async Task InitializeAsync()
    {
        if (_useExternal)
        {
            var dbHost = Environment.GetEnvironmentVariable("REALTIME_DB_HOST") ?? "localhost";
            var dbPort = Environment.GetEnvironmentVariable("REALTIME_DB_PORT") ?? "5432";
            var dbUser = Environment.GetEnvironmentVariable("REALTIME_DB_USER") ?? "asterisk";
            var dbPass = Environment.GetEnvironmentVariable("REALTIME_DB_PASSWORD") ?? "asterisk";
            var dbName = Environment.GetEnvironmentVariable("REALTIME_DB_NAME") ?? "asterisk";

            var connStr = $"Host={dbHost};Port={dbPort};Database={dbName};Username={dbUser};Password={dbPass}";
            _dataSource = NpgsqlDataSource.Create(connStr);
        }
        else
        {
            await _stack!.InitializeAsync().ConfigureAwait(false);
            AmiHost = _stack.Asterisk.Host;
            AmiPort = _stack.Asterisk.AmiPort;
            _dataSource = NpgsqlDataSource.Create(_stack.Postgres.ConnectionString);
        }

        await using var conn = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
        await conn.ExecuteAsync("SELECT 1").ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
            await _dataSource.DisposeAsync().ConfigureAwait(false);

        if (_stack is not null)
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
