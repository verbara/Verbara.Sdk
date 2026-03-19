using Asterisk.Sdk.VoiceAi.Events;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.VoiceAi.Internal;

internal static partial class VoiceAiLog
{
    [LoggerMessage(LogLevel.Information, "VoiceAi pipeline started for channel {ChannelId}")]
    internal static partial void PipelineStarted(ILogger logger, Guid channelId);

    [LoggerMessage(LogLevel.Information, "VoiceAi pipeline stopped for channel {ChannelId}")]
    internal static partial void PipelineStopped(ILogger logger, Guid channelId);

    [LoggerMessage(LogLevel.Warning, "VoiceAi pipeline error [{Source}] for channel {ChannelId}: {Message}")]
    internal static partial void PipelineError(ILogger logger, PipelineErrorSource source, Guid channelId, string message);

    [LoggerMessage(LogLevel.Debug, "Barge-in detected for channel {ChannelId}")]
    internal static partial void BargInDetected(ILogger logger, Guid channelId);

    [LoggerMessage(LogLevel.Error, "VoiceAi session error [{ChannelId}]")]
    internal static partial void SessionError(ILogger logger, Guid channelId, Exception exception);
}
