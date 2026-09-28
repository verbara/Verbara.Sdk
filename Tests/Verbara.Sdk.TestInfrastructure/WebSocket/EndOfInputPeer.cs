using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Verbara.Sdk.TestInfrastructure.WebSocket;

/// <summary>What an <see cref="EndOfInputPeer"/> does once the client has sent its end of input.</summary>
public enum EndOfInputPeerMode
{
    /// <summary>
    /// Sends <see cref="EndOfInputPeer.OnEndOfInput"/> and then nothing more: it never answers, never
    /// closes, and does not answer a close from the client either. A vendor that went silent with the
    /// connection still open.
    /// </summary>
    NeverAnswer,

    /// <summary>
    /// Sends <see cref="EndOfInputPeer.OnEndOfInput"/>, then holds until the test calls
    /// <see cref="EndOfInputPeer.AnswerAsync"/>, which sends <see cref="EndOfInputPeer.Answer"/> and
    /// closes with <see cref="WebSocketCloseStatus.NormalClosure"/>.
    /// </summary>
    AnswerOnRequest,

    /// <summary>
    /// Sends <see cref="EndOfInputPeer.OnEndOfInput"/>, then drops the connection with no close frame.
    /// </summary>
    DropTcp,

    /// <summary>
    /// Sends <see cref="EndOfInputPeer.OnEndOfInput"/>, then one round of
    /// <see cref="EndOfInputPeer.Progress"/> per <see cref="EndOfInputPeer.SendFrameAsync"/> call: a
    /// vendor that is slow but still sending. <see cref="EndOfInputPeer.AnswerAsync"/> ends it as in
    /// <see cref="AnswerOnRequest"/>.
    /// </summary>
    FrameOnRequest,
}

/// <summary>One frame an <see cref="EndOfInputPeer"/> sends.</summary>
public readonly record struct PeerFrame(WebSocketMessageType Type, byte[] Payload)
{
    /// <summary>A text frame carrying <paramref name="text"/> as UTF-8.</summary>
    public static PeerFrame Text(string text) => new(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(text));

    /// <summary>A binary frame of <paramref name="length"/> zero bytes.</summary>
    public static PeerFrame Binary(int length) => new(WebSocketMessageType.Binary, new byte[length]);
}

/// <summary>
/// A vendor-agnostic WebSocket peer for the end of a streaming session: it speaks just enough of a
/// client's protocol to reach the client's end of input, and then does what its
/// <see cref="EndOfInputPeerMode"/> says.
/// </summary>
/// <remarks>
/// <para>
/// A streaming speech client ends its input with an in-band frame (a terminator, a flush, an end of
/// stream) and then waits for the vendor to end the session. What the vendor does next is the variable
/// under test: nothing, an answer, a dropped connection, or frames that keep coming. Each is a mode, and
/// the per-client matchers and frames are configured by the test project that knows the vendor
/// (<see cref="IsEndOfInput"/>, <see cref="Preamble"/>, <see cref="Replies"/>).
/// </para>
/// <para>
/// No mode paces itself on the wall clock. What happens after the end of input happens when the test
/// asks for it (<see cref="AnswerAsync"/>, <see cref="SendFrameAsync"/>), and what the peer saw is
/// published as signals (<see cref="EndOfInputSeen"/>, <see cref="ClientCloseSeen"/>), so a test that
/// drives a bound on a manual clock never races the peer (<c>ADR-0045</c>).
/// </para>
/// <para>
/// One session per peer: the signals are one-shot. The peer serves it on its own
/// <see cref="WebSocketTestServer"/> after <see cref="Start"/>, or on a session another listener
/// accepted, through <see cref="HandleSessionAsync"/>.
/// </para>
/// </remarks>
public sealed class EndOfInputPeer : IAsyncDisposable
{
    private readonly TaskCompletionSource _endOfInputSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _clientCloseSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<PeerRequest> _requests = Channel.CreateUnbounded<PeerRequest>();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private WebSocketTestServer? _server;
    private int _textFrames;
    private int _binaryFrames;

    /// <summary>A peer that acts on the end of input as <paramref name="mode"/> says.</summary>
    public EndOfInputPeer(EndOfInputPeerMode mode)
    {
        Mode = mode;
    }

    /// <summary>What the peer does once the end of input has arrived.</summary>
    public EndOfInputPeerMode Mode { get; }

