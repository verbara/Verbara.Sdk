using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.VoiceAi.Pipeline;

/// <summary>
/// Hosted service that wires <see cref="AudioSocketServer.OnSessionStarted"/>
/// to <see cref="VoiceAiPipeline.HandleSessionAsync"/>, spawning a pipeline
/// loop for each incoming AudioSocket session.
/// </summary>
public sealed class VoiceAiSessionBroker : IHostedService
{
    private readonly AudioSocketServer _server;
    private readonly VoiceAiPipeline _pipeline;
    private readonly ILogger<VoiceAiSessionBroker> _logger;
    private CancellationToken _stoppingToken;

    /// <summary>Creates a new session broker.</summary>
    public VoiceAiSessionBroker(
        AudioSocketServer server,
        VoiceAiPipeline pipeline,
        ILogger<VoiceAiSessionBroker> logger)
    {
        _server = server;
        _pipeline = pipeline;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingToken = cancellationToken;

        _server.OnSessionStarted += session =>
        {
            _ = _pipeline.HandleSessionAsync(session, _stoppingToken)
                .AsTask()
                .ContinueWith(
                    t => VoiceAiLog.SessionError(_logger, session.ChannelId, t.Exception!),
                    TaskContinuationOptions.OnlyOnFaulted);
            return ValueTask.CompletedTask;
        };

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
