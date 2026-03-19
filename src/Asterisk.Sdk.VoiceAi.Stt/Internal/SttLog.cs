using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.VoiceAi.Stt.Internal;

internal static partial class SttLog
{
    [LoggerMessage(LogLevel.Debug, "STT {Provider} stream started")]
    internal static partial void StreamStarted(ILogger logger, string provider);

    [LoggerMessage(LogLevel.Debug, "STT {Provider} stream completed")]
    internal static partial void StreamCompleted(ILogger logger, string provider);
}
