using System.Globalization;
using System.Net;
using System.Net.WebSockets;

namespace Verbara.Sdk.VoiceAi;

/// <summary>
/// How a provider failure reached this client. Carried on
/// <see cref="SpeechProviderFailureException"/> so a caller never has to infer the channel from the
/// shape of the exception.
/// </summary>
/// <remarks>
/// This is deliberately <em>not</em> a retryability discriminator (<c>ADR-0050</c> E4): a rate-limit
/// rejection is retryable and an invalid-credential rejection is not, and both arrive as
/// <see cref="ErrorFrame"/>. Policy reads <see cref="SpeechProviderFailureException.Code"/>.
/// </remarks>
public enum SpeechProviderFailureSignal
{
    /// <summary>
    /// An in-band message whose meaning is "this failed" — the vendor's own error frame. The door
    /// <c>ADR-0049</c> D1 names: a receive loop that keeps only content message types discards
    /// every one of these by construction.
    /// </summary>
    ErrorFrame,

    /// <summary>
    /// The WebSocket close code. A failure signal in its own right on measured surfaces — a
    /// Speechmatics session rejects a credential with <c>101</c> and then close <c>4001
    /// not_authorised</c> — and read nowhere in this SDK before <c>ADR-0050</c>.
    /// </summary>
    CloseCode,

    /// <summary>
    /// The vendor rejected the HTTP upgrade, so no session ever opened. Wrapped rather than left as
    /// a raw transport exception (<c>ADR-0050</c> E7) because <em>where</em> a vendor validates a
    /// credential is a property of the vendor, not of this client, and it can change with no line of
    /// this repository changing.
    /// </summary>
    Handshake,

    /// <summary>
    /// The connection died mid-session. Distinct from the others in that nothing was said: the
    /// evidence is the inner exception. Previously this ended the stream <em>normally</em>, which
    /// left a caller an empty — or silently truncated — result and no error.
    /// </summary>
    Transport
}

/// <summary>Base exception for failures raised by a speech provider surface (TTS or STT).</summary>
/// <remarks>
/// <para>
/// Rooted at <see cref="Exception"/> rather than at this SDK's <c>AsteriskException</c>, deliberately
/// and as recorded in <c>ADR-0050</c> E3: this package family does not reference the PBX layer, and a
/// rejected TTS credential is not an Asterisk error. It is the one place the SDK's otherwise uniform
/// exception rooting is not followed.
/// </para>
/// <para>
/// Thrown from the receive loop and surfaced at the caller's <c>MoveNextAsync</c> — see
/// <see cref="SpeechSynthesizer.SynthesizeAsync"/> and <see cref="SpeechRecognizer.StreamAsync"/>.
/// </para>
/// </remarks>
public class SpeechProviderException(string provider, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>
    /// The provider that failed — the value of <see cref="SpeechSynthesizer.ProviderName"/> or
    /// <see cref="SpeechRecognizer.ProviderName"/> for the client that raised this.
    /// </summary>
    public string Provider { get; } = provider;
}

