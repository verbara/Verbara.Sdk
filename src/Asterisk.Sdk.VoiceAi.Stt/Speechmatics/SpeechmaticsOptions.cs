using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Stt.Speechmatics;

/// <summary>
/// Configuration options for the Speechmatics Realtime WebSocket STT provider.
/// </summary>
/// <remarks>
/// Speechmatics has no official C# SDK. This provider is a hand-rolled, AOT-clean
/// implementation of their Realtime wire protocol
/// (<see href="https://docs.speechmatics.com/rt-api-ref"/>).
/// </remarks>
public sealed class SpeechmaticsOptions
{
    /// <summary>Speechmatics API key (required). Passed as the <c>jwt</c> query parameter.</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// WebSocket base URI. Must begin with <c>wss://</c> or <c>ws://</c>. The language
    /// segment is appended at connect time (e.g. <c>{BaseUri}/{Language}</c>).
    /// </summary>
    [Required]
    [RegularExpression(@"^wss?://.+", ErrorMessage = "BaseUri must start with wss:// or ws://.")]
    public string BaseUri { get; set; } = "wss://eu2.rt.speechmatics.com/v2";

    /// <summary>Language code for recognition (e.g. <c>en</c>, <c>es</c>).</summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Accuracy/latency trade-off. Valid values: <c>standard</c> or <c>enhanced</c>.
    /// Propagated into <c>transcription_config.operating_point</c>.
    /// </summary>
    public string OperatingPoint { get; set; } = "enhanced";

    /// <summary>Audio sample rate in Hz. Speechmatics Realtime expects 8000 or 16000.</summary>
    public int SampleRate { get; set; } = 16000;

    /// <summary>When <c>true</c>, the server emits <c>AddPartialTranscript</c> messages.</summary>
    public bool EnablePartials { get; set; } = true;

    /// <summary>Maximum latency the server is allowed to introduce before emitting a final.</summary>
    public int MaxDelaySeconds { get; set; } = 2;

    /// <summary>WebSocket connect timeout in seconds.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;
}

/// <summary>AOT-safe source-generated validator for <see cref="SpeechmaticsOptions"/>.</summary>
[OptionsValidator]
public sealed partial class SpeechmaticsOptionsValidator : IValidateOptions<SpeechmaticsOptions> { }
