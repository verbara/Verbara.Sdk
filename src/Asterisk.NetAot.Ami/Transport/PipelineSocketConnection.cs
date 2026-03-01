using System.IO.Pipelines;
using System.Net.Sockets;

namespace Asterisk.NetAot.Ami.Transport;

/// <summary>
/// TCP socket connection backed by System.IO.Pipelines for zero-copy I/O.
/// </summary>
public sealed class PipelineSocketConnection : ISocketConnection
{
    private TcpClient? _client;
    private Stream? _stream;
    private Pipe? _inputPipe;
    private Pipe? _outputPipe;

    public bool IsConnected => _client?.Connected ?? false;

    public PipeReader Input => _inputPipe?.Reader ?? throw new InvalidOperationException("Not connected");
    public PipeWriter Output => _outputPipe?.Writer ?? throw new InvalidOperationException("Not connected");

    public async ValueTask ConnectAsync(string hostname, int port, bool useSsl = false, CancellationToken cancellationToken = default)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(hostname, port, cancellationToken);
        _stream = _client.GetStream();

        // TODO: Wrap with SslStream if useSsl

        _inputPipe = new Pipe();
        _outputPipe = new Pipe();

        // TODO: Start background pump tasks (socket -> inputPipe, outputPipe -> socket)
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync();
        }

        _client?.Dispose();
        _client = null;
    }

    public ValueTask DisposeAsync() => CloseAsync();
}
