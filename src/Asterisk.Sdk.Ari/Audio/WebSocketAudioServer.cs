using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.Ari.Audio;

internal static partial class WebSocketAudioServerLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[WebSocketAudio] Server started: port={Port}")]
    public static partial void ServerStarted(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "[WebSocketAudio] Server stopped")]
    public static partial void ServerStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[WebSocketAudio] Connection accepted: remote={RemoteEndpoint} channel_id={ChannelId}")]
    public static partial void ConnectionAccepted(ILogger logger, string? remoteEndpoint, string channelId);

    [LoggerMessage(Level = LogLevel.Error, Message = "[WebSocketAudio] Connection error")]
    public static partial void ConnectionError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[WebSocketAudio] Invalid upgrade request")]
    public static partial void InvalidUpgrade(ILogger logger);
}

/// <summary>
/// Listens for incoming WebSocket connections from Asterisk ExternalMedia channels.
/// Uses TcpListener + manual HTTP upgrade + WebSocket.CreateFromStream() (ADR-1).
/// </summary>
public sealed class WebSocketAudioServer : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly AudioServerOptions _options;
    private readonly ILogger<WebSocketAudioServer> _logger;
    private readonly ConcurrentDictionary<string, WebSocketAudioSession> _streams = new();
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

    public WebSocketAudioServer(AudioServerOptions options, ILogger<WebSocketAudioServer> logger)
    {
        _options = options;
        _logger = logger;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Parse(_options.ListenAddress), _options.WebSocketPort);
        _listener.Start();
        IsRunning = true;

        WebSocketAudioServerLog.ServerStarted(_logger, _options.WebSocketPort);

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
        try
        {
            var stream = client.GetStream();

            // Read HTTP upgrade request
            var (wsKey, channelId) = await ReadUpgradeRequestAsync(stream, ct);
            if (wsKey is null || channelId is null)
            {
                WebSocketAudioServerLog.InvalidUpgrade(_logger);
                client.Dispose();
                return;
            }

            // Send HTTP 101 response
            await SendUpgradeResponseAsync(stream, wsKey, ct);

            // Create ManagedWebSocket
            var webSocket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });

            var session = new WebSocketAudioSession(webSocket, channelId, _options.DefaultFormat);
            session.Start();

            var endpoint = client.Client.RemoteEndPoint?.ToString();
            WebSocketAudioServerLog.ConnectionAccepted(_logger, endpoint, channelId);

            _streams.TryAdd(channelId, session);
            _streamSubject.OnNext(session);

            // Wait for session to disconnect
            var tcs = new TaskCompletionSource();
            using var sub = session.StateChanges.Subscribe(state =>
            {
                if (state is AudioStreamState.Disconnected or AudioStreamState.Error)
                    tcs.TrySetResult();
            });

            if (!session.IsConnected)
                tcs.TrySetResult();

            await tcs.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            WebSocketAudioServerLog.ConnectionError(_logger, ex);
        }
        finally
        {
            // Clean up — find and remove any session associated with this connection
            foreach (var kvp in _streams)
            {
                if (!kvp.Value.IsConnected)
                {
                    if (_streams.TryRemove(kvp.Key, out var removed))
                        await removed.DisposeAsync();
                }
            }
            client.Dispose();
        }
    }

    /// <summary>
    /// Read HTTP upgrade request headers, extract Sec-WebSocket-Key and channel ID from URL path.
    /// Expected URL: /ws/{channelId} or /{channelId}
    /// </summary>
    internal static async Task<(string? wsKey, string? channelId)> ReadUpgradeRequestAsync(
        Stream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var totalRead = 0;

        // Read until we get the full HTTP headers (double CRLF)
        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead), ct);
            if (bytesRead == 0) return (null, null);
            totalRead += bytesRead;

            if (Encoding.ASCII.GetString(buffer, 0, totalRead).Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }

        var request = Encoding.ASCII.GetString(buffer, 0, totalRead);
        var lines = request.Split("\r\n");
        if (lines.Length == 0) return (null, null);

        // Parse request line: GET /ws/{channelId} HTTP/1.1
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return (null, null);

        var path = requestLine[1];
        var channelId = path.TrimStart('/').Split('/').LastOrDefault()?.Split('?').FirstOrDefault();

        string? wsKey = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            {
                wsKey = line["Sec-WebSocket-Key:".Length..].Trim();
                break;
            }
        }

        return (wsKey, channelId);
    }

    /// <summary>Send HTTP 101 Switching Protocols response.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "SHA1 is required by RFC 6455 WebSocket protocol")]
    internal static async Task SendUpgradeResponseAsync(Stream stream, string wsKey, CancellationToken ct)
    {
        var acceptKey = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(wsKey + WebSocketGuid)));

        var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {acceptKey}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        await stream.FlushAsync(ct);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _listener?.Stop();

        if (_cts is not null)
            await _cts.CancelAsync();

        if (_acceptLoop is not null)
            await _acceptLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        foreach (var session in _streams.Values)
            await session.DisposeAsync();
        _streams.Clear();

        IsRunning = false;
        WebSocketAudioServerLog.ServerStopped(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning) await StopAsync();
        _streamSubject.OnCompleted();
        _streamSubject.Dispose();
        _cts?.Dispose();
    }
}
