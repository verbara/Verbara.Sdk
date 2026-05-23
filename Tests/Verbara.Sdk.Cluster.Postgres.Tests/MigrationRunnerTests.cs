using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Cluster.Postgres.Migrations;
using Xunit;

namespace Verbara.Sdk.Cluster.Postgres.Tests;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public sealed class MigrationRunnerTests
{
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
}
