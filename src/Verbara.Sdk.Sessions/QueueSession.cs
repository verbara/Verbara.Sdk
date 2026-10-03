namespace Verbara.Sdk.Sessions;

/// <summary>
/// One queue's calls, as the queue tracker counted them: counters over a rolling metrics window
/// (<see cref="Manager.SessionOptions.QueueMetricsWindow"/>, from <see cref="WindowStart"/>), and the callers waiting now.
/// Each queue visit (a caller's join, until the queue connects it or it leaves) is counted at the moment Asterisk
/// reports what happened to it, in the window current then.
/// </summary>
public sealed class QueueSession
{
    /// <summary>The queue's name, as Asterisk names it.</summary>
    public string QueueName { get; }

    /// <summary>When the current metrics window started; the counters below count from it.</summary>
    public DateTimeOffset WindowStart { get; internal set; }

    /// <summary>
    /// Queue visits that started in the window: every caller's join counts one, and a caller put back into the queue
    /// counts again. The denominator of <see cref="ServiceLevel"/>, <see cref="AbandonRate"/> and
    /// <see cref="AnswerRate"/>.
    /// </summary>
    public int CallsOffered { get; internal set; }

    /// <summary>Queue visits the queue connected to a member, counted when Asterisk reports the connection.</summary>
    public int CallsAnswered { get; internal set; }

    /// <summary>
    /// Queue visits Asterisk counts abandoned, as app_queue's own <c>Abandoned</c>: the caller hung up while waiting,
    /// or the queue let it go without a connection (its timeout, the queue emptying, a withdrawal, a redirect). Counted
    /// when app_queue reports the abandon (<c>QueueCallerAbandon</c>), just before the caller's leave. It includes
    /// <see cref="CallsTimedOut"/>, and excludes a caller that left by pressing the queue's exit key, which Asterisk
    /// counts neither answered nor abandoned.
    /// <para>
    /// A leave with no abandon report counts as a key exit only while the SDK lost no event since the visit opened: no
    /// reconnect, and no event dropped by the AMI connection's full event buffer (readable only over the SDK's own
    /// <c>AmiConnection</c>; over another connection only reconnects count). Otherwise the visit is counted abandoned at
    /// the leave. A visit whose leave the SDK never received (it fell in an outage) is counted abandoned when the
    /// reload's completed queue snapshot no longer lists the caller, or at the caller's next join or hang-up, whichever
    /// comes first; an answer during an outage cannot be observed, so such a visit counts abandoned.
    /// </para>
    /// <para>
    /// Over an <see cref="Manager.ICallSessionManager"/> other than the SDK's own, Asterisk's reports do not reach the
    /// tracker: every visit that ends without a connection, key exits included, is counted abandoned when the caller
    /// joins a queue again or hangs up.
    /// </para>
    /// </summary>
    public int CallsAbandoned { get; internal set; }

    /// <summary>
    /// The part of <see cref="CallsAbandoned"/> that app_queue's own timeout ended (<c>Queue()</c>'s timeout argument
    /// or its <c>n</c> option): <c>QUEUESTATUS</c> was <c>TIMEOUT</c>. Counted once per visit, when app_queue sets it.
    /// <see cref="CallsAbandoned"/> minus this is the callers that hung up, or that the queue let go for another
    /// reason.
    /// <para>
    /// Read from the <c>VarSet</c> of <c>QUEUESTATUS</c>, which Asterisk sends only to an AMI user whose read classes
    /// include <c>dialplan</c>. Without it this stays 0, and a timed-out visit is still counted in
    /// <see cref="CallsAbandoned"/>. A timeout while the AMI connection is down is not counted here.
    /// </para>
    /// <para>
    /// The abandon is counted at app_queue's abandon report and the timeout a moment later: when a metrics window ends
    /// between the two, they land in different windows, so in one window this can exceed <see cref="CallsAbandoned"/>
    /// by the visits that straddled its start.
    /// </para>
    /// </summary>
    public int CallsTimedOut { get; internal set; }

