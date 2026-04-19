using System.Diagnostics;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible ActivitySource for distributed tracing of OpenAI Realtime sessions.
/// Produces spans for session lifecycle and function call dispatch. Tag names match the
/// draft in <c>docs/research/2026-04-19-otel-sip-semantic-conventions.md</c> and the
/// consumer-facing <c>Asterisk.Sdk.AsteriskSemanticConventions</c> catalog.
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
            activity.SetTag("asterisk.channel.id", channelId.ToString());
            activity.SetTag("voiceai.provider", "OpenAI");
            activity.SetTag("voiceai.operation", "realtime");
            activity.SetTag("voiceai.model", model);
        }

        return activity;
    }
}
