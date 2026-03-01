using System.Net;
using System.Net.Sockets;

namespace Asterisk.NetAot.Ami.Transport;

/// <summary>
/// Async TCP server that accepts connections and wraps them as ISocketConnection
/// using System.IO.Pipelines. Used by the FastAGI server.
/// </summary>
public sealed class AsyncServerSocket : IAsyncDisposable
{
    private TcpListener? _listener;
    private volatile bool _disposed;

    private int _port;

    /// <summary>The actual port the server is listening on (resolved after Start if 0 was passed).</summary>
    public int Port => _listener is not null
        ? ((IPEndPoint)_listener.LocalEndpoint).Port
        : _port;

    public bool IsListening => _listener?.Server.IsBound ?? false;

    /// <param name="port">Port to listen on. Use 0 to let the OS assign a free port.</param>
    public AsyncServerSocket(int port)
    {
        _port = port;
    }

    /// <summary>Start listening for incoming connections.</summary>
    public void Start(int backlog = 100)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start(backlog);
    }

    /// <summary>Accept the next incoming connection as an ISocketConnection backed by Pipelines.</summary>
    public async ValueTask<ISocketConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_listener is null)
        {
            throw new InvalidOperationException("Server not started. Call Start() first.");
        }

        var client = await _listener.AcceptTcpClientAsync(cancellationToken);
        client.NoDelay = true;

        return PipelineSocketConnection.FromStream(client.GetStream());
    }

    /// <summary>Stop listening and release the port.</summary>
    public void Stop()
    {
        _listener?.Stop();
        _listener = null;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        Stop();
        return ValueTask.CompletedTask;
    }
}