    /// <summary>The sum of the answered visits' waits, each from the visit's start to its connection.</summary>
    public TimeSpan TotalWaitTime { get; internal set; }

    /// <summary>The longest answered visit's wait in the window.</summary>
    public TimeSpan MaxWaitTime { get; internal set; }

    /// <summary>The shortest answered visit's wait in the window; <see cref="TimeSpan.MaxValue"/> before the first.</summary>
    public TimeSpan MinWaitTime { get; internal set; } = TimeSpan.MaxValue;

    /// <summary>Answered visits whose wait was within <see cref="Manager.SessionOptions.SlaThreshold"/>.</summary>
    public int CallsWithinSla { get; internal set; }

    /// <summary>
    /// Callers waiting in the queue now: visits that started and that Asterisk has not yet reported leaving the queue.
    /// The leave ends the wait, at every exit: a connection, a hang-up, a timeout, an emptied queue, a withdrawal, a
    /// redirect or a key. A caller whose leave fell in an AMI outage stops counting when the reload's completed queue
    /// snapshot no longer lists it. Not reset by a new window.
    /// </summary>
    public int CallsWaiting { get; internal set; }

    /// <summary>
    /// The percentage of offered visits answered within <see cref="Manager.SessionOptions.SlaThreshold"/>:
    /// <see cref="CallsWithinSla"/> over <see cref="CallsOffered"/>; 100 when none was offered.
    /// <para>
    /// Not Asterisk's: app_queue's <c>ServiceLevelPerf</c> divides the calls answered within the threshold by the
    /// answered calls, and its <c>ServiceLevelPerf2</c> divides the calls answered or abandoned within the threshold by
    /// the answered and abandoned calls. On one queue the three can read 0 %, 0 % and 100 %. Compare this one with
    /// another reading of the same formula, not with app_queue's.
    /// </para>
    /// </summary>
    public double ServiceLevel => CallsOffered > 0
        ? (double)CallsWithinSla / CallsOffered * 100.0
        : 100.0;

    /// <summary>The answered visits' mean wait: <see cref="TotalWaitTime"/> over <see cref="CallsAnswered"/>.</summary>
    public TimeSpan AvgWaitTime => CallsAnswered > 0
        ? TotalWaitTime / CallsAnswered
        : TimeSpan.Zero;

    /// <summary>
    /// The percentage of offered visits abandoned: <see cref="CallsAbandoned"/> over <see cref="CallsOffered"/>; 0 when
    /// none was offered. Timeouts are in it, as abandons; key exits are offered and not abandoned.
    /// </summary>
    public double AbandonRate => CallsOffered > 0
        ? (double)CallsAbandoned / CallsOffered * 100.0
        : 0.0;

    /// <summary>The percentage of offered visits answered: <see cref="CallsAnswered"/> over <see cref="CallsOffered"/>.</summary>
    public double AnswerRate => CallsOffered > 0
        ? (double)CallsAnswered / CallsOffered * 100.0
        : 0.0;

    internal readonly Lock SyncRoot = new();

    /// <summary>A queue with no calls counted yet, whose first window starts now.</summary>
    public QueueSession(string queueName)
    {
        QueueName = queueName;
        WindowStart = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Resets window counters for a new metrics window.
    /// CallsWaiting is NOT reset — it tracks live state.
    /// </summary>
    internal void ResetWindow()
    {
        WindowStart = DateTimeOffset.UtcNow;
        CallsOffered = 0;
        CallsAnswered = 0;
        CallsAbandoned = 0;
        CallsTimedOut = 0;
        TotalWaitTime = TimeSpan.Zero;
        MaxWaitTime = TimeSpan.Zero;
        MinWaitTime = TimeSpan.MaxValue;
        CallsWithinSla = 0;
    }
}
