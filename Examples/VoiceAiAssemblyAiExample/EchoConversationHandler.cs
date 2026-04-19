using Asterisk.Sdk.VoiceAi;
using Microsoft.Extensions.Logging;

namespace VoiceAiAssemblyAiExample;

public sealed partial class EchoConversationHandler(ILogger<EchoConversationHandler> logger) : IConversationHandler
{
    public ValueTask<string> HandleAsync(
        string transcript,
        ConversationContext context,
        CancellationToken ct = default)
    {
        LogUserTranscript(logger, context.ChannelId, transcript);
        var response = $"You said: {transcript}";
        LogAssistantResponse(logger, context.ChannelId, response);
        return ValueTask.FromResult(response);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ChannelId}] User: {Transcript}")]
    private static partial void LogUserTranscript(ILogger logger, Guid channelId, string transcript);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ChannelId}] Assistant: {Response}")]
    private static partial void LogAssistantResponse(ILogger logger, Guid channelId, string response);
}
