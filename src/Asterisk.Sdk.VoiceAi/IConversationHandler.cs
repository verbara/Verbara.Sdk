namespace Asterisk.Sdk.VoiceAi;

/// <summary>
/// Handles a conversation turn: receives a user transcript and produces
/// an assistant response. Implementations may call LLM APIs, run business
/// logic, or perform lookups.
/// </summary>
public interface IConversationHandler
{
    /// <summary>Processes a user transcript and returns the assistant response text.</summary>
    ValueTask<string> HandleAsync(
        string transcript,
        ConversationContext context,
        CancellationToken ct = default);
}
