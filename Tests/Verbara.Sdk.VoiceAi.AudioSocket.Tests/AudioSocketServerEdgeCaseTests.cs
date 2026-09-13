using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Tests;

public sealed class AudioSocketServerEdgeCaseTests : IAsyncDisposable
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The event ids the logging source generator gives <c>NoUuidFrame</c> and
    /// <c>HandleConnectionError</c>. A consumer's log filter can key on them, so they are asserted
    /// along with the names.
    /// </summary>
    private const int NoUuidFrameEventId = 1608362970;

    private const int HandleConnectionErrorEventId = 663784587;

    private readonly AudioSocketServer _server;

    public AudioSocketServerEdgeCaseTests()
    {
        var options = new AudioSocketOptions
        {
            Port = 0,
            ConnectionTimeout = TimeSpan.FromSeconds(1),
            MaxConcurrentSessions = 2
        };
        _server = new AudioSocketServer(options, NullLogger<AudioSocketServer>.Instance);
    }

    [Fact]
    public async Task Server_ShouldRejectConnection_WhenNoUuidSentWithinTimeout()
    {
        await _server.StartAsync(CancellationToken.None);

        // Connect a raw TCP client that does NOT send a UUID frame
        using var rawClient = new TcpClient();
        await rawClient.ConnectAsync("127.0.0.1", _server.BoundPort);

        // Wait longer than the timeout
        await Task.Delay(1500);

        // The server should have closed the connection
        _server.ActiveSessionCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldLogOnlyNoUuidFrameWarning_WhenNoUuidArrivesWithinTimeout()
    {
        // Arrange — a real loopback connection whose server end goes straight to the handler, as the
        // accept loop hands it over. The UUID timeout runs on a fake clock, so it fires when the test
        // moves the clock and at no other moment; nothing here waits on a real one.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var rawClient = new TcpClient();
        await rawClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();

        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();
        var connectionTimeout = TimeSpan.FromSeconds(30);
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0, ConnectionTimeout = connectionTimeout },
            logger,
            time);

        var handling = server.HandleConnectionAsync(accepted, CancellationToken.None);
        var uuidTimeout = await NextTimerAsync(time);
        uuidTimeout.DueTime.Should().Be(connectionTimeout, "the wait for the UUID frame is bounded by ConnectionTimeout");
        handling.IsCompleted.Should().BeFalse("with nothing sent and the clock not moved, the handler is still waiting for the UUID frame");

        // Act — the client has sent nothing; move the clock to the deadline
        time.Advance(connectionTimeout);
        await handling.WaitAsync(SignalTimeout);

        // Assert — the handler has returned, so these are all the entries it logged for the connection
        logger.Entries.Should().Equal(
            [new LogEntry(LogLevel.Warning, NoUuidFrameEventId, "NoUuidFrame", null)],
            "a client that misses the UUID deadline gets the designed warning and nothing else, no connection error");
        (await ReadFromServerAsync(rawClient)).Should().Be(0, "the server closes the connection it gave up on");
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldCloseWithoutWarningOrError_WhenServerStopsBeforeUuidArrives()
    {
        // Arrange — a real loopback connection whose server end goes straight to the handler, with the
        // test's own token standing in for the one StartAsync hands every connection. Called directly,
        // the handler returns at its first await, the UUID read, so it is known to be waiting there
        // before the token is cancelled; through the accept loop that would be a race. The UUID timeout
        // runs on a fake clock that never moves, so only the token can end the wait.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var rawClient = new TcpClient();
        await rawClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();

        var logger = new CapturingLogger();
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0, ConnectionTimeout = TimeSpan.FromSeconds(30) },
            logger,
            new FakeTimeProvider());
        using var serverStopping = new CancellationTokenSource();

        var handling = server.HandleConnectionAsync(accepted, serverStopping.Token);
        handling.IsCompleted.Should().BeFalse("with nothing sent, the handler is still waiting for the UUID frame");

        // Act
        await serverStopping.CancelAsync();
        await handling.WaitAsync(SignalTimeout);

        // Assert
        logger.Entries.Should().NotContain(
            entry => entry.Level >= LogLevel.Warning,
            "a stopping server that closes a connection still waiting for its UUID has hit neither the timeout nor an error");
        (await ReadFromServerAsync(rawClient)).Should().Be(0, "the server still closes the connection");
    }

    [Fact]
    public async Task Server_ShouldLogHandleConnectionError_WhenOnSessionStartedThrowsOperationCanceledException()
    {
        // Arrange — a subscriber runs only once the UUID frame has arrived, so an
        // OperationCanceledException it raises is the consumer's own failure, not the end of the wait
        // for the UUID. The UUID timeout runs on a fake clock that never moves, so it cannot fire.
        var logger = new CapturingLogger();
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0 },
            logger,
            new FakeTimeProvider());
        server.OnSessionStarted += _ => ValueTask.FromException(new OperationCanceledException());
        await server.StartAsync(CancellationToken.None);

        // Act
        await using var client = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client.ConnectAsync();
        var firstWarningOrAbove = await logger.FirstWarningOrAbove.WaitAsync(SignalTimeout);
        var audioBytes = await AudioBytesUntilServerClosesAsync(client).WaitAsync(SignalTimeout);

        // Assert — the handler closes the connection after it logs, so once the client has seen the close,
        // every entry the handler logs for the connection is in. The session's read loop can still add a
        // warning of its own as the connection goes, so the full list is not pinned.
        firstWarningOrAbove.Should().Be(
            new LogEntry(LogLevel.Error, HandleConnectionErrorEventId, "HandleConnectionError", nameof(OperationCanceledException)),
            "a cancellation raised by a subscriber after the UUID arrived is a connection error, as it always was");
        logger.Entries.Should().NotContain(
            entry => entry.EventName == "NoUuidFrame",
            "the client sent its UUID frame, so it missed no deadline");
        logger.Entries.Where(entry => entry.Level >= LogLevel.Error).Should().ContainSingle(
            "the subscriber's failure is logged once");
        audioBytes.Should().Be(0, "the server sends no audio and closes the connection");
    }

    [Fact]
    public async Task Server_ShouldRejectConnection_WhenMaxSessionsReached()
    {
        await _server.StartAsync(CancellationToken.None);

        // Fill up to max (2 sessions)
        await using var client1 = new AudioSocketClient("127.0.0.1", _server.BoundPort, Guid.NewGuid());
        await using var client2 = new AudioSocketClient("127.0.0.1", _server.BoundPort, Guid.NewGuid());
        await client1.ConnectAsync();
        await client2.ConnectAsync();
        await Task.Delay(300);

        _server.ActiveSessionCount.Should().Be(2);

        // Third connection should be rejected
        await using var client3 = new AudioSocketClient("127.0.0.1", _server.BoundPort, Guid.NewGuid());
        await client3.ConnectAsync();
        await Task.Delay(300);

        // Session count should not exceed max
        _server.ActiveSessionCount.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public void Server_BoundPort_ShouldBeZero_BeforeStart()
    {
        var options = new AudioSocketOptions { Port = 0 };
        var server = new AudioSocketServer(options, NullLogger<AudioSocketServer>.Instance);

        server.BoundPort.Should().Be(0);
    }

    [Fact]
    public async Task Server_StopAsync_ShouldCleanUpAllSessions()
    {
        await _server.StartAsync(CancellationToken.None);

        await using var client1 = new AudioSocketClient("127.0.0.1", _server.BoundPort, Guid.NewGuid());
        await client1.ConnectAsync();
        await Task.Delay(200);

        _server.ActiveSessionCount.Should().Be(1);

        await _server.StopAsync(CancellationToken.None);

        _server.ActiveSessionCount.Should().Be(0);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    /// <summary>
    /// Reads from a connection the server never writes to, so the only way the read completes is the
    /// server closing its end, which reads as 0.
    /// </summary>
    private static Task<int> ReadFromServerAsync(TcpClient client) =>
        client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(SignalTimeout);

    /// <summary>
    /// Totals the audio the server sends until it closes the connection. The enumeration ends only on
    /// that close, so completing at all is the signal.
    /// </summary>
    private static async Task<int> AudioBytesUntilServerClosesAsync(AudioSocketClient client)
    {
        var bytes = 0;
        await foreach (var frame in client.ReadAudioAsync())
            bytes += frame.Length;

        return bytes;
    }

    /// <summary>The next timer created on <paramref name="time"/>, as soon as it exists.</summary>
    private static Task<FakeTimeProvider.FakeTimer> NextTimerAsync(FakeTimeProvider time) =>
        time.TimersCreated.ReadAsync().AsTask().WaitAsync(SignalTimeout);

    /// <summary>A server log entry, reduced to what these tests assert on.</summary>
    private sealed record LogEntry(LogLevel Level, int EventId, string? EventName, string? ExceptionType);

    /// <summary>
    /// Records every entry the server logs, and completes <see cref="FirstWarningOrAbove"/> with the
    /// first one at <see cref="LogLevel.Warning"/> or above.
    /// </summary>
    private sealed class CapturingLogger : ILogger<AudioSocketServer>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        private readonly TaskCompletionSource<LogEntry> _firstWarningOrAbove =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public Task<LogEntry> FirstWarningOrAbove => _firstWarningOrAbove.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = new LogEntry(logLevel, eventId.Id, eventId.Name, exception?.GetType().Name);
            _entries.Enqueue(entry);
            if (logLevel >= LogLevel.Warning)
                _firstWarningOrAbove.TrySetResult(entry);
        }
    }
}
