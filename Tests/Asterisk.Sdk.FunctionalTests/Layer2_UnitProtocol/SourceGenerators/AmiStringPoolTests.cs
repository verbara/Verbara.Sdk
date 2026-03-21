namespace Asterisk.Sdk.FunctionalTests.Layer2_UnitProtocol.SourceGenerators;

using System.Text;
using Asterisk.Sdk.Ami.Internal;
using FluentAssertions;

[Trait("Category", "Unit")]
public sealed class AmiStringPoolTests
{
    [Fact]
    public void GetKey_ShouldReturnInternedString_ForKnownKey()
    {
        var bytes = "Channel"u8;

        var first = AmiStringPool.GetKey(bytes);
        var second = AmiStringPool.GetKey(bytes);

        first.Should().Be("Channel");
        ReferenceEquals(first, second).Should().BeTrue("known keys should return the same interned string instance");
    }

    [Fact]
    public void GetKey_ShouldAllocateNewString_ForUnknownKey()
    {
        var bytes = Encoding.UTF8.GetBytes("XCustomKey12345");

        var result = AmiStringPool.GetKey(bytes);

        result.Should().Be("XCustomKey12345");
    }

    [Fact]
    public void GetValue_ShouldReturnInternedString_ForCommonValue()
    {
        var bytes = "Success"u8;

        var first = AmiStringPool.GetValue(bytes);
        var second = AmiStringPool.GetValue(bytes);

        first.Should().Be("Success");
        ReferenceEquals(first, second).Should().BeTrue("common values should return the same interned string instance");
    }
}
