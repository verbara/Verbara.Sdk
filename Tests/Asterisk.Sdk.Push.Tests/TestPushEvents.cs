namespace Asterisk.Sdk.Push.Tests;

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
        string? userId = null) =>
        new()
        {
            Payload = payload,
            Metadata = new PushEventMetadata(
                TenantId: tenantId,
                UserId: userId,
                OccurredAt: DateTimeOffset.UtcNow,
                CorrelationId: null),
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
    private int _started;

    public BlockingObserver(ManualResetEventSlim release) => _release = release;

    public List<PushEvent> Items { get; } = [];
    public bool Started => Volatile.Read(ref _started) != 0;

    public void OnNext(PushEvent value)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            // First event: park the dispatcher loop until the test releases us.
            _release.Wait(TimeSpan.FromSeconds(5));
        }
        lock (Items) Items.Add(value);
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }
}
