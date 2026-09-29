using System.Net;
using System.Net.Sockets;

namespace Verbara.Sdk.TestInfrastructure.WebSocket;

/// <summary>
/// A TCP listener that accepts every connection, reads the client's HTTP upgrade request and does not
/// answer it until the test says so — a far end that took the connection and went silent, or answered
/// late.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WebSocketTestServer"/> writes its <c>101</c> before invoking any handler, so nothing built
/// on it can hold a client inside <c>ClientWebSocket.ConnectAsync</c>. Here the upgrade <em>cannot</em>
/// complete on its own: once <see cref="RequestReceived"/> has fired, only the client's own deadline,
/// its caller's token or <see cref="AnswerUpgradeAsync"/> can end the connect, which is exactly the
/// property a connect-bound test pins. No clock is involved on this side.
/// </para>
/// <para>
/// <see cref="AnswerUpgradeAsync"/> completes the oldest held upgrade with a valid <c>101</c>, so an
/// upgrade answered inside the client's bound is pinned without a delay. The upgraded connection then
/// runs the handler given at construction (an <see cref="EndOfInputPeer"/>'s
/// <see cref="EndOfInputPeer.HandleSessionAsync"/>, say), or is held open when there is none.
/// </para>
/// <para>
/// Every accepted connection is held until disposal (ADR-0045 rule 2), and each one that delivers a
/// complete request is counted, so a reconnect loop's repeated dials are observable.
/// </para>
/// </remarks>
public sealed class StalledUpgradeListener : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _requestReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<WebSocketTestSession, Task>? _onUpgraded;
    private readonly List<Task> _held = [];
    private readonly Queue<HeldUpgrade> _unanswered = new();
    private readonly Lock _gate = new();
    private int _requests;
    private Task? _acceptLoop;

    /// <summary>A listener whose answered upgrades run <paramref name="onUpgraded"/>, or are held open when it is <see langword="null"/>.</summary>
    public StalledUpgradeListener(Func<WebSocketTestSession, Task>? onUpgraded = null)
    {
        _onUpgraded = onUpgraded;
    }

    /// <summary>The bound port. Valid after <see cref="Start"/>.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Completes once the first upgrade request has been read off the wire.</summary>
    public Task RequestReceived => _requestReceived.Task;

    /// <summary>How many complete upgrade requests have arrived so far.</summary>
    public int RequestCount => Volatile.Read(ref _requests);

    /// <summary>Starts accepting connections on a free loopback port.</summary>
    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>
    /// Answers the oldest held upgrade with a valid <c>101</c>. Completes once the response has been
    /// written, before the handler runs. Wait for <see cref="RequestReceived"/> first: with no upgrade
    /// held this throws.
    /// </summary>
    public Task AnswerUpgradeAsync()
    {
        HeldUpgrade? held;
        lock (_gate)
        {
            if (!_unanswered.TryDequeue(out held))
                throw new InvalidOperationException("No upgrade request is held; wait for RequestReceived first.");
        }

        held.Answer.TrySetResult();
        return held.Answered.Task;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Disposal cancelled the pending accept, or stopped the listener under it.
                return;
            }

            lock (_gate) _held.Add(HoldAsync(client));
        }
    }

    private async Task HoldAsync(TcpClient client)
    {
        using var _ = client;
        HeldUpgrade? held = null;
        try
        {
            var stream = client.GetStream();
            var (wsKey, requestUri, headers) = await WebSocketTestServer.ReadUpgradeRequestAsync(stream, _cts.Token).ConfigureAwait(false);
            if (wsKey is null)
                return;

            held = new HeldUpgrade();
            lock (_gate) _unanswered.Enqueue(held);
            Interlocked.Increment(ref _requests);
            _requestReceived.TrySetResult();

            // Never answer on its own: only the test, or disposal, ends the hold.
            await held.Answer.Task.WaitAsync(_cts.Token).ConfigureAwait(false);

            await WebSocketTestServer.SendUpgradeResponseAsync(stream, wsKey, _cts.Token).ConfigureAwait(false);
            using var ws = System.Net.WebSockets.WebSocket.CreateFromStream(
                stream,
                new System.Net.WebSockets.WebSocketCreationOptions { IsServer = true });
            held.Answered.TrySetResult();

            if (_onUpgraded is not null)
                await _onUpgraded(new WebSocketTestSession(ws, requestUri, headers, stream, _cts.Token)).ConfigureAwait(false);
            else
                await EndOfInputPeer.WaitForCancellationAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException
                                       or System.Net.WebSockets.WebSocketException)
        {
            // Disposed, or the client abandoned the dial — both are how a held connection ends. An
            // answer the test asked for and could not write is reported to the test that asked.
            if (held is not null && held.Answer.Task.IsCompleted)
                held.Answered.TrySetException(ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Cancel first, stop last, as WebSocketTestServer does: a Stop that overlaps a pending accept can
        // interleave into a NullReferenceException, and the accept loop ends on the cancellation alone.
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_acceptLoop is not null)
                await _acceptLoop.ConfigureAwait(false);
        }
        finally
        {
            _listener.Stop();
        }

        Task[] held;
        lock (_gate) held = [.. _held];
        await Task.WhenAll(held).ConfigureAwait(false);
        _cts.Dispose();
    }

    private sealed class HeldUpgrade
    {
        /// <summary>Set by the test: write the <c>101</c>.</summary>
        public TaskCompletionSource Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set by the holder once the <c>101</c> is on the wire, or faulted if it could not be.</summary>
        public TaskCompletionSource Answered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
