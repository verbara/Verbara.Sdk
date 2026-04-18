using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Asterisk.Sdk.Push.Webhooks;

/// <summary>
/// Default <see cref="IWebhookSigner"/>: computes <c>sha256=&lt;hex&gt;</c> using the
/// subscription secret (UTF-8 bytes) as the HMAC key. Receivers verify by recomputing the
/// same HMAC over the received body and comparing in constant time.
/// </summary>
public sealed class HmacSha256Signer : IWebhookSigner
{
    /// <inheritdoc />
    public string Sign(ReadOnlySpan<byte> payload, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        Span<byte> hash = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(keyBytes, payload, hash);

        var sb = new StringBuilder("sha256=", capacity: 7 + (hash.Length * 2));
        for (var i = 0; i < hash.Length; i++)
            sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));

        return sb.ToString();
    }
}
