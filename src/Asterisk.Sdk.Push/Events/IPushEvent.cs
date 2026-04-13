namespace Asterisk.Sdk.Push.Events;

/// <summary>
/// Marker interface implemented by every push event. Enables AOT-friendly type
/// discrimination without runtime reflection over the inheritance hierarchy.
/// </summary>
public interface IPushEvent
{
    /// <summary>Discriminator for the event type, e.g. <c>"conversation.assigned"</c>.</summary>
    string EventType { get; }

    /// <summary>Cross-cutting metadata (tenant, target user, timestamps, correlation id).</summary>
    PushEventMetadata Metadata { get; }
}
