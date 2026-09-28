using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Stt.Cartesia;

/// <summary>Configuration options for the Cartesia Ink-Whisper WebSocket STT provider.</summary>
public sealed class CartesiaOptions
{
    /// <summary>Cartesia API key (required).</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>WebSocket base URI. Must begin with <c>wss://</c> or <c>ws://</c>.</summary>
    [Required]
    [RegularExpression(@"^wss?://.+", ErrorMessage = "BaseUri must start with wss:// or ws://.")]
    public string BaseUri { get; set; } = "wss://api.cartesia.ai/stt/websocket";

    /// <summary>Cartesia model name. Defaults to <c>ink-whisper</c> (conversational telephony).</summary>
    public string Model { get; set; } = "ink-whisper";

    /// <summary>Language code for recognition (e.g. <c>en</c>, <c>es</c>).</summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Cartesia API version header (<c>Cartesia-Version</c>). Wire contract
    /// is pinned by the vendor — keep aligned with their docs.
    /// </summary>
    public string ApiVersion { get; set; } = "2024-11-13";

    /// <summary>
    /// How long, in whole seconds, the WebSocket connect (the TCP dial, TLS and the HTTP upgrade) may
    /// take before the stream fails with a <see cref="SpeechProviderFailureException"/> whose
    /// <see cref="SpeechProviderFailureException.Signal"/> is <see cref="SpeechProviderFailureSignal.Handshake"/>,
    /// the failure a refused upgrade also takes. Must be positive. Defaults to 5.
    /// </summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>WebSocket keep-alive interval in seconds.</summary>
    public int KeepAliveSeconds { get; set; } = 20;
}

/// <summary>AOT-safe source-generated validator for <see cref="CartesiaOptions"/>.</summary>
[OptionsValidator]
public sealed partial class CartesiaOptionsValidator : IValidateOptions<CartesiaOptions> { }
