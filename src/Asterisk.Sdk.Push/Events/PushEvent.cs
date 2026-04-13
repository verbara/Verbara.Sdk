namespace Asterisk.Sdk.Push.Events;

/// <summary>
/// Base type for all push events flowing through <see cref="Bus.IPushEventBus"/>.
/// Concrete events derive from this record and override <see cref="EventType"/>.
/// </summary>
public abstract record PushEvent : IPushEvent
{
    /// <summary>Cross-cutting metadata (tenant, target user, timestamps, correlation id).
    /// Concrete events either set this via object initializer or override the getter
    /// with a computed value derived from their own fields.</summary>
    public virtual PushEventMetadata Metadata { get; init; } = null!;

    /// <summary>Discriminator for the event type, e.g. <c>"conversation.assigned"</c>.</summary>
    public abstract string EventType { get; }
}
