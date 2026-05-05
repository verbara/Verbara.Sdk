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
    /// HTTP POST fallback. Uses <c>https://api.lmnt.com/v1/ai/speech/generate</c>.
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
    /// Audio format. Supported values: <c>mp3</c>, <c>raw</c> (raw PCM), <c>wav</c>.
    /// Defaults to <c>raw</c> (raw PCM — telephony-friendly, zero container overhead).
    /// </summary>
    /// <remarks>
    /// When using WebSocket transport LMNT streams audio in the requested format as binary frames.
    /// For telephony pipelines prefer <c>raw</c> (16-bit signed PCM) to avoid MP3 decode overhead.
    /// </remarks>
    public string Format { get; set; } = "raw";

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
    /// Optional LMNT model identifier (e.g. <c>aurora</c>, <c>blizzard</c>).
    /// When <see langword="null"/> LMNT selects the default model for the requested voice.
    /// </summary>
    /// <remarks>
    /// Verify available model identifiers against the live LMNT API at integration test time.
    /// Use the LMNT account dashboard or <see href="https://docs.lmnt.com"/> to enumerate supported models.
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
