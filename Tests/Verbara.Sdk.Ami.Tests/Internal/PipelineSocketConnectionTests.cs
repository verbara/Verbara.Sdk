using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Verbara.Sdk.Ami.Transport;
using FluentAssertions;

namespace Verbara.Sdk.Ami.Tests.Internal;

public class PipelineSocketConnectionTests
{
    [Fact]
    public async Task ConnectAsync_ShouldEstablishConnection()
    {
        // Arrange: start a local TCP listener
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using var conn = new PipelineSocketConnection();

        // Act
        var connectTask = conn.ConnectAsync("127.0.0.1", port);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;

        // Assert
        conn.IsConnected.Should().BeTrue();

        listener.Stop();
    }

    [Fact]
    public async Task Input_ShouldReceiveDataFromRemote()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using var conn = new PipelineSocketConnection();
        var connectTask = conn.ConnectAsync("127.0.0.1", port);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;

        var testMessage = "Response: Success\r\nMessage: Pong\r\n\r\n"u8.ToArray();

        // Act: server sends data
        await serverClient.GetStream().WriteAsync(testMessage);
        await serverClient.GetStream().FlushAsync();

        // Read from the pipeline
        var readResult = await conn.Input.ReadAsync();
        var receivedText = Encoding.UTF8.GetString(readResult.Buffer.FirstSpan.ToArray());
        conn.Input.AdvanceTo(readResult.Buffer.End);

        // Assert
        receivedText.Should().Be("Response: Success\r\nMessage: Pong\r\n\r\n");

