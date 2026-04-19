using System.Globalization;
using System.Text;

using Asterisk.Sdk.Push.Events;

namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// AOT-safe default payload serializer. Writes a minimal JSON envelope with EventType
/// + metadata using <see cref="StringBuilder"/> (no <see cref="System.Text.Json.JsonSerializer"/>
/// reflection). Mirrors <c>DefaultWebhookPayloadSerializer</c> so both bridges emit the
/// same wire shape — downstream consumers can treat them interchangeably.
/// </summary>
internal sealed class DefaultNatsPayloadSerializer : INatsPayloadSerializer
{
    public byte[] Serialize(PushEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var sb = new StringBuilder(capacity: 256);
        sb.Append("{\"eventType\":").Append(JsonQuote(evt.EventType));

        var meta = evt.Metadata;
        if (meta is not null)
        {
            sb.Append(",\"metadata\":{");
            sb.Append("\"tenantId\":").Append(JsonQuote(meta.TenantId));
            if (meta.UserId is not null)
                sb.Append(",\"userId\":").Append(JsonQuote(meta.UserId));
            sb.Append(",\"occurredAt\":").Append(JsonQuote(meta.OccurredAt.ToString("O", CultureInfo.InvariantCulture)));
            if (meta.CorrelationId is not null)
                sb.Append(",\"correlationId\":").Append(JsonQuote(meta.CorrelationId));
            if (meta.TopicPath is not null)
                sb.Append(",\"topicPath\":").Append(JsonQuote(meta.TopicPath));
            if (meta.TraceContext is not null)
                sb.Append(",\"traceContext\":").Append(JsonQuote(meta.TraceContext));
            sb.Append('}');
        }

        sb.Append('}');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string JsonQuote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
