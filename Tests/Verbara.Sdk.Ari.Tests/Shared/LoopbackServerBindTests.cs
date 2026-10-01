using System.Net;
using System.Net.Sockets;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Tests.Shared.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

// Namespace, not folder: "Shared" is a reserved word in VB, and CA1716 rejects it in a namespace
// that declares public types.
namespace Verbara.Sdk.Ari.Tests.SharedSockets;

/// <summary>
/// Pins the shared <see cref="LoopbackServerBind"/> helper against real binds: a port a test holds is
/// retried onto a fresh server and the failed one is disposed; any other bind failure propagates on
/// the first attempt; exhaustion names every port tried; and the two-port variant retries both.
/// </summary>
public sealed class LoopbackServerBindTests
{
    /// <summary>TEST-NET-1 (RFC 5737): never assigned to a local interface, so a bind there fails, and not with address-in-use.</summary>
    private const string UnassignedAddress = "192.0.2.1";

    [Fact]
    public async Task StartAsync_ShouldRetryOntoAFreshServerAndDisposeTheFirst_WhenTheFirstProbedPortIsHeld()
    {
        using var held = HoldLoopbackPort(out var heldPort);
        var created = new List<TrackedServer>();
        var probes = 0;

        var (server, ports) = await LoopbackServerBind.StartAsync(
            portCount: 1,
            create: p => Track(created, NewAudioSocketServer(p[0]), p[0]),
            start: s => s.Inner.StartAsync(),
            probe: count => probes++ == 0 ? [heldPort] : LoopbackServerBind.ProbePorts(count),
            maxAttempts: LoopbackServerBind.MaxAttempts);
        await using var _ = server;

        created.Should().HaveCount(2, "the held port fails the first bind and a fresh server is made for the retry");
        created[0].Port.Should().Be(heldPort);
        created[0].Disposed.Should().BeTrue("the server whose bind failed is disposed, not leaked");
        created[0].Inner.IsRunning.Should().BeFalse();
        server.Should().BeSameAs(created[1]);
        ports.Should().ContainSingle().Which.Should().NotBe(heldPort);
        server.Inner.IsRunning.Should().BeTrue();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ports[0]);
        client.Connected.Should().BeTrue("the returned port is the one the running server bound");
    }

    [Fact]
    public async Task StartAsync_ShouldPropagateOnTheFirstAttempt_WhenTheBindFailsForAnotherReason()
    {
        var created = new List<TrackedServer>();
        var probes = 0;

        var act = () => LoopbackServerBind.StartAsync(
            portCount: 1,
            create: p => Track(created, NewAudioSocketServer(p[0], UnassignedAddress), p[0]),
            start: s => s.Inner.StartAsync(),
            probe: count =>
            {
                probes++;
                return LoopbackServerBind.ProbePorts(count);
            },
            maxAttempts: LoopbackServerBind.MaxAttempts);

        var thrown = await act.Should().ThrowAsync<SocketException>();
        thrown.Which.SocketErrorCode.Should().NotBe(SocketError.AddressAlreadyInUse);
        probes.Should().Be(1, "only address-in-use is retried");
        created.Should().ContainSingle().Which.Disposed.Should().BeTrue("the failed server is still disposed");
    }

    [Fact]
    public async Task StartAsync_ShouldNameEveryPortTried_WhenEveryAttemptFindsItsPortTaken()
    {
        using var first = HoldLoopbackPort(out var port1);
        using var second = HoldLoopbackPort(out var port2);
        using var third = HoldLoopbackPort(out var port3);
        var held = new Queue<int>([port1, port2, port3]);
        var created = new List<TrackedServer>();

        var act = () => LoopbackServerBind.StartAsync(
            portCount: 1,
            create: p => Track(created, NewAudioSocketServer(p[0]), p[0]),
            start: s => s.Inner.StartAsync(),
            probe: _ => [held.Dequeue()],
            maxAttempts: 3);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain(port1.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .And.Contain(port2.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .And.Contain(port3.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .And.Contain(nameof(SocketError.AddressAlreadyInUse));
        thrown.Which.InnerException.Should().BeOfType<SocketException>()
            .Which.SocketErrorCode.Should().Be(SocketError.AddressAlreadyInUse);
        created.Should().HaveCount(3).And.OnlyContain(s => s.Disposed, "every failed server is disposed");
    }

    [Fact]
    public async Task StartAsync_ShouldRetryBothPorts_WhenTheSecondProbedPortOfAPairIsHeld()
    {
        using var held = HoldLoopbackPort(out var heldPort);
        var created = new List<TrackedPair>();
        var probes = 0;

        var (pair, ports) = await LoopbackServerBind.StartAsync(
            portCount: 2,
            create: p => TrackPair(created, p[0], p[1]),
            start: StartPairAsync,
            probe: count => probes++ == 0 ? [LoopbackServerBind.ProbePorts(1)[0], heldPort] : LoopbackServerBind.ProbePorts(count),
            maxAttempts: LoopbackServerBind.MaxAttempts);
        await using var _ = pair;

        created.Should().HaveCount(2);
        created[0].Disposed.Should().BeTrue("the pair whose second bind failed is disposed, the first server included");
        created[0].AudioSocket.IsRunning.Should().BeFalse();
        ports.Should().HaveCount(2).And.NotContain(heldPort);
        pair.AudioSocket.IsRunning.Should().BeTrue();
        pair.WebSocket.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_ShouldReturnARunningServerOnTheReturnedPort_WhenCalledThroughThePublicOverloads()
    {
        var (single, port) = await LoopbackServerBind.StartAsync(p => NewAudioSocketServer(p), s => s.StartAsync());
        await using var disposeSingle = single;
        var (pair, audioSocketPort, webSocketPort) = await LoopbackServerBind.StartAsync(
            (a, w) => new TrackedPair(NewAudioSocketServer(a), NewWebSocketServer(w)),
            StartPairAsync);
        await using var disposePair = pair;

        single.IsRunning.Should().BeTrue();
        audioSocketPort.Should().NotBe(webSocketPort, "the two ports of a pair are probed while both are held");
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        client.Connected.Should().BeTrue();
    }

    private static TcpListener HoldLoopbackPort(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static AudioServerOptions Options(string listenAddress) => new()
    {
        ListenAddress = listenAddress,
        DefaultFormat = "slin16",
    };

    private static AudioSocketServer NewAudioSocketServer(int port, string listenAddress = "127.0.0.1")
    {
        var options = Options(listenAddress);
        options.AudioSocketPort = port;
        return new AudioSocketServer(options, NullLogger<AudioSocketServer>.Instance);
    }

    private static WebSocketAudioServer NewWebSocketServer(int port)
    {
        var options = Options("127.0.0.1");
        options.WebSocketPort = port;
        return new WebSocketAudioServer(options, NullLogger<WebSocketAudioServer>.Instance);
    }

    private static TrackedServer Track(List<TrackedServer> created, AudioSocketServer inner, int port)
    {
        var tracked = new TrackedServer(inner, port);
        created.Add(tracked);
        return tracked;
    }

    private static TrackedPair TrackPair(List<TrackedPair> created, int audioSocketPort, int webSocketPort)
    {
        var pair = new TrackedPair(NewAudioSocketServer(audioSocketPort), NewWebSocketServer(webSocketPort));
        created.Add(pair);
        return pair;
    }

    private static async ValueTask StartPairAsync(TrackedPair pair)
    {
        await pair.AudioSocket.StartAsync();
        await pair.WebSocket.StartAsync();
    }

    /// <summary>A real server that records whether the helper disposed it.</summary>
    private sealed class TrackedServer(AudioSocketServer inner, int port) : IAsyncDisposable
    {
        public AudioSocketServer Inner { get; } = inner;

        public int Port { get; } = port;

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return Inner.DisposeAsync();
        }
    }

    /// <summary>The composite shape: an AudioSocket and a WebSocket server started together.</summary>
    private sealed class TrackedPair(AudioSocketServer audioSocket, WebSocketAudioServer webSocket) : IAsyncDisposable
    {
        public AudioSocketServer AudioSocket { get; } = audioSocket;

        public WebSocketAudioServer WebSocket { get; } = webSocket;

        public bool Disposed { get; private set; }

        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            await AudioSocket.DisposeAsync();
            await WebSocket.DisposeAsync();
        }
    }
}
