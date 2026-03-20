using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Internal;

/// <summary>Source-generated high-performance log messages for OpenAI Realtime.</summary>
internal static partial class RealtimeLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[{ChannelId}] Realtime session started")]
    public static partial void SessionStarted(ILogger logger, Guid channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ChannelId}] Realtime session ended")]
    public static partial void SessionEnded(ILogger logger, Guid channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ChannelId}] WebSocket connected to OpenAI Realtime API")]
    public static partial void WebSocketConnected(ILogger logger, Guid channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ChannelId}] session.created received")]
    public static partial void SessionCreated(ILogger logger, Guid channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{ChannelId}] Barge-in: response cancelled by OpenAI")]
    public static partial void ResponseCancelled(ILogger logger, Guid channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{ChannelId}] Unknown function tool '{FunctionName}' — ignoring")]
    public static partial void UnknownFunction(ILogger logger, Guid channelId, string functionName);

    [LoggerMessage(Level = LogLevel.Error, Message = "[{ChannelId}] Realtime session error: {ErrorMessage}")]
    public static partial void SessionError(ILogger logger, Guid channelId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Error, Message = "[{ChannelId}] OpenAI error: {ErrorMessage}")]
    public static partial void OpenAiError(ILogger logger, Guid channelId, string errorMessage);
}
