using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Sessions.Manager;

/// <summary>
/// Counts each queue's calls from the session manager's domain events and, over a manager that is an
/// <see cref="IQueueVisitSource"/> (the SDK's own <see cref="CallSessionManager"/>), from Asterisk's reports on each
/// visit: the leave, the abandon report and the timeout.
/// <para>
/// A visit is <b>open</b> from its join (counted offered and waiting), <b>left</b> once Asterisk reports the caller
/// leaving the queue (no longer waiting), and <b>gone</b> once the queue connects it. Every transition happens under
/// one lock, and each counter moves at most once per visit, whatever order the domain events and the visit signals
/// arrive in. A visit signal applies only to the visit whose start it names.
/// </para>
/// <list type="bullet">
/// <item>The abandon is counted at app_queue's abandon report, which it sends just before the leave for every visit it
/// counts abandoned (a hang-up, its timeout, an emptied queue, a withdrawal, a redirect).</item>
/// <item>The leave ends the wait. A leave with no abandon report is a key exit, counted neither answered nor abandoned,
/// but only while the SDK lost no event since the visit opened. Otherwise the report may have been lost: the visit is
/// counted abandoned at the call's next join or at its end, unless the queue connects it first (the connection follows
/// the leave). A visit closed because a completed queue snapshot no longer lists the caller is counted abandoned
/// there: its leave was never received, and no connection can follow.</item>
/// <item>A timeout counts <see cref="QueueSession.CallsTimedOut"/> once; its abandon was counted at the report. A
/// timeout on a visit still open (its leave was missed) also closes it abandoned.</item>
/// <item>A connect after the leave (every answered visit is join, leave, connect) counts answered, with the wait from
/// the visit's start, and does not end the wait again.</item>
/// <item>A visit still open at the call's next join or at its end (its leave was never received) closes abandoned
/// there.</item>
/// </list>
/// Over any other <see cref="ICallSessionManager"/> no visit signal arrives: a visit closes only at its connect, at the
/// call's next join or at its end, abandoned unless connected, and <see cref="QueueSession.CallsTimedOut"/> stays 0.
/// </summary>
internal sealed class QueueSessionTracker : IQueueSessionTracker, IDisposable
{
    private readonly ConcurrentDictionary<string, QueueSession> _queues = new();
    private readonly Dictionary<string, Visit> _visits = []; // sessionId → the call's current queue visit
    private readonly Lock _waitingLock = new();
    private readonly TimeSpan _metricsWindow;
    private readonly TimeSpan _slaThreshold;
    private readonly IDisposable _subscription;
    private readonly IQueueVisitSource? _visitSource;

    public QueueSessionTracker(ICallSessionManager manager, IOptions<SessionOptions> options)
    {
        _metricsWindow = options.Value.QueueMetricsWindow;
        _slaThreshold = options.Value.SlaThreshold;
        _subscription = manager.Events.Subscribe(new EventObserver(this));
        _visitSource = manager as IQueueVisitSource;
        if (_visitSource is not null)
        {
            _visitSource.VisitLeft += HandleVisitLeft;
            _visitSource.VisitAbandonReported += HandleAbandonReported;
            _visitSource.VisitTimedOut += HandleTimedOut;
        }
    }

    public QueueSession? GetByQueueName(string queueName) =>
        _queues.GetValueOrDefault(queueName);

    public IEnumerable<QueueSession> ActiveQueues => _queues.Values;

    private QueueSession GetOrCreateQueue(string queueName) =>
        _queues.GetOrAdd(queueName, static name => new QueueSession(name));

    private void CheckWindowExpiry(QueueSession queue)
    {
        if (DateTimeOffset.UtcNow - queue.WindowStart > _metricsWindow)
            queue.ResetWindow();
    }

