using System.Net;
using System.Net.Sockets;
using Asterisk.NetAot.Abstractions;
using Microsoft.Extensions.Logging;

namespace Asterisk.NetAot.Agi.Server;

internal static partial class FastAgiServerLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "FastAGI server started on port {Port}")]
    public static partial void ServerStarted(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "FastAGI server stopped")]
    public static partial void ServerStopped(ILogger logger);
}

/// <summary>
/// Async FastAGI TCP server. Accepts connections from Asterisk
/// and dispatches them to mapped AGI scripts.
/// </summary>
public sealed class FastAgiServer : IAgiServer
{
    private readonly ILogger<FastAgiServer> _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; }
    public bool IsRunning { get; private set; }

    public FastAgiServer(int port, ILogger<FastAgiServer> logger)
    {
        Port = port;
        _logger = logger;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        IsRunning = true;

        FastAgiServerLog.ServerStarted(_logger, Port);

        // TODO: Accept loop with async connection handling
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _listener?.Stop();
        _cts?.Cancel();
        IsRunning = false;

        FastAgiServerLog.ServerStopped(_logger);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _listener?.Stop();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
