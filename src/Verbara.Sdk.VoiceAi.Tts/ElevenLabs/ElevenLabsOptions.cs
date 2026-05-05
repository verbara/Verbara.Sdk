namespace Verbara.Sdk.VoiceAi.Tts.ElevenLabs;

/// <summary>Canonical ElevenLabs model identifiers.</summary>
public static class ElevenLabsModels
{
    /// <summary>ElevenLabs Flash v2.5 — lowest-latency model; recommended default for real-time voice agents.</summary>
    public const string Flash25 = "eleven_flash_v2_5";

    /// <summary>ElevenLabs Turbo v2 — previous default prior to SDK v1.15.3.</summary>
    public const string Turbo2 = "eleven_turbo_v2";

    /// <summary>ElevenLabs Multilingual v2 — highest-quality multilingual model.</summary>
    public const string Multilingual2 = "eleven_multilingual_v2";
}

/// <summary>
/// Streaming-latency optimization level passed to ElevenLabs'
/// <c>optimize_streaming_latency</c> URL parameter (0–4 scale).
/// Higher values reduce latency at the cost of slight quality trade-offs.
/// </summary>
public enum ElevenLabsLatencyOptimization
{
    /// <summary>No optimization (default). Highest audio quality.</summary>
    Off = 0,

    /// <summary>Low optimization.</summary>
    Low = 1,

    /// <summary>Mid optimization — balanced latency / quality trade-off.</summary>
    Mid = 2,

    /// <summary>High optimization.</summary>
    High = 3,

    /// <summary>Maximum optimization. Lowest latency, some quality trade-off.</summary>
    Max = 4
}

/// <summary>
/// PCM output format passed to ElevenLabs' <c>output_format</c> URL parameter.
/// All formats produce 16-bit little-endian mono PCM.
/// </summary>
public enum ElevenLabsOutputFormat
{
    /// <summary>PCM 16-bit mono at 16 000 Hz — default; compatible with most telephony paths.</summary>
    Pcm16k,

    /// <summary>PCM 16-bit mono at 22 050 Hz.</summary>
    Pcm22050,

    /// <summary>PCM 16-bit mono at 24 000 Hz — recommended for Flash 2.5 highest-quality output.</summary>
    Pcm24k
}

/// <summary>Configuration for the ElevenLabs WebSocket streaming TTS provider.</summary>
public sealed class ElevenLabsOptions
{
    /// <summary>ElevenLabs API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Voice identifier to use for synthesis.</summary>
    public string VoiceId { get; set; } = string.Empty;

    /// <summary>
    /// Model identifier.
    /// Default changed from <c>eleven_turbo_v2</c> to <c>eleven_flash_v2_5</c> in SDK v1.15.3
    /// to target the lower-latency Flash 2.5 model. Callers that set this property explicitly
    /// are unaffected. Use <see cref="ElevenLabsModels"/> constants for well-known values.
    /// </summary>
    public string ModelId { get; set; } = ElevenLabsModels.Flash25;

    /// <summary>Voice stability (0.0–1.0). Lower values produce more variation.</summary>
    public float Stability { get; set; } = 0.5f;

    /// <summary>Similarity boost (0.0–1.0). Higher values make the voice more consistent.</summary>
    public float SimilarityBoost { get; set; } = 0.75f;

    /// <summary>
    /// Streaming-latency optimization level (default: <see cref="ElevenLabsLatencyOptimization.Off"/>).
    /// Maps to ElevenLabs' <c>optimize_streaming_latency</c> URL parameter.
    /// </summary>
    public ElevenLabsLatencyOptimization LatencyOptimization { get; set; } = ElevenLabsLatencyOptimization.Off;

    /// <summary>
    /// PCM output format (default: <see cref="ElevenLabsOutputFormat.Pcm16k"/>).
    /// When set, overrides the sample-rate derived from <see cref="Verbara.Sdk.Audio.AudioFormat"/>
    /// passed to <c>SynthesizeAsync</c>.
    /// Maps to ElevenLabs' <c>output_format</c> URL parameter.
    /// </summary>
    public ElevenLabsOutputFormat OutputFormat { get; set; } = ElevenLabsOutputFormat.Pcm16k;
}
