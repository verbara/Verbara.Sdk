using System.Diagnostics;

namespace Asterisk.Sdk.VoiceAi.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible ActivitySource for distributed tracing of Voice AI operations.
/// Produces spans for pipeline sessions, STT transcriptions, and TTS syntheses.
/// Tag names match the draft in <c>docs/research/2026-04-19-otel-sip-semantic-conventions.md</c>
/// and the consumer-facing <c>Asterisk.Sdk.AsteriskSemanticConventions</c> catalog.
/// <para>
/// To enable tracing, register the source name with your OpenTelemetry tracer:
/// <c>builder.AddSource("Asterisk.Sdk.VoiceAi")</c>
/// </para>
/// </summary>
public static class VoiceAiActivitySource
{
    public static readonly ActivitySource Source = new("Asterisk.Sdk.VoiceAi", "1.0.0");

    internal static Activity? StartSession(Guid channelId, string handlerType)
    {
        var activity = Source.StartActivity("voiceai.session", ActivityKind.Server);
        if (activity is not null)
        {
            activity.SetTag("asterisk.channel.id", channelId.ToString());
            activity.SetTag("voiceai.handler", handlerType);
        }
        return activity;
    }

    internal static Activity? StartRecognition(string providerType)
    {
        var activity = Source.StartActivity("voiceai.stt.transcription", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("voiceai.provider", providerType);
            activity.SetTag("voiceai.operation", "stt");
        }
        return activity;
    }

    internal static Activity? StartSynthesis(string providerType, int characterCount)
    {
        var activity = Source.StartActivity("voiceai.tts.synthesis", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("voiceai.provider", providerType);
            activity.SetTag("voiceai.operation", "tts");
            activity.SetTag("tts.characters", characterCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return activity;
    }
}
