using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Verbara.Sdk.VoiceAi.AudioSocket.Diagnostics;
using Verbara.Sdk.VoiceAi.AudioSocket.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.VoiceAi.AudioSocket;

/// <summary>
/// TCP server that accepts AudioSocket connections from Asterisk.
/// Implements <see cref="IHostedService"/> for DI lifecycle management.
/// </summary>
public sealed class AudioSocketServer : IHostedService, IAsyncDisposable
{
    /// <summary>The wait after the first of a run of failed accepts.</summary>
    internal static readonly TimeSpan InitialAcceptBackoff = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest wait between failed accepts, however long the run.</summary>
    internal static readonly TimeSpan MaxAcceptBackoff = TimeSpan.FromSeconds(5);

    private readonly AudioSocketOptions _options;
    private readonly ILogger<AudioSocketServer> _logger;
    private readonly TimeProvider _timeProvider;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<Guid, AudioSocketSession> _sessions = new();
    private readonly Meter _instanceMeter;

    /// <summary>Raised when a new AudioSocket session has been established and the UUID frame received.</summary>
    public event Func<AudioSocketSession, ValueTask>? OnSessionStarted;

    /// <summary>The actual port the server is listening on. Available after <see cref="StartAsync"/>.</summary>
    public int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;

    /// <summary>Number of currently active sessions.</summary>
    public int ActiveSessionCount => _sessions.Count;

    /// <summary>Initializes a new instance.</summary>
    public AudioSocketServer(AudioSocketOptions options, ILogger<AudioSocketServer> logger)
        : this(options, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance whose accept backoff and per-connection UUID timeout both wait on
    /// <paramref name="timeProvider"/>, so a test can drive those waits with a fake clock.
    /// </summary>
    internal AudioSocketServer(AudioSocketOptions options, ILogger<AudioSocketServer> logger, TimeProvider timeProvider)
    {
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
        _instanceMeter = new Meter("Verbara.Sdk.VoiceAi.AudioSocket", "1.0.0");
    }

    /// <summary>
    /// Replaces the listener's accept when set. Settable by tests (via InternalsVisibleTo), so a test
    /// can make accepts fail on demand instead of exhausting file descriptors to get a failure.
    /// </summary>
    internal Func<CancellationToken, ValueTask<TcpClient>>? AcceptOverride { get; set; }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        var endpoint = new IPEndPoint(IPAddress.Parse(_options.ListenAddress), _options.Port);
        _listener = new TcpListener(endpoint);
        _listener.Start(_options.MaxConcurrentSessions);
        AudioSocketLog.ServerListening(_logger, _options.ListenAddress, _options.Port);
        _instanceMeter.CreateObservableGauge<long>(
            "audiosocket.sessions.active",
            () => ActiveSessionCount,
            unit: "{sessions}",
            description: "Active AudioSocket sessions");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token), cancellationToken);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
            await _cts.CancelAsync().ConfigureAwait(false);

        _listener?.Stop();

        foreach (var session in _sessions.Values)
            await session.DisposeAsync().ConfigureAwait(false);

        _sessions.Clear();
        AudioSocketLog.ServerStopped(_logger);
    }

    internal async Task AcceptLoopAsync(CancellationToken ct)
    {
        var backoff = InitialAcceptBackoff;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await AcceptAsync(ct).ConfigureAwait(false);
                backoff = InitialAcceptBackoff;
                _ = Task.Run(() => HandleConnectionAsync(client, ct), ct);
                continue;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AudioSocketLog.AcceptError(_logger, ex);
            }

            // The catch-all keeps the server accepting through a failure, but a failure that persists
            // (EMFILE, say) fails every accept at once, and without a pause this loop would spin and log
            // without bound. The wait doubles with each consecutive failure up to the cap, and a
            // successful accept above starts it over.
            try
            {
                await Task.Delay(backoff, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxAcceptBackoff.Ticks));
        }
    }

    private ValueTask<TcpClient> AcceptAsync(CancellationToken ct) =>
        AcceptOverride?.Invoke(ct) ?? _listener!.AcceptTcpClientAsync(ct);

    /// <summary>
    /// Serves one accepted connection; <paramref name="ct"/> is the server's stopping token. Internal so
    /// a test can hand it a connection and its own token, then end the wait for the UUID frame at a
    /// moment it controls: by cancelling that token, or by moving a fake clock past the timeout.
    /// </summary>
    internal async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(_options.ConnectionTimeout, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            var stream = client.GetStream();
            var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: _options.ReceiveBufferSize));
            Guid channelId = default;
            var gotUuid = false;

            while (!linked.Token.IsCancellationRequested && !gotUuid)
            {
                ReadResult result;
                try
                {
                    result = await reader.ReadAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    // The wait for the UUID frame is over: the timeout fired or the server is
                    // stopping. Neither is a connection error; the no-UUID check below sorts them.
                    break;
                }

                var buffer = result.Buffer;

                if (AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame) &&
                    frame.Type == AudioSocketFrameType.Uuid)
                {
                    channelId = AudioSocketFrameCodec.ParseUuid(frame.Payload.Span);
                    reader.AdvanceTo(buffer.Start);
                    gotUuid = true;
                }
                else
                {
                    reader.AdvanceTo(buffer.Start, buffer.End);
                }

                if (result.IsCompleted) break;
            }

            if (!gotUuid)
            {
                // A stopping server closes the connection quietly: the client missed no deadline.
                if (!ct.IsCancellationRequested)
                    AudioSocketLog.NoUuidFrame(_logger);

                client.Dispose();
                return;
            }

            var sessionStart = Stopwatch.GetTimestamp();
            AudioSocketMetrics.ConnectionsAccepted.Add(1);
            using var activity = AudioSocketActivitySource.StartSession(channelId);

            var session = new AudioSocketSession(channelId, client, reader, _options.DefaultFormat, _logger);
            session.OnHangup += () =>
            {
                _sessions.TryRemove(channelId, out _);
                AudioSocketMetrics.ConnectionsClosed.Add(1);
                AudioSocketMetrics.SessionDurationMs.Record(
                    Stopwatch.GetElapsedTime(sessionStart).TotalMilliseconds);
            };

            if (_sessions.Count >= _options.MaxConcurrentSessions || !_sessions.TryAdd(channelId, session))
            {
                AudioSocketLog.SessionLimitReached(_logger, _options.MaxConcurrentSessions);
#pragma warning disable IDISP016 // False positive — session was just created, this is the first dispose
                await session.DisposeAsync().ConfigureAwait(false);
#pragma warning restore IDISP016
                return;
            }

            session.StartReadLoop();

            AudioSocketLog.SessionStarted(_logger, channelId);

            if (OnSessionStarted is not null)
                await OnSessionStarted(session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AudioSocketLog.HandleConnectionError(_logger, ex);
            client.Dispose();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _cts?.Dispose();
        _instanceMeter.Dispose();
        _listener = null;
    }
}
