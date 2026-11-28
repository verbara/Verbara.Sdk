using System.Net;
using System.Net.Sockets;

namespace Verbara.Sdk.TestInfrastructure.WebSocket;

/// <summary>
/// A loopback TCP port that the test owns and that refuses every connection, for tests that need a
/// connection to be refused. Hold it with <c>using</c> for as long as the test may dial the port.
/// </summary>
/// <remarks>
/// <para>
/// A test of the handshake-failure wrap needs a session that never opens, and the honest way to
/// produce one is a port the OS refuses. The port is bound and never put in the listening state: the
/// kernel answers a connect with a reset (<see cref="SocketError.ConnectionRefused"/>) because no
/// listener matches, and the bind keeps the port owned, so no other socket can take it while this
/// object lives.
/// </para>
/// <para>
/// Owned has to hold against a competitor that asks for address reuse, and every TCP socket .NET
/// binds on Linux asks for it: the runtime sets <c>SO_REUSEADDR</c> inside <see cref="Socket.Bind"/>,
/// whatever was set before, and two non-listening sockets that both carry it may share a port. So the
/// option is cleared on the owner after the bind; a later bind to the port, with or without reuse,
/// then fails with <see cref="SocketError.AddressAlreadyInUse"/>. <see cref="Socket.ExclusiveAddressUse"/>
/// is set before the bind as the Windows counterpart, where reuse lets a second socket take a port
/// outright; only the Linux behaviour is exercised, because CI runs on Linux. <c>ClosedPortTests</c>
/// pins both bind refusals and the connect refusal.
/// </para>
/// <para>
/// An earlier version bound <c>:0</c>, read the port and released it before returning. Another process
/// could take the port in the gap before the test connected, and the test then read that process's
/// answer (an HTTP <c>404</c> seen in a run) instead of a refusal. Owning the port for the test's
/// whole lifetime removes the gap instead of narrowing it.
/// </para>
/// <para>
/// Always <see cref="IPAddress.Loopback"/> — IPv4, explicitly. <c>localhost</c> resolves to
/// <c>::1</c> first on this platform, and that ambiguity was the real cause behind an earlier round
/// of provider-test flakes (<c>ADR-0044</c>); a test seam that names the address leaves no room for
/// the resolver to have an opinion.
/// </para>
/// </remarks>
public sealed class ClosedPort : IDisposable
{
    private readonly Socket _owner;

    private ClosedPort(Socket owner, int port)
    {
        _owner = owner;
        Port = port;
    }

    /// <summary>The owned loopback port. Every connection to it is refused until disposal.</summary>
    public int Port { get; }

    /// <summary>
    /// Binds a free loopback port without listening on it and keeps it until <see cref="Dispose"/>.
    /// </summary>
    public static ClosedPort Bind()
    {
        var owner = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ExclusiveAddressUse = true,
        };
        try
        {
            owner.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            owner.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            return new ClosedPort(owner, ((IPEndPoint)owner.LocalEndPoint!).Port);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    /// <summary>Releases the port.</summary>
    public void Dispose() => _owner.Dispose();
}
