namespace Asterisk.Sdk.VoiceAi.Stt.Whisper;

/// <summary>Configuration options for the Azure OpenAI Whisper REST STT provider.</summary>
public sealed class AzureWhisperOptions
{
    /// <summary>Azure OpenAI API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Azure OpenAI resource endpoint (e.g. https://myresource.openai.azure.com/openai/deployments).</summary>
    public Uri Endpoint { get; set; } = default!;

    /// <summary>Azure deployment name for the Whisper model.</summary>
    public string DeploymentName { get; set; } = string.Empty;

    /// <summary>Azure API version.</summary>
    public string ApiVersion { get; set; } = "2024-06-01";
}
