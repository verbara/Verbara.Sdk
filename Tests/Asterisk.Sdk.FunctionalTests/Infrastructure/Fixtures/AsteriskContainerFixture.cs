namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;

using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

public sealed class AsteriskContainerFixture : IAsyncLifetime
{
    public static string Host => AmiConnectionFactory.Host;
    public static int AmiPort => AmiConnectionFactory.Port;

    public async Task InitializeAsync()
    {
        // Wait for Asterisk to be healthy (started by docker-compose externally)
        try
        {
            await DockerControl.WaitForHealthyAsync(timeout: TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            // Container not running — tests will skip via attribute
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
