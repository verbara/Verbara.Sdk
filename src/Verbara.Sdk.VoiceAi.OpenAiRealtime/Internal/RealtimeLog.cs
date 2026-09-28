using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal;

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

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ChannelId}] OpenAI closed the socket: {CloseStatus} {CloseDescription}")]
    public static partial void VendorClosed(ILogger logger, Guid channelId, int closeStatus, string closeDescription);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{ChannelId}] OpenAI did not answer the close within {BoundMs} ms after the caller hung up (the bound pauses while a function runs); the session ended without the vendor's close code")]
    public static partial void CloseUnanswered(ILogger logger, Guid channelId, double boundMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ChannelId}] Function '{FunctionName}' returned after the caller hung up; its result was not sent")]
    public static partial void FunctionResultNotSent(ILogger logger, Guid channelId, string functionName);
}
