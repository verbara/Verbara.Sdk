namespace Verbara.Sdk.Push.Bus;

/// <summary>
/// Strategy applied when a subscriber's buffer reaches capacity.
/// </summary>
public enum BackpressureStrategy
{
    /// <summary>Drop the oldest buffered event to make room for the new one.</summary>
    DropOldest,

    /// <summary>Evict the most recently buffered event to admit the new one (the BCL <c>BoundedChannelFullMode.DropNewest</c>); the oldest buffered events survive and the published event is always enqueued.</summary>
    DropNewest,

    /// <summary>Block the publisher until buffer space is available.</summary>
    Block,
}
