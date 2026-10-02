namespace Verbara.Sdk.Push.AspNetCore;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Verbara.Sdk.Push.Events;

/// <summary>Builds whole SSE frames as the UTF-8 bytes written on the wire.</summary>
internal static class SseFrameFormat
{
    /// <summary>The event name reserved for the stream's own gap marker.</summary>
    internal const string GapEventName = ".gap";

    /// <summary>What an event whose name would be the reserved <see cref="GapEventName"/> is written as.</summary>
    internal const string EscapedGapEventName = "%2Egap";

    /// <summary>The heartbeat frame, an SSE comment.</summary>
    internal static readonly byte[] Heartbeat = Encoding.UTF8.GetBytes(": heartbeat\n\n");

    /// <summary>
    /// The event frame: one <c>event:</c> line (the topic path, or the event type when the topic path is null or
    /// empty — the name the stream matched the event by) and one <c>data:</c> line
    /// of JSON. CR and LF in the name are percent-encoded, and the reserved gap-marker name is escaped.
    /// </summary>
    internal static byte[] Event(PushEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var topicPath = evt.Metadata?.TopicPath;
        var name = EventName(string.IsNullOrEmpty(topicPath) ? evt.EventType : topicPath);
        var data = JsonSerializer.Serialize(evt, SseJsonContext.Default.PushEvent);
        return Encoding.UTF8.GetBytes("event: " + name + "\ndata: " + data + "\n\n");
    }

    /// <summary>The gap marker: <c>event: .gap</c> with <c>{"dropped":N}</c>.</summary>
    internal static byte[] Gap(long dropped) =>
        Encoding.UTF8.GetBytes("event: " + GapEventName + "\ndata: {\"dropped\":" + dropped.ToString(CultureInfo.InvariantCulture) + "}\n\n");

    /// <summary>The <c>event:</c> value for a name: never a line break, never the reserved gap-marker name.</summary>
    internal static string EventName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var encoded = name.Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);
        return string.Equals(encoded, GapEventName, StringComparison.Ordinal) ? EscapedGapEventName : encoded;
    }
}
