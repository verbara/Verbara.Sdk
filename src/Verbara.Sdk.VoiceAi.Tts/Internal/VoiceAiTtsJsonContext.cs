using System.Text.Json.Serialization;

namespace Verbara.Sdk.VoiceAi.Tts.Internal;

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

/// <summary>
/// Server → client text frame from the ElevenLabs streaming endpoint. This is where the audio is:
/// a live run of the shipped request received <b>zero</b> binary bytes and four text frames keyed
/// <c>alignment, audio, isFinal, normalizedAlignment</c>, the base64 in <c>audio</c> decoding to
/// 86 193 B of 16 kHz PCM.
/// </summary>
/// <remarks>
/// The two alignment members are deliberately <b>not</b> modelled. They are optional metadata the
/// synthesizer has no use for, and <c>System.Text.Json</c> skips unmapped members by default — so
/// tolerating them costs nothing and modelling them would pin a shape this SDK does not consume.
/// Adding required-member fences is <c>provider-dto-robustness-fences</c>' business, not this type's.
/// </remarks>
internal sealed class ElevenLabsAudioOutput
{
    /// <summary>Base64 of the audio in the requested <c>output_format</c>. Absent on other frames.</summary>
    [JsonPropertyName("audio")] public string? Audio { get; set; }

    /// <summary>Set on the vendor's last frame of an utterance.</summary>
    [JsonPropertyName("isFinal")] public bool? IsFinal { get; set; }
}

// --- Cartesia TTS DTOs ---
internal sealed class CartesiaTtsRequest
{
    [JsonPropertyName("model_id")] public string ModelId { get; set; } = string.Empty;
    [JsonPropertyName("voice")] public CartesiaTtsVoice Voice { get; set; } = new();
    [JsonPropertyName("output_format")] public CartesiaTtsOutputFormat OutputFormat { get; set; } = new();
    [JsonPropertyName("language")] public string Language { get; set; } = string.Empty;
    [JsonPropertyName("transcript")] public string Transcript { get; set; } = string.Empty;

    /// <summary>
    /// Identifier the server echoes on every frame of this synthesis. <b>Required</b> — omitting it
    /// is not "the server picks one".
    /// </summary>
    /// <remarks>
    /// Measured against the live endpoint: the shipped request, which sent no <c>context_id</c> at
    /// all, was answered with a single text frame
    /// <c>{"type":"error","status_code":400,"done":true,"error":"context_id is invalid: …"}</c> and
    /// zero audio. A control differing only in carrying one received audio. A prior hypothesis that
    /// <c>"continue": null</c> caused the rejection was <em>refuted</em> by an A/B: both forms
    /// produced the identical error, so <see cref="Continue"/> is left exactly as it was.
    /// </remarks>
    [JsonPropertyName("context_id")] public string ContextId { get; set; } = string.Empty;

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

/// <summary>
/// Server → client text frame from the Cartesia streaming endpoint — the union of the message types
/// this client acts on. This is where the audio is: a live run of a corrected request received
/// <b>zero</b> binary bytes and seven <c>chunk</c> text frames keyed
/// <c>context_id, data, done, flush_id, status_code, step_time, type</c>, the base64 in <c>data</c>
/// decoding to 32 694 B of PCM, followed by a terminator keyed
/// <c>context_id, done, status_code, type</c>.
/// </summary>
/// <remarks>
/// <c>flush_id</c>, <c>step_time</c> and the echoed <c>context_id</c> are deliberately <b>not</b>
/// modelled: this client consumes none of them, and <c>System.Text.Json</c> skips unmapped members
/// by default, so tolerating them costs nothing while modelling them would pin a shape nothing here
/// reads. This type replaces <c>CartesiaTtsControlMessage</c>, which modelled <c>type</c> alone —
/// enough to recognise the terminator, and blind to the frame carrying every byte of audio.
/// </remarks>
internal sealed class CartesiaTtsServerMessage
{
    /// <summary>Discriminator: <c>chunk</c>, <c>done</c>, <c>error</c>, …</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;

    /// <summary>Base64 of the audio in the requested <c>output_format</c>. Present on <c>chunk</c>.</summary>
    [JsonPropertyName("data")] public string? Data { get; set; }

    /// <summary>Human-readable reason, on <c>error</c> frames only.</summary>
    [JsonPropertyName("error")] public string? Error { get; set; }

    /// <summary>Set on the frame that ends the stream.</summary>
    [JsonPropertyName("done")] public bool? Done { get; set; }

    /// <summary>HTTP-shaped status the vendor puts on every frame.</summary>
    [JsonPropertyName("status_code")] public int? StatusCode { get; set; }
}

// --- Speechmatics TTS DTOs ---
internal sealed class SpeechmaticsTtsRequest
{
    // No "voice" field: the API selects the voice by path segment (/generate/{voice}).
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
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

    // Omitted when null rather than sent as `"model": null`. LMNT's WS endpoint validates the field
    // against a literal set that does not include null, and answers an explicit null with
    // `1002 protocol error` + zero audio — which is the default configuration of this client.
    // Serializing null here is not a cosmetic difference; it is a total outage.
    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }
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
[JsonSerializable(typeof(ElevenLabsAudioOutput))]
[JsonSerializable(typeof(CartesiaTtsRequest))]
[JsonSerializable(typeof(CartesiaTtsVoice))]
[JsonSerializable(typeof(CartesiaTtsOutputFormat))]
[JsonSerializable(typeof(CartesiaTtsServerMessage))]
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
