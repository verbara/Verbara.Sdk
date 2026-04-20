using System.ComponentModel.DataAnnotations;

namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// Configuration options for <see cref="NatsBridge"/>. The bridge subscribes to the
/// in-process Push bus and republishes every event to a NATS subject derived from the
/// event's <c>TopicPath</c>.
/// </summary>
public sealed class NatsBridgeOptions
{
    /// <summary>
    /// NATS server URL (must start with <c>nats://</c>). Default: <c>nats://127.0.0.1:4222</c>.
    /// </summary>
    [Required]
    public string Url { get; set; } = "nats://127.0.0.1:4222";

    /// <summary>Optional NATS username.</summary>
    public string? Username { get; set; }

    /// <summary>Optional NATS password.</summary>
    public string? Password { get; set; }

    /// <summary>Optional NATS auth token (mutually exclusive with username/password).</summary>
    public string? Token { get; set; }

    /// <summary>
    /// Prefix prepended to every NATS subject. Combined with the translated topic path
    /// (e.g. prefix <c>asterisk.sdk</c> + topic <c>push.channels.42</c> →
    /// <c>asterisk.sdk.push.channels.42</c>). Default: <c>asterisk.sdk</c>.
    /// </summary>
    [Required]
    public string SubjectPrefix { get; set; } = "asterisk.sdk";

    /// <summary>Connection timeout in seconds. Default: 10.</summary>
    [Range(1, 600)]
    public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Optional stable identifier for this process/node. When set, the serializer writes
    /// it into the envelope's <c>source</c> field so subscribers on other nodes can see
    /// where a message originated and — if they share the same prefix — drop messages
    /// they published themselves (<see cref="NatsSubscribeOptions.SkipSelfOriginated"/>).
    /// Backwards-compatible: when <see langword="null"/> the field is omitted entirely.
    /// </summary>
    public string? NodeId { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the bridge only publishes local events to NATS and does
    /// not subscribe to remote events. Retained for backwards compatibility: the
    /// authoritative control is now <see cref="Subscribe"/> — the bridge consumes from
    /// NATS iff <see cref="Subscribe"/> is non-<see langword="null"/>. This flag stays
    /// as a documentation signal for consumers upgrading from v1.12.
    /// </summary>
    public bool PublishOnly { get; set; } = true;

    /// <summary>
    /// Opt-in subscribe-side configuration. Default <see langword="null"/> keeps the
    /// bridge in the publish-only mode introduced in v1.12. When set, the bridge
    /// consumes the configured NATS subject filters and reinjects every decoded event
    /// into the local Push bus as an <see cref="Events.RemotePushEvent"/>.
    /// </summary>
    public NatsSubscribeOptions? Subscribe { get; set; }
}
