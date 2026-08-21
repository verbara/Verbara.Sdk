using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.TestInfrastructure.WebSocket;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

/// <summary>
/// In-process WebSocket server that simulates the OpenAI Realtime API protocol.
/// Sends session.created on connect, then delivers configured events.
/// </summary>
/// <remarks>
/// <para>
/// Built on the shared <see cref="WebSocketTestServer"/> — the substrate the other eight WebSocket
/// fakes in this repo already run on. The <see cref="System.Net.HttpListener"/> path it replaces
/// forced a check-then-bind port probe (bind a <c>TcpListener</c> on port 0, read the port, stop it,
/// hand the now-free port to <c>HttpListener</c>, retry on collision), because <c>HttpListener</c>
/// cannot adopt an already-bound socket. <see cref="WebSocketTestServer"/> binds
/// <c>TcpListener(IPAddress.Loopback, 0)</c> and keeps it, so that window has no equivalent here and
/// the probe is deleted rather than carried over.
/// </para>
/// <para>
/// This session answers on protocol, never on a timer: it waits for the client's
/// <c>session.update</c> (see <see cref="_sessionUpdateReceived"/>) before delivering the configured
/// events, and closes when they are delivered rather than after a fixed settle. The three
/// <c>Task.Delay</c> calls it used to sequence itself with — 30 ms before the events, 5 ms between
/// them, 100 ms before the close — were a race the fake happened to win on the machine it was
/// written on.
/// </para>
/// </remarks>
internal sealed class RealtimeFakeServer : IAsyncDisposable
{
    /// <summary>
    /// How long the session waits for the client's <c>session.update</c> before answering anyway.
    /// Reaching it means the protocol assumption below is wrong — not that the machine was busy —
    /// so it is set far above any plausible scheduling delay for a loopback socket.
    /// </summary>
    private static readonly TimeSpan SessionUpdateTimeout = TimeSpan.FromSeconds(10);

    private readonly WebSocketTestServer _server;

    /// <summary>
    /// Released by the client's <c>session.update</c> frame — the bridge's unconditional first
    /// frame, sent immediately after <c>ConnectAsync</c> and before either loop starts
    /// (<c>src/Verbara.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeBridge.cs</c>, the send that precedes
    /// <c>Task.WhenAll(InputLoop, OutputLoop)</c>). Nothing else the client sends qualifies:
    /// <c>input_audio_buffer.append</c> only appears once the caller speaks, and
    /// <c>conversation.item.create</c> only after a function call — so a session with neither would
    /// never release a sentinel keyed on those.
    /// </summary>
    private readonly TaskCompletionSource _sessionUpdateReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<string> _receivedMessages = [];

    /// <summary>Waiters registered by <see cref="WaitForClientFrameAsync"/>, keyed by the fragment they match.</summary>
    private readonly List<(string Fragment, TaskCompletionSource Source)> _frameWaiters = [];

    /// <summary>Volatile because <see cref="SocketState"/> is read from the test thread while the
    /// session handler writes it.</summary>
    private volatile System.Net.WebSockets.WebSocket? _socket;

    /// <summary>Volatile for the same reason as <see cref="_socket"/>; see <see cref="FramesCapturedWhenAnswering"/>.</summary>
    private volatile string[] _framesCapturedWhenAnswering = [];

    public int Port => _server.Port;

    /// <summary>
    /// Every text frame received from the client — a snapshot. The receive loop runs on its own
    /// thread and may still be appending while a test reads this, so handing out the live list
    /// would be a torn read of a collection under concurrent mutation.
    /// </summary>
    public IReadOnlyList<string> ReceivedMessages
    {
        get { lock (_receivedMessages) return _receivedMessages.ToArray(); }
    }

    /// <summary>
    /// JSON event strings to send after session.created, in order. Deliberately a plain writable
    /// list and deliberately unsynchronised: this is test-to-server configuration, written before
    /// <see cref="Start"/> and never touched by the receive loop. It is not a capture, so the
    /// snapshot rule that governs <see cref="ReceivedMessages"/> does not apply — do not "fix" it.
    /// </summary>
    public List<string> EventsToSend { get; } = [];

    /// <summary>
    /// When <see langword="true"/>, the session neither closes nor aborts the socket after
    /// delivering its events; the connection stays open until this server is disposed. A
    /// cancellation test sets it so the bridge's <c>OutputLoop</c> is blocked on a <em>live</em>
    /// socket when the token fires — otherwise the loop has already returned on the server's close
    /// and the test attributes to cancellation something cancellation did not do.
    /// </summary>
    public bool HoldOpenUntilDisposed { get; set; }

    /// <summary>
    /// Completes once the client's <c>session.update</c> frame has been captured — the join point a
    /// test waits on instead of guessing a delay.
    /// </summary>
    public Task SessionUpdateReceived => _sessionUpdateReceived.Task;

    /// <summary>
    /// Live server-side socket state, or <see langword="null"/> before the first connection is
    /// accepted. A cancellation test asserts on this to prove the socket was still open at the
    /// moment its token fired.
    /// </summary>
    public WebSocketState? SocketState => _socket?.State;

    /// <summary>
    /// The client frames captured at the instant this session began delivering
    /// <see cref="EventsToSend"/> — the fake's own answer to "what had the client asked for when I
    /// answered?". Empty until it answers.
    /// </summary>
    /// <remarks>
    /// This is what makes the protocol sentinel <em>testable</em> rather than merely present. A fake
    /// that answers on protocol necessarily has the client's <c>session.update</c> here; one that
    /// answers on a timer has whatever happened to have arrived by then, which under load is
    /// nothing. Asserting on it is the difference between a fence that is checked and a fence that
    /// is assumed — with the sentinel replaced by the fixed delay it superseded, every other
    /// assertion in this suite still passes (measured: 20/20 green under CPU saturation), because
    /// the drain loop captures <c>session.update</c> whenever it arrives and the assertions only
    /// ever read the end state.
    /// </remarks>
    public IReadOnlyList<string> FramesCapturedWhenAnswering => _framesCapturedWhenAnswering;

