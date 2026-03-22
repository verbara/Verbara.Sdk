using Asterisk.Sdk.TestInfrastructure.Containers;

namespace Asterisk.Sdk.TestInfrastructure.Stacks;

/// <summary>
/// Minimal fixture for integration tests: a single Asterisk container with no shared network.
/// </summary>
public sealed class IntegrationFixture : IAsyncLifetime
{
    public AsteriskContainer Asterisk { get; } = new();

    public async Task InitializeAsync()
    {
        await Asterisk.StartAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await Asterisk.DisposeAsync().ConfigureAwait(false);
    }
}
