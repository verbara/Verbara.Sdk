namespace Asterisk.Sdk.VoiceAi;

/// <summary>
/// Represents a single turn in the conversation: the user's transcript,
/// the assistant's response, and when it occurred.
/// </summary>
public readonly record struct ConversationTurn(
    string UserTranscript,
    string AssistantResponse,
    DateTimeOffset Timestamp);