        listener.Stop();
    }

    [Fact]
    public async Task Output_ShouldSendDataToRemote()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using var conn = new PipelineSocketConnection();
        var connectTask = conn.ConnectAsync("127.0.0.1", port);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;

        var testMessage = "Action: Ping\r\nActionID: 1\r\n\r\n"u8.ToArray();

        // Act: write to the pipeline
        await conn.Output.WriteAsync(new ReadOnlyMemory<byte>(testMessage));
        await conn.Output.FlushAsync();

        // Read from server side
        var buffer = new byte[1024];
        var stream = serverClient.GetStream();
        stream.ReadTimeout = 2000;
        var bytesRead = await stream.ReadAsync(buffer);
        var receivedText = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        // Assert
        receivedText.Should().Be("Action: Ping\r\nActionID: 1\r\n\r\n");

        listener.Stop();
    }

    [Fact]
    public async Task CloseAsync_ShouldStopPumpsGracefully()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var conn = new PipelineSocketConnection();
        var connectTask = conn.ConnectAsync("127.0.0.1", port);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;

        conn.IsConnected.Should().BeTrue();

        // Act
        await conn.CloseAsync();

        // Assert
        conn.IsConnected.Should().BeFalse();

        listener.Stop();
    }

    // The connect token bounds the TCP connect and the TLS handshake, not the pumps that outlive the call. A
    // direct caller that cancels it once ConnectAsync returned must still get both directions pumped; only
    // CloseAsync or disposal ends the pumps (CloseAsync_ShouldStopPumpsGracefully: CloseAsync awaits both).

    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConnectAsync_ShouldKeepPumpingWhatThePeerSends_WhenItsTokenIsCancelledAfterTheConnect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var conn = new PipelineSocketConnection();
        using var connect = new CancellationTokenSource();
        var connectTask = conn.ConnectAsync("127.0.0.1", port, useSsl: false, connect.Token);
        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(SignalTimeout);
        await connectTask;

        await connect.CancelAsync();
        var sent = "Response: Success\r\nMessage: Pong\r\n\r\n"u8.ToArray();
        await peer.GetStream().WriteAsync(sent);
        await peer.GetStream().FlushAsync();
        var received = await ReadInputWithinBoundAsync(conn.Input, sent.Length);

        received.SequenceEqual(sent).Should().BeTrue(
            $"the peer's bytes were not pumped to Input after the connect token was cancelled (received {received.Length} " +
            "bytes): the read pump runs on a source linked to that token, so the cancel ended it and completed the input " +
            "as if the remote had closed");
        conn.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task ConnectAsync_ShouldKeepPumpingWhatIsWritten_WhenItsTokenIsCancelledAfterTheConnect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var conn = new PipelineSocketConnection();
        using var connect = new CancellationTokenSource();
        var connectTask = conn.ConnectAsync("127.0.0.1", port, useSsl: false, connect.Token);
        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(SignalTimeout);
        await connectTask;

        await connect.CancelAsync();
        var written = "Action: Ping\r\nActionID: 1\r\n\r\n"u8.ToArray();
        await conn.Output.WriteAsync(written);
        await conn.Output.FlushAsync();
        var received = await ReadPeerWithinBoundAsync(peer.GetStream(), written.Length);

        received.SequenceEqual(written).Should().BeTrue(
            $"the bytes written on Output were not pumped to the peer after the connect token was cancelled (received " +
            $"{received.Length} bytes): the write pump runs on a source linked to that token, so the cancel ended it " +
            "and later writes are dropped");
        conn.IsConnected.Should().BeTrue();
    }

    /// <summary>
    /// Reads <paramref name="input"/> until <paramref name="expected"/> bytes arrived, the pipe completed, or the bound
    /// passed; returns what arrived.
    /// </summary>
    private static async Task<byte[]> ReadInputWithinBoundAsync(PipeReader input, int expected)
    {
        using var hang = new CancellationTokenSource(SignalTimeout);
        var arrived = new List<byte>();
        try
        {
            while (arrived.Count < expected)
            {
                var result = await input.ReadAsync(hang.Token);
                arrived.AddRange(result.Buffer.ToArray());
                input.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException) when (hang.IsCancellationRequested)
        {
            // The bound: nothing more arrived.
        }

        return [.. arrived];
    }

    /// <summary>Reads the peer's side until <paramref name="expected"/> bytes arrived, it closed, or the bound passed.</summary>
    private static async Task<byte[]> ReadPeerWithinBoundAsync(NetworkStream stream, int expected)
    {
        using var hang = new CancellationTokenSource(SignalTimeout);
        var buffer = new byte[expected];
        var total = 0;
        try
        {
            while (total < expected)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), hang.Token);
                if (read == 0)
                    break;
                total += read;
            }
        }
        catch (OperationCanceledException) when (hang.IsCancellationRequested)
        {
            // The bound: nothing more arrived.
        }

        return buffer[..total];
    }

    [Fact]
    public async Task FromStream_ShouldWorkWithMemoryStream_Bidirectional()
    {
        // Arrange: use a pair of connected TCP sockets via loopback
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var clientSide = new TcpClient();
        var connectTask = clientSide.ConnectAsync("127.0.0.1", port);
        using var serverSide = await listener.AcceptTcpClientAsync();
        await connectTask;

        // Create pipeline connection from the server-side stream
        await using var conn = PipelineSocketConnection.FromStream(serverSide.GetStream());

        // Act: client sends, server reads via pipeline
        var message = "Hello from client\r\n"u8.ToArray();
        await clientSide.GetStream().WriteAsync(message);
        await clientSide.GetStream().FlushAsync();

        var readResult = await conn.Input.ReadAsync();
        var received = Encoding.UTF8.GetString(readResult.Buffer.FirstSpan.ToArray());
        conn.Input.AdvanceTo(readResult.Buffer.End);

        // Assert
        received.Should().Be("Hello from client\r\n");

        listener.Stop();
    }

    [Fact]
    public async Task RemoteDisconnect_ShouldCompleteInputPipe()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using var conn = new PipelineSocketConnection();
        var connectTask = conn.ConnectAsync("127.0.0.1", port);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;

        // Act: server closes connection
        serverClient.Close();

        // Read should complete (IsCompleted = true)
        var readResult = await conn.Input.ReadAsync();

        // Assert
        readResult.IsCompleted.Should().BeTrue();
        conn.Input.AdvanceTo(readResult.Buffer.End);

        listener.Stop();
    }
}
