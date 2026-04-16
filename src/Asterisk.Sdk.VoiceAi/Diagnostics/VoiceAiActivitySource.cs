using System.Diagnostics;

namespace Asterisk.Sdk.VoiceAi.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible ActivitySource for distributed tracing of Voice AI operations.
/// Produces spans for pipeline sessions, STT transcriptions, and TTS syntheses.
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
            activity.SetTag("voiceai.channel_id", channelId.ToString());
            activity.SetTag("voiceai.handler", handlerType);
        }
        return activity;
    }

    internal static Activity? StartRecognition(string providerType)
    {
        var activity = Source.StartActivity("voiceai.stt.transcription", ActivityKind.Client);
        activity?.SetTag("stt.provider", providerType);
        return activity;
    }

    internal static Activity? StartSynthesis(string providerType, int characterCount)
    {
        var activity = Source.StartActivity("voiceai.tts.synthesis", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("tts.provider", providerType);
            activity.SetTag("tts.characters", characterCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return activity;
    }
}
