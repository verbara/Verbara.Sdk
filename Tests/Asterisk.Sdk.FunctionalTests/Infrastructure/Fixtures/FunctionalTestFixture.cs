using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
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

    public async Task InitializeAsync()
    {
        await _inner.InitializeAsync().ConfigureAwait(false);
        // Expose the AsteriskContainer so DockerControl.Kill/Start/Restart use the
        // Testcontainers-native lifecycle API instead of the docker CLI. This avoids
        // the docker-CLI-managed restart failing silently in CI.
        DockerControl.Container = _inner.Asterisk;
        // Also set the name for WaitForHealthyAsync TCP-probe path (docker-compose compat).
        DockerControl.DefaultContainerName = _inner.Asterisk.ContainerName.TrimStart('/');
    }

    public async Task DisposeAsync()
    {
        // Reset to defaults so DockerControl does not leak state into other collections.
        DockerControl.Container = null;
        DockerControl.DefaultContainerName = "asterisk-sdk-test";
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
