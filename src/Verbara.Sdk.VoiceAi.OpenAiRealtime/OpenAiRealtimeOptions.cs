using System.ComponentModel.DataAnnotations;
using Verbara.Sdk.Audio;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime;

/// <summary>Configuration for the OpenAI Realtime bridge.</summary>
public sealed class OpenAiRealtimeOptions
{
    /// <summary>OpenAI API key (required).</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// OpenAI Realtime model identifier (required). Defaults to <c>gpt-realtime</c>, the vendor's
    /// generally available alias.
    /// </summary>
    /// <remarks>
    /// A preview or dated identifier is retired on the vendor's schedule, and the endpoint closes a
    /// session that asks for a model it no longer serves with <c>4004</c> (<c>model_not_found</c>).
    /// Set this to pin another identifier the vendor lists.
    /// </remarks>
    [Required]
    public string Model { get; set; } = "gpt-realtime";

    /// <summary>Voice for TTS output. Defaults to <c>alloy</c>.</summary>
    public string Voice { get; set; } = "alloy";

    /// <summary>System instructions sent to the model in <c>session.update</c>.</summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>VAD mode. <see cref="VadMode.ServerSide"/> (default) lets OpenAI detect turn boundaries.</summary>
    public VadMode VadMode { get; set; } = VadMode.ServerSide;

    /// <summary>
    /// Audio format of the Asterisk AudioSocket stream.
    /// The bridge resamples between <see cref="AudioFormat.SampleRate"/> and 24000 Hz (OpenAI's required rate).
    /// If <see cref="AudioFormat.SampleRate"/> is already 24000, no resampling is applied.
    /// </summary>
    public AudioFormat InputFormat { get; set; } = AudioFormat.Slin16Mono8kHz;
}
