using Asterisk.Sdk.TestInfrastructure.Containers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Stacks;

/// <summary>
/// Realtime fixture: shared network, PostgreSQL + Asterisk (realtime mode).
/// Equivalent to the Asterisk half of FunctionalFixture, minus PSTN/SIPp/Toxiproxy.
/// </summary>
public sealed class RealtimeFixture : IAsyncLifetime
{
    private readonly INetwork _network;

    public PostgresContainer Postgres { get; }
    public AsteriskContainer Asterisk { get; }

    public RealtimeFixture()
    {
        _network = new NetworkBuilder().Build();
        Postgres = new PostgresContainer(_network);
        Asterisk = new AsteriskContainer(_network);
    }

    public async Task InitializeAsync()
    {
        await _network.CreateAsync().ConfigureAwait(false);
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
