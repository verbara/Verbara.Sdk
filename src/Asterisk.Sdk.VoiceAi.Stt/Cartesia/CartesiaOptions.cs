using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Stt.Cartesia;

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

    /// <summary>WebSocket connect timeout in seconds.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>WebSocket keep-alive interval in seconds.</summary>
    public int KeepAliveSeconds { get; set; } = 20;
}

/// <summary>AOT-safe source-generated validator for <see cref="CartesiaOptions"/>.</summary>
[OptionsValidator]
public sealed partial class CartesiaOptionsValidator : IValidateOptions<CartesiaOptions> { }
