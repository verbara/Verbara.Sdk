namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;

using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

public sealed class ToxiproxyFixture : IAsyncLifetime
{
    public const string AmiProxyName = "ami-proxy";

    public Task InitializeAsync()
    {
        // No-op: the Toxiproxy proxy is created by FunctionalTestFixture.InitializeAsync()
        // immediately after the inner FunctionalFixture has started all containers and set
        // the TOXIPROXY_API_URL / ASTERISK_AMI_PORT env vars.  Doing proxy setup there
        // (rather than here) avoids xunit collection-fixture ordering surprises.
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        try { await ToxiproxyControl.ResetAsync(); } catch { }
    }
}
