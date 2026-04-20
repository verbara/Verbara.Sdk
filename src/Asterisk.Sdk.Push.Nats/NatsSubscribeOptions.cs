namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// Configuration for the NATS bridge subscribe side. When
/// <see cref="NatsBridgeOptions.Subscribe"/> is non-<see langword="null"/> the bridge
/// additionally subscribes to the configured NATS subject filters and re-publishes
/// every decoded message to the local Push bus as a
/// <see cref="Events.RemotePushEvent"/>.
/// </summary>
/// <remarks>
/// Opt-in by design — default <c>null</c> preserves the v1.12 publish-only behavior.
/// Loop prevention relies on <see cref="NatsBridgeOptions.NodeId"/> being populated
/// on both publisher and subscriber: the serializer writes the node id into the
/// envelope's <c>source</c> field; the subscriber compares and drops messages whose
/// source equals the local node id when <see cref="SkipSelfOriginated"/> is
/// <see langword="true"/>.
/// </remarks>
public sealed class NatsSubscribeOptions
{
    /// <summary>
    /// NATS subject filters to subscribe to. Standard NATS wildcards apply
    /// (<c>*</c> matches one token, <c>&gt;</c> matches the tail). When empty, the
    /// bridge derives a default filter of <c>{SubjectPrefix}.&gt;</c> so every event
    /// published by any node sharing the prefix is consumed.
    /// </summary>
    public string[] SubjectFilters { get; set; } = [];

    /// <summary>
    /// Optional NATS queue group name. When <see langword="null"/> (default) every
    /// subscriber receives every message (fan-out pub/sub). When set, NATS distributes
    /// each message to exactly one consumer in the group — work-queue semantics
    /// suitable for horizontally scaling a dispatcher tier.
    /// </summary>
    public string? QueueGroup { get; set; }

    /// <summary>
    /// When <see langword="true"/> (default), messages whose envelope <c>source</c>
    /// field matches <see cref="NatsBridgeOptions.NodeId"/> are dropped before being
    /// republished to the local bus. Prevents infinite loops when the same node both
    /// publishes to and subscribes from a shared subject prefix. Requires
    /// <see cref="NatsBridgeOptions.NodeId"/> to be set on both sides.
    /// </summary>
    public bool SkipSelfOriginated { get; set; } = true;
}
