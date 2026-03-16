using System.Net;
using System.Net.Sockets;
using Asterisk.Sdk;
using Asterisk.Sdk.Agi.Mapping;
using Asterisk.Sdk.Ami.Transport;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.Agi.Server;

internal static partial class FastAgiServerLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[AGI] Server started: port={Port}")]
    public static partial void ServerStarted(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "[AGI] Server stopped")]
    public static partial void ServerStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[AGI] Connection accepted: remote={RemoteEndpoint}")]
    public static partial void ConnectionAccepted(ILogger logger, string? remoteEndpoint);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[AGI] Script executing: script={Script} channel={Channel}")]
    public static partial void ScriptExecuting(ILogger logger, string? script, string? channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AGI] No script mapped: script={Script}")]
    public static partial void NoScriptMapped(ILogger logger, string? script);

    [LoggerMessage(Level = LogLevel.Error, Message = "[AGI] Connection error")]
    public static partial void ConnectionError(ILogger logger, Exception exception);
}

/// <summary>
/// Async FastAGI TCP server. Accepts connections from Asterisk,
/// parses AGI requests, maps them to scripts and executes them.
/// </summary>
public sealed class FastAgiServer : IAgiServer
{
    private readonly IMappingStrategy _mappingStrategy;
    private readonly ILogger<FastAgiServer> _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public int Port { get; }
    public bool IsRunning { get; private set; }

    /// <summary>Maximum time allowed for a single AGI connection/script execution. Default: 5 minutes.</summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public FastAgiServer(int port, IMappingStrategy mappingStrategy, ILogger<FastAgiServer> logger)
    {
        Port = port;
        _mappingStrategy = mappingStrategy;
        _logger = logger;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        IsRunning = true;

        FastAgiServerLog.ServerStarted(_logger, Port);

        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                client.NoDelay = true;

                var endpoint = client.Client.RemoteEndPoint?.ToString();
                FastAgiServerLog.ConnectionAccepted(_logger, endpoint);

                // Handle each connection concurrently
                _ = HandleConnectionAsync(client, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (ObjectDisposedException)
        {
            // Listener stopped
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (ConnectionTimeout > TimeSpan.Zero)
        {
            timeoutCts.CancelAfter(ConnectionTimeout);
        }

        var connectionCt = timeoutCts.Token;

        await using var conn = PipelineSocketConnection.FromStream(client.GetStream());

        try
        {
            var reader = new FastAgiReader(conn.Input);
            var writer = new FastAgiWriter(conn.Output);

            // Read AGI request headers
            var request = await reader.ReadRequestAsync(connectionCt);

            FastAgiServerLog.ScriptExecuting(_logger, request.Script, request.Channel);

            // Map request to script
            var script = _mappingStrategy.Resolve(request);
            if (script is null)
            {
                FastAgiServerLog.NoScriptMapped(_logger, request.Script);
                return;
            }

            // Create channel and execute script
            var channel = new AgiChannel(writer, reader, _logger);
            await script.ExecuteAsync(channel, request, connectionCt);
        }
        catch (AgiHangupException)
        {
            // Normal hangup during script execution
        }
        catch (OperationCanceledException)
        {
            // Server shutting down
        }
        catch (Exception ex)
        {
            FastAgiServerLog.ConnectionError(_logger, ex);
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _listener?.Stop();

        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        IsRunning = false;
        FastAgiServerLog.ServerStopped(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning) await StopAsync();
        _cts?.Dispose();
    }
}
