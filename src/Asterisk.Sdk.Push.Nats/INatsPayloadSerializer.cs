using Asterisk.Sdk.Push.Events;

namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// Serializes a <see cref="PushEvent"/> into the UTF-8 byte payload published to NATS.
/// Override to customize the envelope shape (e.g. switch to MessagePack or add routing
/// headers) and register as a singleton before calling <c>AddPushNats</c>.
/// </summary>
public interface INatsPayloadSerializer
{
    /// <summary>Serialize the event. Implementations must be thread-safe.</summary>
    byte[] Serialize(PushEvent evt);
}
