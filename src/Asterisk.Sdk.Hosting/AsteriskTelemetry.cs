namespace Asterisk.Sdk.Hosting;

/// <summary>
/// Provides source and meter names for all Asterisk SDK telemetry signals.
/// Use with OpenTelemetry: <c>builder.AddSource(AsteriskTelemetry.ActivitySourceNames)</c>.
/// </summary>
public static class AsteriskTelemetry
{
    /// <summary>All ActivitySource names registered by Asterisk SDK packages.</summary>
    public static readonly string[] ActivitySourceNames =
    [
        "Asterisk.Sdk.Ami",
        "Asterisk.Sdk.Ari",
        "Asterisk.Sdk.Agi",
        "Asterisk.Sdk.Live",
        "Asterisk.Sdk.Sessions",
        "Asterisk.Sdk.Push",
        "Asterisk.Sdk.VoiceAi",
        "Asterisk.Sdk.VoiceAi.AudioSocket",
        "Asterisk.Sdk.VoiceAi.OpenAiRealtime"
    ];

    /// <summary>All Meter names registered by Asterisk SDK packages.</summary>
    public static readonly string[] MeterNames =
    [
        "Asterisk.Sdk.Ami",
        "Asterisk.Sdk.Ari",
        "Asterisk.Sdk.Ari.Audio",
        "Asterisk.Sdk.Agi",
        "Asterisk.Sdk.Live",
        "Asterisk.Sdk.Sessions",
        "Asterisk.Sdk.Push",
        "Asterisk.Sdk.VoiceAi",
        "Asterisk.Sdk.VoiceAi.Stt",
        "Asterisk.Sdk.VoiceAi.Tts",
        "Asterisk.Sdk.VoiceAi.AudioSocket",
        "Asterisk.Sdk.VoiceAi.OpenAiRealtime"
    ];
}
