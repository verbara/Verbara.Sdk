namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;

using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

public sealed class ToxiproxyFixture : IAsyncLifetime
{
    public const string AmiProxyName = "ami-proxy";

    public async Task InitializeAsync()
    {
        try
        {
            await ToxiproxyControl.ResetAsync();
            await ToxiproxyControl.CreateProxyAsync(
                AmiProxyName, "0.0.0.0:15038", "asterisk:5038");
        }
        catch { /* Toxiproxy not running — tests skip via attribute */ }
    }

    public async Task DisposeAsync()
    {
        try { await ToxiproxyControl.ResetAsync(); } catch { }
    }
}
