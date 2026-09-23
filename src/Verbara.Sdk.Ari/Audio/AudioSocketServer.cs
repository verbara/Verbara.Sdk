using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.Ari.Audio;

internal static partial class AudioSocketServerLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[AudioSocket] Server started: port={Port}")]
    public static partial void ServerStarted(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "[AudioSocket] Server stopped")]
    public static partial void ServerStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[AudioSocket] Connection accepted: remote={RemoteEndpoint} channel_id={ChannelId}")]
    public static partial void ConnectionAccepted(ILogger logger, string? remoteEndpoint, string channelId);

    [LoggerMessage(Level = LogLevel.Error, Message = "[AudioSocket] Connection error")]
    public static partial void ConnectionError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "[AudioSocket] Accept failed — the server stays bound and accepts again after a backoff")]
    public static partial void AcceptLoopFailed(ILogger logger, Exception exception);
}

/// <summary>
/// Listens for incoming AudioSocket TCP connections from Asterisk ExternalMedia channels.
/// Each connection becomes an IAudioStream.
/// </summary>
public sealed class AudioSocketServer : IAudioServer, IAsyncDisposable
{
    /// <summary>The wait after the first of a run of failed accepts.</summary>
    internal static readonly TimeSpan InitialAcceptBackoff = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest wait between failed accepts, however long the run.</summary>
    internal static readonly TimeSpan MaxAcceptBackoff = TimeSpan.FromSeconds(5);

    private readonly AudioServerOptions _options;
    private readonly ILogger<AudioSocketServer> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, AudioSocketSession> _streams = new();
    private readonly Subject<IAudioStream> _streamSubject = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _running;

    /// <summary>Observable that emits each new audio stream when a connection is established.</summary>
    public IObservable<IAudioStream> OnStreamConnected => _streamSubject;

    /// <summary>Get an active stream by channel ID.</summary>
    public IAudioStream? GetStream(string channelId) =>
        _streams.TryGetValue(channelId, out var session) ? session : null;

    /// <summary>All currently active audio streams.</summary>
    public IEnumerable<IAudioStream> ActiveStreams => _streams.Values;

    /// <summary>Number of currently active audio streams.</summary>
    public int ActiveStreamCount => _streams.Count;

    /// <summary>
    /// Whether the server is bound and accepting. Reads <c>false</c> from the moment a stop
    /// <em>begins</em>, not from the moment its teardown finishes, and is published through a
    /// barrier so the accept loop's thread cannot read a stale value.
    /// </summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public AudioSocketServer(AudioServerOptions options, ILogger<AudioSocketServer> logger)
        : this(options, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance whose accept backoff waits on <paramref name="timeProvider"/>, so a
    /// test can drive that wait with a fake clock instead of sitting out a real one.
    /// </summary>
    internal AudioSocketServer(
        AudioServerOptions options,
        ILogger<AudioSocketServer> logger,
        TimeProvider timeProvider)
    {
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Replaces the listener's accept when set. Settable by tests (via InternalsVisibleTo), so a test
    /// can make accepts fail on demand instead of exhausting file descriptors to get a failure.
    /// </summary>
    internal Func<CancellationToken, ValueTask<TcpClient>>? AcceptOverride { get; set; }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        // First, and before anything is built: a second start that got as far as replacing _cts
        // would leave the first accept loop running against a source nothing can cancel, and the
        // listener, the source and the loop task it overwrote all leaked.
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return ValueTask.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Parse(_options.ListenAddress), _options.AudioSocketPort);
        _listener.Start();

        AudioSocketServerLog.ServerStarted(_logger, _options.AudioSocketPort);

        _acceptLoop = AcceptLoopAsync(_cts.Token);
        return ValueTask.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var backoff = InitialAcceptBackoff;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await AcceptAsync(ct);
                backoff = InitialAcceptBackoff;
                client.NoDelay = true;

                if (_streams.Count >= _options.MaxConcurrentStreams)
                {
                    client.Dispose();
                    continue;
                }

                // Started inline on the loop's own continuation and not through `Task.Run`, which is
                // how this server has always handed a connection over and is left unchanged: `ct`
                // travels with it as an argument, so there is no scheduler hand-off for CA2016 to
                // forward a token to. The sibling loop in `AriOutboundListener` does use `Task.Run`,
                // and its comment there claiming this method does the same is stale.
                _ = HandleConnectionAsync(client, ct);
                continue;
            }
            catch (OperationCanceledException)
            {
                // The stop path. `ct` is `_cts.Token`, cancelled by `StopAsync` and so by
                // `DisposeAsync`, and the pending accept ended with it. Nothing is meant to be
                // accepted after that, so the loop ends rather than backing off.
                break;
            }
            catch (ObjectDisposedException)
            {
                // The Windows shape of that same stop: `Stop()` disposes the underlying socket under
                // a pending accept and the accept surfaces the disposal. NOT reached on Linux, where
                // `Stop()` on a pending accept raises `SocketException(OperationAborted)` and the
                // filtered catch below takes it instead — this block is live on Windows, not dead
                // code.
                break;
            }
            catch (SocketException) when (!IsRunning)
            {
                // The Linux shape of that same stop, and the arm `StopAsync` had to be reordered
                // before it could exist: `StopAsync` now clears `_running` BEFORE it calls `Stop()`,
                // so an accept aborted by that `Stop()` always arrives with `IsRunning` already
                // false. Measured on a real loopback listener, this is the shape 7 stops in 10 take,
                // against 3 in 10 for the `OperationCanceledException` above — so without this arm a
                // routine shutdown would be reported as an accept failure most of the time.
                // `IsRunning` is the discriminator and not `ct`, because `StopAsync` cancels `_cts`
                // only after `Stop()` returns — a filter on `ct.IsCancellationRequested` would race
                // that ordering.
                break;
            }
            catch (SocketException ex)
            {
                // An accept that failed while the server is still meant to be running: EMFILE/ENFILE,
                // a connection aborted in the backlog, ENOBUFS. Ending the loop here would leave
                // `IsRunning` reporting true over a socket still in LISTEN — the kernel would keep
                // completing handshakes nobody reads, so Asterisk's ExternalMedia channels would hang
                // instead of being refused, and `StartAsync` could not restart the loop because
                // `_running` is already 1. So it is logged and the loop keeps accepting.
                AudioSocketServerLog.AcceptLoopFailed(_logger, ex);
            }

