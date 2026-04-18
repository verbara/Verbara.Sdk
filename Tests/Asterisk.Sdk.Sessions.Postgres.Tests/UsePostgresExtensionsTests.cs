using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Hosting;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Extensions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Asterisk.Sdk.Sessions.Postgres.Tests;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public sealed class UsePostgresExtensionsTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public UsePostgresExtensionsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.FlushAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UsePostgres_ReplacesDefaultSessionStore_WhenCalledOnBuilder()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddAsteriskSessionsBuilder()
            .UsePostgres(_fixture.DataSource);

        // Act
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<SessionStoreBase>();

        // Assert - concrete type check
        store.Should().BeOfType<PostgresSessionStore>();

        // Round-trip through the resolved store to prove Postgres is wired
        var session = new CallSession("builder-1", "linked-builder-1", "server-1", CallDirection.Inbound);
        session.SetMetadata("origin", "builder-test");
        await store.SaveAsync(session, CancellationToken.None);

        var loaded = await store.GetAsync("builder-1", CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Metadata.Should().ContainKey("origin").WhoseValue.Should().Be("builder-test");
    }

    [Fact]
    public void UsePostgres_OverridesInMemoryDefault_WhenCalledAfterBuilderRegistration()
    {
        // Arrange - default builder registers InMemorySessionStore
        var services = new ServiceCollection();
        services.AddAsteriskSessionsBuilder();

        // Sanity check: before UsePostgres, the default store is NOT PostgresSessionStore
        using (var provider1 = services.BuildServiceProvider())
        {
            var defaultStore = provider1.GetRequiredService<SessionStoreBase>();
            defaultStore.Should().NotBeOfType<PostgresSessionStore>();
        }

        // Act - call UsePostgres after AddAsteriskSessionsBuilder
        services.AddAsteriskSessionsBuilder()
            .UsePostgres(_fixture.DataSource);

        // Assert - Replace ensures PostgresSessionStore supersedes InMemorySessionStore
        using var provider2 = services.BuildServiceProvider();
        var store = provider2.GetRequiredService<SessionStoreBase>();
        store.Should().BeOfType<PostgresSessionStore>();

        // ISessionStore must also forward to the same Postgres-backed instance
        var iface = provider2.GetRequiredService<ISessionStore>();
        iface.Should().BeSameAs(store);
    }

    [Fact]
    public async Task UsePostgres_ConnectionString_RegistersDataSource_AndResolves()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddAsteriskSessionsBuilder()
            .UsePostgres(_fixture.ConnectionString);

        // Act
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<SessionStoreBase>();

        // Assert
        store.Should().BeOfType<PostgresSessionStore>();

        var session = new CallSession("connstr-1", "linked-connstr-1", "server-1", CallDirection.Inbound);
        await store.SaveAsync(session, CancellationToken.None);

        var loaded = await store.GetAsync("connstr-1", CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.SessionId.Should().Be("connstr-1");
    }
}
