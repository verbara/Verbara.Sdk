using Microsoft.Extensions.Hosting;

namespace Asterisk.Sdk.Agi.Hosting;

/// <summary>
/// Hosted service that starts and stops the FastAGI server as part of the application lifecycle.
/// </summary>
public sealed class AgiHostedService(IAgiServer agiServer) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await agiServer.StartAsync(cancellationToken).ConfigureAwait(false);

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await agiServer.StopAsync(cancellationToken).ConfigureAwait(false);
}
