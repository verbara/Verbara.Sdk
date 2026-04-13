namespace Asterisk.Sdk.Push.Bus;

/// <summary>
/// Strategy applied when a subscriber's buffer reaches capacity.
/// </summary>
public enum BackpressureStrategy
{
    /// <summary>Drop the oldest buffered event to make room for the new one.</summary>
    DropOldest,

    /// <summary>Drop the newest event (do not enqueue) to preserve existing buffer.</summary>
    DropNewest,

    /// <summary>Block the publisher until buffer space is available.</summary>
    Block,
}
