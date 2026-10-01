using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Verbara.Sdk.Tests.Shared.Sockets;

/// <summary>
/// Starts an in-process server on a loopback port a test can dial, making the server's own bind the
/// reservation. It probes a free port, creates a <b>fresh</b> server on it and starts it; when the
/// start fails with <see cref="SocketError.AddressAlreadyInUse"/> — something took the port between
/// the probe and the bind — it disposes that server and tries again with a new probe, immediately,
/// up to <see cref="MaxAttempts"/> times. Any other failure propagates on the attempt it happened on.
/// Exhaustion throws naming every port tried.
/// </summary>
/// <remarks>
/// <para>
/// A fresh server per attempt is required, not a style choice: the ARI audio servers set their
/// running flag before the listener's bind can throw, so a second <c>StartAsync</c> on the instance
/// whose bind failed silently does nothing.
/// </para>
/// <para>
/// The retry has no delay: a new probe is all it needs, and a wait would be an unmarked barrier for
/// the sync-fence guard. A server that exposes the port it bound (<c>FastAgiServer.BoundPort</c>)
/// needs none of this: construct it with port 0 and read the port back.
/// </para>
/// </remarks>
internal static class LoopbackServerBind
{
    /// <summary>How many fresh servers are tried before giving up.</summary>
    public const int MaxAttempts = 5;

    /// <summary>Starts a server on one probed loopback port, retrying only on address-in-use.</summary>
    public static async Task<(TServer Server, int Port)> StartAsync<TServer>(
        Func<int, TServer> create,
        Func<TServer, ValueTask> start)
        where TServer : IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(create);

        var (server, ports) = await StartAsync(1, p => create(p[0]), start, ProbePorts, MaxAttempts).ConfigureAwait(false);
        return (server, ports[0]);
    }

    /// <summary>
    /// Starts a server that binds two probed loopback ports (the composite AudioSocket + WebSocket
    /// shape), retrying both on address-in-use from either bind.
    /// </summary>
    public static async Task<(TServer Server, int FirstPort, int SecondPort)> StartAsync<TServer>(
        Func<int, int, TServer> create,
        Func<TServer, ValueTask> start)
        where TServer : IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(create);

        var (server, ports) = await StartAsync(2, p => create(p[0], p[1]), start, ProbePorts, MaxAttempts).ConfigureAwait(false);
        return (server, ports[0], ports[1]);
    }

    /// <summary>
    /// The loop itself, with the probe and the attempt count as seams so the retry, the propagation
    /// and the exhaustion can be tested against ports a test holds.
    /// </summary>
    internal static async Task<(TServer Server, IReadOnlyList<int> Ports)> StartAsync<TServer>(
        int portCount,
        Func<IReadOnlyList<int>, TServer> create,
        Func<TServer, ValueTask> start,
        Func<int, IReadOnlyList<int>> probe,
        int maxAttempts)
        where TServer : IAsyncDisposable
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(portCount, 1);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        var attempted = new List<IReadOnlyList<int>>();
        SocketException? lastInUse = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var ports = probe(portCount);
            attempted.Add(ports);
            var server = create(ports);
            var started = false;
            try
            {
                await start(server).ConfigureAwait(false);
                started = true;
                return (server, ports);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                lastInUse = ex;
            }
            finally
            {
                if (!started)
                    await server.DisposeAsync().ConfigureAwait(false);
            }
        }

        var tried = string.Join(", ", attempted.Select(p => string.Join('+', p.Select(n => n.ToString(CultureInfo.InvariantCulture)))));
        throw new InvalidOperationException(
            $"Could not start a loopback server in {maxAttempts} attempts: every bind failed with " +
            $"{nameof(SocketError.AddressAlreadyInUse)}. Ports tried: {tried}.",
            lastInUse);
    }

    /// <summary>
    /// <paramref name="count"/> distinct loopback ports the OS reports free. All of them are held at
    /// once before any is released, so two probes never return the same port.
    /// </summary>
    public static IReadOnlyList<int> ProbePorts(int count)
    {
        using var held = new HeldListeners(count);
        for (var i = 0; i < count; i++)
            held.StartOne();

        return held.Ports();
    }

    /// <summary>
    /// The listeners one probe holds. Each is owned before its <c>Start</c> can throw, and all of them
    /// are released together, in the order they were opened, only after every port has been read.
    /// </summary>
    private sealed class HeldListeners(int capacity) : IDisposable
    {
        private readonly List<TcpListener> _listeners = new(capacity);

        public void StartOne()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            _listeners.Add(listener);
            listener.Start();
        }

        public IReadOnlyList<int> Ports() => [.. _listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port)];

        public void Dispose()
        {
            foreach (var listener in _listeners)
                listener.Dispose();
        }
    }
}
