namespace Asterisk.Sdk.Push.Events;

/// <summary>
/// Envelope metadata attached to every push event for routing and auditing.
/// </summary>
public sealed record PushEventMetadata(
    string TenantId,
    string? UserId,
    DateTimeOffset OccurredAt,
    string? CorrelationId);
