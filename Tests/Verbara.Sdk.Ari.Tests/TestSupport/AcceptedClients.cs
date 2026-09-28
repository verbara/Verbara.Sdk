using System.Net.Sockets;

namespace Verbara.Sdk.Ari.Tests.TestSupport;

/// <summary>
/// A client an accept seam hands to a server, and the socket it wraps, kept so a test can see whether
/// the server closed that socket.
/// </summary>
internal sealed class AcceptedClient : IDisposable
{
    internal AcceptedClient(TcpClient client, Socket socket)
    {
        Client = client;
        Socket = socket;
    }

    /// <summary>What the accept seam returns.</summary>
    public TcpClient Client { get; }

    /// <summary>The socket inside <see cref="Client"/>.</summary>
    public Socket Socket { get; }

    /// <summary>Whether the socket's handle has been released: the connection was closed, not leaked.</summary>
    public bool IsSocketClosed => Socket.SafeHandle.IsClosed;

    public void Dispose()
    {
        Client.Dispose();
        Socket.Dispose();
    }
}

/// <summary>
/// Accepted connections built to fail at a chosen step between the accept and serving, for the
/// <c>AcceptOverride</c> seam of the ARI servers. None of them is a natural trigger, and none was found
/// on Linux: each one is a socket built to fail at that step.
/// </summary>
/// <remarks>
/// <c>Verbara.Sdk.Agi.Tests</c> carries a copy for <c>FastAgiServer</c>, because referencing this
/// project from there would drag this suite into that one. Change both together.
/// </remarks>
internal static class AcceptedClients
{
    /// <summary>
    /// A client whose socket is a UDP socket, so <c>client.NoDelay = true</c> throws a
    /// <see cref="SocketException"/> (<c>setsockopt(TCP_NODELAY)</c> answers <c>ENOPROTOOPT</c>). The
    /// socket is open when it is handed over, so a server that does not close it leaks it.
    /// </summary>
    public static AcceptedClient UdpBacked()
    {
        var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var client = new TcpClient(AddressFamily.InterNetwork);
        var original = client.Client;
        client.Client = udp;
        original.Dispose();
        return new AcceptedClient(client, udp);
    }

    /// <summary>
    /// A client whose socket is already disposed, so <c>client.NoDelay = true</c> throws an
    /// <see cref="ObjectDisposedException"/>: the type an accept loop reads as its own listener's stop.
    /// </summary>
    public static AcceptedClient DisposedSocket()
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        var socket = client.Client;
        socket.Dispose();
        return new AcceptedClient(client, socket);
    }

    /// <summary>
    /// A TCP client that was never connected: <c>client.NoDelay = true</c> succeeds, and
    /// <c>client.GetStream()</c> throws an <see cref="InvalidOperationException"/>. The socket is open
    /// when it is handed over.
    /// </summary>
    public static AcceptedClient NeverConnected()
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        return new AcceptedClient(client, client.Client);
    }

    /// <summary>
    /// An accept that never returns a connection and ends only when <paramref name="token"/> does, so
    /// a loop parked on it attempts nothing further and puts no wait on any clock, fake or real.
    /// </summary>
    public static async ValueTask<TcpClient> ParkUntilCancelledAsync(CancellationToken token)
    {
        var parked = new TaskCompletionSource<TcpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => parked.TrySetCanceled(token));
        return await parked.Task;
    }
}
