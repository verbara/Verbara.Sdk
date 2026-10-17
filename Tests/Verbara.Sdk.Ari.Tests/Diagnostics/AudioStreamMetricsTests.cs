using System.Diagnostics.Metrics;
using System.Reflection;
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
    public void StreamsOpened_ShouldIncrementCounter()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Name == "audio.streams.opened")
                ml.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "audio.streams.opened")
                observed += measurement;
        });
        listener.Start();

        AudioStreamMetrics.StreamsOpened.Add(1);

        observed.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void StreamsClosed_ShouldBeCounter()
    {
        AudioStreamMetrics.StreamsClosed.Should().NotBeNull();
        AudioStreamMetrics.StreamsClosed.Name.Should().Be("audio.streams.closed");
    }

    [Fact]
    public void FramesReceived_ShouldIncrementCounter()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Name == "audio.frames.received")
                ml.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "audio.frames.received")
                observed += measurement;
        });
        listener.Start();

        AudioStreamMetrics.FramesReceived.Add(1);

        observed.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void FramesSent_ShouldBeCounter()
    {
        AudioStreamMetrics.FramesSent.Should().NotBeNull();
    }

    [Fact]
    public void BytesReceived_ShouldBeCounter()
    {
        AudioStreamMetrics.BytesReceived.Should().NotBeNull();
    }

    [Fact]
    public void BytesSent_ShouldIncrementCounter()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Name == "audio.bytes.sent")
                ml.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "audio.bytes.sent")
                observed += measurement;
        });
        listener.Start();

        AudioStreamMetrics.BytesSent.Add(512);

        observed.Should().BeGreaterThanOrEqualTo(512);
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

    [Fact]
    public void HangupFrames_ShouldBeCounter()
    {
        AudioStreamMetrics.HangupFrames.Should().NotBeNull();
    }

    [Fact]
    public void ErrorFrames_ShouldBeCounter()
    {
        AudioStreamMetrics.ErrorFrames.Should().NotBeNull();
    }

    [Fact]
    public void FrameLatency_ShouldRecordHistogram()
    {
        double observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Name == "audio.frame.latency")
                ml.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "audio.frame.latency")
                observed += measurement;
        });
        listener.Start();

        AudioStreamMetrics.FrameLatency.Record(3.14);

        observed.Should().BeGreaterThanOrEqualTo(3.14);
    }
}
