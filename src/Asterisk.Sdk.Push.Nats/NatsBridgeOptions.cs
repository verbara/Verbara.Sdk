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
    /// When <see langword="true"/>, the bridge only publishes local events to NATS and does
    /// not subscribe to remote events. Default: <see langword="true"/>. Remote subscription
    /// is a planned future feature tracked in the v1.12.0 roadmap.
    /// </summary>
    public bool PublishOnly { get; set; } = true;
}
