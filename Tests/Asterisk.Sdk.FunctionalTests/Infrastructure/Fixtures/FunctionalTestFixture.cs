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
        // Expose the actual Testcontainers container name so DockerControl.Kill/Start/
        // WaitForHealthy operations target the right container instead of the legacy
        // hardcoded "asterisk-sdk-test" name used by docker-compose mode.
        DockerControl.DefaultContainerName = _inner.Asterisk.ContainerName.TrimStart('/');
    }

    public async Task DisposeAsync()
    {
        // Reset to default so DockerControl does not leak state into other collections.
        DockerControl.DefaultContainerName = "asterisk-sdk-test";
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
