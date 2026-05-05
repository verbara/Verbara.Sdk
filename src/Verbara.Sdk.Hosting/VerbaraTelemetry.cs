namespace Verbara.Sdk.Hosting;

/// <summary>
/// Provides source and meter names for all Asterisk SDK telemetry signals.
/// Use with OpenTelemetry: <c>builder.AddSource(VerbaraTelemetry.ActivitySourceNames)</c>.
/// </summary>
public static class VerbaraTelemetry
{
    /// <summary>All ActivitySource names registered by Asterisk SDK packages.</summary>
    public static readonly string[] ActivitySourceNames =
    [
        "Verbara.Sdk.Ami",
        "Verbara.Sdk.Ari",
        "Verbara.Sdk.Agi",
        "Verbara.Sdk.Live",
        "Verbara.Sdk.Sessions",
        "Verbara.Sdk.Push",
        "Verbara.Sdk.VoiceAi",
        "Verbara.Sdk.VoiceAi.AudioSocket",
        "Verbara.Sdk.VoiceAi.OpenAiRealtime"
    ];

    /// <summary>All Meter names registered by Asterisk SDK packages.</summary>
    public static readonly string[] MeterNames =
    [
        "Verbara.Sdk.Ami",
        "Verbara.Sdk.Ari",
        "Verbara.Sdk.Ari.Audio",
        "Verbara.Sdk.Agi",
        "Verbara.Sdk.Live",
        "Verbara.Sdk.Sessions",
        "Verbara.Sdk.Push",
        "Verbara.Sdk.Push.Webhooks",
        "Verbara.Sdk.Push.Nats",
        "Verbara.Sdk.Resilience",
        "Verbara.Sdk.VoiceAi",
        "Verbara.Sdk.VoiceAi.Stt",
        "Verbara.Sdk.VoiceAi.Tts",
        "Verbara.Sdk.VoiceAi.AudioSocket",
        "Verbara.Sdk.VoiceAi.OpenAiRealtime"
    ];
}
