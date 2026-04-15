namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;

using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

public sealed class ToxiproxyFixture : IAsyncLifetime
{
    public const string AmiProxyName = "ami-proxy";

    public async Task InitializeAsync()
    {
        await ToxiproxyControl.ResetAsync();

        // Use host.docker.internal:{AmiPort} so Toxiproxy reaches Asterisk via the
        // host-mapped port. Docker's embedded DNS for the 'asterisk' alias is
        // unreliable from distroless Go containers; routing through the Docker host
        // gateway is always stable and survives Asterisk container restarts.
        var amiPort = AmiConnectionFactory.Port;
        await ToxiproxyControl.CreateProxyAsync(
            AmiProxyName, "0.0.0.0:15038", $"host.docker.internal:{amiPort}");
    }

    public async Task DisposeAsync()
    {
        try { await ToxiproxyControl.ResetAsync(); } catch { }
    }
}
