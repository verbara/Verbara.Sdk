using System.Diagnostics.Metrics;

namespace Verbara.Sdk.Activities.Internal;

/// <summary>
/// The activities' instruments, on the meter <c>Verbara.Sdk.Activities</c> (listed in
/// <c>VerbaraTelemetry.MeterNames</c>, so a host that registers the SDK's meters sees them).
/// </summary>
internal static class ActivityMetrics
{
    /// <summary>The meter's name, as <c>VerbaraTelemetry.MeterNames</c> lists it.</summary>
    internal const string MeterName = "Verbara.Sdk.Activities";

    /// <summary>The tag naming the concrete activity type a measurement belongs to.</summary>
    internal const string ActivityTypeTag = "activity.type";

    internal static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>
    /// Status subscriber failures: a subscriber of an activity's <c>StatusChanges</c> that threw on a status, on the
    /// replay of the current status when it subscribed, or on the completion, one per failure, tagged
    /// <c>activity.type</c> with the activity's type name. Nothing is logged: the activity bases hold no logger. The
    /// other subscribers, the activity's own work and the call that raised the status go on.
    /// </summary>
    internal static readonly Counter<long> StatusSubscriberFaults =
        Meter.CreateCounter<long>("activities.status.subscriber_faults", "faults",
            "Activity status subscriber failures (a StatusChanges subscriber threw); delivery goes on");
}
