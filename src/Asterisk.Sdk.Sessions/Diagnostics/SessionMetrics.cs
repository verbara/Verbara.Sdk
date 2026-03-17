using System.Diagnostics.Metrics;

namespace Asterisk.Sdk.Sessions.Diagnostics;

public static class SessionMetrics
{
    public static readonly Meter Meter = new("Asterisk.Sdk.Sessions", "1.0.0");

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
}
