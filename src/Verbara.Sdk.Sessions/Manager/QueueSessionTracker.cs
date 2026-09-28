using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Sessions.Manager;

internal sealed class QueueSessionTracker : IQueueSessionTracker, IDisposable
{
    private readonly ConcurrentDictionary<string, QueueSession> _queues = new();
    private readonly Dictionary<string, OpenVisit> _openVisits = []; // sessionId → the queue visit it is waiting in
    private readonly Lock _waitingLock = new();
    private readonly TimeSpan _metricsWindow;
    private readonly TimeSpan _slaThreshold;
    private readonly IDisposable _subscription;

    public QueueSessionTracker(ICallSessionManager manager, IOptions<SessionOptions> options)
    {
        _metricsWindow = options.Value.QueueMetricsWindow;
        _slaThreshold = options.Value.SlaThreshold;
        _subscription = manager.Events.Subscribe(new EventObserver(this));
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

    private void HandleCallQueued(CallQueuedEvent evt)
    {
        // A call still waiting in one queue that joins another (an overflow, or any join while waiting)
        // left the first without a connection: that visit closes there as abandoned.
        bool leftOpenVisit;
        OpenVisit left;
        lock (_waitingLock)
        {
            leftOpenVisit = _openVisits.Remove(evt.SessionId, out left);
            _openVisits[evt.SessionId] = new OpenVisit(evt.QueueName, evt.Timestamp);
        }

        if (leftOpenVisit)
            CloseAbandoned(left.QueueName);

        var queue = GetOrCreateQueue(evt.QueueName);
        lock (queue.SyncRoot)
        {
            CheckWindowExpiry(queue);
            queue.CallsOffered++;
            queue.CallsWaiting++;
        }
    }

    private void HandleCallConnected(CallConnectedEvent evt)
    {
        if (evt.QueueName is null) return;

        bool wasWaiting;
        OpenVisit visit;
        lock (_waitingLock)
        {
            wasWaiting = _openVisits.Remove(evt.SessionId, out visit);
        }

        // The visit's wait runs from its join to this connection. When no join was observed (the tracker
        // was built after it, or the session was reconstructed), the event's own wait since the call was
        // created stands in for it, so the answer still carries a wait.
        var wait = wasWaiting ? evt.Timestamp - visit.OpenedAt : evt.WaitTime;

        var queue = GetOrCreateQueue(evt.QueueName);
        lock (queue.SyncRoot)
        {
            CheckWindowExpiry(queue);
            queue.CallsAnswered++;

            if (wasWaiting)
                queue.CallsWaiting--;

            // Record wait time
            queue.TotalWaitTime += wait;
            if (wait > queue.MaxWaitTime)
                queue.MaxWaitTime = wait;
            if (wait < queue.MinWaitTime)
                queue.MinWaitTime = wait;

            // SLA check
            if (wait <= _slaThreshold)
                queue.CallsWithinSla++;
        }
    }

    private void HandleCallEnded(CallEndedEvent evt)
    {
        OpenVisit visit;
        lock (_waitingLock)
        {
            if (!_openVisits.Remove(evt.SessionId, out visit))
                return;
        }

        // Caller ended while still waiting in queue → abandoned
        CloseAbandoned(visit.QueueName);
    }

    private void CloseAbandoned(string queueName)
    {
        var queue = GetOrCreateQueue(queueName);
        lock (queue.SyncRoot)
        {
            CheckWindowExpiry(queue);
            queue.CallsAbandoned++;
            queue.CallsWaiting--;
        }
    }

    public void Dispose() => _subscription.Dispose();

    /// <summary>A queue visit still waiting for a connection: the queue it joined, and when.</summary>
    private readonly record struct OpenVisit(string QueueName, DateTimeOffset OpenedAt);

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
