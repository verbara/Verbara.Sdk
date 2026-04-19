using Asterisk.Sdk.VoiceAi;
using Microsoft.Extensions.Logging;

namespace VoiceAiCartesiaExample;

public sealed partial class EchoConversationHandler(ILogger<EchoConversationHandler> logger) : IConversationHandler
{
    public ValueTask<string> HandleAsync(
        string transcript,
        ConversationContext context,
        CancellationToken ct = default)
    {
        LogUserTranscript(logger, context.ChannelId, transcript);
        var response = $"Dijiste: {transcript}";
        LogAssistantResponse(logger, context.ChannelId, response);
        return ValueTask.FromResult(response);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ChannelId}] Usuario: {Transcript}")]
    private static partial void LogUserTranscript(ILogger logger, Guid channelId, string transcript);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ChannelId}] Asistente: {Response}")]
    private static partial void LogAssistantResponse(ILogger logger, Guid channelId, string response);
}
