using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Verbara.Sdk.Ari.Audio;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.Ari.Tests.Audio;

public class AudioSocketServerTests : IAsyncDisposable
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private AudioSocketServer? _server;

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
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

    private AudioSocketServer CreateServer(
        int port,
        int maxStreams = 1000,
        TimeSpan? idleTimeout = null,
        ILogger<AudioSocketServer>? logger = null)
    {
        var options = new AudioServerOptions
        {
            AudioSocketPort = port,
            ListenAddress = "127.0.0.1",
            MaxConcurrentStreams = maxStreams,
            DefaultFormat = "slin16",
            IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(5)
        };
        _server = new AudioSocketServer(options, logger ?? NullLogger<AudioSocketServer>.Instance);
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

    // -------------------------------------- a connection that fails after the server took it on

    [Fact]
    public async Task HandleConnection_ShouldReportIt_WhenAnObserverOfNewStreamsThrows()
    {
        // Arrange — OnStreamConnected is published from inside the connection handler, synchronously,
        // so a consumer's subscription throwing is an exception the handler is left holding. Until
        // this test that arm had no coverage of any kind, which left "a consumer's fault is not the
        // server's" as an intention rather than a fact.
        var port = GetFreePort();
        var logger = new CapturingLogger();
        var server = CreateServer(port, logger: logger);
        await server.StartAsync();

        using var sub = server.OnStreamConnected.Subscribe(
            _ => throw new InvalidOperationException("the consumer's handler failed"));

        using var client = await ConnectAsync(port);
        var stream = client.GetStream();

        // Act — a UUID frame is what takes the connection past the handler's wait and into the
        // publish that throws
        await stream.WriteAsync(BuildUuidFrame(Guid.NewGuid()));
        await stream.FlushAsync();

        // Assert — the read returns 0 only once the handler has released the connection, so the wait
        // is on the server winding it up and not on a clock. By then the catch has already logged.
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(SignalTimeout);

        read.Should().Be(
            0,
            "the handler releases the connection on the failing path too, rather than leaking it");
        logger.Entries.Should().ContainSingle(
            entry => entry.Level == LogLevel.Error
                && entry.EventName == "ConnectionError"
                && entry.ExceptionType == nameof(InvalidOperationException),
            "a failure that is neither the stop nor the idle deadline is the loss of that " +
            "connection, so it is logged at Error instead of vanishing into the unobserved task " +
            "the accept loop started the handler in");
        server.ActiveStreamCount.Should().Be(
            0,
            "the finally deregisters the session on the failing path as well as the clean one");
        server.IsRunning.Should().BeTrue("one connection failing is not the server failing");
    }

    [Fact]
    public async Task HandleConnection_ShouldWindUpAtOnce_WhenThePeerHangsUpAsSoonAsItIdentifies()
    {
        // Arrange — the idle deadline is set far beyond the wait below, deliberately: if the handler
        // ever needed the deadline to notice a session that was already gone, this test would sit out
        // the 10 s SignalTimeout and fail. What it pins is that the wind-up is driven by the
        // disconnect itself.
        //
        // The line that makes that true is not separable by this test, and saying so is the finding
        // rather than an omission. The handler checks `!session.IsConnected` after subscribing, and
        // the subscription is to a BehaviorSubject, which replays the session's current state to a
        // new subscriber — so on this ordering the replay has ALREADY completed the wait by the time
        // the check runs. The check still covers the case the replay cannot: a session disposed
        // while its last published state is still Connected. Removing it leaves this test green.
        var port = GetFreePort();
        var logger = new CapturingLogger();
        var server = CreateServer(port, idleTimeout: TimeSpan.FromSeconds(30), logger: logger);
        await server.StartAsync();

        using var client = await ConnectAsync(port);
        var stream = client.GetStream();

        // Act — identify, then hang up in the same breath. The half-close keeps this end readable,
        // so the server's own release is still observable from here.
        await stream.WriteAsync(BuildUuidFrame(Guid.NewGuid()));
        await stream.FlushAsync();
        client.Client.Shutdown(SocketShutdown.Send);

        // Assert
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(SignalTimeout);

        read.Should().Be(
            0,
            "a peer that hangs up the instant after it identifies itself is wound up when it hangs " +
            "up, not when a deadline nobody is waiting for expires");
        server.ActiveStreamCount.Should().Be(0, "the session is deregistered as it is wound up");
        logger.Entries.Should().NotContain(
            entry => entry.EventName == "ConnectionError",
            "a peer hanging up is an ending, not a failure, so it is not reported as one");
    }

    // ---------------------------------------------------------------------------------- helpers

    /// <summary>A server log entry, reduced to what these tests assert on.</summary>
    private sealed record LogEntry(LogLevel Level, string? EventName, string? ExceptionType);

    /// <summary>Records every entry the server logs.</summary>
    private sealed class CapturingLogger : ILogger<AudioSocketServer>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(new LogEntry(logLevel, eventId.Name, exception?.GetType().Name));
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
