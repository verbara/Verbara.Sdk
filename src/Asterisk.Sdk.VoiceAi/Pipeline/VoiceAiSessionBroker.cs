using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.VoiceAi.Pipeline;

/// <summary>
/// Hosted service that wires <see cref="AudioSocketServer.OnSessionStarted"/>
/// to <see cref="ISessionHandler.HandleSessionAsync"/>, spawning a handler
/// loop for each incoming AudioSocket session.
/// </summary>
public sealed class VoiceAiSessionBroker : IHostedService
{
    private readonly AudioSocketServer _server;
    private readonly ISessionHandler _handler;
    private readonly ILogger<VoiceAiSessionBroker> _logger;
    private CancellationToken _stoppingToken;

    /// <summary>Creates a new session broker.</summary>
    public VoiceAiSessionBroker(
        AudioSocketServer server,
        ISessionHandler handler,
        ILogger<VoiceAiSessionBroker> logger)
    {
        _server = server;
        _handler = handler;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingToken = cancellationToken;

        _server.OnSessionStarted += session =>
        {
            _ = _handler.HandleSessionAsync(session, _stoppingToken)
                .AsTask()
                .ContinueWith(
                    t => VoiceAiLog.SessionError(_logger, session.ChannelId, t.Exception!),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            return ValueTask.CompletedTask;
        };

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
