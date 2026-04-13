using Asterisk.Sdk.TestInfrastructure.Containers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Stacks;

/// <summary>
/// Integration fixture: PostgreSQL (for Realtime) + Asterisk (unified, realtime mode).
/// PostgreSQL starts first so Asterisk can connect on boot.
/// </summary>
public sealed class IntegrationFixture : IAsyncLifetime
{
    private readonly INetwork _network;

    public PostgresContainer Postgres { get; }
    public AsteriskContainer Asterisk { get; private set; } = null!;

    public IntegrationFixture()
    {
        _network = new NetworkBuilder().Build();
        Postgres = new PostgresContainer(_network);
    }

    public async Task InitializeAsync()
    {
        await _network.CreateAsync().ConfigureAwait(false);

        var image = await AsteriskContainer.CreateImageAsync().ConfigureAwait(false);
        Asterisk = new AsteriskContainer(_network, image);

        await Postgres.StartAsync().ConfigureAwait(false);
        await Asterisk.StartAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await Asterisk.DisposeAsync().ConfigureAwait(false);
        await Postgres.DisposeAsync().ConfigureAwait(false);
        await _network.DisposeAsync().ConfigureAwait(false);
    }
}
