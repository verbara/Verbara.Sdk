using System.Text.Json.Serialization;

namespace Verbara.Sdk.VoiceAi.Stt.Internal;

// --- Deepgram DTOs ---
internal sealed class DeepgramResultMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("is_final")] public bool IsFinal { get; set; }
    [JsonPropertyName("channel")] public DeepgramChannel? Channel { get; set; }

    /// <summary>
    /// Detail on a <c>type: "Error"</c> frame — the vendor's documented member, <b>not observed</b>.
    /// This surface rejects a bad credential at the handshake (<c>401</c>), so no live run has produced
    /// an in-band failure frame here and the field set of this message type is uncharacterised.
    /// </summary>
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>
    /// The short label the same documented frame carries alongside <see cref="Description"/>. Also
    /// unobserved; read only as a fallback when <see cref="Description"/> is absent.
    /// </summary>
    [JsonPropertyName("message")] public string? Message { get; set; }
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
internal sealed class CartesiaSttTranscriptMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("is_final")] public bool IsFinal { get; set; }
    [JsonPropertyName("confidence")] public float? Confidence { get; set; }

    /// <summary>
    /// Numeric code on a <c>type: "error"</c> frame. Measured, not documented: the run that found the
    /// missing query string was answered
    /// <c>{"type":"error","code":400,"message":"Missing sample_rate: …"}</c> in band, then close
    /// <c>1008</c> — so the frame carries an HTTP-shaped code inside a WebSocket session.
    /// </summary>
    [JsonPropertyName("code")] public int? Code { get; set; }

    /// <summary>The reason accompanying <see cref="Code"/> on an error frame.</summary>
    [JsonPropertyName("message")] public string? Message { get; set; }
}

// --- AssemblyAI Universal Streaming v3 DTOs ---
internal sealed class AssemblyAiTurnMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("transcript")] public string Transcript { get; init; } = string.Empty;
    [JsonPropertyName("end_of_turn")] public bool EndOfTurn { get; init; }
    [JsonPropertyName("turn_is_formatted")] public bool TurnIsFormatted { get; init; }

    /// <summary>
    /// The vendor's numeric code on a <c>type: "Error"</c> frame. Measured: a sub-floor audio message
    /// is answered <c>3007</c> in band and the session then closes with the same <c>3007</c> as its
    /// WebSocket code.
    /// </summary>
    [JsonPropertyName("error_code")] public int? ErrorCode { get; init; }

    /// <summary>
    /// The reason accompanying <see cref="ErrorCode"/> — measured as <c>"Input Duration Error: Input
    /// Duration Violation: 25.0 ms. Expected between 50 and 1000 ms"</c>, which is the rejection that
    /// states the window this client now coalesces to.
    /// </summary>
    [JsonPropertyName("error")] public string? Error { get; init; }
}

// --- Speechmatics Realtime DTOs ---
internal sealed class SpeechmaticsStartRecognitionMessage
{
    [JsonPropertyName("message")] public string Message { get; set; } = "StartRecognition";
    [JsonPropertyName("audio_format")] public SpeechmaticsAudioFormat AudioFormat { get; set; } = new();
    [JsonPropertyName("transcription_config")] public SpeechmaticsTranscriptionConfig TranscriptionConfig { get; set; } = new();
}

internal sealed class SpeechmaticsAudioFormat
{
    [JsonPropertyName("type")] public string Type { get; set; } = "raw";
    [JsonPropertyName("encoding")] public string Encoding { get; set; } = "pcm_s16le";
    [JsonPropertyName("sample_rate")] public int SampleRate { get; set; }
}

internal sealed class SpeechmaticsTranscriptionConfig
{
    [JsonPropertyName("language")] public string Language { get; set; } = string.Empty;
    [JsonPropertyName("operating_point")] public string OperatingPoint { get; set; } = "enhanced";
    [JsonPropertyName("enable_partials")] public bool EnablePartials { get; set; }
    [JsonPropertyName("max_delay")] public double MaxDelay { get; set; }
}

/// <summary>
/// The end-of-input terminator. <c>last_seq_no</c> is the number of audio chunks sent on the
/// session — the service uses it to know how much audio it is still expected to account for, which
/// is why the send loop counts binary frames rather than sending a constant frame the way the other
/// three clients can.
/// </summary>
internal sealed class SpeechmaticsEndOfStreamMessage
{
    [JsonPropertyName("message")] public string Message { get; set; } = "EndOfStream";
    [JsonPropertyName("last_seq_no")] public int LastSeqNo { get; set; }
}

internal sealed class SpeechmaticsTranscriptMessage
{
    /// <summary>
    /// The message <em>kind</em>, not human text — <c>AddTranscript</c>, <c>RecognitionStarted</c>,
    /// <c>Error</c>. This vendor names the discriminator <c>message</c>, where others name it
    /// <c>type</c>.
    /// </summary>
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;

    [JsonPropertyName("results")] public SpeechmaticsResult[]? Results { get; set; }

    /// <summary>
    /// The vendor's symbolic code on an <c>Error</c> or <c>Warning</c> message —
    /// <c>not_authorised</c> and the like. Note the inversion against every other provider here:
    /// on this surface <c>type</c> is the code and <c>message</c> is the kind.
    /// </summary>
    [JsonPropertyName("type")] public string? Type { get; set; }

    /// <summary>Human-readable detail accompanying <see cref="Type"/>.</summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

internal sealed class SpeechmaticsResult
{
    [JsonPropertyName("alternatives")] public SpeechmaticsAlternative[]? Alternatives { get; set; }
}

internal sealed class SpeechmaticsAlternative
{
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("confidence")] public float Confidence { get; set; }
}

[JsonSerializable(typeof(DeepgramResultMessage))]
[JsonSerializable(typeof(WhisperTranscriptionResponse))]
[JsonSerializable(typeof(GoogleSpeechRequest))]
[JsonSerializable(typeof(GoogleSpeechConfig))]
[JsonSerializable(typeof(GoogleSpeechAudio))]
[JsonSerializable(typeof(GoogleSpeechResponse))]
[JsonSerializable(typeof(GoogleSpeechResult))]
[JsonSerializable(typeof(GoogleSpeechAlternative))]
[JsonSerializable(typeof(CartesiaSttTranscriptMessage))]
[JsonSerializable(typeof(AssemblyAiTurnMessage))]
[JsonSerializable(typeof(SpeechmaticsStartRecognitionMessage))]
[JsonSerializable(typeof(SpeechmaticsAudioFormat))]
[JsonSerializable(typeof(SpeechmaticsTranscriptionConfig))]
[JsonSerializable(typeof(SpeechmaticsEndOfStreamMessage))]
[JsonSerializable(typeof(SpeechmaticsTranscriptMessage))]
[JsonSerializable(typeof(SpeechmaticsResult))]
[JsonSerializable(typeof(SpeechmaticsAlternative))]
internal partial class VoiceAiSttJsonContext : JsonSerializerContext;
