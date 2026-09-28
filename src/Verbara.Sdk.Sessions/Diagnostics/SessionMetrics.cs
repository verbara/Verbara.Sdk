using System.Diagnostics.Metrics;

namespace Verbara.Sdk.Sessions.Diagnostics;

public static class SessionMetrics
{
    public static readonly Meter Meter = new("Verbara.Sdk.Sessions", "1.0.0");

    public static readonly Counter<long> SessionsCreated =
        Meter.CreateCounter<long>("sessions.created", "sessions", "Total sessions created");
    public static readonly Counter<long> SessionsCompleted =
        Meter.CreateCounter<long>("sessions.completed", "sessions", "Total sessions completed");
    public static readonly Counter<long> SessionsFailed =
        Meter.CreateCounter<long>("sessions.failed", "sessions", "Total sessions failed");
    public static readonly Counter<long> SessionsTimedOut =
        Meter.CreateCounter<long>("sessions.timed_out", "sessions", "Total sessions timed out");
    public static readonly Counter<long> SessionsOrphaned =
        Meter.CreateCounter<long>("sessions.orphaned", "sessions", "Orphaned sessions detected");

    public static readonly Histogram<double> WaitTimeMs =
        Meter.CreateHistogram<double>("sessions.wait_time", "ms", "Queue wait time");
    public static readonly Histogram<double> TalkTimeMs =
        Meter.CreateHistogram<double>("sessions.talk_time", "ms", "Talk time");
    public static readonly Histogram<double> HoldTimeMs =
        Meter.CreateHistogram<double>("sessions.hold_time", "ms", "Hold time");
    public static readonly Histogram<double> DurationMs =
        Meter.CreateHistogram<double>("sessions.duration", "ms", "Total session duration");

    // Resident-count gauges. They are not on Meter above: each CallSessionManager publishes its own, on
    // the meter CreateResidencyMeter gives it, because each reads one manager's state and has to be
    // withdrawn when that manager is disposed.
    //   sessions.active    calls the manager holds that have not ended
    //   sessions.retained  ended calls the manager still holds

    /// <summary>
    /// Creates the meter a <c>CallSessionManager</c> publishes its resident counts on: a meter of its own
    /// carrying this meter's name and version — so a listener or an exporter subscribed to
    /// <c>Verbara.Sdk.Sessions</c> by name collects them, and no meter name is added — with two gauges,
    /// <c>sessions.active</c> and <c>sessions.retained</c>, read from <paramref name="active"/> and
    /// <paramref name="retained"/> whenever a listener collects. The caller owns the meter and disposes
    /// it, which withdraws both gauges. <c>VerbaraServer</c> does the same for the Live gauges.
    /// </summary>
    internal static Meter CreateResidencyMeter(Func<long> active, Func<long> retained)
    {
        var meter = new Meter(Meter.Name, Meter.Version);
        meter.CreateObservableGauge("sessions.active", active, "sessions",
            "Calls in progress: sessions held that have not ended");
        meter.CreateObservableGauge("sessions.retained", retained, "sessions",
            "Ended sessions still held");
        return meter;
    }
}
