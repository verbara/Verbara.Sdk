using System.Buffers;
using System.Net.Sockets;
using System.Text;
using Asterisk.NetAot.Ami.Transport;
using FluentAssertions;

namespace Asterisk.NetAot.Ami.Tests.Internal;

public class AsyncServerSocketTests
{
    [Fact]
    public async Task Start_ShouldListenOnPort()
    {
        // Arrange & Act
        await using var server = new AsyncServerSocket(0); // port 0 = OS assigns
        server.Start();

        // Assert
        server.IsListening.Should().BeTrue();
    }

    [Fact]
    public async Task AcceptAsync_ShouldReturnConnectionOnClientConnect()
    {
        // Arrange
        await using var server = new AsyncServerSocket(0);
        server.Start();

        using var client = new TcpClient();

        // Act
        var acceptTask = server.AcceptAsync();
        await client.ConnectAsync("127.0.0.1", server.Port);
        await using var conn = await acceptTask;

        // Assert
        conn.Should().NotBeNull();
        conn.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task AcceptedConnection_ShouldExchangeData()
    {
        // Arrange
        await using var server = new AsyncServerSocket(0);
        server.Start();

        using var client = new TcpClient();
        var acceptTask = server.AcceptAsync();
        await client.ConnectAsync("127.0.0.1", server.Port);
        await using var serverConn = await acceptTask;

        // Act: client sends data
        var message = "agi_network: yes\r\n\r\n"u8.ToArray();
        await client.GetStream().WriteAsync(message);
        await client.GetStream().FlushAsync();

        // Server reads via pipeline
        var readResult = await serverConn.Input.ReadAsync();
        var received = Encoding.UTF8.GetString(readResult.Buffer.FirstSpan.ToArray());
        serverConn.Input.AdvanceTo(readResult.Buffer.End);

        // Assert
        received.Should().Be("agi_network: yes\r\n\r\n");
    }

    [Fact]
    public async Task Stop_ShouldStopListening()
    {
        // Arrange
        await using var server = new AsyncServerSocket(0);
        server.Start();
        server.IsListening.Should().BeTrue();

        // Act
        server.Stop();

        // Assert
        server.IsListening.Should().BeFalse();
    }

    [Fact]
    public async Task AcceptAsync_ShouldThrow_WhenNotStarted()
    {
        // Arrange
        await using var server = new AsyncServerSocket(0);

        // Act & Assert
        var act = () => server.AcceptAsync().AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
