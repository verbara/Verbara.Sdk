using System.Text.Json.Serialization;

namespace Asterisk.Sdk.VoiceAi.Tts.Internal;

internal sealed class ElevenLabsTextChunk
{
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("flush")] public bool? Flush { get; set; }
    [JsonPropertyName("voice_settings")] public ElevenLabsVoiceSettings? VoiceSettings { get; set; }
}

internal sealed class ElevenLabsVoiceSettings
{
    [JsonPropertyName("stability")] public float Stability { get; set; }
    [JsonPropertyName("similarity_boost")] public float SimilarityBoost { get; set; }
}

[JsonSerializable(typeof(ElevenLabsTextChunk))]
[JsonSerializable(typeof(ElevenLabsVoiceSettings))]
internal partial class VoiceAiTtsJsonContext : JsonSerializerContext;
