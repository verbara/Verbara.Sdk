using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.VoiceAi.AudioSocket.Internal;

/// <summary>Source-generated high-performance log messages for AudioSocket.</summary>
internal static partial class AudioSocketLog
{
    // ── Server ──

    [LoggerMessage(Level = LogLevel.Information, Message = "[AudioSocket] Server listening on {Host}:{Port}")]
    public static partial void ServerListening(ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "[AudioSocket] Server stopped")]
    public static partial void ServerStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "[AudioSocket] Error accepting connection")]
    public static partial void AcceptError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AudioSocket] Connection did not send UUID frame within timeout, closing")]
    public static partial void NoUuidFrame(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AudioSocket] Session limit reached ({Limit}), rejecting connection")]
    public static partial void SessionLimitReached(ILogger logger, int limit);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[AudioSocket] Session started: channel_id={ChannelId}")]
    public static partial void SessionStarted(ILogger logger, Guid channelId);

    [LoggerMessage(Level = LogLevel.Error, Message = "[AudioSocket] Error handling connection")]
    public static partial void HandleConnectionError(ILogger logger, Exception exception);

    // ── Session ──

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AudioSocket] Read loop ended for channel {ChannelId}")]
    public static partial void ReadLoopEnded(ILogger logger, Exception exception, Guid channelId);
}
