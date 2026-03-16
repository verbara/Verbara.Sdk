using Asterisk.Sdk;
using Microsoft.Extensions.Hosting;

namespace Asterisk.Sdk.Hosting;

/// <summary>
/// Hosted service that starts the AsteriskServer (Live layer) on application start.
/// </summary>
public sealed class AsteriskServerHostedService(IAsteriskServer server) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await server.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
