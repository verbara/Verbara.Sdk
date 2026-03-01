using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Asterisk.Sdk.Ami.Transport;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Internal;

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
