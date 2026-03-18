using StackExchange.Redis;
using Xunit;

namespace Asterisk.Sdk.Redis.Spike.Fixtures;

public sealed class RedisFixture : IAsyncLifetime
{
    private ConnectionMultiplexer? _redis;

    public IConnectionMultiplexer Redis => _redis ?? throw new InvalidOperationException("Redis not connected");
    public IDatabase Database => Redis.GetDatabase();

    public async Task InitializeAsync()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379";
        _redis = await ConnectionMultiplexer.ConnectAsync($"{host}:{port},allowAdmin=true");
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
            await _redis.DisposeAsync();
    }

    /// <summary>Flush the current database between tests.</summary>
    public async Task FlushAsync()
    {
        var server = _redis!.GetServer(_redis.GetEndPoints()[0]);
        await server.FlushDatabaseAsync();
    }
}

#pragma warning disable CA1711 // Identifiers should not have incorrect suffix — xunit convention
[CollectionDefinition("Redis")]
public class RedisCollection : ICollectionFixture<RedisFixture>;
#pragma warning restore CA1711
