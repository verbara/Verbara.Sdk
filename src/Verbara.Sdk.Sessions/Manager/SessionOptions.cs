using System.ComponentModel.DataAnnotations;

namespace Verbara.Sdk.Sessions.Manager;

public sealed class SessionOptions
{
    /// <summary>
    /// How often the reconciliation sweep runs: the one <c>AddVerbaraSessions</c> registers for the single server, and
    /// the one <c>AddVerbaraSessionsMultiServer</c> registers for every server of the pool. At each run, when a held
    /// call is older than <see cref="DialingTimeout"/>, the sweep checks the held calls once against the channels
    /// Asterisk reports — on a pool, once per server that holds such a call, against that server's channels.
    /// <see cref="Timeout.InfiniteTimeSpan"/> switches the sweep off: no timer is started and nothing is sent; in
    /// configuration it is written <c>-00:00:00.001</c>. Any other value of zero or less fails the host's start with
    /// <see cref="ArgumentOutOfRangeException"/>. Default: 30 seconds.
    /// </summary>
    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The age, measured from when the session was opened, after which the reconciliation sweep checks the
    /// held calls against the channels Asterisk reports. The sweep never ends a call because of its age: a
    /// call whose channels Asterisk still reports is left alone, and a call whose channels it no longer
    /// reports ends as a reload ends it — <c>Completed</c> if it was answered, <c>Failed</c> otherwise, with no
    /// hangup cause. Default: 60 seconds.
    /// </summary>
    public TimeSpan DialingTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Not read: nothing in the SDK reads this value since 2.7.0, and no call is ended because it has rung
    /// for longer, at any setting; <see cref="DialingTimeout"/> is the only age the reconciliation sweep
    /// reads.
    /// <para>Kept, with its default, so that existing configuration keeps binding.</para>
    /// </summary>
    public TimeSpan RingingTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Not a bound: nothing in the SDK reads this value. It does not limit how many ended calls the
    /// session manager or the default in-memory store hold, at any setting;
    /// <see cref="CompletedRetention"/> is the only bound.
    /// <para>
    /// Kept, with its default and its validation, so that existing configuration keeps binding and
    /// validating.
    /// </para>
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxCompletedSessions { get; set; } = 1000;

    /// <summary>
    /// How long an ended call stays held after it ends: reachable through
    /// <see cref="ICallSessionManager.GetById"/>, <see cref="ICallSessionManager.GetByLinkedId"/> and
    /// <see cref="ICallSessionManager.GetRecentCompleted"/>, and through the default in-memory store.
    /// This is the only bound on the ended calls held; <see cref="MaxCompletedSessions"/> bounds
    /// nothing. Default: 10 minutes.
    /// <para>
    /// Nothing runs on a timer. Once this period has passed since a call ended, the session manager and
    /// the default store release it on the next arrival or ending of a call; a process that receives no
    /// further calls releases nothing. A store that provides durability keeps its own retention: the
    /// Redis store expires its keys on its own <c>CompletedRetention</c> option.
    /// </para>
    /// </summary>
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan QueueMetricsWindow { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan SlaThreshold { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Not applied: nothing in the SDK reads this value, and the SDK raises no
    /// <see cref="CallWrapUpEvent"/>. An agent that <see cref="IAgentSessionTracker"/> moves to
    /// <see cref="AgentSessionState.WrapUp"/> when its call ends stays in wrap-up until its next call
    /// connects, whatever this is set to, and the tracker adds nothing to its
    /// <see cref="AgentSession.TotalWrapUpTime"/>.
    /// <para>Kept, with its default, so that existing configuration keeps binding.</para>
    /// </summary>
    public TimeSpan WrapUpDuration { get; set; } = TimeSpan.FromSeconds(30);

    [Required]
    public string[] InboundContextPatterns { get; set; } = ["from-trunk", "from-pstn", "from-external"];

    [Required]
    public string[] OutboundContextPatterns { get; set; } = ["from-internal", "from-sip", "from-users"];
}
