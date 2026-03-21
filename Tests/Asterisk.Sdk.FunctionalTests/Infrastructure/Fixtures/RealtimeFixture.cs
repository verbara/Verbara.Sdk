namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;

using Dapper;
using Npgsql;

/// <summary>
/// Shared fixture for realtime DB tests. Provides a NpgsqlDataSource
/// to the functional PostgreSQL container and AMI connection details.
/// </summary>
public sealed class RealtimeFixture : IAsyncLifetime
{
    private NpgsqlDataSource? _dataSource;

    public static string PostgresConnectionString =>
        Environment.GetEnvironmentVariable("REALTIME_POSTGRES_CONNECTION")
        ?? "Host=localhost;Port=15432;Database=asterisk;Username=asterisk;Password=asterisk";

    public static string AmiHost =>
        Environment.GetEnvironmentVariable("REALTIME_AMI_HOST") ?? "localhost";
    public static int AmiPort =>
        int.Parse(Environment.GetEnvironmentVariable("REALTIME_AMI_PORT") ?? "15038",
            System.Globalization.CultureInfo.InvariantCulture);
    public static string AmiUsername =>
        Environment.GetEnvironmentVariable("REALTIME_AMI_USERNAME") ?? "dashboard";
    public static string AmiPassword =>
        Environment.GetEnvironmentVariable("REALTIME_AMI_PASSWORD") ?? "dashboard";

    public NpgsqlDataSource DataSource => _dataSource
        ?? throw new InvalidOperationException("RealtimeFixture not initialized");

    public async Task InitializeAsync()
    {
        _dataSource = NpgsqlDataSource.Create(PostgresConnectionString);
        await using var conn = await _dataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("SELECT 1");
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }

    public async Task CleanupTestEndpointAsync(string endpointId)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM ps_endpoints WHERE id = @Id", new { Id = endpointId });
        await conn.ExecuteAsync("DELETE FROM ps_auths WHERE id = @Id", new { Id = endpointId });
        await conn.ExecuteAsync("DELETE FROM ps_aors WHERE id = @Id", new { Id = endpointId });
    }

    public async Task CleanupTestQueueAsync(string queueName)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM queue_members WHERE queue_name = @Name", new { Name = queueName });
        await conn.ExecuteAsync("DELETE FROM queue_table WHERE name = @Name", new { Name = queueName });
    }
}
