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
/// <para>
/// How a session ends after its burst is chosen by one knob at most. With none, the fake closes with
/// <c>1000 "done"</c> as soon as the burst is out. <see cref="HoldOpenUntilDisposed"/> never answers
/// the client's close; <see cref="HoldOpenUntilClientCloses"/> answers it at once, the way the live
/// vendor did; <see cref="AnswerClientCloseOnRequest"/> answers it only when the test calls
/// <see cref="AnswerClientCloseAsync"/>. <see cref="SendCloseAsync"/> closes from the server side
/// with a chosen code, and <see cref="ClientCloseReceived"/> tells a test when the client's close
/// arrived. None of them waits on a clock.
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

    private static readonly IReadOnlyDictionary<string, string> NoHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase).AsReadOnly();

    /// <summary>
    /// A close frame with no payload, as the server sends it: FIN plus opcode <c>0x8</c>, unmasked,
    /// length zero. It is how a peer closes without a status code (RFC 6455 §5.5.1).
    /// </summary>
    private static readonly byte[] CloseFrameWithNoCode = [0x88, 0x00];

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

    /// <summary>Completed by the receive loop with the status of the client's close frame.</summary>
    private readonly TaskCompletionSource<WebSocketCloseStatus> _clientCloseReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completed by <see cref="AnswerClientCloseAsync"/> once its answer is on the wire; it releases
    /// the <see cref="AnswerClientCloseOnRequest"/> hold.
    /// </summary>
    private readonly TaskCompletionSource _closeAnswered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 1 once this session has sent its close frame, by any route. A session sends one close, so
    /// every route claims this first, and a second close — or a data frame after the close — is
    /// refused rather than put on the wire.
    /// </summary>
    private int _serverCloseSent;

    /// <summary>Waiters registered by <see cref="WaitForClientFrameAsync"/>, keyed by the fragment they match.</summary>
    private readonly List<(string Fragment, TaskCompletionSource Source)> _frameWaiters = [];

    /// <summary>Volatile because <see cref="SocketState"/> is read from the test thread while the
    /// session handler writes it.</summary>
    private volatile System.Net.WebSockets.WebSocket? _socket;

    /// <summary>Volatile for the same reason as <see cref="_socket"/>; see <see cref="FramesCapturedWhenAnswering"/>.</summary>
    private volatile string[] _framesCapturedWhenAnswering = [];

    /// <summary>Volatile for the same reason as <see cref="_socket"/>; see <see cref="UpgradeHeaders"/>.</summary>
    private volatile IReadOnlyDictionary<string, string> _upgradeHeaders = NoHeaders;

    /// <summary>Volatile for the same reason as <see cref="_socket"/>; see <see cref="UpgradeRequestUri"/>.</summary>
    private volatile string? _upgradeRequestUri;

    /// <summary>The stream beneath <see cref="_socket"/>, for the one frame the socket will not write.</summary>
    private volatile Stream? _transport;

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
    /// When <see langword="true"/>, the session holds the socket open after its events and answers
    /// the client's close frame with <c>1000</c> and no reason the moment it arrives.
    /// </summary>
    /// <remarks>
    /// This is the live vendor's answer to a client that closes: <c>1000</c>, measured about 1.1 s
    /// after the bridge's close (the change's <c>H-healthy-hangup.log</c>). The fake answers at once,
    /// because nothing a test asserts depends on that interval.
    /// </remarks>
    public bool HoldOpenUntilClientCloses { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the session holds the socket open after its events and, once the
    /// client's close frame arrives, keeps holding without answering until the test calls
    /// <see cref="AnswerClientCloseAsync"/>.
    /// </summary>
    /// <remarks>
    /// While it holds, <see cref="SendEventAsync"/> still sends: a vendor that keeps talking after the
    /// client's close. A test chooses the moment of the answer, and its code, instead of a timer.
    /// </remarks>
    public bool AnswerClientCloseOnRequest { get; set; }

    /// <summary>
    /// Completes with the status of the client's close frame when it arrives — the signal that the
    /// client has closed. Cancelled if the server is disposed first.
    /// </summary>
    /// <remarks>
    /// A .NET peer reads a close frame with no code as <see cref="WebSocketCloseStatus.NormalClosure"/>,
    /// so this can only report <see cref="WebSocketCloseStatus.Empty"/> if the socket reports no status.
    /// </remarks>
    public Task<WebSocketCloseStatus> ClientCloseReceived => _clientCloseReceived.Task;

    /// <summary>
    /// Headers of the upgrade request that opened the session, looked up case-insensitively (RFC 9110
    /// §5.1). Empty until a session is accepted; readable once <see cref="SessionUpdateReceived"/> has
    /// completed.
    /// </summary>
    public IReadOnlyDictionary<string, string> UpgradeHeaders => _upgradeHeaders;

    /// <summary>
    /// The upgrade request's target, path and query as sent (for example
    /// <c>/v1/realtime?model=gpt-realtime</c>), or <see langword="null"/> until a session is accepted.
    /// Readable once <see cref="SessionUpdateReceived"/> has completed.
    /// </summary>
    public string? UpgradeRequestUri => _upgradeRequestUri;

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

    public void Start()
    {
        var holds = (HoldOpenUntilDisposed ? 1 : 0)
            + (HoldOpenUntilClientCloses ? 1 : 0)
            + (AnswerClientCloseOnRequest ? 1 : 0);
        if (holds > 1)
        {
            throw new InvalidOperationException(
                "HoldOpenUntilDisposed, HoldOpenUntilClientCloses and AnswerClientCloseOnRequest each say how "
                + "the session ends. Set one of them, or none.");
        }

        _server.Start();
    }

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

    /// <summary>
    /// Sends one event on the live session socket, outside <see cref="EventsToSend"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="EventsToSend"/> is delivered as a single burst the moment the client's
    /// <c>session.update</c> lands, which cannot express an event that has to arrive <em>after</em>
    /// something the test does — a hangup on the Asterisk side, say. This can, because the test
    /// chooses the moment. It is deliberately refused while <see cref="EventsToSend"/> is non-empty:
    /// the burst runs on the session handler's thread, and a second writer on one socket is the
    /// concurrency violation this fake was rewritten to stop hiding (ADR-0045 rule 1). With the list
    /// empty the burst body never executes, so this is the only writer.
    /// </remarks>
    public async Task SendEventAsync(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (EventsToSend.Count > 0)
        {
            throw new InvalidOperationException(
                "SendEventAsync would race the EventsToSend burst on the same socket. Send every "
                + "event this way, or none of them.");
        }

        var ws = _socket ?? throw new InvalidOperationException(
            "No session has been accepted yet — wait on SessionUpdateReceived first.");

        if (Volatile.Read(ref _serverCloseSent) != 0)
        {
            throw new InvalidOperationException(
                "This session has sent its close frame, and nothing follows a close on the wire (RFC 6455 §5.5.1).");
        }

        // Sent raw rather than through SendJsonAsync, which swallows: a send the test asked for and
        // that silently did not happen would surface ten seconds later as an unexplained timeout.
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Tears down the server side of the live session with no close frame, so the client's pending
    /// read meets a connection that ended mid-stream.
    /// </summary>
    /// <remarks>
    /// This is the transport dying under a session, as opposed to the far end closing it politely.
    /// <see cref="System.Net.WebSockets.WebSocket.Abort"/> disposes the stream beneath the socket, and
    /// that stream owns the TCP connection, so the client reads end-of-stream rather than a close
    /// handshake. Use it with <see cref="HoldOpenUntilDisposed"/>: without the hold, the session has
    /// already closed by the time a test could call this, and nothing is left to tear down. Abort
    /// does not write to the socket, so unlike <see cref="SendEventAsync"/> it cannot race the
    /// <see cref="EventsToSend"/> burst.
    /// </remarks>
    public void Abort()
    {
        var ws = _socket ?? throw new InvalidOperationException(
            "No session has been accepted yet — wait on SessionUpdateReceived first.");

        ws.Abort();
    }

    /// <summary>
    /// Closes the live session from the server side with <paramref name="status"/> and
    /// <paramref name="description"/>, as a vendor that ends the session does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="WebSocketCloseStatus.Empty"/> sends a close that carries no code: a close frame with
    /// no payload, written on the transport directly. The socket's own close would write the reserved
    /// code 1005 into the payload instead, which RFC 6455 §7.4.1 forbids on the wire and a .NET client
    /// rejects as a protocol error, so it could never stand for a vendor that closed without a code.
    /// A .NET client reads the frame this sends as <see cref="WebSocketCloseStatus.NormalClosure"/>
    /// with an empty reason.
    /// </para>
    /// <para>
    /// Refused while <see cref="EventsToSend"/> is non-empty, for the single-writer reason
    /// <see cref="SendEventAsync"/> gives, and refused once this session has sent a close. It does not
    /// wait for the client's answer; <see cref="ClientCloseReceived"/> reports it.
    /// </para>
    /// </remarks>
    public async Task SendCloseAsync(WebSocketCloseStatus status, string? description)
    {
        if (status == WebSocketCloseStatus.Empty && !string.IsNullOrEmpty(description))
            throw new ArgumentException("A close that carries no code carries no reason either.", nameof(description));

        if (EventsToSend.Count > 0)
        {
            throw new InvalidOperationException(
                "SendCloseAsync would race the EventsToSend burst on the same socket. Send every "
                + "event with SendEventAsync, or none of them.");
        }

        var ws = _socket ?? throw new InvalidOperationException(
            "No session has been accepted yet — wait on SessionUpdateReceived first.");

        ClaimTheClose();
        await SendCloseFrameAsync(ws, status, description).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers the client's close frame with <paramref name="status"/> and
    /// <paramref name="description"/>, and releases the <see cref="AnswerClientCloseOnRequest"/> hold.
    /// </summary>
    /// <remarks>
    /// Only an <see cref="AnswerClientCloseOnRequest"/> session waits for this, and only after the
    /// client's close has arrived is there anything to answer; either missing is a mistake in the test
    /// and throws. A second answer is refused, because a session sends one close.
    /// </remarks>
    public async Task AnswerClientCloseAsync(WebSocketCloseStatus status, string description)
    {
        ArgumentNullException.ThrowIfNull(description);

        if (!AnswerClientCloseOnRequest)
        {
            throw new InvalidOperationException(
                "Only an AnswerClientCloseOnRequest session waits for its answer: HoldOpenUntilClientCloses "
                + "answers on its own, and the other endings never answer.");
        }

        if (!_clientCloseReceived.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException(
                "The client has not closed, so there is nothing to answer — wait on ClientCloseReceived "
                + "first, or close first with SendCloseAsync.");
        }

        var ws = _socket ?? throw new InvalidOperationException("No session has been accepted.");

        ClaimTheClose();
        await SendCloseFrameAsync(ws, status, description).ConfigureAwait(false);
        _closeAnswered.TrySetResult();
    }

    /// <summary>Claims the session's one close, or throws if it has been sent already.</summary>
    private void ClaimTheClose()
    {
        if (!TryClaimTheClose())
        {
            throw new InvalidOperationException(
                "This session has already sent its close frame; a session sends one.");
        }
    }

    private bool TryClaimTheClose() => Interlocked.Exchange(ref _serverCloseSent, 1) == 0;

    /// <summary>
    /// Puts the close on the wire without waiting for the peer's, and without swallowing: a close the
    /// test asked for and that did not happen would surface later as an unexplained timeout.
    /// </summary>
    private async Task SendCloseFrameAsync(
        System.Net.WebSockets.WebSocket ws, WebSocketCloseStatus status, string? description)
    {
        if (status != WebSocketCloseStatus.Empty)
        {
            await ws.CloseOutputAsync(status, description, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        // The claim above made this the session's only writer from here on, so these bytes cannot
        // interleave with a frame of the socket's.
        var transport = _transport ?? throw new InvalidOperationException("No session has been accepted.");
        await transport.WriteAsync(CloseFrameWithNoCode, CancellationToken.None).ConfigureAwait(false);
        await transport.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task HandleSessionAsync(WebSocketTestSession session)
    {
        var ws = session.WebSocket;
        var ct = session.ServerCancellationToken;

        // Snapshots of the upgrade, taken before the receive loop starts, so they are in place by the
        // time SessionUpdateReceived — which only that loop completes — lets a test read them.
        _upgradeHeaders = new Dictionary<string, string>(session.Headers, StringComparer.OrdinalIgnoreCase)
            .AsReadOnly();
        _upgradeRequestUri = session.RequestUri;
        _transport = session.Transport;
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
            try { await receiveTask.ConfigureAwait(false); } catch (OperationCanceledException) { /* the token was cancelled before the loop started */ }
            return;
        }

        if (HoldOpenUntilClientCloses)
        {
            // The client's close, or the end of the receive loop without one: a dropped connection, or
            // this server's disposal cancelling the read. Only a close is answered.
            await Task.WhenAny(_clientCloseReceived.Task, receiveTask).ConfigureAwait(false);
            if (_clientCloseReceived.Task.IsCompletedSuccessfully && TryClaimTheClose())
                await AnswerQuietlyAsync(ws).ConfigureAwait(false);

            try { await receiveTask.ConfigureAwait(false); } catch (OperationCanceledException) { /* the token was cancelled before the loop started */ }
            return;
        }

        if (AnswerClientCloseOnRequest)
        {
            // Hold until the test has answered, which AnswerClientCloseAsync does on the socket itself,
            // or until this server is disposed. The client's close does not end the hold: the test may
            // still send frames after it, and chooses when to answer.
            try { await _closeAnswered.Task.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* disposed before an answer: release the socket */ }
            try { await receiveTask.ConfigureAwait(false); } catch (OperationCanceledException) { /* the token was cancelled before the loop started */ }
            return;
        }

        // CloseOutputAsync, not CloseAsync: CloseAsync also *waits* for the peer's close frame, which
        // means receiving — and the drain below already owns the receive path. Two concurrent receives
        // on one socket is exactly the violation this fake used to hide behind `catch { }` on the
        // HttpListener substrate (§1.4). Sending the close frame and then draining until the client's
        // own close arrives keeps a single receiver and still completes the handshake.
        if (TryClaimTheClose())
            await CloseOutputAsync(ws).ConfigureAwait(false);
        try { await receiveTask.ConfigureAwait(false); } catch (OperationCanceledException) { /* the token was cancelled before the loop started */ }
    }

    /// <summary>
    /// The <see cref="HoldOpenUntilClientCloses"/> answer: <c>1000</c> with no reason, as the live
    /// vendor sent it.
    /// </summary>
    private static async Task AnswerQuietlyAsync(System.Net.WebSockets.WebSocket ws)
    {
        try
        {
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (WebSocketTestServer.IsSessionEnding(ex))
        {
            // The client dropped the connection after its close; nothing is left to answer.
        }
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
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // The socket's own ends: peer closed or aborted, token cancelled, socket disposed.
                    // Anything else is not a normal end of the session, so it faults this task.
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _clientCloseReceived.TrySetResult(ws.CloseStatus ?? WebSocketCloseStatus.Empty);
                    break;
                }
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
        _clientCloseReceived.TrySetCanceled();
    }
}
