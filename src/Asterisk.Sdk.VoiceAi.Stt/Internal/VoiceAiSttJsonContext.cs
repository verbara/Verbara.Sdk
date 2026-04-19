using System.Text.Json.Serialization;

namespace Asterisk.Sdk.VoiceAi.Stt.Internal;

// --- Deepgram DTOs ---
internal sealed class DeepgramResultMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("is_final")] public bool IsFinal { get; set; }
    [JsonPropertyName("channel")] public DeepgramChannel? Channel { get; set; }
}

internal sealed class DeepgramChannel
{
    [JsonPropertyName("alternatives")] public DeepgramAlternative[]? Alternatives { get; set; }
}

internal sealed class DeepgramAlternative
{
    [JsonPropertyName("transcript")] public string Transcript { get; set; } = string.Empty;
    [JsonPropertyName("confidence")] public float Confidence { get; set; }
}

// --- Whisper / Azure Whisper DTO (shared) ---
internal sealed class WhisperTranscriptionResponse
{
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
}

// --- Google STT DTOs ---
internal sealed class GoogleSpeechRequest
{
    [JsonPropertyName("config")] public GoogleSpeechConfig Config { get; set; } = new();
    [JsonPropertyName("audio")] public GoogleSpeechAudio Audio { get; set; } = new();
}

internal sealed class GoogleSpeechConfig
{
    [JsonPropertyName("encoding")] public string Encoding { get; set; } = "LINEAR16";
    [JsonPropertyName("sampleRateHertz")] public int SampleRateHertz { get; set; }
    [JsonPropertyName("languageCode")] public string LanguageCode { get; set; } = "es-CO";
    [JsonPropertyName("model")] public string Model { get; set; } = "default";
}

internal sealed class GoogleSpeechAudio
{
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
}

internal sealed class GoogleSpeechResponse
{
    [JsonPropertyName("results")] public GoogleSpeechResult[]? Results { get; set; }
}

internal sealed class GoogleSpeechResult
{
    [JsonPropertyName("alternatives")] public GoogleSpeechAlternative[]? Alternatives { get; set; }
}

internal sealed class GoogleSpeechAlternative
{
    [JsonPropertyName("transcript")] public string Transcript { get; set; } = string.Empty;
    [JsonPropertyName("confidence")] public float Confidence { get; set; }
}

// --- Cartesia STT DTOs ---
internal sealed class CartesiaSttInitMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "start";
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("language")] public string Language { get; set; } = string.Empty;
    [JsonPropertyName("encoding")] public string Encoding { get; set; } = "pcm_s16le";
    [JsonPropertyName("sample_rate")] public int SampleRate { get; set; }
}

internal sealed class CartesiaSttTranscriptMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("is_final")] public bool IsFinal { get; set; }
    [JsonPropertyName("confidence")] public float? Confidence { get; set; }
}

// --- AssemblyAI Universal Streaming v3 DTOs ---
internal sealed class AssemblyAiTurnMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("transcript")] public string Transcript { get; init; } = string.Empty;
    [JsonPropertyName("end_of_turn")] public bool EndOfTurn { get; init; }
    [JsonPropertyName("turn_is_formatted")] public bool TurnIsFormatted { get; init; }
}

[JsonSerializable(typeof(DeepgramResultMessage))]
[JsonSerializable(typeof(WhisperTranscriptionResponse))]
[JsonSerializable(typeof(GoogleSpeechRequest))]
[JsonSerializable(typeof(GoogleSpeechConfig))]
[JsonSerializable(typeof(GoogleSpeechAudio))]
[JsonSerializable(typeof(GoogleSpeechResponse))]
[JsonSerializable(typeof(GoogleSpeechResult))]
[JsonSerializable(typeof(GoogleSpeechAlternative))]
[JsonSerializable(typeof(CartesiaSttInitMessage))]
[JsonSerializable(typeof(CartesiaSttTranscriptMessage))]
[JsonSerializable(typeof(AssemblyAiTurnMessage))]
internal partial class VoiceAiSttJsonContext : JsonSerializerContext;