/// <summary>
/// Thrown when a provider reported a failure: the vendor said something, and this carries what it
/// said.
/// </summary>
/// <remarks>
/// One type for all four channels in <see cref="SpeechProviderFailureSignal"/>, so a caller's
/// <c>catch</c> does not depend on which validation regime a vendor happens to use.
/// </remarks>
public class SpeechProviderFailureException(
    string provider,
    SpeechProviderFailureSignal signal,
    string? code,
    string message,
    Exception? innerException = null)
    : SpeechProviderException(provider, message, innerException)
{
    /// <summary>Which channel carried the failure.</summary>
    public SpeechProviderFailureSignal Signal { get; } = signal;

    /// <summary>
    /// The vendor's own code, verbatim and unparsed — an error code from a failure frame
    /// (<c>invalid_api_key</c>, <c>3007</c>), a WebSocket close code (<c>4001</c>), an HTTP status
    /// from a rejected upgrade (<c>401</c>) — or <see langword="null"/> when the vendor gave none,
    /// which is always the case for <see cref="SpeechProviderFailureSignal.Transport"/>.
    /// </summary>
    /// <remarks>
    /// A string because the four channels do not share a numbering scheme, and this SDK does not
    /// normalise across vendors. Retry policy reads this; it must not read
    /// <see cref="Signal"/> or the exception type.
    /// </remarks>
    public string? Code { get; } = code;

    /// <summary>
    /// The vendor's own failure frame: <paramref name="code"/> and <paramref name="vendorMessage"/>
    /// verbatim, whatever the frame called those members.
    /// </summary>
    /// <remarks>
    /// The eight WebSocket clients each recognise their own vendor's failure frame and then come
    /// here, so the exception message has one shape across all of them.
    /// </remarks>
    public static SpeechProviderFailureException FromErrorFrame(
        string provider,
        string? code,
        string? vendorMessage)
        => new(
            provider,
            SpeechProviderFailureSignal.ErrorFrame,
            code,
            code is null
                ? $"{provider} reported a failure: {vendorMessage ?? "(no message)"}"
                : $"{provider} reported a failure ({code}): {vendorMessage ?? "(no message)"}");

    /// <summary>
    /// The close-code rule, in one place: a failure exception for a close status that means failure,
    /// or <see langword="null"/> for one that means the session ended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A normal closure and a close carrying no code at all are not failures; every other code is,
    /// including the <c>1xxx</c> protocol codes and the <c>3xxx</c>/<c>4xxx</c> ranges vendors define
    /// for themselves (Speechmatics' <c>4001 not_authorised</c>, ElevenLabs' <c>1008</c>,
    /// AssemblyAI's <c>3007</c>, Cartesia STT's <c>1008 Missing sample_rate</c> — all measured).
    /// </para>
    /// <para>
    /// Shared rather than restated per client on purpose: this door was open at all eight of them,
    /// and eight copies of a rule is how two of them end up disagreeing about what <c>1001</c> means.
    /// </para>
    /// </remarks>
    public static SpeechProviderFailureException? FromCloseStatus(
        string provider,
        WebSocketCloseStatus? closeStatus,
        string? closeStatusDescription)
    {
        if (closeStatus is null or WebSocketCloseStatus.NormalClosure or WebSocketCloseStatus.Empty)
            return null;

        var code = ((int)closeStatus.Value).ToString(CultureInfo.InvariantCulture);
        var reason = string.IsNullOrEmpty(closeStatusDescription)
            ? "no reason given"
            : closeStatusDescription;

        return new SpeechProviderFailureException(
            provider,
            SpeechProviderFailureSignal.CloseCode,
            code,
            $"{provider} closed the session with code {code}: {reason}");
    }

    /// <summary>
    /// A rejected — or otherwise failed — connection upgrade, wrapped so the caller catches the same
    /// type whether the vendor validates in the handshake or in band (<c>ADR-0050</c> E7).
    /// </summary>
    /// <param name="provider">The provider name.</param>
    /// <param name="httpStatusCode">
    /// The upgrade response status, available when the client set
    /// <see cref="ClientWebSocketOptions.CollectHttpResponseDetails"/>. Zero or
    /// <see langword="null"/> means the upgrade failed without an HTTP answer — a refused
    /// connection, a name that does not resolve, a TLS failure — and no code is reported rather than
    /// a made-up one.
    /// </param>
    /// <param name="innerException">The transport exception this wraps; never discarded.</param>
    public static SpeechProviderFailureException FromHandshake(
        string provider,
        HttpStatusCode? httpStatusCode,
        Exception innerException)
    {
        var code = httpStatusCode is null || (int)httpStatusCode.Value == 0
            ? null
            : ((int)httpStatusCode.Value).ToString(CultureInfo.InvariantCulture);

        return new SpeechProviderFailureException(
            provider,
            SpeechProviderFailureSignal.Handshake,
            code,
            code is null
                ? $"{provider}: the connection upgrade failed and no session opened."
                : $"{provider} rejected the connection upgrade with HTTP {code}; no session opened.",
            innerException);
    }

    /// <summary>
    /// A connection that died mid-session. The result is empty or truncated, and the third door
    /// <c>ADR-0050</c> E2 closes: this used to end the stream as though it had completed normally.
    /// </summary>
    public static SpeechProviderFailureException FromTransport(string provider, Exception innerException)
        => new(
            provider,
            SpeechProviderFailureSignal.Transport,
            null,
            $"{provider}: the connection failed mid-session, so the result is incomplete.",
            innerException);
}

/// <summary>
/// Thrown when a provider session ended cleanly having produced nothing, and said nothing about why.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="SpeechProviderFailureException"/>: no code, no vendor message,
/// because there was none. Operationally these are different events — the other type is an alert
/// with a cause attached, this one is the trigger to probe that surface against the vendor
/// (<c>ADR-0048</c>).
/// </para>
/// <para>
/// What counts as "nothing" is per surface (<c>ADR-0050</c> E5) and not the same test on both:
/// synthesis fails on zero audio, recognition fails only when <em>no vendor frame arrived at all</em>.
/// A recognition session that received lifecycle frames and produced zero transcripts completes
/// normally — voice activity detection flushes on any turn trigger, so noise with no speech is a
/// healthy zero-transcript session and must not be reported as an error.
/// </para>
/// <para>
/// Caller cancellation never produces this (<c>ADR-0050</c> E6); it surfaces as
/// <see cref="OperationCanceledException"/>, as it always has.
/// </para>
/// </remarks>
public class SpeechProviderEmptyResultException(string provider, string message)
    : SpeechProviderException(provider, message);
