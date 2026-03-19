namespace Asterisk.Sdk.VoiceAi.Stt.Whisper;

/// <summary>Configuration options for the OpenAI Whisper REST STT provider.</summary>
public sealed class WhisperOptions
{
    /// <summary>OpenAI API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Whisper model name (default: whisper-1).</summary>
    public string Model { get; set; } = "whisper-1";

    /// <summary>Language code for recognition.</summary>
    public string Language { get; set; } = "es";

    /// <summary>API endpoint URL.</summary>
    public Uri Endpoint { get; set; } = new("https://api.openai.com/v1/audio/transcriptions");
}
