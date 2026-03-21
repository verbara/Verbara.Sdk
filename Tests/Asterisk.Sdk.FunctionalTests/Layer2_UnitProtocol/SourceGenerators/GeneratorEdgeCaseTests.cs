namespace Asterisk.Sdk.FunctionalTests.Layer2_UnitProtocol.SourceGenerators;

using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.Ami.Generated;
using Asterisk.Sdk.Ami.Internal;
using FluentAssertions;

[Trait("Category", "Unit")]
public sealed class GeneratorEdgeCaseTests
{
    /// <summary>
    /// Helper: builds an AmiMessage from a dictionary with an Event key.
    /// Uses OrdinalIgnoreCase to match production AmiProtocolReader behavior.
    /// </summary>
    private static AmiMessage CreateMessage(string eventName, Dictionary<string, string> extra)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = eventName,
        };

        foreach (var (k, v) in extra)
            fields[k] = v;

        return new AmiMessage(fields);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData("no", false)]
    public void BooleanParsing_ShouldOnlyRecognize1AndTrue(string fieldValue, bool expected)
    {
        // ConfbridgeTalkingEvent.TalkingStatus is bool?
        var msg = CreateMessage("ConfbridgeTalking", new Dictionary<string, string>
        {
            ["TalkingStatus"] = fieldValue,
        });

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<ConfbridgeTalkingEvent>();
        var typed = (ConfbridgeTalkingEvent)evt;
        typed.TalkingStatus.Should().Be(expected);
    }

    [Fact]
    public void BooleanParsing_ShouldReturnNull_WhenFieldAbsent()
    {
        // No TalkingStatus field at all => null (not false)
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = "ConfbridgeTalking",
        };
        var msg = new AmiMessage(fields);

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<ConfbridgeTalkingEvent>();
        var typed = (ConfbridgeTalkingEvent)evt;
        typed.TalkingStatus.Should().BeNull();
    }

    [Fact]
    public void IntParsing_ShouldReturnNull_ForMalformedInput()
    {
        // HangupEvent.Cause is int? — "abc" should fail TryParse, leaving null
        var msg = CreateMessage("Hangup", new Dictionary<string, string>
        {
            ["Cause"] = "abc",
        });

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<HangupEvent>();
        var typed = (HangupEvent)evt;
        typed.Cause.Should().BeNull();
    }

    [Fact]
    public void LongParsing_ShouldReturnNull_ForOverflow()
    {
        // RtcpReceivedEvent.Pt is long? — a value exceeding long.MaxValue should fail TryParse
        var overflow = "99999999999999999999999999999";
        var msg = CreateMessage("RtcpReceived", new Dictionary<string, string>
        {
            ["Pt"] = overflow,
        });

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<RtcpReceivedEvent>();
        var typed = (RtcpReceivedEvent)evt;
        typed.Pt.Should().BeNull("long.TryParse returns false on overflow");
    }

    [Theory]
    [InlineData("NaN", double.NaN)]
    [InlineData("Infinity", double.PositiveInfinity)]
    [InlineData("-Infinity", double.NegativeInfinity)]
    public void DoubleParsing_ShouldAcceptSpecialValues(string specialValue, double expected)
    {
        // double.TryParse with NumberStyles.Float + InvariantCulture accepts NaN and Infinity
        var msg = CreateMessage("RtcpReceived", new Dictionary<string, string>
        {
            ["Rtt"] = specialValue,
        });

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<RtcpReceivedEvent>();
        var typed = (RtcpReceivedEvent)evt;
        typed.Rtt.Should().Be(expected);
    }

    [Fact]
    public void DoubleParsing_ShouldReturnNull_ForEmptyString()
    {
        var msg = CreateMessage("RtcpReceived", new Dictionary<string, string>
        {
            ["Rtt"] = "",
        });

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<RtcpReceivedEvent>();
        var typed = (RtcpReceivedEvent)evt;
        typed.Rtt.Should().BeNull();
    }

    [Fact]
    public void WhitespaceInValues_ShouldNotParse_ForIntField()
    {
        // int.TryParse with NumberStyles.Integer + InvariantCulture does NOT trim by default
        // Wait — actually NumberStyles.Integer includes AllowLeadingWhite | AllowTrailingWhite
        // So " 42 " SHOULD parse successfully
        var msg = CreateMessage("Hangup", new Dictionary<string, string>
        {
            ["Cause"] = " 42 ",
        });

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<HangupEvent>();
        var typed = (HangupEvent)evt;
        // NumberStyles.Integer allows leading/trailing whitespace
        typed.Cause.Should().Be(42);
    }

    [Fact]
    public void SpecialCharactersInValues_ShouldBePreserved()
    {
        // Unicode and special chars in string fields should pass through unchanged
        var unicodeValue = "SIP/\u00e9l\u00e8ve-0001 \ud83d\udcde";
        var msg = CreateMessage("NewChannel", new Dictionary<string, string>
        {
            ["Channel"] = unicodeValue,
        });

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<NewChannelEvent>();
        var typed = (NewChannelEvent)evt;
        typed.Channel.Should().Be(unicodeValue);
    }

    [Fact]
    public void FieldNameLookup_ShouldBeCaseInsensitive()
    {
        // AmiMessage dictionary is OrdinalIgnoreCase, so "channel" should map to Channel property
        var msg = CreateMessage("NewChannel", new Dictionary<string, string>
        {
            ["channel"] = "SIP/lower-0001",
            ["channelstatedesc"] = "Up",
        });

        var evt = GeneratedEventDeserializer.Deserialize(msg);

        evt.Should().BeOfType<NewChannelEvent>();
        var typed = (NewChannelEvent)evt;
        typed.Channel.Should().Be("SIP/lower-0001");
        typed.ChannelStateDesc.Should().Be("Up");
    }
}
