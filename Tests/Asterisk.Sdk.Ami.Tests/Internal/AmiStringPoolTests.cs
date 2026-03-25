using System.Text;
using Asterisk.Sdk.Ami.Internal;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Internal;

public sealed class AmiStringPoolTests
{
    // ── GetKey: known keys return interned (same reference) ──

    [Theory]
    [InlineData("Event")]
    [InlineData("Channel")]
    [InlineData("Uniqueid")]
    [InlineData("Response")]
    [InlineData("ActionID")]
    [InlineData("CallerIDNum")]
    [InlineData("Queue")]
    [InlineData("Priority")]
    [InlineData("Context")]
    [InlineData("Exten")]
    public void GetKey_ShouldReturnSameReference_WhenKeyIsKnown(string key)
    {
        var utf8 = Encoding.UTF8.GetBytes(key).AsSpan();

        var result1 = AmiStringPool.GetKey(utf8);
        var result2 = AmiStringPool.GetKey(utf8);

        result1.Should().Be(key);
        ReferenceEquals(result1, result2).Should().BeTrue("interned strings should be the same object");
    }

    [Fact]
    public void GetKey_ShouldReturnNewString_WhenKeyIsUnknown()
    {
        var unknownKey = "XyzNotInPool_12345";
        var utf8 = Encoding.UTF8.GetBytes(unknownKey).AsSpan();

        var result = AmiStringPool.GetKey(utf8);

        result.Should().Be(unknownKey);
    }

    [Fact]
    public void GetKey_ShouldAllocateNewStrings_ForUnknownKeys()
    {
        var unknownKey = "CustomHeaderField_67890";
        var utf8 = Encoding.UTF8.GetBytes(unknownKey).AsSpan();

        var result1 = AmiStringPool.GetKey(utf8);
        var result2 = AmiStringPool.GetKey(utf8);

        result1.Should().Be(unknownKey);
        result2.Should().Be(unknownKey);
        // Unknown keys allocate new strings each time
        ReferenceEquals(result1, result2).Should().BeFalse();
    }

    // ── GetValue: known values return interned ──

    [Theory]
    [InlineData("Success")]
    [InlineData("Error")]
    [InlineData("Up")]
    [InlineData("Down")]
    [InlineData("Ringing")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("5")]
    [InlineData("default")]
    [InlineData("en")]
    [InlineData("call,all")]
    [InlineData("agent,all")]
    public void GetValue_ShouldReturnSameReference_WhenValueIsKnown(string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value).AsSpan();

        var result1 = AmiStringPool.GetValue(utf8);
        var result2 = AmiStringPool.GetValue(utf8);

        result1.Should().Be(value);
        ReferenceEquals(result1, result2).Should().BeTrue("interned values should be the same object");
    }

    [Fact]
    public void GetValue_ShouldReturnEmpty_WhenInputIsEmpty()
    {
        var result = AmiStringPool.GetValue(ReadOnlySpan<byte>.Empty);

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetValue_ShouldReturnNewString_WhenValueIsUnknown()
    {
        var unknownValue = "some-random-long-value-not-in-pool";
        var utf8 = Encoding.UTF8.GetBytes(unknownValue).AsSpan();

        var result = AmiStringPool.GetValue(utf8);

        result.Should().Be(unknownValue);
    }

    [Fact]
    public void GetValue_ShouldReturnNewString_WhenLengthExceedsPoolSize()
    {
        // Pool only stores values up to length 23
        var longValue = new string('A', 30);
        var utf8 = Encoding.UTF8.GetBytes(longValue).AsSpan();

        var result = AmiStringPool.GetValue(utf8);

        result.Should().Be(longValue);
    }

    [Fact]
    public void GetValue_ShouldHandleShortUnknownValues()
    {
        // A short value that is not in the pool
        var unknownShort = "ZZ";
        var utf8 = Encoding.UTF8.GetBytes(unknownShort).AsSpan();

        var result = AmiStringPool.GetValue(utf8);

        result.Should().Be(unknownShort);
    }

    // ── GetKey: all digits work (common in ChannelState, Priority) ──

    [Theory]
    [InlineData("Privilege")]
    [InlineData("Timestamp")]
    [InlineData("Message")]
    [InlineData("Agent")]
    [InlineData("Interface")]
    [InlineData("Penalty")]
    [InlineData("Status")]
    [InlineData("BridgeUniqueid")]
    [InlineData("BridgeType")]
    public void GetKey_ShouldInternAllCommonProtocolFields(string key)
    {
        var utf8 = Encoding.UTF8.GetBytes(key).AsSpan();

        var result = AmiStringPool.GetKey(utf8);

        result.Should().Be(key);
    }

    // ── Verify privilege values used in AMI EventMask ──

    [Theory]
    [InlineData("system,all")]
    [InlineData("command,all")]
    [InlineData("reporting,all")]
    [InlineData("security,all")]
    public void GetValue_ShouldInternPrivilegeValues(string privilege)
    {
        var utf8 = Encoding.UTF8.GetBytes(privilege).AsSpan();

        var result1 = AmiStringPool.GetValue(utf8);
        var result2 = AmiStringPool.GetValue(utf8);

        result1.Should().Be(privilege);
        ReferenceEquals(result1, result2).Should().BeTrue();
    }

    // ── Channel state descriptions ──

    [Theory]
    [InlineData("Rsrvd")]
    [InlineData("OffHook")]
    [InlineData("Dialing")]
    [InlineData("Ring")]
    [InlineData("Busy")]
    [InlineData("Follows")]
    [InlineData("Goodbye")]
    public void GetValue_ShouldInternChannelStateDescriptions(string stateDesc)
    {
        var utf8 = Encoding.UTF8.GetBytes(stateDesc).AsSpan();

        var result = AmiStringPool.GetValue(utf8);

        result.Should().Be(stateDesc);
    }
}
