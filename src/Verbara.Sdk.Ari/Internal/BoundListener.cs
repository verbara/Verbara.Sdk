using System.Net;
using System.Net.Sockets;

namespace Verbara.Sdk.Ari.Internal;

/// <summary>
/// Builds and starts the <see cref="TcpListener"/> of an ARI server's <c>StartAsync</c>, leaving nothing
/// behind when it cannot: an invalid address throws <see cref="FormatException"/> before any socket
/// exists, and a bind that fails disposes the listener before its <see cref="SocketException"/> propagates.
/// </summary>
internal static class BoundListener
{
    /// <summary>A started listener on <paramref name="address"/>:<paramref name="port"/>, or an exception and no socket.</summary>
    public static TcpListener Start(string address, int port)
    {
        var listener = new TcpListener(IPAddress.Parse(address), port);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            listener.Dispose();
            throw;
        }

        return listener;
    }
}
