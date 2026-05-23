using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Verbara.Sdk.Cluster.Postgres.DependencyInjection;
using Verbara.Sdk.Cluster.Primitives;
using Xunit;

namespace Verbara.Sdk.Cluster.Postgres.Tests;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public sealed class ClusterPostgresServiceCollectionExtensionsTests
{
    private readonly PostgresFixture _fixture;

    public ClusterPostgresServiceCollectionExtensionsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void AddPostgresDistributedLock_ShouldResolveAsIDistributedLock_FromDIContainer()
    {
        // Arrange — host registers a keyed NpgsqlDataSource under "Cluster" (the README pattern).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyedSingleton<NpgsqlDataSource>("Cluster", (_, _) =>
            NpgsqlDataSource.Create(_fixture.ConnectionString));

        services.AddPostgresDistributedLock(connectionStringName: "Cluster");

        // Act
        using var provider = services.BuildServiceProvider();
        var lockService = provider.GetRequiredService<IDistributedLock>();

        // Assert
        lockService.Should().NotBeNull();
        lockService.Should().BeAssignableTo<IDistributedLock>();
    }

    [Fact]
    public void AddPostgresDistributedLock_ShouldThrow_WhenConnectionStringNameIsWhitespace()
    {
        var services = new ServiceCollection();

        var act = () => services.AddPostgresDistributedLock("   ");

        act.Should().Throw<ArgumentException>();
    }
}
