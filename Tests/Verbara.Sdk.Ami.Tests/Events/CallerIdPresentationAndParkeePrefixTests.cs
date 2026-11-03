using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Generated;
using Verbara.Sdk.Ami.Internal;

namespace Verbara.Sdk.Ami.Tests.Events;

/// <summary>
/// The presentation members of <see cref="NewCallerIdEvent"/> and the generated <c>Parkee</c> prefix, on frames composed
/// here. A composed frame makes no claim about what Asterisk sends: the captured frames that hold those claims are
/// replayed in the session functional suite.
/// </summary>
public sealed class CallerIdPresentationAndParkeePrefixTests
{
    [Fact]
    public void NewCallerId_ShouldLeaveTheNumberNull_WhenThePresentationStartsWithNoNumber()
    {
        var evt = (NewCallerIdEvent)GeneratedEventDeserializer.Deserialize(Message("NewCallerid",
            ("Channel", "PJSIP/a-1"), ("CID-CallingPres", "Presentation Unknown")));

        using var scope = new AssertionScope();
        evt.CidCallingPresTxt.Should().Be("Presentation Unknown");
        evt.CidCallingPres.Should().BeNull("the value starts with no number, and reading it does not fail");
    }

    [Theory]
    [InlineData("")]
    [InlineData("(Allowed)")]
    [InlineData("99999999999 (overflows)")]
    public void NewCallerId_ShouldNotFail_WhateverThePresentationHolds(string value)
    {
        var evt = (NewCallerIdEvent)GeneratedEventDeserializer.Deserialize(Message("NewCallerid", ("CID-CallingPres", value)));

        var read = () => evt.CidCallingPres;

        read.Should().NotThrow();
        evt.CidCallingPres.Should().BeNull();
    }

    [Fact]
    public void NewCallerId_ShouldReturnTheAssignedNumber_WhenOneIsAssignedExplicitly()
    {
        var evt = new NewCallerIdEvent
        {
            CidCallingPresTxt = "0 (Presentation Allowed, Not Screened)",
            CidCallingPres = 1,
        };

        using var scope = new AssertionScope();
        evt.CidCallingPres.Should().Be(1, "an explicit assignment wins over the number the text starts with");
        evt.CidCallingPresTxt.Should().Be("0 (Presentation Allowed, Not Screened)");
    }

    [Fact]
    public void NewCallerId_ShouldDeriveTheNumber_WhenTheAssignmentIsClearedAgain()
    {
        var evt = new NewCallerIdEvent { CidCallingPresTxt = "32 (Presentation Prohibited, Not Screened)", CidCallingPres = 1 };

        evt.CidCallingPres = null;

        evt.CidCallingPres.Should().Be(32);
    }

    [Fact]
    public void Deserialize_ShouldReadNoParkeeHeader_WhenTheEventDoesNotDeriveFromTheParkingBase()
    {
        var evt = (NewChannelEvent)GeneratedEventDeserializer.Deserialize(Message("Newchannel",
            ("Channel", "PJSIP/a-1"), ("Uniqueid", "1.1"), ("Exten", "100"),
            ("ParkeeChannel", "PJSIP/parked-1"), ("ParkeeUniqueid", "2.2"), ("ParkeeExten", "park")));

        using var scope = new AssertionScope();
        evt.Channel.Should().Be("PJSIP/a-1");
        evt.UniqueId.Should().Be("1.1");
        evt.Exten.Should().Be("100");
    }

    [Fact]
    public void Deserialize_ShouldReadTheParkeeHeadersOverTheUnprefixedOnes_WhenTheEventDerivesFromTheParkingBase()
    {
        var evt = (ParkedCallGiveUpEvent)GeneratedEventDeserializer.Deserialize(Message("ParkedCallGiveUp",
            ("Channel", "PJSIP/not-the-parkee"), ("Uniqueid", "1.1"),
            ("ParkeeChannel", "PJSIP/parked-1"), ("ParkeeUniqueid", "2.2"), ("ParkeePriority", "3")));

        using var scope = new AssertionScope();
        evt.Channel.Should().Be("PJSIP/parked-1");
        evt.UniqueId.Should().Be("2.2");
        evt.Priority.Should().Be(3);
    }

    private static AmiMessage Message(string name, params (string Key, string Value)[] headers)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Event"] = name };
        foreach (var (key, value) in headers)
            fields[key] = value;
        return new AmiMessage(fields);
    }
}
