using Asterisk.Sdk.Push.Events;

namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// Decodes a raw NATS envelope back into a <see cref="RemotePushEvent"/> that the
/// bridge can reinject into the local Push bus. Mirror of <see cref="INatsPayloadSerializer"/>
/// for the subscribe side.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe and AOT-safe (no reflection). Register a custom
/// implementation via DI before calling <c>AddPushNats</c> when you need a wire shape
/// different from the one produced by <c>DefaultNatsPayloadSerializer</c>.
/// </remarks>
public interface INatsPayloadDeserializer
{
    /// <summary>
    /// Decode <paramref name="payload"/> received on <paramref name="subject"/> into a
    /// <see cref="RemotePushEvent"/>. Return <see langword="null"/> when the payload
    /// is structurally invalid (malformed JSON, missing <c>eventType</c>) — the bridge
    /// treats this as a decode failure and increments the corresponding metric.
    /// </summary>
    RemotePushEvent? Deserialize(string subject, ReadOnlySpan<byte> payload);
}
