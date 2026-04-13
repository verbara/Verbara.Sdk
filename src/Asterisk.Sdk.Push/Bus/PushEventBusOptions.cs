namespace Asterisk.Sdk.Push.Bus;

/// <summary>
/// Configuration options for <see cref="RxPushEventBus"/>. Validated at host startup
/// via <c>IValidateOptions&lt;PushEventBusOptions&gt;</c> (registered by <c>AddAsteriskPush</c>).
/// </summary>
public sealed class PushEventBusOptions
{
    /// <summary>Maximum buffered events before backpressure kicks in. Must be &gt;= 1.</summary>
    public int BufferCapacity { get; set; } = 256;

    /// <summary>Strategy applied when the bounded buffer is full.</summary>
    public BackpressureStrategy BackpressureStrategy { get; set; } = BackpressureStrategy.DropOldest;
}
