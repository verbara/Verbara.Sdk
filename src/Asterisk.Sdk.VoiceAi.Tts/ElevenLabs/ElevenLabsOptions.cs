namespace Asterisk.Sdk.VoiceAi.Tts.ElevenLabs;

/// <summary>Configuration for the ElevenLabs WebSocket streaming TTS provider.</summary>
public sealed class ElevenLabsOptions
{
    /// <summary>ElevenLabs API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Voice identifier to use for synthesis.</summary>
    public string VoiceId { get; set; } = string.Empty;

    /// <summary>Model identifier (default: eleven_turbo_v2).</summary>
    public string ModelId { get; set; } = "eleven_turbo_v2";

    /// <summary>Voice stability (0.0–1.0). Lower values produce more variation.</summary>
    public float Stability { get; set; } = 0.5f;

    /// <summary>Similarity boost (0.0–1.0). Higher values make the voice more consistent.</summary>
    public float SimilarityBoost { get; set; } = 0.75f;
}
