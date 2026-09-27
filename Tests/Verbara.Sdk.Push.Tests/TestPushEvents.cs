namespace Verbara.Sdk.Push.Tests;

internal sealed record TestPushEvent : PushEvent
{
    public override string EventType => "test.event";
    public required string Payload { get; init; }
}

internal sealed record OtherTestPushEvent : PushEvent
{
    public override string EventType => "test.other";
    public required int Value { get; init; }
}

internal static class TestEventFactory
{
    public static TestPushEvent Create(
        string payload = "p",
        string tenantId = "tenant-1",
        string? userId = null,
        string? topicPath = null) =>
        new()
        {
            Payload = payload,
            Metadata = new PushEventMetadata(
                TenantId: tenantId,
                UserId: userId,
                OccurredAt: DateTimeOffset.UtcNow,
                CorrelationId: null,
                TopicPath: topicPath),
        };
}

internal sealed class CapturingObserver<T> : IObserver<T>
{
    public List<T> Items { get; } = [];
    public bool Completed { get; private set; }
    public Exception? Error { get; private set; }
    public void OnCompleted() => Completed = true;
    public void OnError(Exception error) => Error = error;
    public void OnNext(T value)
    {
        lock (Items) Items.Add(value);
    }
}

internal sealed class BlockingObserver : IObserver<PushEvent>
{
    private readonly ManualResetEventSlim _release;
    private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];

    public BlockingObserver(ManualResetEventSlim release) => _release = release;

    public List<PushEvent> Items { get; } = [];

    /// <summary>
    /// Completes when the dispatcher has taken the first event and is about to park inside
    /// <see cref="OnNext"/>: from then on nothing leaves the channel until the test releases it.
    /// </summary>
    public Task Parked => _parked.Task;

    /// <summary>Completes once <paramref name="count"/> events have been received.</summary>
    public Task WhenReceived(int count)
    {
        lock (Items)
        {
            if (Items.Count >= count) return Task.CompletedTask;
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, signal));
            return signal.Task;
        }
    }

    public void OnNext(PushEvent value)
    {
        if (_parked.TrySetResult())
        {
            // First event: park the dispatcher loop until the test releases us.
            _release.Wait(TimeSpan.FromSeconds(5));
        }
        lock (Items)
        {
            Items.Add(value);
            _waiters.RemoveAll(w => Items.Count >= w.Count && w.Signal.TrySetResult());
        }
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }
}
