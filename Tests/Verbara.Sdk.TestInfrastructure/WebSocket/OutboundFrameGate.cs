using System.Net.WebSockets;

namespace Verbara.Sdk.TestInfrastructure.WebSocket;

/// <summary>
/// Parks a fake server's outbound delivery after a chosen number of messages, so a test can cancel
/// while the server demonstrably still has frames to send.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Every cancellation test in the VoiceAi WebSocket suites cancels either
/// before a socket is opened or on a socket whose send queue the test emptied first, so none of them
/// has ever observed cancellation interrupt delivery. Writing that test needs one thing the fakes
/// could not express: a session that is still mid-delivery at the moment the token fires. Without a
/// hold the fakes send everything up front and then park in their receive loop, and a cancel landing
/// there proves only that a live-but-idle socket can be cancelled — which is the case the Lmnt test
/// already covers.
/// </para>
/// <para>
/// <b>Why it lives in the substrate.</b> "Stop writing after the Nth message" is a property of the
/// transport, not of any vendor's protocol, and all eight fakes share this one transport. Putting it
/// here states it once instead of eight times, and keeps each fake's session handler about its own
/// wire format.
/// </para>
/// <para>
/// <b>Why it is opt-in.</b> A gate is attached only when a test arms one: with none armed,
/// <see cref="WebSocketTestSession.WebSocket"/> is the raw socket, byte for byte the object it was
/// before this type existed. The 274 tests in these two suites therefore cannot be affected by it,
/// which is a stronger statement than "the decorator forwards faithfully".
/// </para>
/// <para>
/// The count is of <em>outbound messages the fake sends</em>, greeting frames included, because that
/// is what the fake's own code makes countable. Each surface's greeting differs, so each test names
/// its own number rather than inheriting one — see the callers.
/// </para>
/// </remarks>
public sealed class OutboundFrameGate
{
    private readonly TaskCompletionSource _held =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _delivered;

    /// <summary>Create a gate that lets <paramref name="messages"/> through and holds the next one.</summary>
    public OutboundFrameGate(int messages)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(messages);
        HoldAfter = messages;
    }

    /// <summary>How many outbound messages pass before delivery parks.</summary>
    public int HoldAfter { get; }

    /// <summary>Outbound messages actually written to the socket.</summary>
    public int Delivered => Volatile.Read(ref _delivered);

    /// <summary>
    /// Completes the first time a send is parked — i.e. the fake reached message
    /// <see cref="HoldAfter"/> + 1 and still has it to give. A test asserting on this is asserting
    /// that undelivered frames existed, rather than assuming it.
    /// </summary>
    public Task Held => _held.Task;

    /// <summary>Let the parked send, and everything after it, through.</summary>
    public void Release() => _released.TrySetResult();

    /// <summary>
    /// Called by the decorator before each outbound write. Returns once the write may proceed;
    /// throws <see cref="OperationCanceledException"/> if the server is disposed while parked, which
    /// is what keeps a held session from outliving its test.
    /// </summary>
    internal async ValueTask PassAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _delivered) < HoldAfter)
        {
            Interlocked.Increment(ref _delivered);
            return;
        }

        _held.TrySetResult();
        await _released.Task.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _delivered);
    }
}

/// <summary>
/// A <see cref="System.Net.WebSockets.WebSocket"/> that forwards everything to an inner socket and
/// asks an <see cref="OutboundFrameGate"/> for permission before each write.
/// </summary>
/// <remarks>
/// Only the six members the fakes actually use carry behaviour worth stating — <c>State</c>,
/// <c>SendAsync</c>, <c>ReceiveAsync</c>, <c>Abort</c>, <c>CloseAsync</c>, <c>CloseOutputAsync</c>.
/// Both <c>SendAsync</c> overloads route through the same gated path so a fake cannot slip past the
/// hold by choosing the other one.
/// </remarks>
internal sealed class GatedWebSocket : System.Net.WebSockets.WebSocket
{
    private readonly System.Net.WebSockets.WebSocket _inner;
    private readonly OutboundFrameGate _gate;
    private readonly CancellationToken _serverToken;

    internal GatedWebSocket(
        System.Net.WebSockets.WebSocket inner,
        OutboundFrameGate gate,
        CancellationToken serverToken)
    {
        _inner = inner;
        _gate = gate;
        _serverToken = serverToken;
    }

    public override WebSocketCloseStatus? CloseStatus => _inner.CloseStatus;

    public override string? CloseStatusDescription => _inner.CloseStatusDescription;

    public override WebSocketState State => _inner.State;

    public override string? SubProtocol => _inner.SubProtocol;

    public override void Abort() => _inner.Abort();

    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => _inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => _inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    /// <remarks>
    /// <see cref="System.Net.WebSockets.WebSocket.Dispose"/> is abstract, so there is no base
    /// implementation to chain to — disposing the inner socket is the whole of it.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "IDisposableAnalyzers.Correctness",
        "IDISP010:Call base.Dispose(bool)",
        Justification = "WebSocket.Dispose is abstract; there is no base implementation to call.")]
    public override void Dispose() => _inner.Dispose();

    public override Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer, CancellationToken cancellationToken)
        => _inner.ReceiveAsync(buffer, cancellationToken);

    public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer, CancellationToken cancellationToken)
        => _inner.ReceiveAsync(buffer, cancellationToken);

    public override async Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        await GateAsync(cancellationToken).ConfigureAwait(false);
        await _inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        await GateAsync(cancellationToken).ConfigureAwait(false);
        await _inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The hold is released by the server's own token as well as the caller's, so disposing the
    /// server always unblocks a parked session even when the fake passed <c>CancellationToken.None</c>.
    /// </summary>
    private async ValueTask GateAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _serverToken);
        await _gate.PassAsync(linked.Token).ConfigureAwait(false);
    }
}
