using Verbara.Sdk.Enums;

namespace Verbara.Sdk;

/// <summary>
/// Represents an async connection to the Asterisk Manager Interface (AMI).
/// </summary>
public interface IAmiConnection : IAsyncDisposable
{
    /// <summary>Current connection state.</summary>
    AmiConnectionState State { get; }

    /// <summary>Asterisk server version detected after login.</summary>
    string? AsteriskVersion { get; }

    /// <summary>Connect and authenticate to the Asterisk AMI.</summary>
    /// <exception cref="System.ArgumentException">
    /// The configured username contains a line break (CR or LF), which would split the login action
    /// into several on the wire. The login action is not sent.
    /// </exception>
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Send an action and wait for the response.</summary>
    /// <exception cref="System.ArgumentException">
    /// The action's ActionID, or a key or value among the fields it serializes to, contains a line
    /// break (CR or LF), which would split one action into several on the wire. Nothing of the action
    /// is sent, no response is awaited, and the connection stays usable. The message names the field
    /// but never includes its value.
    /// </exception>
    ValueTask<ManagerResponse> SendActionAsync(ManagerAction action, CancellationToken cancellationToken = default);

    /// <summary>Send an action and wait for a typed response.</summary>
    /// <exception cref="System.ArgumentException">
    /// The action's ActionID, or a key or value among the fields it serializes to, contains a line
    /// break (CR or LF), which would split one action into several on the wire. Nothing of the action
    /// is sent, no response is awaited, and the connection stays usable. The message names the field
    /// but never includes its value.
    /// </exception>
    ValueTask<TResponse> SendActionAsync<TResponse>(ManagerAction action, CancellationToken cancellationToken = default)
        where TResponse : ManagerResponse;

    /// <summary>Send an event-generating action and stream the resulting events.</summary>
    /// <remarks>
    /// <para>
    /// The sequence holds the events that carry the action's ActionID, and ends when Asterisk ends the action: at an
    /// event whose name ends in <c>Complete</c>, which marks the end of a list and is not yielded; at the
    /// <c>OriginateResponse</c> of an asynchronous <c>Originate</c>, which is the originate's outcome and is yielded
    /// before the sequence ends; or at the <c>Response: Error</c> with which Asterisk refuses the action. It also ends
    /// when the connection's session ends first. Otherwise the connection's event timeout
    /// (<c>AmiConnectionOptions.DefaultEventTimeout</c> on <c>AmiConnection</c>) ends it with an
    /// <see cref="System.OperationCanceledException"/>.
    /// </para>
    /// <para>
    /// An event is a message with an <c>Event</c> header, whether or not it also carries a <c>Response</c> header, as
    /// <c>OriginateResponse</c> does: it is delivered as an event, never taken for the action's response.
    /// </para>
    /// </remarks>
    /// <exception cref="System.ArgumentException">
    /// Surfaced by the first <c>MoveNextAsync</c> of the returned sequence: the action's ActionID, or
    /// a key or value among the fields it serializes to, contains a line break (CR or LF), which would
    /// split one action into several on the wire. Nothing of the action is sent, no events are
    /// awaited, and the connection stays usable. The message names the field but never includes its
    /// value.
    /// </exception>
    IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(
        ManagerAction action, CancellationToken cancellationToken = default);

    /// <summary>Subscribe to all AMI events via IObservable.</summary>
    IDisposable Subscribe(IObserver<ManagerEvent> observer);

    /// <summary>Event raised when an AMI event is received.</summary>
    event Func<ManagerEvent, ValueTask>? OnEvent;

    /// <summary>Fired after a successful automatic reconnection.</summary>
    /// <remarks>
    /// Delivered on the thread pool, never on the connection's reader, heartbeat or reconnect loop, and in order: every
    /// handler told of the loss that caused the reconnect has returned before the first <c>Reconnected</c> handler of
    /// the same outage runs. Handlers run one at a time; one that throws is logged, and the handlers after it still
    /// receive the event. A slow handler delays the handlers queued behind it, so keep handlers short.
    /// </remarks>
    event Action? Reconnected;

