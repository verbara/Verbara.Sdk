using System.Net;
using System.Net.Sockets;

namespace Verbara.Sdk.Ami.Transport;

/// <summary>
/// Async TCP server that accepts one connection per <see cref="AcceptAsync"/> call and returns it as an
/// <see cref="ISocketConnection"/> backed by System.IO.Pipelines.
/// </summary>
public sealed class AsyncServerSocket : IAsyncDisposable
{
    private TcpListener? _listener;
    private volatile bool _disposed;

    private readonly int _port;

    /// <summary>
    /// Test-only seam: when set, the accept runs this instead of the listener's own. Production never sets it.
    /// </summary>
    internal Func<CancellationToken, ValueTask<TcpClient>>? AcceptOverride { get; set; }

    /// <summary>The actual port the server is listening on (resolved after Start if 0 was passed).</summary>
    public int Port => _listener is not null
        ? ((IPEndPoint)_listener.LocalEndpoint).Port
        : _port;

    public bool IsListening => _listener?.Server.IsBound ?? false;

    /// <summary>Creates a server socket that listens on <paramref name="port"/> once <see cref="Start"/> is called.</summary>
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
    /// <remarks>
    /// The accepted connection is owned by this call until it is returned: when configuring it fails, it is closed and
    /// the original exception reaches the caller unchanged. Such an exception while <see cref="IsListening"/> is
    /// <see langword="true"/> belongs to that one connection, which has been closed; the listener keeps listening, so
    /// accept again.
    /// </remarks>
    public async ValueTask<ISocketConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_listener is null)
        {
            throw new InvalidOperationException("Server not started. Call Start() first.");
        }

        var client = await (AcceptOverride?.Invoke(cancellationToken) ?? _listener.AcceptTcpClientAsync(cancellationToken));
        // Nothing else owns the accepted connection until it is returned: if configuring it fails, it is closed here and
        // the original exception goes on to the caller unchanged.
        try
        {
            client.NoDelay = true;
            return PipelineSocketConnection.FromStream(client.GetStream());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            client.Dispose();
            throw;
        }
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
