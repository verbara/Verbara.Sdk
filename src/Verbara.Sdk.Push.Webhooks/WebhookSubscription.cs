using Verbara.Sdk.Push.Topics;

namespace Verbara.Sdk.Push.Webhooks;

/// <summary>
/// An outbound webhook subscription: pushes events matching <see cref="TopicPattern"/>
/// to <see cref="TargetUrl"/> via HTTP POST with an HMAC-SHA256 signature.
/// </summary>
public sealed record WebhookSubscription
{
    /// <summary>Stable identifier for the subscription. Used in logs and telemetry.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Topic pattern to match against <see cref="Events.PushEventMetadata.TopicPath"/>, or against
    /// <see cref="Events.PushEvent.EventType"/> when the topic path is null or empty (an event type that is not a
    /// valid topic is received only by the pattern <c>**</c>).
    /// Supports <c>*</c> (single-level), <c>**</c> (multi-level), and <c>{self}</c> placeholders; <c>{self}</c>
    /// resolves to <see cref="Events.PushEventMetadata.UserId"/>.
    /// </summary>
    public required TopicPattern TopicPattern { get; init; }

    /// <summary>Target HTTP URL. Must be absolute. Typically HTTPS in production.</summary>
    public required Uri TargetUrl { get; init; }

    /// <summary>
    /// Shared secret used to compute the HMAC-SHA256 signature sent in the
    /// <c>X-Signature</c> header as <c>sha256=&lt;hex&gt;</c>. Rotate by creating a new
    /// subscription and deleting the old one.
    /// </summary>
    public string? Secret { get; init; }

    /// <summary>Optional additional HTTP headers sent with every delivery.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Override the default retry cap for this subscription. When null the
    /// <see cref="WebhookDeliveryOptions.MaxRetries"/> value is used.
    /// </summary>
    public int? MaxRetries { get; init; }
}
