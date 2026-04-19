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

// --- Cartesia TTS DTOs ---
internal sealed class CartesiaTtsRequest
{
    [JsonPropertyName("model_id")] public string ModelId { get; set; } = string.Empty;
    [JsonPropertyName("voice")] public CartesiaTtsVoice Voice { get; set; } = new();
    [JsonPropertyName("output_format")] public CartesiaTtsOutputFormat OutputFormat { get; set; } = new();
    [JsonPropertyName("language")] public string Language { get; set; } = string.Empty;
    [JsonPropertyName("transcript")] public string Transcript { get; set; } = string.Empty;
    [JsonPropertyName("continue")] public bool? Continue { get; set; }
}

internal sealed class CartesiaTtsVoice
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "id";
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
}

internal sealed class CartesiaTtsOutputFormat
{
    [JsonPropertyName("container")] public string Container { get; set; } = "raw";
    [JsonPropertyName("encoding")] public string Encoding { get; set; } = "pcm_s16le";
    [JsonPropertyName("sample_rate")] public int SampleRate { get; set; }
}

internal sealed class CartesiaTtsControlMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
}

// --- Speechmatics TTS DTOs ---
internal sealed class SpeechmaticsTtsRequest
{
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("voice")] public string Voice { get; set; } = string.Empty;
    [JsonPropertyName("language")] public string Language { get; set; } = string.Empty;
    [JsonPropertyName("sample_rate")] public int SampleRate { get; set; }
}

[JsonSerializable(typeof(ElevenLabsTextChunk))]
[JsonSerializable(typeof(ElevenLabsVoiceSettings))]
[JsonSerializable(typeof(CartesiaTtsRequest))]
[JsonSerializable(typeof(CartesiaTtsVoice))]
[JsonSerializable(typeof(CartesiaTtsOutputFormat))]
[JsonSerializable(typeof(CartesiaTtsControlMessage))]
[JsonSerializable(typeof(SpeechmaticsTtsRequest))]
internal partial class VoiceAiTtsJsonContext : JsonSerializerContext;
