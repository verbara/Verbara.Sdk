using Asterisk.Sdk.TestInfrastructure.Containers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Stacks;

/// <summary>
/// Full functional fixture: Postgres (realtime DB) + Asterisk (realtime) + PSTN emulator (file) + Toxiproxy + SIPp.
/// Postgres starts first, then Asterisk + PstnEmulator + Toxiproxy in parallel, then SIPp.
/// </summary>
public sealed class FunctionalFixture : IAsyncLifetime
{
    private readonly INetwork _network;

    public PostgresContainer Postgres { get; }
    public AsteriskContainer Asterisk { get; }
    public PstnEmulatorContainer PstnEmulator { get; }
    public ToxiproxyContainer Toxiproxy { get; }
    public SippContainer Sipp { get; }

    public FunctionalFixture()
    {
        _network = new NetworkBuilder().Build();
        Postgres = new PostgresContainer(_network);
        Asterisk = new AsteriskContainer(_network);
        PstnEmulator = new PstnEmulatorContainer(_network);
        Toxiproxy = new ToxiproxyContainer(_network);
        Sipp = new SippContainer(_network);
    }

    public async Task InitializeAsync()
    {
        await _network.CreateAsync().ConfigureAwait(false);

        // Postgres must be ready before Asterisk realtime can connect
        await Postgres.StartAsync().ConfigureAwait(false);

        // Asterisk, PstnEmulator, and Toxiproxy start in parallel
        await Task.WhenAll(
            Asterisk.StartAsync(),
            PstnEmulator.StartAsync(),
            Toxiproxy.StartAsync()).ConfigureAwait(false);

        // SIPp needs Asterisk ready before it can dial
        await Sipp.StartAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await Sipp.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(
            Toxiproxy.DisposeAsync().AsTask(),
            PstnEmulator.DisposeAsync().AsTask(),
            Asterisk.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Postgres.DisposeAsync().ConfigureAwait(false);
        await _network.DisposeAsync().ConfigureAwait(false);
    }
}
