namespace Asterisk.Sdk.VoiceAi.Stt.Google;

/// <summary>Configuration options for the Google Cloud Speech-to-Text REST provider.</summary>
public sealed class GoogleSpeechOptions
{
    /// <summary>Google Cloud API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>BCP-47 language code for recognition.</summary>
    public string LanguageCode { get; set; } = "es-CO";

    /// <summary>Speech recognition model.</summary>
    public string Model { get; set; } = "default";
}
