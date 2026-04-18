namespace Asterisk.Sdk.Push.Webhooks;

/// <summary>
/// Signer abstraction used to authenticate webhook deliveries. The default implementation
/// (<see cref="HmacSha256Signer"/>) produces <c>sha256=&lt;hex&gt;</c> values suitable for the
/// <c>X-Signature</c> header; consumers can swap this interface for JWT, asymmetric, or other
/// schemes.
/// </summary>
public interface IWebhookSigner
{
    /// <summary>
    /// Sign a webhook payload with the per-subscription <paramref name="secret"/>. Implementations
    /// return the full header value (including algorithm prefix, e.g. <c>"sha256=..."</c>).
    /// </summary>
    string Sign(ReadOnlySpan<byte> payload, string secret);
}
