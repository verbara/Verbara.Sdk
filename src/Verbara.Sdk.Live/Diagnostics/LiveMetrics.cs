using System.Diagnostics.Metrics;

namespace Verbara.Sdk.Live.Diagnostics;

/// <summary>
/// Live API metrics exposed via System.Diagnostics.Metrics.
/// Observable gauges are registered by <see cref="Server.VerbaraServer"/> at startup.
/// <para>
/// Usage: <c>dotnet-counters monitor --process-id &lt;pid&gt; Verbara.Sdk.Live</c>
/// </para>
/// </summary>
public static class LiveMetrics
{
    public static readonly Meter Meter = new("Verbara.Sdk.Live", "1.0.0");

    // Observable gauges are registered at runtime by VerbaraServer
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

    /// <summary>Total Status requests refused by Asterisk, tagged with the reader and the pool id when known.</summary>
    public static readonly Counter<long> StatusRefused =
        Meter.CreateCounter<long>("live.status.refused", "refusals", "Total Status requests refused by Asterisk");

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

    /// <summary>
    /// Channel subscriber failures: a handler of a <c>ChannelManager</c> event that threw, one per failure. Each is also
    /// logged at Warning; the later subscribers and the table's work go on.
    /// </summary>
    internal static readonly Counter<long> ChannelSubscriberFaults =
        Meter.CreateCounter<long>("live.channels.subscriber_faults", "faults",
            "Channel subscriber failures (a channel event handler threw); delivery goes on");

    /// <summary>
    /// Queue subscriber failures: a handler of a <c>QueueManager</c> event that threw, one per failure. Each is also
    /// logged at Warning; the later subscribers and the table's work go on.
    /// </summary>
    internal static readonly Counter<long> QueueSubscriberFaults =
        Meter.CreateCounter<long>("live.queues.subscriber_faults", "faults",
            "Queue subscriber failures (a queue event handler threw); delivery goes on");

    /// <summary>
    /// Conference subscriber failures: a handler of a <c>ConferenceManager</c> event that threw, one per failure. Each
    /// is also logged at Warning; the later subscribers and the table's work go on. The instrument keeps its published
    /// name.
    /// </summary>
    internal static readonly Counter<long> ConferenceSubscriberFaults =
        Meter.CreateCounter<long>("live.meetme.subscriber_faults", "faults",
            "Conference subscriber failures (a conference event handler threw); delivery goes on");

    /// <summary>
    /// Agent subscriber failures: a handler of an <c>AgentManager</c> event that threw, one per failure. Each is also
    /// logged at Warning; the later subscribers, the events that follow and the table's work go on.
    /// </summary>
    internal static readonly Counter<long> AgentSubscriberFaults =
        Meter.CreateCounter<long>("live.agents.subscriber_faults", "faults",
            "Agent subscriber failures (an agent event handler threw); delivery goes on");

    /// <summary>
    /// Bridge subscriber failures: a handler of a <c>BridgeManager</c> event that threw, one per failure. Each is also
    /// logged at Warning; the later subscribers and the table's work go on.
    /// </summary>
    internal static readonly Counter<long> BridgeSubscriberFaults =
        Meter.CreateCounter<long>("live.bridges.subscriber_faults", "faults",
            "Bridge subscriber failures (a bridge event handler threw); delivery goes on");

    /// <summary>
    /// Failures of subscribers of the server's own notices: a handler of <c>VerbaraServer.UnreconciledLossDelivered</c>
    /// that threw, one per failure. Each is also logged at Warning; the later subscribers are still told.
    /// </summary>
    internal static readonly Counter<long> ServerSubscriberFaults =
        Meter.CreateCounter<long>("live.server.subscriber_faults", "faults",
            "Failures of subscribers of the server's own notices (a notice handler threw); the others are still told");

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
