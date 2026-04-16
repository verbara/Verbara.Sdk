using System.Diagnostics;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible ActivitySource for distributed tracing of OpenAI Realtime sessions.
/// Produces spans for session lifecycle and function call dispatch.
/// <para>
/// To enable tracing, register the source name with your OpenTelemetry tracer:
/// <c>builder.AddSource("Asterisk.Sdk.VoiceAi.OpenAiRealtime")</c>
/// </para>
/// </summary>
public static class RealtimeActivitySource
{
    public static readonly ActivitySource Source = new("Asterisk.Sdk.VoiceAi.OpenAiRealtime", "1.0.0");

    internal static Activity? StartSession(Guid channelId, string model)
    {
        var activity = Source.StartActivity("openai_realtime.session", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("openai_realtime.channel_id", channelId.ToString());
            activity.SetTag("openai_realtime.model", model);
        }

        return activity;
    }
}
