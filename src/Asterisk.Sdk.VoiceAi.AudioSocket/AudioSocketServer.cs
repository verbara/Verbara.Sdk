using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Asterisk.Sdk.VoiceAi.AudioSocket.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.VoiceAi.AudioSocket;

/// <summary>
/// TCP server that accepts AudioSocket connections from Asterisk.
/// Implements <see cref="IHostedService"/> for DI lifecycle management.
/// </summary>
public sealed class AudioSocketServer : IHostedService, IAsyncDisposable
{
    private readonly AudioSocketOptions _options;
    private readonly ILogger<AudioSocketServer> _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<Guid, AudioSocketSession> _sessions = new();

    /// <summary>Raised when a new AudioSocket session has been established and the UUID frame received.</summary>
    public event Func<AudioSocketSession, ValueTask>? OnSessionStarted;

    /// <summary>Number of currently active sessions.</summary>
    public int ActiveSessionCount => _sessions.Count;

    /// <summary>Initializes a new instance.</summary>
    public AudioSocketServer(AudioSocketOptions options, ILogger<AudioSocketServer> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        var endpoint = new IPEndPoint(IPAddress.Parse(_options.ListenAddress), _options.Port);
        _listener = new TcpListener(endpoint);
        _listener.Start(_options.MaxConcurrentSessions);
        AudioSocketLog.ServerListening(_logger, _options.ListenAddress, _options.Port);
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

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleConnectionAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AudioSocketLog.AcceptError(_logger, ex);
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(_options.ConnectionTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            var stream = client.GetStream();
            var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: _options.ReceiveBufferSize));
            Guid channelId = default;
            var gotUuid = false;

            while (!linked.Token.IsCancellationRequested && !gotUuid)
            {
                var result = await reader.ReadAsync(linked.Token).ConfigureAwait(false);
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
                AudioSocketLog.NoUuidFrame(_logger);
                client.Dispose();
                return;
            }

            var session = new AudioSocketSession(channelId, client, reader, _options.DefaultFormat, _logger);
            session.OnHangup += () => _sessions.TryRemove(channelId, out _);

            if (_sessions.Count >= _options.MaxConcurrentSessions || !_sessions.TryAdd(channelId, session))
            {
                AudioSocketLog.SessionLimitReached(_logger, _options.MaxConcurrentSessions);
                await session.DisposeAsync().ConfigureAwait(false);
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
        _listener = null;
    }
}
