using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Hosting;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Extensions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Asterisk.Sdk.Sessions.Redis.Tests;

[Collection("Redis")]
[Trait("Category", "Integration")]
public sealed class UseRedisExtensionsTests : IAsyncLifetime
{
    private readonly RedisFixture _fixture;

    public UseRedisExtensionsTests(RedisFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.FlushAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UseRedis_ReplacesDefaultSessionStore_WhenCalledOnBuilder()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddAsteriskSessionsBuilder()
            .UseRedis(_fixture.Redis, opts => opts.KeyPrefix = "builder-test:");

        // Act
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<SessionStoreBase>();

        // Assert - concrete type check
        store.Should().BeOfType<RedisSessionStore>();

        // Round-trip through the resolved store to prove Redis is wired
        var session = new CallSession("builder-1", "linked-builder-1", "server-1", CallDirection.Inbound);
        session.SetMetadata("origin", "builder-test");
        await store.SaveAsync(session, CancellationToken.None);

        var loaded = await store.GetAsync("builder-1", CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Metadata.Should().ContainKey("origin").WhoseValue.Should().Be("builder-test");
    }

    [Fact]
    public void UseRedis_OverridesInMemoryDefault_WhenCalledAfterBuilderRegistration()
    {
        // Arrange - default builder registers InMemorySessionStore
        var services = new ServiceCollection();
        services.AddAsteriskSessionsBuilder();

        // Sanity check: before UseRedis, the default store is NOT RedisSessionStore
        using (var provider1 = services.BuildServiceProvider())
        {
            var defaultStore = provider1.GetRequiredService<SessionStoreBase>();
            defaultStore.Should().NotBeOfType<RedisSessionStore>();
        }

        // Act - call UseRedis after AddAsteriskSessionsBuilder
        services.AddAsteriskSessionsBuilder()
            .UseRedis(_fixture.Redis);

        // Assert - Replace ensures RedisSessionStore supersedes InMemorySessionStore
        using var provider2 = services.BuildServiceProvider();
        var store = provider2.GetRequiredService<SessionStoreBase>();
        store.Should().BeOfType<RedisSessionStore>();

        // ISessionStore must also forward to the same Redis-backed instance
        var iface = provider2.GetRequiredService<ISessionStore>();
        iface.Should().BeSameAs(store);
    }

    [Fact]
    public async Task UseRedis_WithConnectionString_RegistersMultiplexerAndStore()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddAsteriskSessionsBuilder()
            .UseRedis(_fixture.ConnectionString, opts => opts.KeyPrefix = "connstr-test:");

        // Act
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<SessionStoreBase>();

        // Assert
        store.Should().BeOfType<RedisSessionStore>();

        var session = new CallSession("connstr-1", "linked-connstr-1", "server-1", CallDirection.Inbound);
        await store.SaveAsync(session, CancellationToken.None);

        var loaded = await store.GetAsync("connstr-1", CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.SessionId.Should().Be("connstr-1");
    }
}
