using System.Diagnostics.Tracing;

namespace Verbara.Sdk.Ari.Tests.TestSupport;

internal sealed class ReceiveStartSignal : EventListener
{
    private readonly AsyncLocal<bool> _watched = new();
    private string? _firstSocket;
    private int _closeSendStarted;
    private int _closeSendsCompleted;
    private int _firstRelease;
    private bool _closeCompletedBeforeRelease;
    internal int CloseSendsCompleted => Volatile.Read(ref _closeSendsCompleted);
    internal bool CloseCompletedBeforeRelease => Volatile.Read(ref _firstRelease) != 0 && _closeCompletedBeforeRelease;
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
        if (!_watched.Value) return;
        var socket = Payload(eventData, "objName");
        if (eventData.EventName == "ReceiveStart")
        {
            Interlocked.CompareExchange(ref _firstSocket, socket, null);
            _started.TrySetResult();
        }
        if (socket is null || socket != Volatile.Read(ref _firstSocket)) return;
        // Like the existing receive hook, these private runtime events must be observed or the
        // test fails. SendStop follows the stream write; a peer receipt is joined separately.
        if (eventData.EventName == "SendStart" && Payload(eventData, "opcode") == "Close")
            Volatile.Write(ref _closeSendStarted, 1);
        if (eventData.EventName == "SendStop" && Interlocked.Exchange(ref _closeSendStarted, 0) == 1)
            Interlocked.Increment(ref _closeSendsCompleted);
        if (eventData.EventName == "WsTrace" && Payload(eventData, "memberName") == "Dispose" &&
            Volatile.Read(ref _firstRelease) == 0)
        {
            _closeCompletedBeforeRelease = CloseSendsCompleted == 1;
            Volatile.Write(ref _firstRelease, 1);
        }
    }
    private static string? Payload(EventWrittenEventArgs data, string name)
    {
        if (data.PayloadNames is null || data.Payload is null) return null;
        for (var index = 0; index < data.PayloadNames.Count; index++)
            if (data.PayloadNames[index] == name) return data.Payload[index]?.ToString();
        return null;
    }
}
