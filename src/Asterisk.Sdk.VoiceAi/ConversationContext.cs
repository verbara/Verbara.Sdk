using Asterisk.Sdk.Audio;

namespace Asterisk.Sdk.VoiceAi;

/// <summary>
/// Provides contextual information about an ongoing conversation session,
/// including channel identity, conversation history, and audio format.
/// </summary>
public sealed class ConversationContext
{
    /// <summary>Unique identifier for the audio channel / call leg.</summary>
    public Guid ChannelId { get; init; }

    /// <summary>Ordered conversation turns up to the current point.</summary>
    public IReadOnlyList<ConversationTurn> History { get; init; } = [];

    /// <summary>Audio format of the incoming stream from the caller.</summary>
    public AudioFormat InputFormat { get; init; }
}
