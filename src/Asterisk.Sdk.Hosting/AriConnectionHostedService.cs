using Asterisk.Sdk;
using Microsoft.Extensions.Hosting;

namespace Asterisk.Sdk.Hosting;

/// <summary>
/// Hosted service that connects the ARI client on application start and disconnects on stop.
/// </summary>
public sealed class AriConnectionHostedService(IAriClient client) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await client.ConnectAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await client.DisconnectAsync(cancellationToken);
}
