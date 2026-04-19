using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Tts.Speechmatics;

/// <summary>
/// Configuration options for the Speechmatics REST TTS provider.
/// </summary>
/// <remarks>
/// Unlike Cartesia's streaming WebSocket, Speechmatics TTS is a plain HTTPS POST
/// that returns the full audio body in a single response. This provider reads the
/// response body in chunks to keep memory bounded.
/// </remarks>
public sealed class SpeechmaticsOptions
{
    /// <summary>Speechmatics API key (required). Sent as <c>Authorization: Bearer {ApiKey}</c>.</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// REST endpoint. Must begin with <c>https://</c> or <c>http://</c>. Defaults to the
    /// public preview endpoint documented at
    /// <see href="https://docs.speechmatics.com/tts-api-ref"/>.
    /// </summary>
    [Required]
    [RegularExpression(@"^https?://.+", ErrorMessage = "BaseUri must start with https:// or http://.")]
    public string BaseUri { get; set; } = "https://preview.tts.speechmatics.com/generate";

    /// <summary>Voice identifier to use for synthesis.</summary>
    public string Voice { get; set; } = "eleanor";

    /// <summary>Language code (e.g. <c>en</c>, <c>es</c>).</summary>
    public string Language { get; set; } = "en";

    /// <summary>Output sample rate in Hz. Defaults to 16000.</summary>
    public int SampleRate { get; set; } = 16000;

    /// <summary>HTTP connect / request timeout in seconds.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 10;
}

/// <summary>AOT-safe source-generated validator for <see cref="SpeechmaticsOptions"/>.</summary>
[OptionsValidator]
public sealed partial class SpeechmaticsOptionsValidator : IValidateOptions<SpeechmaticsOptions> { }
