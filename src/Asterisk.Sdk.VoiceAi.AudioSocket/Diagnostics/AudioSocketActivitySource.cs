using System.Diagnostics;

namespace Asterisk.Sdk.VoiceAi.AudioSocket.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible ActivitySource for distributed tracing of AudioSocket operations.
/// Produces spans for AudioSocket session lifecycle.
/// <para>
/// To enable tracing, register the source name with your OpenTelemetry tracer:
/// <c>builder.AddSource("Asterisk.Sdk.VoiceAi.AudioSocket")</c>
/// </para>
/// </summary>
public static class AudioSocketActivitySource
{
    public static readonly ActivitySource Source = new("Asterisk.Sdk.VoiceAi.AudioSocket", "1.0.0");

    internal static Activity? StartSession(Guid channelId)
    {
        var activity = Source.StartActivity("audiosocket.session", ActivityKind.Server);
        activity?.SetTag("audiosocket.channel_id", channelId.ToString());
        return activity;
    }
}
