using System.Diagnostics.Metrics;
using Verbara.Sdk.Sessions.Diagnostics;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// Sums what is added to <c>sessions.events.subscriber_faults</c> while it listens. The counter is process-wide, so a
/// class that reads it runs in <see cref="SessionSubscriberFaultsGroup"/>.
/// </summary>
internal sealed class SubscriberFaultCounter : IDisposable
{
    private readonly MeterListener _listener = new();
    private long _total;

    public SubscriberFaultCounter()
    {
        // Read before the listener starts: a first read of SessionMetrics runs its static initializer, which publishes
        // the instrument while the field is still null, so a comparison made from inside that publication never
        // matches and the listener counts nothing.
        var counter = SessionMetrics.SubscriberFaults;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument, counter))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _total, value));
        _listener.Start();
    }

    public long Total => Interlocked.Read(ref _total);

    public void Dispose() => _listener.Dispose();
}
