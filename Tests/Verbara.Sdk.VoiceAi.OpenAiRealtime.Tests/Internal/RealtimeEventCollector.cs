namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

/// <summary>
/// Subscribes to a bridge's event stream and completes <see cref="Satisfied"/> the moment the
/// accumulated events satisfy a caller-supplied predicate — the deterministic end of a test's Act.
/// </summary>
/// <remarks>
/// <para>
/// The tests this serves used to reach their assertions when a five-second
/// <see cref="CancellationTokenSource"/> expired, so each cost exactly five seconds and asserted on
/// data that had arrived milliseconds in. Waiting on the event a test asserts on ends the test at
/// the moment the fact under assertion becomes true.
/// </para>
/// <para>
/// <see cref="Events"/> is a snapshot for the same reason the fake server's captures are: the
/// bridge publishes on its own <c>OutputLoop</c> thread and may still be appending while the test
/// enumerates.
/// </para>
/// </remarks>
internal sealed class RealtimeEventCollector : IDisposable
{
    private readonly List<RealtimeEvent> _events = [];

    private readonly TaskCompletionSource _satisfied =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Func<IReadOnlyList<RealtimeEvent>, bool> _isSatisfied;
    private readonly IDisposable _subscription;

    public RealtimeEventCollector(
        IObservable<RealtimeEvent> events,
        Func<IReadOnlyList<RealtimeEvent>, bool> isSatisfied)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(isSatisfied);

        _isSatisfied = isSatisfied;
        _subscription = events.Subscribe(OnNext);
    }

    /// <summary>Completes on the first event that makes the predicate true; never faults.</summary>
    public Task Satisfied => _satisfied.Task;

    /// <summary>Every event seen so far — a snapshot, taken under the lock the subscription writes under.</summary>
    public IReadOnlyList<RealtimeEvent> Events
    {
        get { lock (_events) return _events.ToArray(); }
    }

    private void OnNext(RealtimeEvent evt)
    {
        RealtimeEvent[] snapshot;
        lock (_events)
        {
            _events.Add(evt);
            snapshot = _events.ToArray();
        }

        // Evaluated outside the lock: the predicate is test-supplied, and running arbitrary code
        // under a lock the publishing thread needs is a habit worth not forming.
        if (_isSatisfied(snapshot))
            _satisfied.TrySetResult();
    }

    public void Dispose() => _subscription.Dispose();
}
