using Asterisk.Sdk.TestInfrastructure.Containers;
using Asterisk.Sdk.TestInfrastructure.Stacks;

namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;

/// <summary>
/// xunit-aware wrapper around <see cref="FunctionalFixture"/> that implements
/// <see cref="Xunit.IAsyncLifetime"/> so the collection lifecycle is properly invoked.
/// </summary>
public sealed class FunctionalTestFixture : Xunit.IAsyncLifetime
{
    private readonly FunctionalFixture _inner = new();

    public AsteriskContainer Asterisk => _inner.Asterisk;
    public PostgresContainer Postgres => _inner.Postgres;
    public PstnEmulatorContainer PstnEmulator => _inner.PstnEmulator;
    public ToxiproxyContainer Toxiproxy => _inner.Toxiproxy;
    public SippContainer Sipp => _inner.Sipp;

    public Task InitializeAsync() => _inner.InitializeAsync();
    public Task DisposeAsync() => _inner.DisposeAsync();
}
