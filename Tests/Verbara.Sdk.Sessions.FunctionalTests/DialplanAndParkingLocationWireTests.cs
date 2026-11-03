using System.Globalization;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Events.Base;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using static Verbara.Sdk.Sessions.FunctionalTests.DialplanAndParkingWireTests;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Where a parked call is parked and where a <c>Newexten</c> says the channel is, held by the four
/// <c>dialplan-and-parking-*</c> captures: the typed members those frames gained, against each frame's own headers.
/// </summary>
public sealed class DialplanAndParkingLocationWireTests
{
    private static readonly string[] ParkingEvents = ["ParkedCall", "UnParkedCall", "ParkedCallTimeOut", "ParkedCallGiveUp"];

    [Theory]
    [MemberData(nameof(Captures), MemberType = typeof(DialplanAndParkingWireTests))]
    public async Task ParkingEvents_ShouldExposeWhereTheCallIsParked_WhenEveryCapturedFrameIsParsed(string fixture, string build)
    {
        _ = build;
        var events = (await AmiCaptureReplay.ReadCaptureAsync(fixture))
            .Where(e => ParkingEvents.Contains(e.EventType, StringComparer.Ordinal))
            .ToList();

        using var scope = new AssertionScope();
        events.Should().NotBeEmpty();
        foreach (var managerEvent in events)
        {
            managerEvent.Should().BeAssignableTo<ParkingEventBase>();
            if (managerEvent is not ParkingEventBase evt)
                continue;

            evt.ParkingSpace.Should().NotBeNullOrEmpty().And.Be(Raw(evt, "ParkingSpace"));
            evt.ParkingLot.Should().NotBeNullOrEmpty().And.Be(Raw(evt, "Parkinglot"));
            evt.ParkingTimeout.Should().Be(Raw(evt, "ParkingTimeout"));
            evt.ParkingDuration.Should().Be(Raw(evt, "ParkingDuration"));
            evt.ParkerDialString.Should().Be(Raw(evt, "ParkerDialString"));
        }
    }

    [Theory]
    [MemberData(nameof(Captures), MemberType = typeof(DialplanAndParkingWireTests))]
    public async Task ParkedCall_ShouldReadTheLotWithTheWiresSpelling_WhenItsHeaderIsParkinglot(string fixture, string build)
    {
        _ = build;
        var bytes = System.Text.Encoding.ASCII.GetString(AmiCaptureReplay.ReadCaptureBytes(fixture));
        var parked = (await AmiCaptureReplay.ReadCaptureAsync(fixture)).OfType<ParkedCallEvent>().ToList();

        using var scope = new AssertionScope();
        bytes.Should().Contain("\r\nParkinglot: default\r\n", "the wire spells the header Parkinglot");
        parked.Should().NotBeEmpty();
        parked.Should().AllSatisfy(e => e.ParkingLot.Should().Be("default"));
    }

    [Theory]
    [MemberData(nameof(Captures), MemberType = typeof(DialplanAndParkingWireTests))]
    public async Task ParkedCall_ShouldKeepTheSpaceApartFromTheExtension_WhenItsFrameCarriesBoth(string fixture, string build)
    {
        _ = build;
        var parked = (await AmiCaptureReplay.ReadCaptureAsync(fixture)).OfType<ParkedCallEvent>()
            .Where(e => Raw(e, "ParkingSpace") == "701" && Raw(e, "ParkeeExten") == "park")
            .ToList();

        using var scope = new AssertionScope();
        parked.Should().NotBeEmpty("each capture parks a call from extension park in space 701");
        foreach (var evt in parked)
        {
            evt.ParkingSpace.Should().Be("701");
            evt.Exten.Should().Be("park");
        }
    }

    [Theory]
    [MemberData(nameof(Captures), MemberType = typeof(DialplanAndParkingWireTests))]
    public async Task ParkedCall_ShouldReadTheTimeLeftInBothMembers_WhenItsFrameSaysTwenty(string fixture, string build)
    {
        _ = build;
        var parked = (await AmiCaptureReplay.ReadCaptureAsync(fixture)).OfType<ParkedCallEvent>()
            .Where(e => Raw(e, "ParkingTimeout") == "20")
            .ToList();

        using var scope = new AssertionScope();
        parked.Should().NotBeEmpty();
        foreach (var evt in parked)
        {
            evt.Timeout.Should().Be(20);
            evt.ParkingTimeout.Should().Be("20");
        }
    }

    [Theory]
    [MemberData(nameof(Captures), MemberType = typeof(DialplanAndParkingWireTests))]
    public async Task NewExten_ShouldCarryItsDialplanLocation_WhenEveryCapturedFrameIsParsed(string fixture, string build)
    {
        _ = build;
        var frames = (await AmiCaptureReplay.ReadCaptureAsync(fixture)).OfType<NewExtenEvent>().ToList();

        using var scope = new AssertionScope();
        frames.Should().NotBeEmpty();
        foreach (var evt in frames)
        {
            evt.Context.Should().NotBeNullOrEmpty().And.Be(Raw(evt, "Context"));
            evt.Exten.Should().NotBeNullOrEmpty().And.Be(Raw(evt, "Exten"));
            evt.Priority.Should().Be(int.Parse(Raw(evt, "Priority")!, CultureInfo.InvariantCulture));
            evt.Extension.Should().Be(Raw(evt, "Extension"));
        }
    }
}
