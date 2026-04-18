namespace Asterisk.Sdk.Push.Events;

/// <summary>
/// Envelope metadata attached to every push event for routing and auditing.
/// </summary>
/// <param name="TenantId">The tenant owning the event.</param>
/// <param name="UserId">Optional user identifier the event pertains to.</param>
/// <param name="OccurredAt">When the event originated.</param>
/// <param name="CorrelationId">Optional business correlation identifier.</param>
/// <param name="TopicPath">Optional resolved topic path (e.g. <c>calls/42</c>).</param>
/// <param name="TraceContext">
/// Optional W3C traceparent (<c>00-{trace-id}-{span-id}-{flags}</c>) for cross-boundary
/// distributed tracing. When present, transports that cross process/network boundaries
/// (e.g. SSE endpoints, Pro.Push backplane) inject it into the wire envelope so
/// downstream subscribers can continue the publisher's trace. Null when no trace context
/// is active. Added in v1.10.1; older consumers safely ignore the unknown field.
/// </param>
public sealed record PushEventMetadata(
    string TenantId,
    string? UserId,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    string? TopicPath = null,
    string? TraceContext = null);
