using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Stt.AssemblyAi;

/// <summary>
/// Configuration options for the AssemblyAI Universal Streaming WebSocket STT provider.
/// </summary>
/// <remarks>
/// The official AssemblyAI .NET SDK was discontinued in April 2025
/// (<see href="https://www.assemblyai.com/blog/announcing-dotnet-sdk"/>).
/// This provider is a hand-rolled, AOT-clean implementation per ADR-0014.
/// </remarks>
public sealed class AssemblyAiOptions
{
    /// <summary>AssemblyAI API key (required). Sent via <c>Authorization</c> header (no "Bearer " prefix).</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>WebSocket base URI. Must begin with <c>wss://</c> or <c>ws://</c>.</summary>
    [Required]
    [RegularExpression(@"^wss?://.+", ErrorMessage = "BaseUri must start with wss:// or ws://.")]
    public string BaseUri { get; set; } = "wss://streaming.assemblyai.com/v3/ws";

    /// <summary>
    /// Fallback audio sample rate in Hz, declared to the service only when the caller's
    /// <see cref="Verbara.Sdk.Audio.AudioFormat"/> carries none. A format that states a rate wins.
    /// </summary>
    /// <remarks>
    /// The default is the rate AssemblyAI documents for Universal Streaming, but the previous summary
    /// here — "expects 16000" — was stronger than the evidence: a session declaring 8000 over 8 kHz
    /// audio recovered all ten digits of a ten-digit utterance (measured 2026-08-17, §3.18). Telephony
    /// callers should let their format speak rather than set this, since the service also derives each
    /// audio message's duration from whatever is declared here.
    /// </remarks>
    public int SampleRate { get; set; } = 16000;

    /// <summary>
    /// When <c>1</c> the server emits full, formatted turns only. When <c>0</c>, only partials
    /// are emitted. Propagated verbatim as the <c>format_turns</c> query parameter.
    /// </summary>
    public int FormatTurns { get; set; } = 1;

    /// <summary>
    /// Silence threshold (milliseconds) the server uses to decide an end-of-turn.
    /// Propagated verbatim as the <c>end_of_turn_confidence_threshold</c> query parameter.
    /// </summary>
    public int EndOfTurnConfidenceThreshold { get; set; } = 800;

    /// <summary>WebSocket connect timeout in seconds.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;
}

/// <summary>AOT-safe source-generated validator for <see cref="AssemblyAiOptions"/>.</summary>
[OptionsValidator]
public sealed partial class AssemblyAiOptionsValidator : IValidateOptions<AssemblyAiOptions> { }
