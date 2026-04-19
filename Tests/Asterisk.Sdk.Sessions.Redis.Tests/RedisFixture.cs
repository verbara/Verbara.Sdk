using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;
using Xunit;

namespace Asterisk.Sdk.Sessions.Redis.Tests;

/// <summary>
/// Testcontainers-backed Redis fixture. Spins up <c>redis:7-alpine</c> in a disposable
/// container and exposes a ready-to-use <see cref="IConnectionMultiplexer"/> with admin
/// access enabled so tests can flush the DB between runs.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private IContainer? _container;
    private ConnectionMultiplexer? _redis;

    public IConnectionMultiplexer Redis =>
        _redis ?? throw new InvalidOperationException("Redis fixture not initialized.");

    public string ConnectionString =>
        $"{_container!.Hostname}:{_container.GetMappedPublicPort(6379)},allowAdmin=true";

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("redis:7-alpine")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();

        await _container.StartAsync();
        _redis = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
            await _redis.DisposeAsync();
        if (_container is not null)
            await _container.DisposeAsync();
    }

    /// <summary>Flush the Redis database between tests for isolation.</summary>
    public async Task FlushAsync()
    {
        var server = Redis.GetServer(Redis.GetEndPoints()[0]);
        await server.FlushDatabaseAsync();
    }
}

#pragma warning disable CA1711 // Identifiers should not have incorrect suffix - xunit convention
[CollectionDefinition("Redis")]
public class RedisCollection : ICollectionFixture<RedisFixture>;
#pragma warning restore CA1711
