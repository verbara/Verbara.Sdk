namespace Asterisk.Sdk.VoiceAi.Stt.Deepgram;

/// <summary>Configuration options for the Deepgram WebSocket STT provider.</summary>
public sealed class DeepgramOptions
{
    /// <summary>Deepgram API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Deepgram model name (default: nova-2).</summary>
    public string Model { get; set; } = "nova-2";

    /// <summary>Language code for recognition.</summary>
    public string Language { get; set; } = "es";

    /// <summary>Whether to receive interim (partial) results.</summary>
    public bool InterimResults { get; set; } = true;

    /// <summary>Whether to enable punctuation in transcripts.</summary>
    public bool Punctuate { get; set; } = true;
}
