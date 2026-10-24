using System.Diagnostics.Tracing;

namespace Verbara.Sdk.Ari.Tests.TestSupport;

internal sealed class ReceiveStartSignal : EventListener
{
    private readonly AsyncLocal<bool> _watched = new();
    private TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Started => _started.Task;
    internal void Arm() => _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal T Watch<T>(Func<T> start)
    {
        _watched.Value = true;
        try { return start(); }
        finally { _watched.Value = false; }
    }
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Private.InternalDiagnostics.System.Net.WebSockets")
            EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
    }
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (_watched.Value && eventData.EventName == "ReceiveStart") _started.TrySetResult();
    }
}