            // A failure that persists fails every accept at once, and without a pause this loop would
            // spin and log without bound. The wait doubles with each consecutive failure up to the
            // cap, and a successful accept above starts it over.
            try
            {
                await Task.Delay(backoff, _timeProvider, ct);
            }
            catch (OperationCanceledException)
            {
                // Same stop path as above, caught while waiting out a backoff rather than while
                // accepting: `_cts` was cancelled by `StopAsync`. The loop ends.
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxAcceptBackoff.Ticks));
        }
    }

    private ValueTask<TcpClient> AcceptAsync(CancellationToken ct) =>
        AcceptOverride?.Invoke(ct) ?? _listener!.AcceptTcpClientAsync(ct);

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            await using var session = new AudioSocketSession(client.GetStream(), _options.DefaultFormat);
            session.Start();

            try
            {
                // Wait for UUID frame to set ChannelId
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_options.IdleTimeout);

                // Poll for ChannelId to be set (set by ReadPump when UUID frame arrives)
                while (string.IsNullOrEmpty(session.ChannelId) && !timeoutCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(10, timeoutCts.Token);
                }

                if (string.IsNullOrEmpty(session.ChannelId))
                    return;

                var endpoint = client.Client.RemoteEndPoint?.ToString();
                AudioSocketServerLog.ConnectionAccepted(_logger, endpoint, session.ChannelId);

                _streams.TryAdd(session.ChannelId, session);
                _streamSubject.OnNext(session);

                // Wait for session to disconnect
                var tcs = new TaskCompletionSource();
                using var sub = session.StateChanges.Subscribe(state =>
                {
                    if (state is AudioStreamState.Disconnected or AudioStreamState.Error)
                        tcs.TrySetResult();
                });

                // If already disconnected
                if (!session.IsConnected)
                    tcs.TrySetResult();

                await tcs.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Two endings share this catch, and both are this one connection being wound up
                // rather than anything the server needs to report. The first is the stop token:
                // `ct` is `_cts.Token`, cancelled by `StopAsync` and `DisposeAsync`, and it is what
                // ends `await tcs.Task.WaitAsync(ct)`, the wait for the session to disconnect. The
                // second is the idle deadline this method schedules itself at the top of the `try`,
                // `timeoutCts.CancelAfter(_options.IdleTimeout)`: a connection that never sends its
                // UUID frame is abandoned when it expires, and the in-flight
                // `Task.Delay(10, timeoutCts.Token)` is what raises it. That deadline's other route
                // out — expiring between two delays, so the `while` condition simply goes false —
                // already returns silently a few lines below, so neither of its routes is reported
                // and this catch adds no silence of its own. The `finally` deregisters the session
                // on every path, and the enclosing `await using` and `using (client)` release it
                // and then close the connection.
                /* Best effort — the connection is being wound up */
            }
            catch (Exception ex)
            {
                AudioSocketServerLog.ConnectionError(_logger, ex);
            }
            finally
            {
                _streams.TryRemove(session.ChannelId, out _);
            }
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        // The running flag goes down FIRST, before anything that can abort a pending accept. The
        // accept loop tells a stop from a failure by reading it, so an abort that reached the loop
        // while the flag was still up would have the loop reporting the server's own shutdown as a
        // failure. Clearing it here also makes a second stop — or a DisposeAsync racing this one —
        // a no-op rather than a second teardown.
        if (Interlocked.Exchange(ref _running, 0) == 0)
            return;

        _listener?.Stop();

        if (_cts is not null)
            await _cts.CancelAsync();

        if (_acceptLoop is not null)
            await _acceptLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Dispose all active sessions
        foreach (var session in _streams.Values)
            await session.DisposeAsync();
        _streams.Clear();

        AudioSocketServerLog.ServerStopped(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning) await StopAsync();
        _streamSubject.OnCompleted();
        _streamSubject.Dispose();
        _cts?.Dispose();
    }
}
