using System.ComponentModel.DataAnnotations;
using Asterisk.Sdk.Audio;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime;

/// <summary>Configuration for the OpenAI Realtime bridge.</summary>
public sealed class OpenAiRealtimeOptions
{
    /// <summary>OpenAI API key (required).</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>OpenAI Realtime model identifier (required).</summary>
    [Required]
    public string Model { get; set; } = "gpt-4o-realtime-preview";

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
