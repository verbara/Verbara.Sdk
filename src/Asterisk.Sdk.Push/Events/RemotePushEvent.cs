namespace Asterisk.Sdk.Push.Events;

/// <summary>
/// Envelope that wraps a push event received from a remote node (e.g. via the NATS
/// bridge subscribe side) and reinjected into the local Push bus.
/// </summary>
/// <remarks>
/// Concrete <see cref="PushEvent"/> subtypes live in consumer assemblies and cannot be
/// enumerated without reflection, so cross-node bridges do not attempt to reconstruct
/// the original subtype. Instead, the deserializer materializes a
/// <see cref="RemotePushEvent"/> that preserves the original discriminator
/// (<see cref="OriginalEventType"/>), the source node identifier for traceability
/// (<see cref="SourceNodeId"/>), and the raw wire bytes (<see cref="RawPayload"/>) for
/// consumers that want to decode the payload with their own JSON context. Topic-based
/// filters and <c>OfType&lt;RemotePushEvent&gt;()</c> subscriptions on the bus
/// continue to function normally.
/// </remarks>
/// <param name="OriginalEventType">The <c>eventType</c> discriminator sent by the origin node.</param>
/// <param name="SourceNodeId">The origin node id carried in the envelope, or <see langword="null"/> if absent.</param>
/// <param name="RawPayload">The raw JSON bytes exactly as received from the transport.</param>
public sealed record RemotePushEvent(
    string OriginalEventType,
    string? SourceNodeId,
    byte[] RawPayload) : PushEvent
{
    /// <inheritdoc />
    public override string EventType => OriginalEventType;
}
