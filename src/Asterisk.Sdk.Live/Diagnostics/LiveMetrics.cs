using System.Diagnostics.Metrics;

namespace Asterisk.Sdk.Live.Diagnostics;

/// <summary>
/// Live API metrics exposed via System.Diagnostics.Metrics.
/// Observable gauges are registered by <see cref="Server.AsteriskServer"/> at startup.
/// <para>
/// Usage: <c>dotnet-counters monitor --process-id &lt;pid&gt; Asterisk.Sdk.Live</c>
/// </para>
/// </summary>
public static class LiveMetrics
{
    public static readonly Meter Meter = new("Asterisk.Sdk.Live", "1.0.0");

    // Observable gauges are registered at runtime by AsteriskServer
    // because they need references to the manager instances:
    //   live.channels.active
    //   live.queues.count
    //   live.agents.total
    //   live.agents.available
    //   live.agents.on_call
    //   live.agents.paused
    //   live.agents.total_hold_secs  (aggregate hold time across all agents)
    //   live.agents.total_talk_secs  (aggregate talk time across all agents)

    // --- Counters ---

    /// <summary>Total channels created.</summary>
    public static readonly Counter<long> ChannelsCreated =
        Meter.CreateCounter<long>("live.channels.created", "channels",
            "Total channels created");

    /// <summary>Total channels destroyed.</summary>
    public static readonly Counter<long> ChannelsDestroyed =
        Meter.CreateCounter<long>("live.channels.destroyed", "channels",
            "Total channels destroyed");

    /// <summary>Total calls joined to queues.</summary>
    public static readonly Counter<long> QueueCallsJoined =
        Meter.CreateCounter<long>("live.queue.calls.joined", "calls",
            "Total calls joined to queues");

    /// <summary>Total calls that left queues.</summary>
    public static readonly Counter<long> QueueCallsLeft =
        Meter.CreateCounter<long>("live.queue.calls.left", "calls",
            "Total calls that left queues");

    /// <summary>Total agent state changes.</summary>
    public static readonly Counter<long> AgentStateChanges =
        Meter.CreateCounter<long>("live.agents.state_changes", "changes",
            "Total agent state changes");

    // --- Histograms ---

    /// <summary>Time a caller waited in queue before being answered.</summary>
    public static readonly Histogram<double> QueueWaitTimeMs =
        Meter.CreateHistogram<double>("live.queue.wait_time", "ms",
            "Time a caller waited in queue before being answered");

    // --- Bridge counters ---

    /// <summary>Total bridges created.</summary>
    public static readonly Counter<long> BridgesCreated =
        Meter.CreateCounter<long>("live.bridges.created", "bridges", "Total bridges created");

    /// <summary>Total bridges destroyed.</summary>
    public static readonly Counter<long> BridgesDestroyed =
        Meter.CreateCounter<long>("live.bridges.destroyed", "bridges", "Total bridges destroyed");
}
