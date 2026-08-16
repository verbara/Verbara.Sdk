using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Tts.Lmnt;

/// <summary>
/// Transport protocol for the LMNT TTS provider.
/// </summary>
public enum LmntTransport
{
    /// <summary>
    /// WebSocket streaming (preferred). Achieves sub-200 ms TTFA via the
    /// <c>wss://api.lmnt.com/v1/ai/speech/stream</c> endpoint.
    /// Auth is sent as <c>X-API-Key</c> inside the first JSON message body
    /// (not in the HTTP upgrade headers).
    /// </summary>
    WebSocket = 0,

    /// <summary>
    /// HTTP POST fallback. Uses <c>https://api.lmnt.com/v1/ai/speech/bytes</c>.
    /// Higher latency than WebSocket; use when outbound WS is blocked on the host network.
    /// </summary>
    Http = 1,
}

/// <summary>Configuration options for the LMNT TTS provider.</summary>
public sealed class LmntTtsOptions
{
    /// <summary>LMNT API key (required). Sent in the WS init message or as the <c>X-API-Key</c> HTTP header.</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Voice identifier. Defaults to <see cref="LmntVoices.Leah"/> (<c>leah</c>),
    /// a low-latency English female voice recommended for telephony.
    /// See <see cref="LmntVoices"/> for the curated catalog.
    /// </summary>
    public string Voice { get; set; } = LmntVoices.Leah;

    /// <summary>
    /// Audio format. Defaults to <c>pcm_s16le</c> — headerless 16-bit signed little-endian PCM,
    /// the only value measured to satisfy this SDK's <see cref="Verbara.Sdk.Audio.AudioFormat"/> contract on
    /// <em>both</em> transports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The synthesizer streams the provider's bytes through unchanged, so the configured format
    /// must already be the PCM the caller asked for. Only <c>pcm_s16le</c> is that on every
    /// transport; every other value needs decoding the SDK does not do.
    /// </para>
    /// <para>
    /// <strong>Do not assume <c>raw</c> means raw PCM.</strong> Measured against the live API on
    /// 2026-08-15, <c>raw</c> is transport-dependent: on the WebSocket stream it is 16-bit PCM, but
    /// on <c>POST /v1/ai/speech/bytes</c> it is an MP3 frame stream (MPEG-2 Layer III, 96 kbps) served
    /// under a <c>Content-Type: application/vnd.lmnt.audio-fp32</c> header that describes neither.
    /// The previous default was <c>raw</c>, so HTTP callers were handed MP3 bytes labelled as
    /// <c>Slin16</c>. Values the API accepted on that date: <c>aac</c>, <c>mp3</c>, <c>raw</c>,
    /// <c>wav</c>, <c>ulaw</c>, <c>webm</c>, <c>pcm_s16le</c> — note <c>ulaw</c> arrives wrapped in a
    /// RIFF/WAV container, not as bare G.711.
    /// </para>
    /// </remarks>
    public string Format { get; set; } = "pcm_s16le";

    /// <summary>
    /// Output sample rate in Hz. Supported values: 8000, 16000 (default), 24000.
    /// 16000 Hz is the standard for wideband telephony (G.711 wideband / Opus).
    /// </summary>
    public int SampleRate { get; set; } = 16000;

    /// <summary>
    /// Speech speed multiplier. Range: 0.25 – 2.0. Defaults to 1.0 (natural speed).
    /// </summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>
    /// Transport protocol. Defaults to <see cref="LmntTransport.WebSocket"/> for sub-200 ms TTFA.
    /// Set to <see cref="LmntTransport.Http"/> when outbound WebSocket connections are blocked.
    /// </summary>
    public LmntTransport Transport { get; set; } = LmntTransport.WebSocket;

    /// <summary>
    /// Optional LMNT model identifier. When <see langword="null"/> the field is omitted from the
    /// request entirely and LMNT selects the default model for the requested voice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Identifiers the API enumerated on 2026-08-15 when rejecting an invalid one: <c>aurora</c>,
    /// <c>blizzard</c>, <c>blizzard-2.0</c>, <c>blizzard-2.1</c>, <c>blizzard-dialogue</c>. That set
    /// is the vendor's, not this SDK's — treat it as a snapshot and re-check against
    /// <see href="https://docs.lmnt.com"/> rather than as a validated contract.
    /// </para>
    /// <para>
    /// The field must be <em>absent</em> rather than explicitly null: the WebSocket endpoint rejects
    /// <c>"model": null</c> outright, closing with <c>1002 protocol error</c> and zero audio frames.
    /// <c>LmntInitMessage.Model</c> therefore carries
    /// <c>[JsonIgnore(Condition = WhenWritingNull)]</c>; removing it silently breaks every default
    /// configuration.
    /// </para>
    /// </remarks>
    public string? Model { get; set; }

    /// <summary>
    /// Language code (e.g. <c>en</c>, <c>es</c>). Defaults to <c>en</c>.
    /// Sent as the <c>language</c> field in WS messages and as a body field in HTTP requests.
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// LMNT API version. Defaults to <c>1.0</c>. Sent as the <c>lmnt-version</c> header
    /// in HTTP requests; not applicable for WebSocket transport.
    /// </summary>
    public string ApiVersion { get; set; } = "1.0";

    /// <summary>WebSocket connect timeout in seconds. Only used when <see cref="Transport"/> is <see cref="LmntTransport.WebSocket"/>.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>HTTP request timeout in seconds. Only used when <see cref="Transport"/> is <see cref="LmntTransport.Http"/>.</summary>
    public int HttpTimeoutSeconds { get; set; } = 30;
}

/// <summary>AOT-safe source-generated validator for <see cref="LmntTtsOptions"/>.</summary>
[OptionsValidator]
public sealed partial class LmntTtsOptionsValidator : IValidateOptions<LmntTtsOptions> { }
