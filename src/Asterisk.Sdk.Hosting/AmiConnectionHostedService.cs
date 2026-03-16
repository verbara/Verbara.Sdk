using Asterisk.Sdk;
using Microsoft.Extensions.Hosting;

namespace Asterisk.Sdk.Hosting;

/// <summary>
/// Hosted service that connects the AMI connection on application start and disconnects on stop.
/// </summary>
public sealed class AmiConnectionHostedService(IAmiConnection connection) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await connection.ConnectAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await connection.DisconnectAsync(cancellationToken);
}
