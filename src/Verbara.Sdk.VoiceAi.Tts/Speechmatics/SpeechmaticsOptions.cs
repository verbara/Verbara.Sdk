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

    /// <summary>
    /// Voice identifier to use for synthesis, sent as the <c>/generate/{Voice}</c> path segment.
    /// Must be one of <see cref="SpeechmaticsVoices"/> unless <see cref="AllowUnlistedVoice"/> is
    /// set; the comparison is case-sensitive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Behavioural change.</b> Before 2.4.1 this defaulted to <c>"eleanor"</c>, which is not a
    /// voice the service offers. The API does not reject an unknown segment — it answers
    /// <c>200 audio/wav</c> in <see cref="SpeechmaticsVoices.Jack"/>'s voice — so every caller on
    /// the old default was already being served Jack. Naming that default <c>jack</c> makes the
    /// configuration describe what the service actually does; the audio callers receive is
    /// unchanged.
    /// </para>
    /// </remarks>
    [Required]
    public string Voice { get; set; } = SpeechmaticsVoices.Jack;

    /// <summary>
    /// Skip the <see cref="SpeechmaticsVoices"/> membership check on <see cref="Voice"/>.
    /// </summary>
    /// <remarks>
    /// The service is a vendor-labelled preview and its roster may grow between SDK releases. Set
    /// this to accept a voice the shipped catalog does not list yet. The cost is the failure mode
    /// the check exists to prevent: a value the service does not recognise still returns
    /// <c>200 audio/wav</c>, in the fallback voice, with nothing in the response to say so.
    /// </remarks>
    public bool AllowUnlistedVoice { get; set; }

    /// <summary>Language code (e.g. <c>en</c>, <c>es</c>).</summary>
    public string Language { get; set; } = "en";

    /// <summary>Output sample rate in Hz. Defaults to 16000.</summary>
    public int SampleRate { get; set; } = 16000;

    /// <summary>HTTP connect / request timeout in seconds.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 10;
}

/// <summary>AOT-safe source-generated DataAnnotations validator for <see cref="SpeechmaticsOptions"/>.</summary>
[OptionsValidator]
internal sealed partial class SpeechmaticsOptionsDataAnnotationsValidator
    : IValidateOptions<SpeechmaticsOptions>
{ }

/// <summary>
/// Validates <see cref="SpeechmaticsOptions"/>: the source-generated DataAnnotations rules first,
/// then the <see cref="SpeechmaticsVoices"/> membership check on <see cref="SpeechmaticsOptions.Voice"/>.
/// </summary>
/// <remarks>
/// The voice check lives here rather than in an attribute because the service will not perform it.
/// <c>POST /generate/{unknown}</c> returns <c>200 audio/wav</c> synthesised in the fallback voice,
/// so a typo is silent at every layer below this one: no exception, no status code, no field in
/// the body. Startup is the last place it can still be caught cheaply.
/// </remarks>
public sealed class SpeechmaticsOptionsValidator : IValidateOptions<SpeechmaticsOptions>
{
    private static readonly SpeechmaticsOptionsDataAnnotationsValidator DataAnnotations = new();

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SpeechmaticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var annotations = DataAnnotations.Validate(name, options);
        if (annotations.Failed)
        {
            return annotations;
        }

        if (options.AllowUnlistedVoice || SpeechmaticsVoices.IsKnown(options.Voice))
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            $"Voice '{options.Voice}' is not a Speechmatics voice. Known voices: "
            + $"{string.Join(", ", SpeechmaticsVoices.All)}. The service does not reject an "
            + "unknown voice — it returns 200 with audio in the fallback voice — so this is "
            + "checked here. Set AllowUnlistedVoice to accept it anyway.");
    }
}
