using Asterisk.Sdk.TestInfrastructure.Containers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Stacks;

/// <summary>
/// Full functional fixture: shared network, Asterisk + PSTN emulator + Toxiproxy + SIPp.
/// Asterisk, PstnEmulator, and Toxiproxy start in parallel; SIPp starts after Asterisk is ready.
/// </summary>
public sealed class FunctionalFixture : IAsyncLifetime
{
    private readonly INetwork _network;

    public AsteriskContainer Asterisk { get; }
    public PstnEmulatorContainer PstnEmulator { get; }
    public ToxiproxyContainer Toxiproxy { get; }
    public SippContainer Sipp { get; }

    public FunctionalFixture()
    {
        _network = new NetworkBuilder().Build();

        Asterisk = new AsteriskContainer(_network);
        PstnEmulator = new PstnEmulatorContainer(_network);
        Toxiproxy = new ToxiproxyContainer(_network);
        Sipp = new SippContainer(_network);
    }

    public async Task InitializeAsync()
    {
        await _network.CreateAsync().ConfigureAwait(false);

        // Start Asterisk, PstnEmulator, and Toxiproxy in parallel
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
        await _network.DisposeAsync().ConfigureAwait(false);
    }
}
