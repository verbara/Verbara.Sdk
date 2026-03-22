using Asterisk.Sdk.TestInfrastructure.Containers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Stacks;

/// <summary>
/// Realtime fixture: shared network, PostgreSQL starts first, then Asterisk realtime (needs DB).
/// </summary>
public sealed class RealtimeFixture : IAsyncLifetime
{
    private readonly INetwork _network;

    public PostgresContainer Postgres { get; }
    public AsteriskRealtimeContainer Asterisk { get; }

    public RealtimeFixture()
    {
        _network = new NetworkBuilder().Build();

        Postgres = new PostgresContainer(_network);
        Asterisk = new AsteriskRealtimeContainer(_network);
    }

    public async Task InitializeAsync()
    {
        await _network.CreateAsync().ConfigureAwait(false);

        // PostgreSQL must be ready before Asterisk realtime connects
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
