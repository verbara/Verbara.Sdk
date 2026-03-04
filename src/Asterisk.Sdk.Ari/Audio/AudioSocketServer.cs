using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.Ari.Audio;

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
}

/// <summary>
/// Listens for incoming AudioSocket TCP connections from Asterisk ExternalMedia channels.
/// Each connection becomes an IAudioStream.
/// </summary>
public sealed class AudioSocketServer : IAudioServer, IAsyncDisposable
{
    private readonly AudioServerOptions _options;
    private readonly ILogger<AudioSocketServer> _logger;
    private readonly ConcurrentDictionary<string, AudioSocketSession> _streams = new();
    private readonly Subject<IAudioStream> _streamSubject = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    /// <summary>Observable that emits each new audio stream when a connection is established.</summary>
    public IObservable<IAudioStream> OnStreamConnected => _streamSubject;

    /// <summary>Get an active stream by channel ID.</summary>
    public IAudioStream? GetStream(string channelId) =>
        _streams.TryGetValue(channelId, out var session) ? session : null;

    /// <summary>All currently active audio streams.</summary>
    public IEnumerable<IAudioStream> ActiveStreams => _streams.Values;

    /// <summary>Number of currently active audio streams.</summary>
    public int ActiveStreamCount => _streams.Count;

    public bool IsRunning { get; private set; }

    public AudioSocketServer(AudioServerOptions options, ILogger<AudioSocketServer> logger)
    {
        _options = options;
        _logger = logger;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Parse(_options.ListenAddress), _options.AudioSocketPort);
        _listener.Start();
        IsRunning = true;

        AudioSocketServerLog.ServerStarted(_logger, _options.AudioSocketPort);

        _acceptLoop = AcceptLoopAsync(_cts.Token);
        return ValueTask.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                client.NoDelay = true;

                if (_streams.Count >= _options.MaxConcurrentStreams)
                {
                    client.Dispose();
                    continue;
                }

                _ = HandleConnectionAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var session = new AudioSocketSession(client.GetStream(), _options.DefaultFormat);
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
            {
                await session.DisposeAsync();
                client.Dispose();
                return;
            }

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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AudioSocketServerLog.ConnectionError(_logger, ex);
        }
        finally
        {
            _streams.TryRemove(session.ChannelId, out _);
            await session.DisposeAsync();
            client.Dispose();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _listener?.Stop();

        if (_cts is not null)
            await _cts.CancelAsync();

        if (_acceptLoop is not null)
            await _acceptLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Dispose all active sessions
        foreach (var session in _streams.Values)
            await session.DisposeAsync();
        _streams.Clear();

        IsRunning = false;
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
