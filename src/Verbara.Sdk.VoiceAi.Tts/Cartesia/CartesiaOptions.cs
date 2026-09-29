using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Tts.Cartesia;

/// <summary>Configuration options for the Cartesia Sonic-3 WebSocket TTS provider.</summary>
public sealed class CartesiaOptions
{
    /// <summary>Cartesia API key (required).</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>WebSocket base URI. Must begin with <c>wss://</c> or <c>ws://</c>.</summary>
    [Required]
    [RegularExpression(@"^wss?://.+", ErrorMessage = "BaseUri must start with wss:// or ws://.")]
    public string BaseUri { get; set; } = "wss://api.cartesia.ai/tts/websocket";

    /// <summary>Cartesia model id. Defaults to <c>sonic-3</c> (best-in-market TTFA 40-90ms).</summary>
    public string Model { get; set; } = "sonic-3";

    /// <summary>Voice identifier to use for synthesis (required).</summary>
    [Required]
    public string VoiceId { get; set; } = string.Empty;

    /// <summary>Language code (e.g. <c>en</c>, <c>es</c>).</summary>
    public string Language { get; set; } = "en";

    /// <summary>Cartesia API version header (<c>Cartesia-Version</c>).</summary>
    public string ApiVersion { get; set; } = "2024-11-13";

    /// <summary>Output encoding sent in the synthesis request (default: raw linear-16 PCM).</summary>
    public string OutputFormat { get; set; } = "pcm_s16le";

    /// <summary>Output sample rate in Hz. Defaults to 16000.</summary>
    public int OutputSampleRate { get; set; } = 16000;

    /// <summary>
    /// How long, in whole seconds, the WebSocket connect (the TCP dial, TLS and the HTTP upgrade) may
    /// take before the synthesis fails with a <see cref="SpeechProviderFailureException"/> whose
    /// <see cref="SpeechProviderFailureException.Signal"/> is <see cref="SpeechProviderFailureSignal.Handshake"/>,
    /// the failure a refused upgrade also takes. Must be positive. Defaults to 5.
    /// </summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;
}

/// <summary>AOT-safe source-generated validator for <see cref="CartesiaOptions"/>.</summary>
[OptionsValidator]
public sealed partial class CartesiaOptionsValidator : IValidateOptions<CartesiaOptions> { }
