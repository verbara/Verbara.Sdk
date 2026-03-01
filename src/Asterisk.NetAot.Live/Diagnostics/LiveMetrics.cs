using System.Diagnostics.Metrics;

namespace Asterisk.NetAot.Live.Diagnostics;

/// <summary>
/// Live API metrics exposed via System.Diagnostics.Metrics.
/// Observable gauges are registered by <see cref="Server.AsteriskServer"/> at startup.
/// <para>
/// Usage: <c>dotnet-counters monitor --process-id &lt;pid&gt; Asterisk.NetAot.Live</c>
/// </para>
/// </summary>
public static class LiveMetrics
{
    public static readonly Meter Meter = new("Asterisk.NetAot.Live", "1.0.0");

    // Observable gauges are registered at runtime by AsteriskServer
    // because they need references to the manager instances:
    //   live.channels.active
    //   live.queues.count
    //   live.agents.total
    //   live.agents.available
    //   live.agents.on_call
    //   live.agents.paused
}
