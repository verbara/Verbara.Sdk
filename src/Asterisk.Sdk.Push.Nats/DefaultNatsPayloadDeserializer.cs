using System.Globalization;
using System.Text.Json;

using Asterisk.Sdk.Push.Events;

namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// AOT-safe default deserializer mirroring <see cref="DefaultNatsPayloadSerializer"/>.
/// Parses the envelope with <see cref="Utf8JsonReader"/> — no reflection, no
/// <c>JsonSerializer.Deserialize</c> — so it survives trimming without a source-gen
/// context.
/// </summary>
/// <remarks>
/// Recognized envelope shape:
/// <code>
/// {
///   "source": "nodeId",          // optional
///   "eventType": "calls.inbound.started",
///   "metadata": {                // optional
///     "tenantId": "...",
///     "userId": "...",
///     "occurredAt": "2026-...",
///     "correlationId": "...",
///     "topicPath": "calls/42",
///     "traceContext": "00-..."
///   }
/// }
/// </code>
/// Unknown top-level or metadata properties are skipped silently — forward-compatible
/// with future envelope extensions.
/// </remarks>
internal sealed class DefaultNatsPayloadDeserializer : INatsPayloadDeserializer
{
    public RemotePushEvent? Deserialize(string subject, ReadOnlySpan<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        if (payload.IsEmpty)
            return null;

        string? source = null;
        string? eventType = null;
        PushEventMetadata? metadata = null;

        try
        {
            var reader = new Utf8JsonReader(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    return null;

                var propertyName = reader.GetString();
                if (!reader.Read())
                    return null;

                switch (propertyName)
                {
                    case "source":
                        source = ReadOptionalString(ref reader);
                        break;
                    case "eventType":
                        if (reader.TokenType != JsonTokenType.String)
                            return null;
                        eventType = reader.GetString();
                        break;
                    case "metadata":
                        metadata = ReadMetadata(ref reader);
                        if (metadata is null)
                            return null;
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(eventType))
            return null;

        var rawPayload = payload.ToArray();
        var remote = new RemotePushEvent(eventType, source, rawPayload);
        if (metadata is not null)
        {
            remote = remote with { Metadata = metadata };
        }
        return remote;
    }

    private static string? ReadOptionalString(ref Utf8JsonReader reader)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            _ => null,
        };
    }

    private static PushEventMetadata? ReadMetadata(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType != JsonTokenType.StartObject)
            return null;

        string? tenantId = null;
        string? userId = null;
        DateTimeOffset occurredAt = default;
        var occurredAtSeen = false;
        string? correlationId = null;
        string? topicPath = null;
        string? traceContext = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                return null;
            var propertyName = reader.GetString();
            if (!reader.Read())
                return null;

            switch (propertyName)
            {
                case "tenantId":
                    if (reader.TokenType != JsonTokenType.String) return null;
                    tenantId = reader.GetString();
                    break;
                case "userId":
                    userId = ReadOptionalString(ref reader);
                    break;
                case "occurredAt":
                    if (reader.TokenType != JsonTokenType.String) return null;
                    var s = reader.GetString();
                    if (s is null) return null;
                    if (!DateTimeOffset.TryParse(
                            s,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out occurredAt))
                    {
                        return null;
                    }
                    occurredAtSeen = true;
                    break;
                case "correlationId":
                    correlationId = ReadOptionalString(ref reader);
                    break;
                case "topicPath":
                    topicPath = ReadOptionalString(ref reader);
                    break;
                case "traceContext":
                    traceContext = ReadOptionalString(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (tenantId is null || !occurredAtSeen)
            return null;

        return new PushEventMetadata(
            TenantId: tenantId,
            UserId: userId,
            OccurredAt: occurredAt,
            CorrelationId: correlationId,
            TopicPath: topicPath,
            TraceContext: traceContext);
    }
}
