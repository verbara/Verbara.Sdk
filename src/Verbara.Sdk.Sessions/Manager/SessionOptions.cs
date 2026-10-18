using System.ComponentModel.DataAnnotations;
using Verbara.Sdk.Sessions.Internal;

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
    /// How long an agent's after-call wrap-up lasts, measured by the session manager on its own clock: Asterisk
    /// reports no end of a queue member's wrap-up over AMI. Default: 30 seconds.
    /// <para>
    /// <b>What opens a wrap-up.</b> For a call that a queue connected to a member, the member's
    /// <c>AgentComplete</c> (the member hung up, or transferred the caller away), keyed on the member that frame
    /// names, even when the SDK did not see the caller join the queue it names (a call answered while the AMI
    /// connection was down, for instance). For a call outside any queue that an agent known by name took, and for
    /// which no such <c>AgentComplete</c> was reported, the end of the call, after its
    /// <see cref="CallEndedEvent"/>; after a transfer it therefore opens when the whole call ends. A wrap-up opens
    /// only when that agent or member has no other connected call left. A call no agent or member took, and a call
    /// whose end the SDK did not observe (one a state reload found gone), open none.
    /// </para>
    /// <para>
    /// <b>What closes it.</b> Whichever comes first: this duration passes; the same agent or member is connected to
    /// its next call (the close is raised before that call's <see cref="CallConnectedEvent"/>); or another of its
    /// legs ends, which closes the open wrap-up and opens a new one. Each close raises exactly one
    /// <see cref="CallWrapUpEvent"/> on <see cref="ICallSessionManager.Events"/> carrying the time measured, and
    /// <see cref="IAgentSessionTracker"/> adds that time to <see cref="AgentSession.TotalWrapUpTime"/> and moves
    /// the agent from <see cref="AgentSessionState.WrapUp"/> to <see cref="AgentSessionState.Idle"/>.
    /// </para>
    /// <para>
    /// <b>Accepted values.</b> From zero to 4 294 967 294 ms (<c>49.17:02:47.294</c>), the largest due time the
    /// system timer accepts. Zero closes each wrap-up the instant it opens, with a <see cref="CallWrapUpEvent"/> of
    /// zero duration: it is the way to switch wrap-up off. There is no value that never ends a wrap-up: a negative
    /// value, <see cref="Timeout.InfiniteTimeSpan"/> included, and a longer one fail the host's start with an
    /// <c>OptionsValidationException</c> naming <c>WrapUpDuration</c>.
    /// </para>
    /// <para>
    /// <b>Set it to Asterisk's <c>wrapuptime</c>.</b> The SDK cannot read the <c>wrapuptime</c> of
    /// <c>queues.conf</c> (or of a realtime queue), so it does not follow it: when the two differ, the wrap-up the
    /// SDK reports and the one app_queue enforces differ too. Without an <see cref="IWrapUpDurationProvider"/>, one
    /// value applies to every queue.
    /// </para>
    /// <para>
    /// <b>With an <see cref="IWrapUpDurationProvider"/>.</b> A host whose queues differ registers a provider, and
    /// the session manager asks it for the length of each wrap-up a queue member's <c>AgentComplete</c> opens, by
    /// server and queue. This value is then the fallback: it applies when the provider has no usable answer (it
    /// returns <see langword="null"/>, a negative duration or one above 4 294 967 294 ms, or throws), and to every
    /// wrap-up opened by the end of a call, which never asks the provider. A provider answer longer than
    /// <see cref="CompletedRetention"/> is used, with the same cost as such a value here: a subscriber that looks
    /// the session up by its <c>SessionId</c> when the <see cref="CallWrapUpEvent"/> arrives may no longer find it.
    /// </para>
    /// <para>
    /// <b>Above <see cref="CompletedRetention"/>.</b> A value longer than <see cref="CompletedRetention"/> is
    /// accepted, but the call may already have been released when its <see cref="CallWrapUpEvent"/> is raised, so
    /// a subscriber that looks the call up by its <c>SessionId</c> (a store, for instance) may no longer find it.
    /// </para>
    /// </summary>
    [WrapUpDurationRange]
    public TimeSpan WrapUpDuration { get; set; } = TimeSpan.FromSeconds(30);

    [Required]
    public string[] InboundContextPatterns { get; set; } = ["from-trunk", "from-pstn", "from-external"];

    [Required]
    public string[] OutboundContextPatterns { get; set; } = ["from-internal", "from-sip", "from-users"];
}
