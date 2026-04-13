using Asterisk.Sdk.Push.Events;

namespace Asterisk.Sdk.Push.Bus;

/// <summary>
/// Publish/subscribe bus for in-process push events.
/// Implementations must be thread-safe and respect the configured
/// <see cref="BackpressureStrategy"/>.
/// </summary>
public interface IPushEventBus
{
    /// <summary>
    /// Publishes an event to all current subscribers. The publish task completes
    /// once the event has been enqueued; delivery to observers happens asynchronously.
    /// </summary>
    ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken ct = default)
        where TEvent : PushEvent;

    /// <summary>Returns a hot observable producing every event flowing through the bus.</summary>
    IObservable<PushEvent> AsObservable();

    /// <summary>Returns a hot observable filtered to events of the requested concrete type.</summary>
    IObservable<TEvent> OfType<TEvent>() where TEvent : PushEvent;
}
