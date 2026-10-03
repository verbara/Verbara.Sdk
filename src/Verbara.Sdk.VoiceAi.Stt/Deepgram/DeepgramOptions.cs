using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Stt.Deepgram;

/// <summary>Configuration options for the Deepgram WebSocket STT provider.</summary>
public sealed class DeepgramOptions
{
    /// <summary>Deepgram API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// WebSocket endpoint to open, including the route. Defaults to Deepgram's hosted realtime
    /// endpoint; point it elsewhere for a self-hosted deployment.
    /// </summary>
    /// <remarks>
    /// The client used to hard-code this host, which left tests no way in except a constructor that
    /// replaced the whole URL and skipped the credential — so the route the client really asks for
    /// was exercised by nothing. Its sibling <c>DeepgramTtsOptions.BaseUri</c> has always carried the
    /// route this way.
    /// <para>
    /// Must start with <c>ws://</c> or <c>wss://</c>. <see cref="DeepgramOptionsValidator"/>, which the SDK's registration
    /// adds, enforces it: a host whose value does not match fails when the provider's options are first resolved.
    /// </para>
    /// </remarks>
    [RegularExpression(@"^wss?://.+", ErrorMessage = "BaseUri must start with wss:// or ws://.")]
    public string BaseUri { get; set; } = "wss://api.deepgram.com/v1/listen";

    /// <summary>Deepgram model name (default: nova-2).</summary>
    public string Model { get; set; } = "nova-2";

    /// <summary>Language code for recognition.</summary>
    public string Language { get; set; } = "es";

    /// <summary>Whether to receive interim (partial) results.</summary>
    public bool InterimResults { get; set; } = true;

    /// <summary>Whether to enable punctuation in transcripts.</summary>
    public bool Punctuate { get; set; } = true;

    /// <summary>
    /// How long, in whole seconds, the WebSocket connect (the TCP dial, TLS and the HTTP upgrade) may
    /// take before the stream fails with a <see cref="SpeechProviderFailureException"/> whose
    /// <see cref="SpeechProviderFailureException.Signal"/> is <see cref="SpeechProviderFailureSignal.Handshake"/>,
    /// the failure a refused upgrade also takes.
    /// Accepts whole seconds from 1 to 600: the options validator rejects any other value, and the client
    /// throws <see cref="ArgumentOutOfRangeException"/> naming this option from its constructor and before each dial.
    /// Defaults to 5, the default of every
    /// other WebSocket speech client in this SDK.
    /// </summary>
    [Range(1, 600)]
    public int ConnectTimeoutSeconds { get; set; } = 5;
}

/// <summary>
/// AOT-safe source-generated validator for <see cref="DeepgramOptions"/>: <c>ConnectTimeoutSeconds</c> within 1–600 and
/// <c>BaseUri</c> a <c>ws://</c> or <c>wss://</c> URI. Registered by the SDK's registration of the provider.
/// </summary>
[OptionsValidator]
public sealed partial class DeepgramOptionsValidator : IValidateOptions<DeepgramOptions> { }
