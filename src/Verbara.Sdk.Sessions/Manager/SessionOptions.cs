using System.ComponentModel.DataAnnotations;

namespace Verbara.Sdk.Sessions.Manager;

public sealed class SessionOptions
{
    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan DialingTimeout { get; set; } = TimeSpan.FromSeconds(60);
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
