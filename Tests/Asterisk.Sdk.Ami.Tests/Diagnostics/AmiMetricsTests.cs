using System.Diagnostics.Metrics;
using Asterisk.Sdk.Ami.Diagnostics;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Diagnostics;

public class AmiMetricsTests
{
    [Fact]
    public void Meter_ShouldHaveCorrectName()
    {
        AmiMetrics.Meter.Name.Should().Be("Asterisk.Sdk.Ami");
        AmiMetrics.Meter.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void EventsReceived_ShouldIncrementCounter()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "ami.events.received")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "ami.events.received")
                observed += measurement;
        });
        listener.Start();

        AmiMetrics.EventsReceived.Add(1);
        listener.RecordObservableInstruments();

        observed.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void EventsDropped_ShouldIncrementCounter()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "ami.events.dropped")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "ami.events.dropped")
                observed += measurement;
        });
        listener.Start();

        AmiMetrics.EventsDropped.Add(3);
        listener.RecordObservableInstruments();

        observed.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void ActionsSent_ShouldIncrementCounter()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "ami.actions.sent")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "ami.actions.sent")
                observed += measurement;
        });
        listener.Start();

        AmiMetrics.ActionsSent.Add(5);
        listener.RecordObservableInstruments();

        observed.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void ResponsesReceived_ShouldIncrementCounter()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "ami.responses.received")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "ami.responses.received")
                observed += measurement;
        });
        listener.Start();

        AmiMetrics.ResponsesReceived.Add(2);
        listener.RecordObservableInstruments();

        observed.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void ActionRoundtripMs_ShouldRecordHistogram()
    {
        double observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "ami.action.roundtrip")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "ami.action.roundtrip")
                observed += measurement;
        });
        listener.Start();

        AmiMetrics.ActionRoundtripMs.Record(42.5);
        listener.RecordObservableInstruments();

        observed.Should().BeGreaterThanOrEqualTo(42.5);
    }
}
