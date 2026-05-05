using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Tts.Deepgram;

/// <summary>Configuration options for the Deepgram Aura 2 WebSocket TTS provider.</summary>
public sealed class DeepgramTtsOptions
{
    /// <summary>Deepgram API key (required). Used in <c>Authorization: Token &lt;key&gt;</c>.</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// WebSocket base URI. Must begin with <c>wss://</c> or <c>ws://</c>.
    /// </summary>
    [Required]
    [RegularExpression(@"^wss?://.+", ErrorMessage = "BaseUri must start with wss:// or ws://.")]
    public string BaseUri { get; set; } = "wss://api.deepgram.com/v1/speak";

    /// <summary>
    /// Deepgram TTS model id. Defaults to <see cref="DeepgramVoices.Thalia"/> (<c>aura-2-thalia-en</c>).
    /// See <see cref="DeepgramVoices"/> for the curated voice catalog.
    /// </summary>
    public string Model { get; set; } = DeepgramVoices.Thalia;

    /// <summary>
    /// Output encoding. Supported values: <c>linear16</c> (default), <c>mulaw</c>, <c>alaw</c>.
    /// </summary>
    public string Encoding { get; set; } = "linear16";

    /// <summary>
    /// Output sample rate in Hz. Supported values: 8000, 16000, 24000 (default), 32000, 48000.
    /// </summary>
    public int SampleRate { get; set; } = 24000;

    /// <summary>
    /// Playback speed multiplier (default: 1.0).
    /// </summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>WebSocket connect timeout in seconds.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;
}

/// <summary>AOT-safe source-generated validator for <see cref="DeepgramTtsOptions"/>.</summary>
[OptionsValidator]
public sealed partial class DeepgramTtsOptionsValidator : IValidateOptions<DeepgramTtsOptions> { }
