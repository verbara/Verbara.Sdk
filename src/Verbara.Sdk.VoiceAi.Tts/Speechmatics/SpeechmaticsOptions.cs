using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Tts.Speechmatics;

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
    /// REST <b>origin</b> — scheme and host only, with no path. Must begin with <c>https://</c>
    /// or <c>http://</c>. The synthesizer appends <c>/generate/{Voice}</c>, because the API selects
    /// the voice by path segment rather than by a body field.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Behavioural change.</b> Before 2.4.1 this property defaulted to
    /// <c>https://preview.tts.speechmatics.com/generate</c> — the complete endpoint — and the voice
    /// was sent as a JSON body field. That request returns <c>404 Not Found</c>: the API has no
    /// <c>/generate</c> route. Callers who set this property must now supply the origin alone; a
    /// value that still carries <c>/generate</c> produces <c>/generate/generate/{Voice}</c>.
    /// </para>
    /// </remarks>
    [Required]
    [RegularExpression(@"^https?://.+", ErrorMessage = "BaseUri must start with https:// or http://.")]
    public string BaseUri { get; set; } = "https://preview.tts.speechmatics.com";

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
