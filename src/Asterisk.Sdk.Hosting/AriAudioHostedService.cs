using Asterisk.Sdk.Ari.Audio;
using Microsoft.Extensions.Hosting;

namespace Asterisk.Sdk.Hosting;

/// <summary>
/// Hosted service that starts and stops ARI audio servers (AudioSocket and optional WebSocket)
/// for automatic lifecycle management with the application host.
/// </summary>
public sealed class AriAudioHostedService(
    AudioSocketServer audioSocketServer,
    WebSocketAudioServer? webSocketAudioServer = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await audioSocketServer.StartAsync(cancellationToken);
        if (webSocketAudioServer is not null)
            await webSocketAudioServer.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (webSocketAudioServer is not null)
            await webSocketAudioServer.StopAsync(cancellationToken);
        await audioSocketServer.StopAsync(cancellationToken);
    }
}