    /// <summary>
    /// Raised for every change of <see cref="State"/>, once per change, with the state before and after, what caused
    /// it, and whether the caller's own connect or ending made it (<see cref="AmiConnectionStateChange"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Order.</b> Changes are delivered in the order the state was written, whoever wrote it: the caller's connect
    /// or ending, the reader that lost the connection, or the reconnect loop. Each change's
    /// <see cref="AmiConnectionStateChange.Previous"/> is the <see cref="AmiConnectionStateChange.Current"/> of the
    /// change delivered before it, and a write that leaves the state as it was announces nothing. Once the caller's
    /// ending has been recorded, no connect announces a state other than the ending's.
    /// </para>
    /// <para>
    /// <b>Queue and thread.</b> The changes share one ordered queue with <see cref="Reconnected"/> (and, on
    /// <c>AmiConnection</c>, with the loss announcement Verbara.Sdk.Live raises <c>ConnectionLost</c> from): the change
    /// that leaves <see cref="AmiConnectionState.Connected"/> is delivered before the loss is announced, and a
    /// reconnect's change to <see cref="AmiConnectionState.Connected"/> before its <see cref="Reconnected"/>. Handlers
    /// run one at a time on the thread pool, never on the connection's reader, heartbeat or reconnect loop, and none of
    /// those waits for them. A slow handler delays only the notifications queued behind it, so keep handlers short.
    /// The handlers are read when a change is delivered, and nothing is queued while the event has no handler. The
    /// caller's own <see cref="AmiConnectionState.Disconnecting"/> and <see cref="AmiConnectionState.Disconnected"/>
    /// can be delivered after <see cref="DisconnectAsync"/> or <c>DisposeAsync</c> has returned.
    /// </para>
    /// <para>
    /// <b>Handlers that throw, or end the connection.</b> A handler that throws is logged, and the handlers after it
    /// still receive the change. A handler may end the connection and wait for that ending: no ending waits for the
    /// queue.
    /// </para>
    /// <para>
    /// <b>Default implementation.</b> An implementation of this interface that does not provide the event raises
    /// nothing: subscribing to it is accepted and has no effect. An implementation that wraps another connection
    /// forwards the inner connection's changes.
    /// </para>
    /// </remarks>
    event Action<AmiConnectionStateChange>? StateChanged
    {
        add { }
        remove { }
    }

    /// <summary>Gracefully disconnect from the AMI.</summary>
    /// <remarks>
    /// The connection ends for good, including a reconnect in progress: the reconnect loop stops wherever it
    /// is, in its backoff delay or in a connect attempt, and afterwards creates no socket, sends no login and
    /// raises no <see cref="Reconnected"/>. When the call returns, <see cref="State"/> is
    /// <see cref="AmiConnectionState.Disconnected"/> and the socket has been released. The connection cannot be
    /// connected again: a later <see cref="ConnectAsync"/> throws <see cref="ObjectDisposedException"/>.
    /// <c>DisposeAsync</c> ends the connection the same way.
    /// <para>
    /// The ending waits for the event whose handler is running, if any; events still buffered are not delivered. They
    /// are counted on <c>ami.events.dropped</c> with <c>reason=caller_ending</c> and logged once at Warning with the
    /// count. A connection lost without this call still delivers every buffered event, in order.
    /// </para>
    /// </remarks>
    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Base class for all AMI manager actions.
/// Concrete actions are source-generated for AOT compatibility.
/// </summary>
public abstract class ManagerAction
{
    public string? ActionId { get; set; }
}

/// <summary>
/// Base class for all AMI manager events.
/// Concrete events are source-generated for AOT compatibility.
/// </summary>
public class ManagerEvent
{
    public string? Privilege { get; set; }
    public string? UniqueId { get; set; }
    public double? Timestamp { get; set; }
    public string? EventType { get; set; }

    /// <summary>Raw fields from the AMI message.</summary>
    public IReadOnlyDictionary<string, string>? RawFields { get; set; }
}

/// <summary>
/// Base class for all AMI manager responses.
/// </summary>
public class ManagerResponse
{
    public string? ActionId { get; set; }
    public string? Response { get; set; }
    public string? Message { get; set; }

    /// <summary>Raw fields from the AMI message.</summary>
    public IReadOnlyDictionary<string, string>? RawFields { get; set; }
}
