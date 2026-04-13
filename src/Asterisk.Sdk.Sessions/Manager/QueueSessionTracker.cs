using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Sessions.Manager;

internal sealed class QueueSessionTracker : IQueueSessionTracker, IDisposable
{
    private readonly ConcurrentDictionary<string, QueueSession> _queues = new();
    private readonly Dictionary<string, string> _waitingSessionQueues = []; // sessionId → queueName
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
        var queue = GetOrCreateQueue(evt.QueueName);
        lock (queue.SyncRoot)
        {
            CheckWindowExpiry(queue);
            queue.CallsOffered++;
            queue.CallsWaiting++;
        }

        lock (_waitingLock)
        {
            _waitingSessionQueues[evt.SessionId] = evt.QueueName;
        }
    }

    private void HandleCallConnected(CallConnectedEvent evt)
    {
        if (evt.QueueName is null) return;

        bool wasWaiting;
        lock (_waitingLock)
        {
            wasWaiting = _waitingSessionQueues.Remove(evt.SessionId);
        }

        var queue = GetOrCreateQueue(evt.QueueName);
        lock (queue.SyncRoot)
        {
            CheckWindowExpiry(queue);
            queue.CallsAnswered++;

            if (wasWaiting)
                queue.CallsWaiting--;

            // Record wait time
            queue.TotalWaitTime += evt.WaitTime;
            if (evt.WaitTime > queue.MaxWaitTime)
                queue.MaxWaitTime = evt.WaitTime;
            if (evt.WaitTime < queue.MinWaitTime)
                queue.MinWaitTime = evt.WaitTime;

            // SLA check
            if (evt.WaitTime <= _slaThreshold)
                queue.CallsWithinSla++;
        }
    }

    private void HandleCallEnded(CallEndedEvent evt)
    {
        string? queueName;
        lock (_waitingLock)
        {
            if (!_waitingSessionQueues.Remove(evt.SessionId, out queueName))
                return;
        }

        // Caller ended while still waiting in queue → abandoned
        var queue = GetOrCreateQueue(queueName);
        lock (queue.SyncRoot)
        {
            CheckWindowExpiry(queue);
            queue.CallsAbandoned++;
            queue.CallsWaiting--;
        }
    }

    public void Dispose() => _subscription.Dispose();

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
