using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Asterisk.Sdk.Push.Webhooks;
using FluentAssertions;

namespace Asterisk.Sdk.Push.Webhooks.Tests;

public sealed class HmacSha256SignerTests
{
    [Fact]
    public void Sign_ShouldReturnSha256Prefix()
    {
        var signer = new HmacSha256Signer();
        var payload = Encoding.UTF8.GetBytes("hello");

        var result = signer.Sign(payload, "secret");

        result.Should().StartWith("sha256=");
    }

    [Fact]
    public void Sign_ShouldMatchReferenceHmac_WhenComputedExternally()
    {
        var signer = new HmacSha256Signer();
        var payload = Encoding.UTF8.GetBytes("hello world");
        const string secret = "my-secret";

        var expected = BuildReferenceSignature(payload, secret);
        var actual = signer.Sign(payload, secret);

        actual.Should().Be(expected);
    }

    [Fact]
    public void Sign_ShouldThrow_WhenSecretIsEmpty()
    {
        var signer = new HmacSha256Signer();
        var payload = Encoding.UTF8.GetBytes("x");

        var act = () => signer.Sign(payload, string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Sign_ShouldProduceDifferentSignatures_WhenSecretChanges()
    {
        var signer = new HmacSha256Signer();
        var payload = Encoding.UTF8.GetBytes("x");

        var a = signer.Sign(payload, "secret-a");
        var b = signer.Sign(payload, "secret-b");

        a.Should().NotBe(b);
    }

    private static string BuildReferenceSignature(byte[] payload, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        Span<byte> hash = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(key, payload, hash);
        var sb = new StringBuilder("sha256=");
        for (var i = 0; i < hash.Length; i++)
            sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
