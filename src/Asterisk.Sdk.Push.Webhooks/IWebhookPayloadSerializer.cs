using Asterisk.Sdk.Push.Events;

namespace Asterisk.Sdk.Push.Webhooks;

/// <summary>
/// Serializes a <see cref="PushEvent"/> to the UTF-8 JSON body sent to the webhook target.
/// Override to inject custom envelope fields or switch to a different wire format.
/// </summary>
public interface IWebhookPayloadSerializer
{
    /// <summary>Serialize the event. Implementations must be thread-safe.</summary>
    byte[] Serialize(PushEvent evt);
}
