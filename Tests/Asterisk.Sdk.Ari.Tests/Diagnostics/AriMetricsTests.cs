using System.Diagnostics.Metrics;
using Asterisk.Sdk.Ari.Diagnostics;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Diagnostics;

public sealed class AriMetricsTests
{
    [Fact]
    public void Meter_ShouldHaveCorrectName()
    {
        AriMetrics.Meter.Name.Should().Be("Asterisk.Sdk.Ari");
        AriMetrics.Meter.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void EventsReceived_ShouldIncrementCounter()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Name == "ari.events.received")
                ml.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "ari.events.received")
                observed += measurement;
        });
        listener.Start();

        AriMetrics.EventsReceived.Add(1);
        listener.RecordObservableInstruments();

        observed.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void EventsDropped_ShouldIncrementCounter()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Name == "ari.events.dropped")
                ml.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "ari.events.dropped")
                observed += measurement;
        });
        listener.Start();

        AriMetrics.EventsDropped.Add(2);
        listener.RecordObservableInstruments();

        observed.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void EventsDispatched_ShouldIncrementCounter()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Name == "ari.events.dispatched")
                ml.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "ari.events.dispatched")
                observed += measurement;
        });
        listener.Start();

        AriMetrics.EventsDispatched.Add(10);
        listener.RecordObservableInstruments();

        observed.Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public void RestRequestsSent_ShouldIncrementCounter()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Name == "ari.rest.requests.sent")
                ml.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "ari.rest.requests.sent")
                observed += measurement;
        });
        listener.Start();

        AriMetrics.RestRequestsSent.Add(3);
        listener.RecordObservableInstruments();

        observed.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Reconnections_ShouldIncrementCounter()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Name == "ari.reconnections")
                ml.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "ari.reconnections")
                observed += measurement;
        });
        listener.Start();

        AriMetrics.Reconnections.Add(1);
        listener.RecordObservableInstruments();

        observed.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void RestRoundtripMs_ShouldRecordHistogram()
    {
        double observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Name == "ari.rest.roundtrip")
                ml.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "ari.rest.roundtrip")
                observed += measurement;
        });
        listener.Start();

        AriMetrics.RestRoundtripMs.Record(15.3);
        listener.RecordObservableInstruments();

        observed.Should().BeGreaterThanOrEqualTo(15.3);
    }

    [Fact]
    public void EventDispatchMs_ShouldRecordHistogram()
    {
        double observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, ml) =>
        {
            if (instrument.Name == "ari.event.dispatch")
                ml.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "ari.event.dispatch")
                observed += measurement;
        });
        listener.Start();

        AriMetrics.EventDispatchMs.Record(0.5);
        listener.RecordObservableInstruments();

        observed.Should().BeGreaterThanOrEqualTo(0.5);
    }
}
