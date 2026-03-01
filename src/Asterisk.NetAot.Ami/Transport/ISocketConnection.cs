using System.IO.Pipelines;

namespace Asterisk.NetAot.Ami.Transport;

/// <summary>
/// Abstraction over a TCP socket connection using System.IO.Pipelines.
/// </summary>
public interface ISocketConnection : IAsyncDisposable
{
    /// <summary>Whether the connection is currently open.</summary>
    bool IsConnected { get; }

    /// <summary>The PipeReader for reading data from the socket.</summary>
    PipeReader Input { get; }

    /// <summary>The PipeWriter for writing data to the socket.</summary>
    PipeWriter Output { get; }

    /// <summary>Connect to the remote host.</summary>
    ValueTask ConnectAsync(string hostname, int port, bool useSsl = false, CancellationToken cancellationToken = default);

    /// <summary>Close the connection.</summary>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}
