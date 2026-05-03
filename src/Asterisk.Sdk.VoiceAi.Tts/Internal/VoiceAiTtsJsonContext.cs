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

// --- Deepgram TTS DTOs ---

/// <summary>Client → server: synthesize text.</summary>
internal sealed class DeepgramSpeakMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "Speak";
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
}

/// <summary>Client → server: Flush / Clear / Close control messages.</summary>
internal sealed class DeepgramControlMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
}

/// <summary>
/// Server → client text frame — union of all server message types.
/// Fields not present in a given <c>type</c> are null.
/// </summary>
internal sealed class DeepgramTtsServerMessage
{
    [JsonPropertyName("type")] public string? Type { get; set; }

    // SpeakV1Metadata fields
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
    [JsonPropertyName("model_name")] public string? ModelName { get; set; }
    [JsonPropertyName("model_version")] public string? ModelVersion { get; set; }

    // SpeakV1Flushed / SpeakV1Cleared fields
    [JsonPropertyName("sequence_id")] public int? SequenceId { get; set; }

    // SpeakV1Warning fields
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
}

// --- LMNT TTS DTOs ---

/// <summary>
/// First WebSocket message sent to the LMNT streaming endpoint.
/// Contains auth (<c>X-API-Key</c>), voice, format, and synthesis parameters.
/// LMNT requires the API key inside this JSON body — NOT in the HTTP upgrade headers.
/// </summary>
/// <remarks>
/// R1.5 follow-up: verify field names and required set against the live LMNT API at integration test time
/// (see <see href="https://docs.lmnt.com"/> and the LMNT Python SDK source for authoritative schema).
/// </remarks>
internal sealed class LmntInitMessage
{
    [JsonPropertyName("X-API-Key")] public string ApiKey { get; set; } = string.Empty;
    [JsonPropertyName("voice")] public string Voice { get; set; } = string.Empty;
    [JsonPropertyName("format")] public string Format { get; set; } = string.Empty;
    [JsonPropertyName("sample_rate")] public int SampleRate { get; set; }
    [JsonPropertyName("language")] public string Language { get; set; } = string.Empty;
    [JsonPropertyName("speed")] public double Speed { get; set; } = 1.0;
    [JsonPropertyName("model")] public string? Model { get; set; }
}

/// <summary>Text input message sent to the LMNT WebSocket endpoint after the init message.</summary>
internal sealed class LmntTextMessage
{
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Flush command sent to the LMNT WebSocket endpoint.
/// Signals that the client wants the server to emit any buffered audio.
/// </summary>
/// <remarks>
/// R1.5 follow-up: verify exact schema (<c>{"flush":true}</c>) against the live LMNT API and Python SDK source.
/// </remarks>
internal sealed class LmntFlushMessage
{
    [JsonPropertyName("flush")] public bool Flush { get; set; } = true;

    internal static readonly LmntFlushMessage Instance = new();
}

/// <summary>
/// EOF command sent to the LMNT WebSocket endpoint.
/// Signals that no more input is coming; server will emit final audio and close.
/// </summary>
internal sealed class LmntEofMessage
{
    [JsonPropertyName("eof")] public bool Eof { get; set; } = true;

    internal static readonly LmntEofMessage Instance = new();
}

/// <summary>
/// JSON notification message received from the LMNT WebSocket server.
/// Server sends text frames for events such as <c>buffer_empty</c>, <c>finish</c>, and <c>error</c>.
/// Binary frames are raw audio data (not JSON).
/// </summary>
internal sealed class LmntServerNotification
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

[JsonSerializable(typeof(ElevenLabsTextChunk))]
[JsonSerializable(typeof(ElevenLabsVoiceSettings))]
[JsonSerializable(typeof(CartesiaTtsRequest))]
[JsonSerializable(typeof(CartesiaTtsVoice))]
[JsonSerializable(typeof(CartesiaTtsOutputFormat))]
[JsonSerializable(typeof(CartesiaTtsControlMessage))]
[JsonSerializable(typeof(SpeechmaticsTtsRequest))]
[JsonSerializable(typeof(DeepgramSpeakMessage))]
[JsonSerializable(typeof(DeepgramControlMessage))]
[JsonSerializable(typeof(DeepgramTtsServerMessage))]
[JsonSerializable(typeof(LmntInitMessage))]
[JsonSerializable(typeof(LmntTextMessage))]
[JsonSerializable(typeof(LmntFlushMessage))]
[JsonSerializable(typeof(LmntEofMessage))]
[JsonSerializable(typeof(LmntServerNotification))]
internal partial class VoiceAiTtsJsonContext : JsonSerializerContext;
