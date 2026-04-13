using Asterisk.Sdk.TestInfrastructure.Containers;
using Asterisk.Sdk.TestInfrastructure.Stacks;

namespace Asterisk.Sdk.IntegrationTests.Infrastructure;

/// <summary>
/// xunit-aware wrapper around <see cref="IntegrationFixture"/> that implements
/// <see cref="Xunit.IAsyncLifetime"/> so the collection lifecycle is properly invoked.
/// The TestInfrastructure library deliberately avoids an xunit dependency, so this
/// wrapper bridges the gap.
/// </summary>
public sealed class IntegrationTestFixture : Xunit.IAsyncLifetime
{
    private readonly Asterisk.Sdk.TestInfrastructure.Stacks.IntegrationFixture _inner = new();

    public AsteriskContainer Asterisk => _inner.Asterisk;
    public PostgresContainer Postgres => _inner.Postgres;

    public Task InitializeAsync() => _inner.InitializeAsync();
    public Task DisposeAsync() => _inner.DisposeAsync();
}
