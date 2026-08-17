using System.Net;
using System.Net.Sockets;

namespace Verbara.Sdk.TestInfrastructure.WebSocket;

/// <summary>
/// A loopback port with nothing listening on it, for tests that need a connection to be refused.
/// </summary>
/// <remarks>
/// <para>
/// The handshake wrap <c>ADR-0050</c> E7 adds needs a session that never opens, and the cheapest
/// honest way to produce one is a port the OS will refuse. Binding <c>:0</c> and releasing it hands
/// back a port the kernel has just confirmed is free, which is far more reliable than picking a
/// number and hoping — but note what it is and is not: nothing prevents another process from taking
/// the port between the release and the connect. On loopback that window is microseconds and the
/// failure mode is a test that connects successfully and fails its assertion, not a silent pass.
/// </para>
/// <para>
/// Always <see cref="IPAddress.Loopback"/> — IPv4, explicitly. <c>localhost</c> resolves to
/// <c>::1</c> first on this platform, and that ambiguity was the real cause behind an earlier round
/// of provider-test flakes (<c>ADR-0044</c>); a test seam that names the address leaves no room for
/// the resolver to have an opinion.
/// </para>
/// </remarks>
public static class ClosedPort
{
    /// <summary>
    /// Returns a loopback TCP port that was free at the moment of the call and has no listener.
    /// </summary>
    public static int Reserve()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
