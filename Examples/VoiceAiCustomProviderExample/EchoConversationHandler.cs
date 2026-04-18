using Asterisk.Sdk.VoiceAi;

namespace VoiceAiCustomProviderExample;

/// <summary>
/// Trivial handler that echoes the user's transcript back. In a real
/// application this is where you'd call your LLM or business logic.
/// </summary>
public sealed class EchoConversationHandler : IConversationHandler
{
    public ValueTask<string> HandleAsync(
        string transcript,
        ConversationContext context,
        CancellationToken ct = default)
        => ValueTask.FromResult($"You said: {transcript}");
}
