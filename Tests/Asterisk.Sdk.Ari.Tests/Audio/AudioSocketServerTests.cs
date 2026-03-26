using System.Net;
using System.Net.Sockets;
using Asterisk.Sdk.Ari.Audio;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.Ari.Tests.Audio;

public class AudioSocketServerTests : IAsyncDisposable
{
    private AudioSocketServer? _server;

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static byte[] BuildFrame(AudioFrameType type, byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        frame[0] = (byte)type;
        frame[1] = (byte)(payload.Length >> 16);
        frame[2] = (byte)(payload.Length >> 8);
        frame[3] = (byte)(payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    private static byte[] BuildUuidFrame(Guid uuid) =>
        BuildFrame(AudioFrameType.Uuid, uuid.ToByteArray());

    private static byte[] BuildHangupFrame() =>
        BuildFrame(AudioFrameType.Hangup, []);

    private AudioSocketServer CreateServer(int port, int maxStreams = 1000, TimeSpan? idleTimeout = null)
    {
        var options = new AudioServerOptions
        {
            AudioSocketPort = port,
            ListenAddress = "127.0.0.1",
            MaxConcurrentStreams = maxStreams,
            DefaultFormat = "slin16",
            IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(5)
        };
        _server = new AudioSocketServer(options, NullLogger<AudioSocketServer>.Instance);
        return _server;
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    /// <summary>Connects, sends a UUID frame, and waits until the server registers the stream.</summary>
    private static async Task<TcpClient> ConnectAndSendUuidAsync(int port, Guid uuid, AudioSocketServer server)
    {
        var client = await ConnectAsync(port);
        var stream = client.GetStream();
        await stream.WriteAsync(BuildUuidFrame(uuid));
        await stream.FlushAsync();

        // Wait for server to register the stream
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (server.GetStream(uuid.ToString()) is null && !cts.Token.IsCancellationRequested)
            await Task.Delay(20, cts.Token);

        return client;
    }

    [Fact]
    public async Task StartAsync_ShouldSetIsRunning()
    {
        var port = GetFreePort();
        var server = CreateServer(port);

        server.IsRunning.Should().BeFalse();

        await server.StartAsync();

        server.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsync_ShouldClearIsRunning()
    {
        var port = GetFreePort();
        var server = CreateServer(port);

        await server.StartAsync();
        server.IsRunning.Should().BeTrue();

        await server.StopAsync();
        server.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_ShouldNotThrow_WhenNotStarted()
    {
        var server = CreateServer(GetFreePort());

        var act = async () => await server.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ActiveStreamCount_ShouldBeZero_WhenNoConnections()
    {
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();

        server.ActiveStreamCount.Should().Be(0);
        server.ActiveStreams.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStream_ShouldReturnNull_WhenNoStreams()
    {
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();

        server.GetStream("nonexistent-channel-id").Should().BeNull();
    }

    [Fact]
    public async Task HandleConnection_ShouldRegisterStream_WhenUuidReceived()
    {
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();

        var uuid = Guid.NewGuid();
        using var client = await ConnectAndSendUuidAsync(port, uuid, server);

        server.ActiveStreamCount.Should().Be(1);
        var registeredStream = server.GetStream(uuid.ToString());
        registeredStream.Should().NotBeNull();
        registeredStream!.ChannelId.Should().Be(uuid.ToString());
        registeredStream.Format.Should().Be("slin16");
    }

    [Fact]
    public async Task HandleConnection_ShouldEmitOnStreamConnected_WhenUuidReceived()
    {
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();

        IAudioStream? emittedStream = null;
        var streamReceived = new TaskCompletionSource();
        using var sub = server.OnStreamConnected.Subscribe(s =>
        {
            emittedStream = s;
            streamReceived.TrySetResult();
        });

        var uuid = Guid.NewGuid();
        using var client = await ConnectAndSendUuidAsync(port, uuid, server);

        await streamReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));

        emittedStream.Should().NotBeNull();
        emittedStream!.ChannelId.Should().Be(uuid.ToString());
    }

    [Fact]
    public async Task HandleConnection_ShouldRemoveStream_WhenHangupReceived()
    {
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();

        var uuid = Guid.NewGuid();
        using var client = await ConnectAndSendUuidAsync(port, uuid, server);

        // Verify stream is registered
        server.GetStream(uuid.ToString()).Should().NotBeNull();

        // Send hangup frame
        var stream = client.GetStream();
        await stream.WriteAsync(BuildHangupFrame());
        await stream.FlushAsync();

        // Wait for server to remove the stream
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (server.GetStream(uuid.ToString()) is not null && !cts.Token.IsCancellationRequested)
            await Task.Delay(20, cts.Token);

        server.GetStream(uuid.ToString()).Should().BeNull();
        server.ActiveStreamCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleConnection_ShouldDisposeSession_WhenNoUuidReceived()
    {
        var port = GetFreePort();
        var server = CreateServer(port, idleTimeout: TimeSpan.FromMilliseconds(300));
        await server.StartAsync();

        // Connect but never send a UUID frame
        using var client = await ConnectAsync(port);

        // Wait longer than the idle timeout
        await Task.Delay(TimeSpan.FromMilliseconds(600));

        // No stream should have been registered
        server.ActiveStreamCount.Should().Be(0);
    }

    [Fact]
    public async Task MaxConcurrentStreams_ShouldDropExcessConnections()
    {
        var port = GetFreePort();
        var server = CreateServer(port, maxStreams: 1);
        await server.StartAsync();

        // First connection — should be accepted
        var uuid1 = Guid.NewGuid();
        using var client1 = await ConnectAndSendUuidAsync(port, uuid1, server);
        server.ActiveStreamCount.Should().Be(1);

        // Second connection — should be dropped by the server (MaxConcurrentStreams=1)
        var uuid2 = Guid.NewGuid();
        using var client2 = await ConnectAsync(port);
        var stream2 = client2.GetStream();
        await stream2.WriteAsync(BuildUuidFrame(uuid2));
        await stream2.FlushAsync();

        // Give the server time to process and drop
        await Task.Delay(300);

        // Only the first stream should be registered
        server.ActiveStreamCount.Should().Be(1);
        server.GetStream(uuid1.ToString()).Should().NotBeNull();
        server.GetStream(uuid2.ToString()).Should().BeNull();
    }

    [Fact]
    public async Task StopAsync_ShouldDisposeAllActiveSessions()
    {
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();

        var uuid = Guid.NewGuid();
        using var client = await ConnectAndSendUuidAsync(port, uuid, server);
        server.ActiveStreamCount.Should().Be(1);

        await server.StopAsync();

        server.ActiveStreamCount.Should().Be(0);
        server.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_ShouldStopRunningServer()
    {
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();
        server.IsRunning.Should().BeTrue();

        await server.DisposeAsync();

        server.IsRunning.Should().BeFalse();
        // Set to null so the fixture cleanup does not double-dispose
        _server = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
