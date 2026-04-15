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
    public AsteriskContainer Asterisk { get; private set; } = null!;
    public PstnEmulatorContainer PstnEmulator { get; private set; } = null!;
    public ToxiproxyContainer Toxiproxy { get; }
    public SippContainer Sipp { get; }

    public FunctionalFixture()
    {
        _network = new NetworkBuilder().Build();
        Postgres = new PostgresContainer(_network);
        Toxiproxy = new ToxiproxyContainer(_network);
        Sipp = new SippContainer(_network);
    }

    public async Task InitializeAsync()
    {
        await _network.CreateAsync().ConfigureAwait(false);

        var image = await AsteriskContainer.CreateImageAsync().ConfigureAwait(false);
        Asterisk = new AsteriskContainer(_network, image);
        PstnEmulator = new PstnEmulatorContainer(_network, image);

        // Postgres must be ready before Asterisk realtime can connect
        await Postgres.StartAsync().ConfigureAwait(false);

        // Asterisk, PstnEmulator, and Toxiproxy start in parallel
        await Task.WhenAll(
            Asterisk.StartAsync(),
            PstnEmulator.StartAsync(),
            Toxiproxy.StartAsync()).ConfigureAwait(false);

        // SIPp needs Asterisk ready before it can dial
        await Sipp.StartAsync().ConfigureAwait(false);

        // Expose container ports via env vars so AmiConnectionFactory / AriClientFactory /
        // ToxiproxyControl resolve to the actual container host:port at runtime.
        Environment.SetEnvironmentVariable("ASTERISK_HOST", Asterisk.Host);
        Environment.SetEnvironmentVariable("ASTERISK_AMI_PORT", Asterisk.AmiPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("ASTERISK_ARI_PORT", Asterisk.AriPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("TOXIPROXY_API_URL", $"http://{Toxiproxy.Host}:{Toxiproxy.ApiPort}");
        Environment.SetEnvironmentVariable("TOXIPROXY_HOST", Toxiproxy.Host);
        Environment.SetEnvironmentVariable("TOXIPROXY_PROXY_PORT", Toxiproxy.ProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task DisposeAsync()
    {
        // Clear env vars so they don't leak into other test collections.
        Environment.SetEnvironmentVariable("ASTERISK_HOST", null);
        Environment.SetEnvironmentVariable("ASTERISK_AMI_PORT", null);
        Environment.SetEnvironmentVariable("ASTERISK_ARI_PORT", null);
        Environment.SetEnvironmentVariable("TOXIPROXY_API_URL", null);
        Environment.SetEnvironmentVariable("TOXIPROXY_HOST", null);
        Environment.SetEnvironmentVariable("TOXIPROXY_PROXY_PORT", null);

        await Sipp.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(
            Toxiproxy.DisposeAsync().AsTask(),
            PstnEmulator.DisposeAsync().AsTask(),
            Asterisk.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Postgres.DisposeAsync().ConfigureAwait(false);
        await _network.DisposeAsync().ConfigureAwait(false);
    }
}
