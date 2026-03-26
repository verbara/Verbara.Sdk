using System.Diagnostics.Metrics;
using Asterisk.Sdk.Ari.Diagnostics;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Diagnostics;

public sealed class AudioStreamMetricsTests
{
    [Fact]
    public void Meter_ShouldHaveCorrectName()
    {
        AudioStreamMetrics.Meter.Name.Should().Be("Asterisk.Sdk.Ari.Audio");
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
    public void BufferUnderruns_ShouldBeCounter()
    {
        AudioStreamMetrics.BufferUnderruns.Should().NotBeNull();
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