    /// <summary>Frames sent as soon as the session opens.</summary>
    public IReadOnlyList<PeerFrame> Preamble { get; init; } = [];

    /// <summary>
    /// The frames sent in reply to a client text frame that is not the end of input; empty for none.
    /// </summary>
    public Func<string, IReadOnlyList<PeerFrame>> Replies { get; init; } = static _ => [];

    /// <summary>
    /// True for the client's in-band end-of-input frame. <see langword="null"/> makes the client's
    /// close frame the end of input.
    /// </summary>
    public Func<string, bool>? IsEndOfInput { get; init; }

    /// <summary>Frames sent the moment the end of input arrives, before the mode acts.</summary>
    public IReadOnlyList<PeerFrame> OnEndOfInput { get; init; } = [];

    /// <summary>The round of frames <see cref="SendFrameAsync"/> sends, in <see cref="EndOfInputPeerMode.FrameOnRequest"/>.</summary>
    public IReadOnlyList<PeerFrame> Progress { get; init; } = [];

    /// <summary>
    /// The frames <see cref="AnswerAsync"/> sends before its <c>1000</c> close. Not sent when the end
    /// of input was the client's close frame: that answer is the close alone.
    /// </summary>
    public IReadOnlyList<PeerFrame> Answer { get; init; } = [];

    /// <summary>The bound loopback port. Valid after <see cref="Start"/>.</summary>
    public int Port => _server?.Port ?? throw new InvalidOperationException("Start the peer before reading its port.");

    /// <summary>Completes when the client's end of input has arrived, before the peer acts on it.</summary>
    public Task EndOfInputSeen => _endOfInputSeen.Task;

    /// <summary>Completes when the client's close frame has arrived.</summary>
    public Task ClientCloseSeen => _clientCloseSeen.Task;

    /// <summary>Complete text frames received from the client, the end of input included.</summary>
    public int TextFramesReceived => Volatile.Read(ref _textFrames);

    /// <summary>Complete binary frames received from the client.</summary>
    public int BinaryFramesReceived => Volatile.Read(ref _binaryFrames);

    /// <summary>Starts this peer's own server on a free loopback port.</summary>
    public void Start()
    {
        if (_server is not null)
            throw new InvalidOperationException("The peer is already started.");

        _server = new WebSocketTestServer(HandleSessionAsync);
        _server.Start();
    }

    /// <summary>
    /// Sends one round of <see cref="Progress"/> once the end of input has arrived. Completes when it
    /// has been written. <see cref="EndOfInputPeerMode.FrameOnRequest"/> only.
    /// </summary>
    public Task SendFrameAsync()
    {
        if (Mode != EndOfInputPeerMode.FrameOnRequest)
            throw new InvalidOperationException($"A peer in {Mode} sends no frame on request.");

        return Enqueue(PeerRequestKind.Frame);
    }

    /// <summary>
    /// Answers the end of input: <see cref="Answer"/>, then a <c>1000</c> close. Completes when the
    /// close has been sent. <see cref="EndOfInputPeerMode.AnswerOnRequest"/> and
    /// <see cref="EndOfInputPeerMode.FrameOnRequest"/> only.
    /// </summary>
    public Task AnswerAsync()
    {
        if (Mode is not (EndOfInputPeerMode.AnswerOnRequest or EndOfInputPeerMode.FrameOnRequest))
            throw new InvalidOperationException($"A peer in {Mode} does not answer.");

        return Enqueue(PeerRequestKind.Answer);
    }

