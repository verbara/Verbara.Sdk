using System.ComponentModel.DataAnnotations;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal;

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

    /// <summary>
    /// How long a function tool may run before the bridge stops waiting for it. Defaults to 30 seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// While a function runs, the session reads nothing from OpenAI, so a function that never returns would hold
    /// the caller in silence for the rest of the call. When this bound elapses first, the bridge answers the call
    /// with the function-call output <c>{"error":"timeout"}</c> and a <c>response.create</c> (nothing is sent once
    /// the caller has hung up), logs a Warning, adds one to <c>openai_realtime.function_calls.timed_out</c>,
    /// publishes the call's <see cref="RealtimeFunctionCalledEvent"/> once with that output, cancels the token it
    /// handed the function, and goes on with the session without waiting for the function any longer. Whatever the
    /// function returns or throws afterwards is logged at Debug and never sent. A function that returns at exactly
    /// the bound is answered as timed out.
    /// </para>
    /// <para>
    /// Accepts more than zero and at most <see cref="int.MaxValue"/> milliseconds. The options validator rejects any
    /// other value naming this option, and the bridge throws <see cref="ArgumentOutOfRangeException"/> naming it from
    /// its constructor and before each function call. In configuration write it as <c>hh:mm:ss</c>
    /// (<c>"00:00:30"</c>): a bare number binds as days, so <c>"5"</c> is five days.
    /// </para>
    /// </remarks>
    [FunctionCallTimeoutRule]
    public TimeSpan FunctionCallTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
