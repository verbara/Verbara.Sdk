using System.Text;
using Asterisk.Sdk.Push.Events;

namespace Asterisk.Sdk.Push.Webhooks;

/// <summary>
/// Default serializer that emits a minimal JSON envelope with the event's EventType + metadata.
/// AOT-safe: does not use <see cref="System.Text.Json.JsonSerializer"/> reflection APIs.
/// </summary>
internal sealed class DefaultWebhookPayloadSerializer : IWebhookPayloadSerializer
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
            sb.Append(",\"occurredAt\":").Append(JsonQuote(meta.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
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
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                        sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