    /// <summary>Serves one session: the per-connection handler, also for a session another listener accepted.</summary>
    public async Task HandleSessionAsync(WebSocketTestSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var ws = session.WebSocket;
        var ct = session.ServerCancellationToken;
        Task? responder = null;
        try
        {
            await SendAsync(ws, Preamble, ct).ConfigureAwait(false);

            var buffer = new byte[1 << 16];
            using var message = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _clientCloseSeen.TrySetResult();
                    if (IsEndOfInput is null && !_endOfInputSeen.Task.IsCompleted)
                        await ActOnCloseFrameAsync(ws, ct).ConfigureAwait(false);
                    break;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                var payload = message.ToArray();
                message.SetLength(0);

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    Interlocked.Increment(ref _binaryFrames);
                    continue;
                }

                Interlocked.Increment(ref _textFrames);
                var text = Encoding.UTF8.GetString(payload);
                if (IsEndOfInput is not null && !_endOfInputSeen.Task.IsCompleted && IsEndOfInput(text))
                {
                    _endOfInputSeen.TrySetResult();
                    await SendAsync(ws, OnEndOfInput, ct).ConfigureAwait(false);
                    if (Mode == EndOfInputPeerMode.DropTcp)
                    {
                        ws.Abort();
                        return;
                    }

                    // Keep reading while the answer waits for the test, so a close from the client is
                    // still seen. A NeverAnswer peer just keeps reading.
                    if (Mode is EndOfInputPeerMode.AnswerOnRequest or EndOfInputPeerMode.FrameOnRequest)
                        responder = ServeRequestsAsync(ws, sendAnswerFrames: true, ct);
                    continue;
                }

                await SendAsync(ws, Replies(text), ct).ConfigureAwait(false);
            }

            if (responder is not null)
                await responder.ConfigureAwait(false);
        }
        catch (Exception ex) when (WebSocketTestServer.IsSessionEnding(ex))
        {
            // The client dropped the connection, aborted its socket, or the server was disposed: the
            // session is over, which is an outcome the test observes on the client side.
        }
        finally
        {
            FailUnservedRequests();
        }
    }

    /// <summary>The end of input was the client's close frame: act on it without reading further.</summary>
    private async Task ActOnCloseFrameAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        _endOfInputSeen.TrySetResult();
        switch (Mode)
        {
            case EndOfInputPeerMode.DropTcp:
                ws.Abort();
                break;

            case EndOfInputPeerMode.AnswerOnRequest or EndOfInputPeerMode.FrameOnRequest:
                await ServeRequestsAsync(ws, sendAnswerFrames: false, ct).ConfigureAwait(false);
                break;

            default:
                // NeverAnswer: the socket has received the close, so there is nothing left to read.
                // Hold it, unanswered, until the server is disposed.
                await WaitForCancellationAsync(ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task ServeRequestsAsync(System.Net.WebSockets.WebSocket ws, bool sendAnswerFrames, CancellationToken ct)
    {
        await foreach (var request in _requests.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                if (request.Kind == PeerRequestKind.Frame)
                {
                    await SendAsync(ws, Progress, ct).ConfigureAwait(false);
                    request.Done.TrySetResult();
                    continue;
                }

                if (sendAnswerFrames)
                    await SendAsync(ws, Answer, ct).ConfigureAwait(false);

                await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }

                request.Done.TrySetResult();
                return;
            }
            catch (Exception ex) when (WebSocketTestServer.IsSessionEnding(ex))
            {
                // The session ended under the request: the test that asked for it gets the reason, and
                // the receive loop sees the ending on its own.
                request.Done.TrySetException(ex);
                return;
            }
        }
    }

    private async Task SendAsync(System.Net.WebSockets.WebSocket ws, IReadOnlyList<PeerFrame> frames, CancellationToken ct)
    {
        if (frames.Count == 0)
            return;

        // The receive loop's replies and the responder's frames share one socket, which takes one send
        // at a time.
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var frame in frames)
                await ws.SendAsync(frame.Payload, frame.Type, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private Task Enqueue(PeerRequestKind kind)
    {
        var request = new PeerRequest(kind);
        if (!_requests.Writer.TryWrite(request))
            request.Done.TrySetException(new InvalidOperationException("The session has ended; nothing can be sent."));
        return request.Done.Task;
    }

    private void FailUnservedRequests()
    {
        _requests.Writer.TryComplete();
        while (_requests.Reader.TryRead(out var request))
            request.Done.TrySetException(new InvalidOperationException("The session ended before the request was served."));
    }

    /// <summary>Completes, without throwing, once <paramref name="ct"/> is cancelled: a hold with no clock in it.</summary>
    internal static async Task WaitForCancellationAsync(CancellationToken ct)
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (ct.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), released).ConfigureAwait(false))
        {
            await released.Task.ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync().ConfigureAwait(false);

        FailUnservedRequests();
        _sendLock.Dispose();
    }

    private enum PeerRequestKind
    {
        Frame,
        Answer,
    }

    private sealed class PeerRequest(PeerRequestKind kind)
    {
        public PeerRequestKind Kind { get; } = kind;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
