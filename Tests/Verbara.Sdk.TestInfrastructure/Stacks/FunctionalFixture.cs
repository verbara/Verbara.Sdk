using Verbara.Sdk.TestInfrastructure.Containers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;

namespace Verbara.Sdk.TestInfrastructure.Stacks;

/// <summary>
/// Full functional fixture: Postgres (realtime DB) + Asterisk (realtime) + Toxiproxy.
/// Postgres starts first, then Asterisk + Toxiproxy in parallel. The PSTN emulator and SIPp run in the two-server
/// fixture (<see cref="MultiServerFixture"/>), where a test uses them.
/// </summary>
public sealed class FunctionalFixture : IAsyncLifetime
{
    private readonly INetwork _network;

    public PostgresContainer Postgres { get; }
    public AsteriskContainer Asterisk { get; private set; } = null!;
    public ToxiproxyContainer Toxiproxy { get; }

    public FunctionalFixture()
    {
        _network = new NetworkBuilder().Build();
        Postgres = new PostgresContainer(_network);
        Toxiproxy = new ToxiproxyContainer(_network);
    }

    public async Task InitializeAsync()
    {
        await _network.CreateAsync().ConfigureAwait(false);

        var image = await AsteriskContainer.CreateImageAsync().ConfigureAwait(false);
        Asterisk = new AsteriskContainer(_network, image);

        // Postgres must be ready before Asterisk realtime can connect
        await Postgres.StartAsync().ConfigureAwait(false);

        // Asterisk and Toxiproxy start in parallel
        await Task.WhenAll(
            Asterisk.StartAsync(),
            Toxiproxy.StartAsync()).ConfigureAwait(false);

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

        await Task.WhenAll(
            Toxiproxy.DisposeAsync().AsTask(),
            Asterisk.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Postgres.DisposeAsync().ConfigureAwait(false);
        await _network.DisposeAsync().ConfigureAwait(false);
    }
}
