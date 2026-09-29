using System.Diagnostics.Tracing;

namespace Verbara.Sdk.TestInfrastructure.WebSocket;

/// <summary>
/// Runs an action inside a client's <c>ReceiveAsync</c>, after the read has its data and before it
/// returns it: the instant a bound that runs out aborts the socket under a read that still succeeds.
/// </summary>
/// <remarks>
/// <para>
/// The runtime reports that instant on its private diagnostics source,
/// <c>Private.InternalDiagnostics.System.Net.WebSockets</c>: the event <c>MutexExit</c> with member
/// <c>ReceiveAsyncPrivate</c> is written when a read releases its lock, after its result is built and
/// before its registration on the token is disposed (<c>ManagedWebSocket</c>, .NET 10.0.12). An action
/// that cancels the read's token there aborts the socket, and the read still returns what it read.
/// </para>
/// <para>
/// Only the reads of the code started through <see cref="Watch{T}"/> are watched: the mark is an
/// <see cref="AsyncLocal{T}"/>, so the fake peer's own reads, started before, are not. The source is
/// not a public contract. If a runtime update moves the event, <see cref="Fired"/> never completes and
/// the test fails at its wait instead of passing without reaching the instant.
/// </para>
/// <para>
/// The action runs in the first watched read to end after <see cref="Arm"/>, so a test arms only once
/// the code under test has read every frame sent before, and then sends the frame the instant is for.
/// </para>
/// <para>
/// <see cref="ReceivesStartedAfterwards"/> counts the watched reads that begin after the action ran, so a
/// test can show that nothing is read from the socket the action aborted.
/// </para>
/// </remarks>
public sealed class WebSocketReceiveHook : EventListener
{
    private const string SourceName = "Private.InternalDiagnostics.System.Net.WebSockets";

    private readonly AsyncLocal<bool> _watched = new();
    private readonly TaskCompletionSource _fired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? _action;
    private int _receivesStartedAfterwards;

    /// <summary>Completes once the action has run inside a read; faults if the action threw.</summary>
    public Task Fired => _fired.Task;

    /// <summary>How many of the watched code's reads began after the action ran.</summary>
    public int ReceivesStartedAfterwards => Volatile.Read(ref _receivesStartedAfterwards);

    /// <summary>
    /// Starts the code whose reads this hook watches. Everything it starts runs in the execution context
    /// marked here; the caller's own context is left unmarked.
    /// </summary>
    public T Watch<T>(Func<T> start)
    {
        ArgumentNullException.ThrowIfNull(start);

        _watched.Value = true;
        try
        {
            return start();
        }
        finally
        {
            _watched.Value = false;
        }
    }

    /// <summary>Runs <paramref name="action"/> in the next watched read that returns.</summary>
    public void Arm(Action action) => Volatile.Write(ref _action, action);

    /// <inheritdoc />
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        ArgumentNullException.ThrowIfNull(eventSource);

        if (eventSource.Name == SourceName)
            EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
    }

    /// <inheritdoc />
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (!_watched.Value)
            return;

        if (_fired.Task.IsCompleted)
        {
            if (eventData.EventName == "ReceiveStart")
                Interlocked.Increment(ref _receivesStartedAfterwards);
            return;
        }

        if (eventData.EventName != "MutexExit" || PayloadText(eventData, "memberName") != "ReceiveAsyncPrivate")
            return;

        var action = Interlocked.Exchange(ref _action, null);
        if (action is null)
            return;

        // A failure is handed to the test through Fired: the event source's dispatch would swallow it.
        try
        {
            action();
            _fired.TrySetResult();
        }
#pragma warning disable CA1031 // Handed to the test through Fired, where it is rethrown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _fired.TrySetException(ex);
        }
    }

    private static string? PayloadText(EventWrittenEventArgs eventData, string name)
    {
        var index = eventData.PayloadNames?.IndexOf(name) ?? -1;
        return index >= 0 && eventData.Payload is { } payload && index < payload.Count
            ? payload[index] as string
            : null;
    }
}