    /// <summary>
    /// Moves <paramref name="queueName"/>'s counters by the given amounts, in the metrics window current now, and
    /// records <paramref name="answeredWait"/> as an answered visit's wait when given.
    /// </summary>
    private void Count(string queueName, int offered = 0, int answered = 0, int abandoned = 0, int timedOut = 0,
        int waiting = 0, TimeSpan? answeredWait = null)
    {
        var queue = GetOrCreateQueue(queueName);
        lock (queue.SyncRoot)
        {
            CheckWindowExpiry(queue);
            queue.CallsOffered += offered;
            queue.CallsAnswered += answered;
            queue.CallsAbandoned += abandoned;
            queue.CallsTimedOut += timedOut;
            queue.CallsWaiting += waiting;

            if (answeredWait is { } wait)
            {
                queue.TotalWaitTime += wait;
                if (wait > queue.MaxWaitTime)
                    queue.MaxWaitTime = wait;
                if (wait < queue.MinWaitTime)
                    queue.MinWaitTime = wait;
                if (wait <= _slaThreshold)
                    queue.CallsWithinSla++;
            }
        }
    }

    private void HandleCallQueued(CallQueuedEvent evt)
    {
        // A visit the call still held open in a queue (its leave never received: an outage, or a manager that sends
        // no visit signals) left that queue without a connection: it closes there as abandoned. A visit already left
        // closed at its leave, and counts nothing more here.
        Visit? previous;
        lock (_waitingLock)
        {
            _visits.Remove(evt.SessionId, out previous);
            _visits[evt.SessionId] = new Visit(evt.QueueName, evt.Timestamp);
        }

        if (previous is { State: VisitState.Open })
            Count(previous.QueueName, abandoned: previous.AbandonCounted ? 0 : 1, waiting: -1);
        else if (previous is { State: VisitState.Left, AbandonPending: true })
            Count(previous.QueueName, abandoned: 1);

        Count(evt.QueueName, offered: 1, waiting: 1);
    }

    private void HandleCallConnected(CallConnectedEvent evt)
    {
        if (evt.QueueName is null) return;

        Visit? visit;
        VisitState was;
        lock (_waitingLock)
        {
            visit = _visits.GetValueOrDefault(evt.SessionId);
            was = visit?.State ?? VisitState.Gone;
            if (visit is not null)
            {
                visit.State = VisitState.Gone;

                // The connection that follows a leave whose abandon report may have been lost: no abandon was lost.
                visit.AbandonPending = false;
            }
        }

        // The visit's wait runs from its start to this connection, whether the leave that precedes every answer was
        // seen or not. When no visit was observed (the tracker was built after its join, or the session was
        // reconstructed), the event's own wait since the call was created stands in for it, so the answer still
        // carries a wait.
        if (visit is null || was == VisitState.Gone)
        {
            Count(evt.QueueName, answered: 1, answeredWait: evt.WaitTime);
            return;
        }

        Count(evt.QueueName, answered: 1, waiting: was == VisitState.Open ? -1 : 0, answeredWait: evt.Timestamp - visit.OpenedAt);
    }

    private void HandleCallEnded(CallEndedEvent evt)
    {
        // Every entry goes at the call's end, whatever its state, so none outlives the call.
        Visit? visit;
        lock (_waitingLock)
        {
            _visits.Remove(evt.SessionId, out visit);
        }

        // A visit still open when the call ended: the caller hung up while waiting, and its leave was not received.
        if (visit is { State: VisitState.Open })
            Count(visit.QueueName, abandoned: visit.AbandonCounted ? 0 : 1, waiting: -1);
        else if (visit is { State: VisitState.Left, AbandonPending: true })
            Count(visit.QueueName, abandoned: 1);
    }

    /// <summary>The call's current visit, when it is the one <paramref name="signal"/> names. Under <see cref="_waitingLock"/>.</summary>
    private Visit? Named(QueueVisitSignal signal) =>
        _visits.TryGetValue(signal.SessionId, out var visit)
        && visit.OpenedAt == signal.Visit
        && string.Equals(visit.QueueName, signal.QueueName, StringComparison.OrdinalIgnoreCase)
            ? visit
            : null;

