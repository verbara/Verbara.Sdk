using System.Globalization;
using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Events.Base;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// What the parking, <c>Newexten</c> and <c>NewCallerid</c> events carry, held by the four
/// <c>dialplan-and-parking-*</c> captures (<c>Recordings/asterisk-ami/</c>) parsed through the production reader and
/// deserializer. Every expected value is the frame's own header, read from the frame's raw fields; no frame here is
/// composed.
/// </summary>
public sealed class DialplanAndParkingWireTests
{
    private static readonly string[] ParkingEvents = ["ParkedCall", "UnParkedCall", "ParkedCallTimeOut", "ParkedCallGiveUp"];

    public static TheoryData<string, string> Captures => new()
    {
        { "dialplan-and-parking-asterisk-20.20.1.raw", "Asterisk 20.20.1" },
        { "dialplan-and-parking-asterisk-22.11.0.raw", "Asterisk 22.11.0" },
        { "dialplan-and-parking-asterisk-23.5.0.raw", "Asterisk 23.5.0" },
        { "dialplan-and-parking-asterisk-24.0.0-rc2.raw", "Asterisk 24.0.0-rc2" },
    };

    [Theory]
    [MemberData(nameof(Captures))]
    public async Task Fixture_ShouldBeTheWireByteForByte_WhenItIsRead(string fixture, string build)
    {
        var bytes = AmiCaptureReplay.ReadCaptureBytes(fixture);
        var events = await AmiCaptureReplay.ReadCaptureAsync(fixture);

        using var scope = new AssertionScope();
        BareLineFeeds(bytes).Should().Be(0, "every line of {0} ends in CRLF", fixture);
        Encoding.ASCII.GetString(bytes).Should().Contain($"AsteriskVersion: {build}\r\n", "the capture names its build in its own bytes");
        foreach (var name in ParkingEvents.Concat(["Newexten", "NewCallerid"]))
            events.Count(e => string.Equals(e.EventType, name, StringComparison.Ordinal))
                .Should().BeGreaterThan(0, "{0} holds at least one {1} frame", fixture, name);
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public async Task ParkedCall_ShouldNameItsChannel_WhenEveryCapturedFrameIsParsed(string fixture, string build)
    {
        _ = build;
        var parked = (await AmiCaptureReplay.ReadCaptureAsync(fixture)).OfType<ParkedCallEvent>().ToList();

        using var scope = new AssertionScope();
        parked.Should().NotBeEmpty();
        foreach (var evt in parked)
        {
            evt.Channel.Should().NotBeNullOrEmpty().And.Be(Raw(evt, "ParkeeChannel"));
            evt.UniqueId.Should().NotBeNullOrEmpty().And.Be(Raw(evt, "ParkeeUniqueid"));
            evt.Exten.Should().Be(Raw(evt, "ParkeeExten"));
        }
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public async Task ParkingEvents_ShouldCarryEveryParkeeHeaderInTheInheritedMember_WhenEveryCapturedFrameIsParsed(
        string fixture, string build)
    {
        _ = build;
        var events = (await AmiCaptureReplay.ReadCaptureAsync(fixture))
            .Where(e => ParkingEvents.Contains(e.EventType, StringComparer.Ordinal))
            .ToList();

        using var scope = new AssertionScope();
        events.Select(static e => e.EventType).Distinct(StringComparer.Ordinal).Should().BeEquivalentTo(ParkingEvents);
        foreach (var managerEvent in events)
        {
            managerEvent.Should().BeAssignableTo<ChannelEventBase>("{0} is a channel event", managerEvent.EventType);
            if (managerEvent is not ChannelEventBase evt)
                continue;

            evt.Channel.Should().Be(Raw(evt, "ParkeeChannel"));
            evt.ChannelState.Should().Be(Raw(evt, "ParkeeChannelState"));
            evt.ChannelStateDesc.Should().Be(Raw(evt, "ParkeeChannelStateDesc"));
            evt.CallerIdNum.Should().Be(Raw(evt, "ParkeeCallerIDNum"));
            evt.CallerIdName.Should().Be(Raw(evt, "ParkeeCallerIDName"));
            evt.ConnectedLineNum.Should().Be(Raw(evt, "ParkeeConnectedLineNum"));
            evt.ConnectedLineName.Should().Be(Raw(evt, "ParkeeConnectedLineName"));
            evt.Language.Should().Be(Raw(evt, "ParkeeLanguage"));
            evt.AccountCode.Should().Be(Raw(evt, "ParkeeAccountCode"));
            evt.Context.Should().Be(Raw(evt, "ParkeeContext"));
            evt.Exten.Should().Be(Raw(evt, "ParkeeExten"));
            evt.Priority.Should().Be(int.Parse(Raw(evt, "ParkeePriority")!, CultureInfo.InvariantCulture));
            evt.UniqueId.Should().Be(Raw(evt, "ParkeeUniqueid"));
            evt.Linkedid.Should().Be(Raw(evt, "ParkeeLinkedid"));
        }
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public async Task ParkedCall_ShouldReadTheTimeLeftAsANumber_WhenEveryCapturedFrameIsParsed(string fixture, string build)
    {
        _ = build;
        var parked = (await AmiCaptureReplay.ReadCaptureAsync(fixture)).OfType<ParkedCallEvent>().ToList();

        using var scope = new AssertionScope();
        parked.Should().Contain(e => Raw(e, "ParkingTimeout") == "20", "the first park of each capture has 20 s left");
        foreach (var evt in parked)
            evt.Timeout.Should().Be(int.Parse(Raw(evt, "ParkingTimeout")!, CultureInfo.InvariantCulture));
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public async Task NewCallerId_ShouldCarryThePresentation_WhenEveryCapturedFrameIsParsed(string fixture, string build)
    {
        _ = build;
        var frames = (await AmiCaptureReplay.ReadCaptureAsync(fixture)).OfType<NewCallerIdEvent>().ToList();

        using var scope = new AssertionScope();
        frames.Should().NotBeEmpty();
        foreach (var evt in frames)
        {
            evt.CidCallingPresTxt.Should().NotBeNull().And.Be(Raw(evt, "CID-CallingPres"));
            evt.CidCallingPres.Should().Be(0);
        }
    }

    /// <summary>The value of the header <paramref name="name"/> as the frame carried it, case-insensitively.</summary>
    internal static string? Raw(Verbara.Sdk.ManagerEvent evt, string name) => evt.RawFields?.GetValueOrDefault(name);

    private static int BareLineFeeds(byte[] bytes)
    {
        var bare = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n' && (i == 0 || bytes[i - 1] != (byte)'\r'))
                bare++;
        }

        return bare;
    }
}
