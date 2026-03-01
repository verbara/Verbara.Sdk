using System.Buffers;
using System.IO.Pipelines;
using System.Net.Security;
using System.Net.Sockets;

namespace Asterisk.NetAot.Ami.Transport;

/// <summary>
/// TCP socket connection backed by System.IO.Pipelines for zero-copy I/O.
/// Runs two background pump tasks:
///   - Read pump: socket stream → input PipeWriter (consumed via Input PipeReader)
///   - Write pump: output PipeReader (written via Output PipeWriter) → socket stream
/// </summary>
public sealed class PipelineSocketConnection : ISocketConnection
{
    private const int MinimumBufferSize = 4096;

    private TcpClient? _client;
    private Stream? _stream;
    private Pipe? _inputPipe;
    private Pipe? _outputPipe;
    private CancellationTokenSource? _pumpCts;
    private Task? _readPumpTask;
    private Task? _writePumpTask;
    private volatile bool _disposed;

    public bool IsConnected => !_disposed && (_stream is not null) && (_client?.Connected ?? true);

    public PipeReader Input => _inputPipe?.Reader ?? throw new InvalidOperationException("Not connected");
    public PipeWriter Output => _outputPipe?.Writer ?? throw new InvalidOperationException("Not connected");

    public async ValueTask ConnectAsync(string hostname, int port, bool useSsl = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_client is not null)
        {
            throw new InvalidOperationException("Already connected. Call CloseAsync first.");
        }

        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(hostname, port, cancellationToken);

        Stream stream = _client.GetStream();

        if (useSsl)
        {
            var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
            await sslStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = hostname },
                cancellationToken);
            stream = sslStream;
        }

        _stream = stream;

        var pipeOptions = new PipeOptions(
            minimumSegmentSize: MinimumBufferSize,
            useSynchronizationContext: false);

        _inputPipe = new Pipe(pipeOptions);
        _outputPipe = new Pipe(pipeOptions);
        _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var token = _pumpCts.Token;
        _readPumpTask = Task.Run(() => ReadPumpAsync(_stream, _inputPipe.Writer, token), CancellationToken.None);
        _writePumpTask = Task.Run(() => WritePumpAsync(_stream, _outputPipe.Reader, token), CancellationToken.None);
    }

    /// <summary>
    /// Reads from the network stream and writes into the input pipe.
    /// The AMI protocol reader consumes from Input (the pipe's Reader side).
    /// </summary>
    private static async Task ReadPumpAsync(Stream stream, PipeWriter writer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var memory = writer.GetMemory(MinimumBufferSize);
                var bytesRead = await stream.ReadAsync(memory, ct);

                if (bytesRead == 0)
                {
                    // Remote closed the connection
                    break;
                }

                writer.Advance(bytesRead);

                var result = await writer.FlushAsync(ct);
                if (result.IsCompleted || result.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (IOException)
        {
            // Socket closed / network error
        }
        catch (SocketException)
        {
            // Connection reset
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    /// <summary>
    /// Reads from the output pipe and writes to the network stream.
    /// The AMI protocol writer writes into Output (the pipe's Writer side).
    /// </summary>
    private static async Task WritePumpAsync(Stream stream, PipeReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted)
                {
                    break;
                }

                foreach (var segment in buffer)
                {
                    await stream.WriteAsync(segment, ct);
                }

                await stream.FlushAsync(ct);
                reader.AdvanceTo(buffer.End);

                if (result.IsCompleted || result.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (IOException)
        {
            // Socket closed / network error
        }
        catch (SocketException)
        {
            // Connection reset
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_pumpCts is not null)
        {
            await _pumpCts.CancelAsync();
        }

        // Complete pipes to unblock any pending reads/writes
        try { _inputPipe?.Writer.Complete(); } catch (InvalidOperationException) { }
        try { _outputPipe?.Reader.Complete(); } catch (InvalidOperationException) { }

        // Wait for pump tasks to finish
        if (_readPumpTask is not null)
        {
            await _readPumpTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (_writePumpTask is not null)
        {
            await _writePumpTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (_stream is not null)
        {
            await _stream.DisposeAsync();
            _stream = null;
        }

        _client?.Dispose();
        _client = null;

        _pumpCts?.Dispose();
        _pumpCts = null;

        _inputPipe = null;
        _outputPipe = null;
        _readPumpTask = null;
        _writePumpTask = null;
    }

    /// <summary>
    /// Create a PipelineSocketConnection from an already-connected stream.
    /// Used by the AGI server for accepted connections and for testing.
    /// </summary>
    public static PipelineSocketConnection FromStream(Stream stream)
    {
        var conn = new PipelineSocketConnection { _stream = stream };

        var pipeOptions = new PipeOptions(
            minimumSegmentSize: MinimumBufferSize,
            useSynchronizationContext: false);

        conn._inputPipe = new Pipe(pipeOptions);
        conn._outputPipe = new Pipe(pipeOptions);
        conn._pumpCts = new CancellationTokenSource();

        var token = conn._pumpCts.Token;
        conn._readPumpTask = Task.Run(() => ReadPumpAsync(stream, conn._inputPipe.Writer, token), CancellationToken.None);
        conn._writePumpTask = Task.Run(() => WritePumpAsync(stream, conn._outputPipe.Reader, token), CancellationToken.None);

        return conn;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await CloseAsync();
    }
}

/// <summary>
/// Default factory for PipelineSocketConnection instances.
/// </summary>
public sealed class PipelineSocketConnectionFactory : ISocketConnectionFactory
{
    public ISocketConnection Create() => new PipelineSocketConnection();

    public ISocketConnection FromStream(Stream stream) => PipelineSocketConnection.FromStream(stream);
}
