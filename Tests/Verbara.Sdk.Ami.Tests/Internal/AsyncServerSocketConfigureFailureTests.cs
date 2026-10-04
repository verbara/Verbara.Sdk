using System.Net.Sockets;
using System.Text;
using Verbara.Sdk.Ami.Tests.TestSupport;
using Verbara.Sdk.Ami.Transport;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Ami.Tests.Internal;

/// <summary>
/// <see cref="AsyncServerSocket.AcceptAsync"/> owns the connection it accepted until it returns it: when configuring
/// that connection fails, the connection is closed and the original exception reaches the caller unchanged, and the
/// listener goes on listening. Before, the accepted socket was left open with no owner.
/// </summary>
/// <remarks>
/// No natural trigger for a configure failure exists on Linux, so each failing cell hands the server, through its
/// internal <c>AcceptOverride</c> seam, a socket built to fail at one step (<see cref="AcceptedClients"/>). The good
/// path and the failure-then-real-client cell use the real listener on a loopback port.
/// </remarks>
public sealed class AsyncServerSocketConfigureFailureTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AcceptAsync_ShouldCloseTheAcceptedSocket_WhenItRejectsTheNoDelayOption()
    {
        using var accepted = AcceptedClients.UdpBacked();
        await using var server = new AsyncServerSocket(0);
        server.Start();
        server.AcceptOverride = _ => ValueTask.FromResult(accepted.Client);

        var act = async () => await server.AcceptAsync().AsTask().WaitAsync(Bound);

        await act.Should().ThrowAsync<SocketException>("the socket rejects the option the server sets on it");
        using (new AssertionScope())
        {
            accepted.IsSocketClosed.Should().BeTrue("the server closes the connection it could not configure");
            server.IsListening.Should().BeTrue("one connection's failure leaves the listener listening");
        }
    }

    [Fact]
    public async Task AcceptAsync_ShouldCloseTheAcceptedSocket_WhenItsStreamCannotBeObtained()
    {
        using var accepted = AcceptedClients.NeverConnected();
        await using var server = new AsyncServerSocket(0);
        server.Start();
        server.AcceptOverride = _ => ValueTask.FromResult(accepted.Client);

        var act = async () => await server.AcceptAsync().AsTask().WaitAsync(Bound);

        await act.Should().ThrowAsync<InvalidOperationException>("a socket that is not connected has no stream");
        using (new AssertionScope())
        {
            accepted.IsSocketClosed.Should().BeTrue("the server closes the connection whose stream it could not obtain");
            server.IsListening.Should().BeTrue("one connection's failure leaves the listener listening");
        }
    }

    [Fact]
    public async Task AcceptAsync_ShouldThrowObjectDisposedAndKeepListening_WhenTheAcceptedSocketIsAlreadyDisposed()
    {
        using var accepted = AcceptedClients.DisposedSocket();
        await using var server = new AsyncServerSocket(0);
        server.Start();
        server.AcceptOverride = _ => ValueTask.FromResult(accepted.Client);

        var act = async () => await server.AcceptAsync().AsTask().WaitAsync(Bound);

        await act.Should().ThrowAsync<ObjectDisposedException>("the accepted socket is already disposed");
        server.IsListening.Should().BeTrue("the server socket still reports itself listening");
    }

    [Fact]
    public async Task AcceptAsync_ShouldReturnTheNextConnection_WhenThePreviousOneFailedItsConfiguration()
    {
        using var failing = AcceptedClients.UdpBacked();
        await using var server = new AsyncServerSocket(0);
        server.Start();
        server.AcceptOverride = _ => ValueTask.FromResult(failing.Client);
        var first = async () => await server.AcceptAsync().AsTask().WaitAsync(Bound);
        await first.Should().ThrowAsync<SocketException>("the first connection fails its configuration");
        var listeningAfterFailure = server.IsListening;

        server.AcceptOverride = null;
        using var client = new TcpClient();
        var accept = server.AcceptAsync().AsTask();
        await client.ConnectAsync("127.0.0.1", server.Port).WaitAsync(Bound);
        await using var connection = await accept.WaitAsync(Bound);
        var received = await SendAndReceiveAsync(client, connection, "agi_network: yes\r\n\r\n");

        using (new AssertionScope())
        {
            listeningAfterFailure.Should().BeTrue("the server socket reports itself listening after the failure");
            connection.IsConnected.Should().BeTrue("the second call returns a connected connection");
            received.Should().Be("agi_network: yes\r\n\r\n", "the second connection exchanges data");
            server.IsListening.Should().BeTrue();
        }
    }

    [Fact]
    public async Task AcceptAsync_ShouldReturnTheConnectionAsBefore_WhenItConfiguresNormally()
    {
        await using var server = new AsyncServerSocket(0);
        server.Start();
        using var client = new TcpClient();

        var accept = server.AcceptAsync().AsTask();
        await client.ConnectAsync("127.0.0.1", server.Port).WaitAsync(Bound);
        await using var connection = await accept.WaitAsync(Bound);
        var received = await SendAndReceiveAsync(client, connection, "hello\r\n");

        using (new AssertionScope())
        {
            connection.IsConnected.Should().BeTrue();
            received.Should().Be("hello\r\n", "the client's bytes arrive over the returned connection");
        }
    }

    /// <summary>
    /// The fixture's own liveness control: a client the test disposes itself reads closed, so a green
    /// <see cref="AcceptedClient.IsSocketClosed"/> elsewhere means the server closed it, not that the probe is blind.
    /// </summary>
    [Fact]
    public void AcceptedClient_ShouldReadClosed_WhenTheTestDisposesItItself()
    {
        var accepted = AcceptedClients.NeverConnected();
        accepted.IsSocketClosed.Should().BeFalse("a fresh socket is open");

        accepted.Client.Dispose();

        accepted.IsSocketClosed.Should().BeTrue("disposing the client closes its socket");
        accepted.Dispose();
    }

    private static async Task<string> SendAndReceiveAsync(TcpClient client, ISocketConnection connection, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await client.GetStream().WriteAsync(bytes);
        await client.GetStream().FlushAsync();

        var received = new StringBuilder();
        while (received.Length < text.Length)
        {
            var read = await connection.Input.ReadAsync().AsTask().WaitAsync(Bound);
            foreach (var segment in read.Buffer)
                received.Append(Encoding.UTF8.GetString(segment.Span));
            connection.Input.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
                break;
        }

        return received.ToString();
    }
}
