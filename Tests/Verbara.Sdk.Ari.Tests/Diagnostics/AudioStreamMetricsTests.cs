using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.RegularExpressions;
using Verbara.Sdk.Ari.Diagnostics;
using FluentAssertions;

namespace Verbara.Sdk.Ari.Tests.Diagnostics;

public sealed partial class AudioStreamMetricsTests
{
    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?")]
    private static partial Regex VersionNumber();

    [Fact]
    public void BufferUnderruns_ShouldNameItsRemovalReleaseAndNoOther_WhenItsObsoletionIsRead()
    {
        var obsolete = typeof(AudioStreamMetrics).GetField("BufferUnderruns", BindingFlags.Public | BindingFlags.Static)?
            .GetCustomAttribute<ObsoleteAttribute>();

        obsolete.Should().NotBeNull();
        obsolete!.Message.Should().EndWith("Removed in Verbara.Sdk 2.11.0.", "the message names the release that removes the field");
        VersionNumber().Matches(obsolete.Message!).Select(static m => m.Value).Should().Equal(["2.11.0"],
            "the message names that one release and no other");
    }

    [Fact]
    public void Meter_ShouldHaveCorrectName()
    {
        AudioStreamMetrics.Meter.Name.Should().Be("Verbara.Sdk.Ari.Audio");
    }

    [Fact]
    public void PublishedInstruments_ShouldKeepTheirNamesAndUnits_WhenTheMeterIsEnumerated()
    {
        // A name and unit pin, not instrumentation coverage: it records nothing and enables no measurement.
        // What each instrument records is witnessed against real sessions in AudioStreamMetricsWitnessTests.
        // Names and units are what an exporter keys a series on, so a rename or a unit change is a red here.
        AudioStreamMetrics.Meter.Name.Should().Be("Verbara.Sdk.Ari.Audio", "reading the meter initialises every instrument field");
        var published = new List<(string Name, string? Unit)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == "Verbara.Sdk.Ari.Audio")
            {
                lock (published)
                    published.Add((instrument.Name, instrument.Unit));
            }
        };
        listener.Start();

        lock (published)
            published.Should().BeEquivalentTo(new (string Name, string? Unit)[]
            {
                ("audio.streams.opened", "streams"),
                ("audio.streams.closed", "streams"),
                ("audio.frames.received", "frames"),
                ("audio.frames.sent", "frames"),
                ("audio.frames.dropped", "frames"),
                ("audio.bytes.received", "bytes"),
                ("audio.bytes.sent", "bytes"),
                ("audio.buffer.underruns", "underruns"),
                ("audio.hangup.frames", "frames"),
                ("audio.error.frames", "frames"),
                ("audio.transport.failures", "sessions"),
                ("audio.connections.refused", "connections"),
                ("audio.control_messages.subscriber_faults", "faults"),
                ("audio.frame.latency", "ms"),
            });
    }

    [Fact]
    public void BufferUnderruns_ShouldBeObsoleteWithVsdk0003SayingItIsNotEmitted_WhenAConsumerReadsIt()
    {
        // Reading the attribute is the point: a consumer building with TreatWarningsAsErrors gets VSDK0003,
        // not a blanket CS0618, and the message tells them the instrument never records anything.
        var field = typeof(AudioStreamMetrics).GetField("BufferUnderruns", BindingFlags.Public | BindingFlags.Static);
        field.Should().NotBeNull("the field stays published; deprecating it removes nothing");

        var obsolete = field!.GetCustomAttribute<ObsoleteAttribute>();

        obsolete.Should().NotBeNull("nothing records audio.buffer.underruns, so the field is deprecated");
        obsolete!.DiagnosticId.Should().Be("VSDK0003");
        obsolete.UrlFormat.Should().Be("https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.0.md#vsdk0003",
            "the warning's help link opens the id's heading of the 2.10.0 migration guide, not the compiler's CS0618 page");
        obsolete.IsError.Should().BeFalse("a deprecation warns; it does not break a build without TreatWarningsAsErrors");
        obsolete.Message.Should().ContainEquivalentOf("not emitted")
            .And.Contain("audio.buffer.underruns");
    }

    [Fact]
    public void BufferUnderruns_ShouldStillBePublishedOnTheMeter_WhenObsolete()
    {
        // The non-removal promise: the deprecated field still compiles and its instrument is still published
        // on the meter under the same name. The listener only enumerates instruments, filtered by meter name;
        // it enables no measurement, so it counts nothing other tests record.
        var published = new List<Instrument>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == "Verbara.Sdk.Ari.Audio" && instrument.Name == "audio.buffer.underruns")
            {
                lock (published)
                    published.Add(instrument);
            }
        };
        listener.Start();

#pragma warning disable VSDK0003 // reading the obsolete field is the point of this test: it must keep compiling
        var field = AudioStreamMetrics.BufferUnderruns;
#pragma warning restore VSDK0003

        lock (published)
            published.Should().ContainSingle("the deprecated instrument stays published on the meter")
                .Which.Should().BeSameAs(field);
        field.Unit.Should().Be("underruns");
    }
}