    public RealtimeFakeServer() => _server = new WebSocketTestServer(HandleSessionAsync);

    public void Start() => _server.Start();

    /// <summary>
    /// A task that completes once a captured client frame contains <paramref name="fragment"/>.
    /// Frames already captured satisfy it immediately, so a caller that registers after the frame
    /// arrived does not wait forever — the race a plain "subscribe then wait" would lose.
    /// </summary>
    public Task WaitForClientFrameAsync(string fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        lock (_receivedMessages)
        {
            foreach (var message in _receivedMessages)
            {
                if (message.Contains(fragment, StringComparison.Ordinal))
                    return Task.CompletedTask;
            }

            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _frameWaiters.Add((fragment, source));
            return source.Task;
        }
    }

    private async Task HandleSessionAsync(WebSocketTestSession session)
    {
        var ws = session.WebSocket;
        var ct = session.ServerCancellationToken;
        _socket = ws;

        var receiveTask = StartReceiveLoopAsync(ws, ct);

        await SendJsonAsync(ws, """{"type":"session.created","session":{}}""", ct).ConfigureAwait(false);

        // Answer only once the client's session.update has arrived. The 30 ms delay this replaces
        // was the whole synchronisation behind HandleSessionAsync_SendsSessionUpdate_OnConnect:
        // nothing made the fake wait for the frame that test asserts on.
        await WaitForSessionUpdateOrTimeoutAsync(ct).ConfigureAwait(false);

        lock (_receivedMessages)
            _framesCapturedWhenAnswering = _receivedMessages.ToArray();

        foreach (var evt in EventsToSend.ToList())
        {
            if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) break;
            await SendJsonAsync(ws, evt, ct).ConfigureAwait(false);
        }

        if (HoldOpenUntilDisposed)
        {
            // Hold until this server is disposed (ct fires). Awaiting the receive loop instead is
            // the Class B trap: it ends the instant the client half-closes, while the socket is
            // still perfectly readable, so returning there would tear the session down at exactly
            // the moment a cancellation test needs it alive.
            // fence-allow: GUARD-TIMEOUT — Timeout.Infinite; the server's own token is the only arm
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* disposed: release the socket */ }
            try { await receiveTask.ConfigureAwait(false); } catch { /* already torn down */ }
            return;
        }

        // CloseOutputAsync, not CloseAsync: CloseAsync also *waits* for the peer's close frame, which
        // means receiving — and the drain below already owns the receive path. Two concurrent receives
        // on one socket is exactly the violation this fake used to hide behind `catch { }` on the
        // HttpListener substrate (§1.4). Sending the close frame and then draining until the client's
        // own close arrives keeps a single receiver and still completes the handshake.
        await CloseOutputAsync(ws).ConfigureAwait(false);
        try { await receiveTask.ConfigureAwait(false); } catch { /* connection may already be closed */ }
    }

    private async Task WaitForSessionUpdateOrTimeoutAsync(CancellationToken ct)
    {
        try
        {
            await _sessionUpdateReceived.Task.WaitAsync(SessionUpdateTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // No session.update in ten seconds: the client never got far enough to send one. Answer
            // anyway so the test fails on its own assertion rather than the suite hanging.
        }
        catch (OperationCanceledException)
        {
            // Disposed mid-wait.
        }
    }

    private Task StartReceiveLoopAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
        => Task.Run(async () =>
        {
            var buf = new byte[65536];
            while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                }
                catch
                {
                    break; // connection closed or cancelled
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                Capture(Encoding.UTF8.GetString(buf, 0, result.Count));
            }
        }, ct);

    private void Capture(string message)
    {
        List<TaskCompletionSource>? released = null;

        lock (_receivedMessages)
        {
            _receivedMessages.Add(message);

            for (var i = _frameWaiters.Count - 1; i >= 0; i--)
            {
                if (!message.Contains(_frameWaiters[i].Fragment, StringComparison.Ordinal))
                    continue;

                (released ??= []).Add(_frameWaiters[i].Source);
                _frameWaiters.RemoveAt(i);
            }
        }

        // Completed outside the lock: the continuations are asynchronous, but releasing a waiter
        // while holding the lock the receive loop needs is a habit worth not forming.
        if (message.Contains("\"session.update\"", StringComparison.Ordinal))
            _sessionUpdateReceived.TrySetResult();

        if (released is null) return;
        foreach (var source in released)
            source.TrySetResult();
    }

    private static async Task SendJsonAsync(System.Net.WebSockets.WebSocket ws, string json, CancellationToken ct)
    {
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Peer closed before we could send — not an error.
        }
    }

    /// <summary>
    /// Send the close frame without waiting for the peer's. The drain loop reads the client's reply,
    /// so the handshake still completes — with a single receiver on the socket.
    /// </summary>
    private static async Task CloseOutputAsync(System.Net.WebSockets.WebSocket ws)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch
        {
            // Socket may already be gone — not an error.
        }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync().ConfigureAwait(false);

        // Nothing may stay parked on a waiter after the server is gone.
        lock (_receivedMessages)
        {
            foreach (var (_, source) in _frameWaiters)
                source.TrySetCanceled();
            _frameWaiters.Clear();
        }

        _sessionUpdateReceived.TrySetCanceled();
    }
}
