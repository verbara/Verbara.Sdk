namespace Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;

using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;

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
        // Best effort: collection fixtures are disposed in no guaranteed order. Once
        // FunctionalTestFixture is disposed, TOXIPROXY_API_URL is cleared, so the reset goes to the
        // default URL, where nothing normally listens. Each network test also removes its own toxic.
        await ToxiproxyControl.TryResetAsync();
    }
}
