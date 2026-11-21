using System.Diagnostics.Metrics;
using Verbara.Sdk.Ari.Diagnostics;
using FluentAssertions;

namespace Verbara.Sdk.Ari.Tests.Diagnostics;

public sealed class AudioStreamMetricsTests
{
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
                ("audio.hangup.frames", "frames"),
                ("audio.error.frames", "frames"),
                ("audio.transport.failures", "sessions"),
                ("audio.connections.refused", "connections"),
                ("audio.control_messages.subscriber_faults", "faults"),
                ("audio.stream_connected.subscriber_faults", "faults"),
                ("audio.frame.latency", "ms"),
            });
    }

    [Fact]
    public void RemovedInstrument_ShouldNotBePublished_WhenTheMeterIsEnumerated()
    {
        // 2.11.0 removed the field that was audio.buffer.underruns' only creation, so the meter no longer publishes it.
        // The listener only enumerates instruments, filtered by meter name; it enables no measurement.
        AudioStreamMetrics.Meter.Name.Should().Be("Verbara.Sdk.Ari.Audio", "reading the meter initialises every instrument field");
        var published = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == "Verbara.Sdk.Ari.Audio")
            {
                lock (published)
                    published.Add(instrument.Name);
            }
        };
        listener.Start();

        lock (published)
        {
            published.Should().NotBeEmpty("the meter's other instruments are published");
            published.Should().NotContain("audio.buffer.underruns", "nothing ever recorded it, and 2.11.0 removed it");
        }
    }
}
