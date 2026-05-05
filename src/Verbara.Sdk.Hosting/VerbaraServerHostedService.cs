using Verbara.Sdk;
using Microsoft.Extensions.Hosting;

namespace Verbara.Sdk.Hosting;

/// <summary>
/// Hosted service that starts the VerbaraServer (Live layer) on application start.
/// </summary>
public sealed class VerbaraServerHostedService(IVerbaraServer server) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await server.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
