using System.Diagnostics.Metrics;

namespace Asterisk.Sdk.Agi.Diagnostics;

/// <summary>
/// AGI server metrics exposed via System.Diagnostics.Metrics.
/// Compatible with OpenTelemetry, Prometheus, dotnet-counters, and any .NET metrics consumer.
/// <para>
/// Usage: <c>dotnet-counters monitor --process-id &lt;pid&gt; Asterisk.Sdk.Agi</c>
/// </para>
/// </summary>
public static class AgiMetrics
{
    public static readonly Meter Meter = new("Asterisk.Sdk.Agi", "1.0.0");

    /// <summary>Total AGI connections accepted.</summary>
    public static readonly Counter<long> ConnectionsAccepted =
        Meter.CreateCounter<long>("agi.connections.accepted", "connections",
            "Total AGI connections accepted");

    /// <summary>Total AGI scripts executed successfully.</summary>
    public static readonly Counter<long> ScriptsExecuted =
        Meter.CreateCounter<long>("agi.scripts.executed", "scripts",
            "Total AGI scripts executed successfully");

    /// <summary>Total AGI scripts that failed with errors.</summary>
    public static readonly Counter<long> ScriptsFailed =
        Meter.CreateCounter<long>("agi.scripts.failed", "scripts",
            "Total AGI scripts that failed with errors");

    /// <summary>AGI requests where no script was mapped.</summary>
    public static readonly Counter<long> ScriptsNotFound =
        Meter.CreateCounter<long>("agi.scripts.not_found", "scripts",
            "AGI requests where no script was mapped");

    /// <summary>AGI scripts interrupted by channel hangup.</summary>
    public static readonly Counter<long> Hangups =
        Meter.CreateCounter<long>("agi.hangups", "hangups",
            "AGI scripts interrupted by channel hangup");

    /// <summary>AGI script execution duration in milliseconds.</summary>
    public static readonly Histogram<double> ScriptDurationMs =
        Meter.CreateHistogram<double>("agi.script.duration", "ms",
            "AGI script execution duration");
}
