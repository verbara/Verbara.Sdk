namespace Asterisk.Sdk.VoiceAi.Tts.Azure;

/// <summary>Configuration for the Azure Cognitive Services TTS REST provider.</summary>
public sealed class AzureTtsOptions
{
    /// <summary>Azure Cognitive Services subscription key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Azure region (e.g., "eastus", "westeurope").</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>Voice name (e.g., "es-CO-SalomeNeural").</summary>
    public string VoiceName { get; set; } = string.Empty;

    /// <summary>Output audio format. Defaults to raw 8 kHz 16-bit mono PCM.</summary>
    public string OutputFormat { get; set; } = AzureTtsOutputFormat.Raw8Khz16BitMonoPcm;
}
