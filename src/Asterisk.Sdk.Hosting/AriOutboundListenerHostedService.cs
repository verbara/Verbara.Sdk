using Asterisk.Sdk.Ari.Outbound;
using Microsoft.Extensions.Hosting;

namespace Asterisk.Sdk.Hosting;

/// <summary>
/// Hosted service wrapper that starts the <see cref="IAriOutboundListener"/> on
/// application start and stops it on shutdown.
/// </summary>
public sealed class AriOutboundListenerHostedService(IAriOutboundListener listener) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await listener.StartAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await listener.StopAsync(cancellationToken);
}