    private void HandleVisitLeft(QueueVisitSignal signal)
    {
        bool countAbandon;
        lock (_waitingLock)
        {
            if (Named(signal) is not { State: VisitState.Open } visit)
                return;

            visit.State = VisitState.Left;

            // An abandon reported before the leave was counted at its report; one the signal says was reported is
            // counted here if its own signal has not been. No abandon report is a key exit only while no event may
            // have been lost since the visit opened: a lost report must not turn an abandon into a key exit. A live
            // leave may still be followed by the queue's connection, so its abandon waits for the call's next join or
            // its end; a leave that was never received is past any connection, and counts now.
            countAbandon = !visit.AbandonCounted && (signal.AbandonReported || signal.LeaveMissed);
            if (countAbandon)
                visit.AbandonCounted = true;
            else if (!visit.AbandonCounted && signal.EventsMayHaveBeenLost)
                visit.AbandonPending = true;
        }

        Count(signal.QueueName, abandoned: countAbandon ? 1 : 0, waiting: -1);
    }

    private void HandleAbandonReported(QueueVisitSignal signal)
    {
        lock (_waitingLock)
        {
            if (Named(signal) is not { } visit || visit.State == VisitState.Gone || visit.AbandonCounted)
                return;

            visit.AbandonCounted = true;
            visit.AbandonPending = false;
        }

        Count(signal.QueueName, abandoned: 1);
    }

    private void HandleTimedOut(QueueVisitSignal signal)
    {
        bool wasOpen;
        bool countAbandon;
        lock (_waitingLock)
        {
            if (Named(signal) is not { } visit || visit.State == VisitState.Gone || visit.TimedOutCounted)
                return;

            visit.TimedOutCounted = true;

            // A timeout on a visit still open: its abandon report and its leave were not received. The timeout ends
            // it, abandoned, as app_queue counted it.
            wasOpen = visit.State == VisitState.Open;
            countAbandon = (wasOpen || visit.AbandonPending) && !visit.AbandonCounted;
            if (countAbandon)
                visit.AbandonCounted = true;
            visit.AbandonPending = false;
            if (wasOpen)
                visit.State = VisitState.Left;
        }

        Count(signal.QueueName, timedOut: 1, abandoned: countAbandon ? 1 : 0, waiting: wasOpen ? -1 : 0);
    }

    public void Dispose()
    {
        _subscription.Dispose();
        if (_visitSource is not null)
        {
            _visitSource.VisitLeft -= HandleVisitLeft;
            _visitSource.VisitAbandonReported -= HandleAbandonReported;
            _visitSource.VisitTimedOut -= HandleTimedOut;
        }
    }

    /// <summary>Open: waiting. Left: Asterisk reported the leave; not waiting. Gone: the queue connected it.</summary>
    private enum VisitState
    {
        Open,
        Left,
        Gone,
    }

    /// <summary>
    /// A call's current queue visit: the queue it joined, its start (which names it), its state, and whether its
    /// abandon and its timeout have been counted. Read and written only under <see cref="_waitingLock"/>.
    /// </summary>
    private sealed class Visit(string queueName, DateTimeOffset openedAt)
    {
        public string QueueName { get; } = queueName;

        public DateTimeOffset OpenedAt { get; } = openedAt;

        public VisitState State { get; set; } = VisitState.Open;

        public bool AbandonCounted { get; set; }

        /// <summary>
        /// Left with no abandon report after events may have been lost: abandoned unless the queue connects it before
        /// the call's next join or its end.
        /// </summary>
        public bool AbandonPending { get; set; }

        public bool TimedOutCounted { get; set; }
    }

    private sealed class EventObserver(QueueSessionTracker tracker) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value)
        {
            switch (value)
            {
                case CallQueuedEvent queued:
                    tracker.HandleCallQueued(queued);
                    break;
                case CallConnectedEvent connected:
                    tracker.HandleCallConnected(connected);
                    break;
                case CallEndedEvent ended:
                    tracker.HandleCallEnded(ended);
                    break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
