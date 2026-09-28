using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Verbara.Sdk.Agi.Diagnostics;
using Verbara.Sdk.Agi.Mapping;
using Verbara.Sdk.Agi.Server;
using Verbara.Sdk.Agi.Tests.TestSupport;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Verbara.Sdk.Agi.Tests.Server;

/// <summary>
/// The window between the accept that returns a connection and the handler that owns it, in
/// <see cref="FastAgiServer"/>: a failure that belongs to one accepted connection is that connection's
/// failure, not the accept loop's, and it is not a failed script.
/// </summary>
/// <remarks>
/// <para>
/// Each failing connection reaches the loop through <c>AcceptOverride</c>, built to fail at one step:
/// configuring its socket (a UDP-backed client, a disposed socket) or obtaining its stream (a client
/// that was never connected). No natural trigger for either was found on Linux; every one of these is
/// a socket built to fail.
/// </para>
/// <para>
/// The class asserts on <c>agi.connections.accepted</c> and <c>agi.scripts.failed</c>, which are
/// process-wide, so it runs in <see cref="AgiMetricsGroup"/>. Nothing here waits on a clock: each wait
/// ends on the signal it asserts, and <see cref="SignalTimeout"/> only bounds it.
/// </para>
/// </remarks>
[Collection(AgiMetricsGroup.Name)]
public sealed class FastAgiConnectionWindowTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The minimal AGI request header block, which is what makes a connection a script run.</summary>
    private const string AgiRequestHeaders =
        "agi_network: yes\nagi_network_script: probe\nagi_channel: SIP/test\nagi_uniqueid: 1.1\n\n";

    // ------------------------------------- a connection whose configuration fails after the accept

    [Fact]
    public async Task AcceptLoopAsync_ShouldCloseTheAcceptedSocket_WhenConfiguringItThrows()
    {
        // Arrange — the accept seam hands over a client whose socket is a UDP socket, so setting
        // TCP_NODELAY on it throws a SocketException (setsockopt answers ENOPROTOOPT) while the socket
        // is still open. The clock is fake and never moves. A loop that takes the failure for an
        // accept failure asks it for a backoff timer, and that timer is the signal the test ends on
        // in that case. Otherwise the next accept is.
        //
        // Ordered by construction: the loop starts the handler inline, and this failure comes before
        // the handler's first await, so the handler is done with the connection before the loop asks
        // for its next accept. The premise is checked below, because a release that came after the
        // next accept could also come after the assertions.
        using var counters = new AgiCounters();
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();
        using var accepted = AcceptedClients.UdpBacked();
        using var client = new ReleaseSignallingClient(accepted.Socket);
        var attempts = 0;
        var releasedAtNextAccept = false;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(Substitute.For<IMappingStrategy>(), logger, time);
        server.AcceptOverride = token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                return ValueTask.FromResult<TcpClient>(client);

            releasedAtNextAccept = client.Released.IsCompleted;
            secondAttempt.TrySetResult();
            return AcceptedClients.ParkUntilCancelledAsync(token);
        };
        var backoffRequested = time.TimersCreated.ReadAsync().AsTask();

        await server.StartAsync();
        try
        {
            // Act
            await Task.WhenAny(secondAttempt.Task, backoffRequested).WaitAsync(SignalTimeout);

            // Assert
            using (new AssertionScope())
            {
                (client.Released.IsCompleted && !releasedAtNextAccept).Should().BeFalse(
                    "the premise of this test is a handler that is done with the connection before the " +
                    "loop accepts again; a release after that could also come after these assertions");
                accepted.IsSocketClosed.Should().BeTrue(
                    "a connection the loop accepted and could not configure is closed, not leaked; the " +
                    "server logged [{0}]",
                    logger.Describe());
                logger.Entries.Should().ContainSingle(
                    entry => entry.Level == LogLevel.Error
                        && entry.EventName == "ConnectionError"
                        && entry.ExceptionType == nameof(SocketException),
                    "the failure belongs to that one connection, so it is reported once as its error");
                logger.Entries.Should().NotContain(
                    entry => entry.EventName == "AcceptLoopFailed",
                    "no accept failed: the accept returned a connection, and configuring it is serving it");
                counters.Failed.Should().Be(
                    0,
                    "no script ran for that connection, so no script failed; counters = {0}",
                    counters.Describe());
                counters.Accepted.Should().Be(
                    0,
                    "a connection that never got a stream is not counted as accepted; counters = {0}",
                    counters.Describe());
                server.IsRunning.Should().BeTrue("one connection failing is not the server failing");
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcceptLoopAsync_ShouldAcceptTheNextConnectionWithoutWaiting_WhenConfiguringOneFails()
    {
        // Arrange — the configure fails with a SocketException, the type the loop backs off for when an
        // accept fails. The clock is fake and never moves, so a backoff would park the loop for good,
        // and the timer it asks for is the signal the test ends on in that case. Otherwise the next
        // accept is.
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();
        using var accepted = AcceptedClients.UdpBacked();
        using var client = new ReleaseSignallingClient(accepted.Socket);
        var attempts = 0;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(Substitute.For<IMappingStrategy>(), logger, time);
        server.AcceptOverride = token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                return ValueTask.FromResult<TcpClient>(client);

            secondAttempt.TrySetResult();
            return AcceptedClients.ParkUntilCancelledAsync(token);
        };
        var backoffRequested = time.TimersCreated.ReadAsync().AsTask();

        await server.StartAsync();
        try
        {
            // Act
            await Task.WhenAny(secondAttempt.Task, backoffRequested).WaitAsync(SignalTimeout);
            var requested = backoffRequested.IsCompleted
                ? (await backoffRequested).DueTime.ToString()
                : "none";

            // Assert — the loop requests a backoff timer before its next accept, never after it, so
            // at the moment of the next accept, no timer means none was requested
            using (new AssertionScope())
            {
                secondAttempt.Task.IsCompleted.Should().BeTrue(
                    "no accept failed, so there is nothing to wait out before the next one; attempts = " +
                    "{0}, backoff timer requested = {1}, the server logged [{2}]",
                    Volatile.Read(ref attempts),
                    requested,
                    logger.Describe());
                backoffRequested.IsCompleted.Should().BeFalse(
                    "one connection's failure costs the next one no wait; backoff timer requested = {0}",
                    requested);
                logger.Entries.Should().NotContain(
                    entry => entry.EventName == "AcceptLoopFailed",
                    "no accept failed");
                server.IsRunning.Should().BeTrue();
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcceptLoopAsync_ShouldKeepAccepting_WhenConfiguringAnAcceptedClientThrowsObjectDisposed()
    {
        // Arrange — the first accept hands over a client whose socket is already disposed, so setting
        // TCP_NODELAY on it throws an ObjectDisposedException: the type the loop reads as its own
        // listener's stop. Every later accept is a real one on a loopback listener, so a real peer can
        // try to reach a script through the same loop afterwards. A loop that ends on the first
        // connection never makes the second accept, and never serves the peer, so in that case only
        // the bound ends each wait.
        var logger = new CapturingLogger();
        using var accepted = AcceptedClients.DisposedSocket();
        using var client = new ReleaseSignallingClient(accepted.Socket);
        using var pair = new TcpListener(IPAddress.Loopback, 0);
        pair.Start();
        var scriptReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var strategy = Substitute.For<IMappingStrategy>();
        strategy.Resolve(Arg.Any<AgiRequest>()).Returns(new SignallingScript(scriptReached));
        var attempts = 0;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(strategy, logger, new FakeTimeProvider());
        server.AcceptOverride = token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                return ValueTask.FromResult<TcpClient>(client);

            secondAttempt.TrySetResult();
            return pair.AcceptTcpClientAsync(token);
        };

        await server.StartAsync();
        try
        {
            var health = await new AgiHealthCheck(server).CheckHealthAsync(new HealthCheckContext());

            // Act — the peer connects only once the first connection has failed, and sends a request
            using var peer = new TcpClient();
            using (new AssertionScope())
            {
                var nextAccept = () => secondAttempt.Task;
                await nextAccept.Should().CompleteWithinAsync(
                    SignalTimeout,
                    "one connection's closed socket is not the server's stop, so the loop accepts again; " +
                    "attempts = {0}, IsRunning = {1}, health = {2} ('{3}'), the server logged [{4}]",
                    Volatile.Read(ref attempts),
                    server.IsRunning,
                    health.Status,
                    health.Description,
                    logger.Describe());

                await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)pair.LocalEndpoint).Port);
                var stream = peer.GetStream();
                await stream.WriteAsync(Encoding.UTF8.GetBytes(AgiRequestHeaders));
                await stream.FlushAsync();

                // Assert
                var served = () => scriptReached.Task;
                await served.Should().CompleteWithinAsync(
                    SignalTimeout,
                    "a peer that connects after one connection's failure is still served: its script " +
                    "is reached");
                server.IsRunning.Should().BeTrue("the server is still bound and still accepting");
                logger.Entries.Should().ContainSingle(
                    entry => entry.Level == LogLevel.Error
                        && entry.EventName == "ConnectionError"
                        && entry.ExceptionType == nameof(ObjectDisposedException),
                    "the failure belongs to that one connection, so it is reported once as its error; " +
                    "the server logged [{0}]",
                    logger.Describe());
                logger.Entries.Should().NotContain(
                    entry => entry.EventName == "AcceptLoopFailed",
                    "no accept failed");
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    // ------------------------------------------- a connection whose stream cannot be obtained

    [Fact]
    public async Task HandleConnectionAsync_ShouldCloseTheSocketAndReportIt_WhenTheAcceptedClientIsNotConnected()
    {
        // Arrange — the accept seam hands over a TCP client that was never connected. Setting
        // TCP_NODELAY on it and reading its remote endpoint succeed, so the failure lands one step
        // later, when the handler asks the client for its stream (GetStream() throws
        // InvalidOperationException). The handler owns the connection by then, so it has to close it
        // and report the failure itself: nothing observes the task the loop discards.
        //
        // Ordered by construction: the loop starts the handler inline, and this failure comes before
        // the handler's first await, so the handler's task has completed before the loop asks for its
        // next accept. That next accept is the signal. The premise is checked below, because the
        // unobserved-exception witness reads nothing from a task that had not completed when the
        // collection ran.
        using var counters = new AgiCounters();
        var logger = new CapturingLogger();
        using var accepted = AcceptedClients.NeverConnected();
        using var client = new ReleaseSignallingClient(accepted.Socket);
        using var unobserved = new UnobservedServerFaults();
        var attempts = 0;
        var releasedAtNextAccept = false;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(Substitute.For<IMappingStrategy>(), logger, new FakeTimeProvider());
        server.AcceptOverride = token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                return ValueTask.FromResult<TcpClient>(client);

            releasedAtNextAccept = client.Released.IsCompleted;
            secondAttempt.TrySetResult();
            return AcceptedClients.ParkUntilCancelledAsync(token);
        };

        await server.StartAsync();
        try
        {
            // Act
            await secondAttempt.Task.WaitAsync(SignalTimeout);
            UnobservedServerFaults.CollectDiscardedTasks();

            // Assert
            using (new AssertionScope())
            {
                (client.Released.IsCompleted && !releasedAtNextAccept).Should().BeFalse(
                    "the premise of this test is a handler that is done with the connection before the " +
                    "loop accepts again; a release after that means the collection above may have run " +
                    "before the handler's task completed");
                accepted.IsSocketClosed.Should().BeTrue(
                    "the handler owns the connection, so it closes it when it cannot serve it; the " +
                    "server logged [{0}]",
                    logger.Describe());
                logger.Entries.Should().ContainSingle(
                    entry => entry.Level == LogLevel.Error
                        && entry.EventName == "ConnectionError"
                        && entry.ExceptionType == nameof(InvalidOperationException),
                    "a connection whose stream cannot be obtained is that connection's failure, reported " +
                    "once as its error; the server logged [{0}]",
                    logger.Describe());
                unobserved.Faults.Should().BeEmpty(
                    "the failure is reported where it happened, so it does not escape the task the loop " +
                    "discards, where nothing observes it; every unobserved exception seen: [{0}]",
                    string.Join("; ", unobserved.All));
                counters.Accepted.Should().Be(
                    0,
                    "a connection that never got a stream is not counted as accepted; counters = {0}",
                    counters.Describe());
                counters.Failed.Should().Be(
                    0,
                    "no script ran for that connection, so no script failed; counters = {0}",
                    counters.Describe());
                logger.Entries.Should().NotContain(
                    entry => entry.EventName == "AcceptLoopFailed",
                    "no accept failed");
                server.IsRunning.Should().BeTrue("one connection failing is not the server failing");
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------------- control: the stop

    [Fact]
    public async Task StopAsync_ShouldReportNeitherAnAcceptFailureNorAConnectionError_WhenTheServerIsStopped()
    {
        // Arrange — the real listener, and a real connection whose script is running when the stop
        // lands, so the loop is parked on its next accept and a handler is in flight. The stop aborts
        // the pending accept and cancels the script. StopAsync does not wait for handlers; the handler
        // releases the connection only after its catch has run, so the peer reading the end of the
        // stream is what orders the assertions after everything the handler logged.
        var scriptReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var strategy = Substitute.For<IMappingStrategy>();
        strategy.Resolve(Arg.Any<AgiRequest>()).Returns(new ParkedScript(scriptReached));
        var logger = new CapturingLogger();
        var server = new FastAgiServer(0, strategy, logger);
        await server.StartAsync();
        try
        {
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, BoundPort(server));
            var stream = peer.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(AgiRequestHeaders));
            await stream.FlushAsync();
            await scriptReached.Task.WaitAsync(SignalTimeout);

            // Act
            await server.StopAsync().AsTask().WaitAsync(SignalTimeout);
            var read = await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(SignalTimeout);

            // Assert
            using (new AssertionScope())
            {
                read.Should().Be(0, "the handler released the connection the stop cut short");
                logger.Entries.Should().NotContain(
                    entry => entry.EventName == "AcceptLoopFailed",
                    "an accept aborted by the stop is the stop, not an accept failure; the server " +
                    "logged [{0}]",
                    logger.Describe());
                logger.Entries.Should().NotContain(
                    entry => entry.EventName == "ConnectionError",
                    "a connection the stop cut short ended with the stop, which is not a connection " +
                    "error; the server logged [{0}]",
                    logger.Describe());
                logger.Entries.Should().Contain(
                    entry => entry.EventName == "ServerStopped",
                    "the stop ran to the end");
                server.IsRunning.Should().BeFalse();
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>
    /// A server on port 0 whose accepts the test supplies through <c>AcceptOverride</c>: its own
    /// listener is bound but never accepted from.
    /// </summary>
    private static FastAgiServer CreateServer(
        IMappingStrategy strategy,
        ILogger<FastAgiServer> logger,
        TimeProvider timeProvider) =>
        new(0, strategy, logger, timeProvider);

    /// <summary>
    /// The port the server's own listener is bound to. The server is started on port 0, so the OS
    /// picks the port when it binds. <see cref="FastAgiServer.Port"/> reports the configured port, not
    /// the bound one. A port probed first and bound later can be taken in between by any process on
    /// the machine, and the start then fails with "Address already in use".
    /// </summary>
    private static int BoundPort(FastAgiServer server) =>
        ((IPEndPoint)ListenerOf(server)!.LocalEndpoint).Port;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_listener")]
    private static extern ref TcpListener? ListenerOf(FastAgiServer server);

    /// <summary>
    /// A client that wraps a fixture's socket and signals when the server disposes it, so a test can
    /// wait on the release instead of polling the socket.
    /// </summary>
    private sealed class ReleaseSignallingClient : TcpClient
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ReleaseSignallingClient(Socket socket)
            : base(AddressFamily.InterNetwork)
        {
            var unused = Client;
            Client = socket;
            unused.Dispose();
        }

        public Task Released => _released.Task;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                _released.TrySetResult();
        }
    }

    /// <summary>
    /// Records every task exception that went unobserved while it is subscribed and whose stack names
    /// this server. The subscription is process-wide, so the filter keeps a fault from another test
    /// class running in parallel out of this one's assertion; <see cref="All"/> keeps everything, for
    /// the failure message.
    /// </summary>
    private sealed class UnobservedServerFaults : IDisposable
    {
        private readonly ConcurrentQueue<Exception> _seen = new();

        public UnobservedServerFaults() => TaskScheduler.UnobservedTaskException += OnUnobserved;

        /// <summary>The unobserved exceptions thrown through <see cref="FastAgiServer"/>.</summary>
        public IReadOnlyList<string> Faults =>
        [
            .. _seen
                .Where(ex => ex.StackTrace?.Contains(nameof(FastAgiServer), StringComparison.Ordinal) == true)
                .Select(ex => $"{ex.GetType().Name}: {ex.Message}")
        ];

        /// <summary>Every unobserved exception seen, whatever threw it.</summary>
        public IReadOnlyList<string> All => [.. _seen.Select(ex => $"{ex.GetType().Name}: {ex.Message}")];

        /// <summary>
        /// Collects, so a faulted task that nothing references any more is finalised and its
        /// exception, if nothing observed it, is published before this returns. Call it only once the
        /// task in question has completed.
        /// </summary>
        public static void CollectDiscardedTasks()
        {
            for (var pass = 0; pass < 3; pass++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        public void Dispose() => TaskScheduler.UnobservedTaskException -= OnUnobserved;

        private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.InnerExceptions)
                _seen.Enqueue(inner);
        }
    }

    /// <summary>A server log entry, reduced to what these tests assert on.</summary>
    private sealed record LogEntry(LogLevel Level, string? EventName, string? ExceptionType);

    /// <summary>Records every entry the server logs.</summary>
    private sealed class CapturingLogger : ILogger<FastAgiServer>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        /// <summary>Every entry as <c>Level:EventName(ExceptionType)</c>, for a failure message.</summary>
        public string Describe() =>
            string.Join(", ", _entries.Select(entry => $"{entry.Level}:{entry.EventName}({entry.ExceptionType})"));

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

    /// <summary>A script that reports it ran and returns, so the connection is served and released.</summary>
    private sealed class SignallingScript : IAgiScript
    {
        private readonly TaskCompletionSource _reached;

        public SignallingScript(TaskCompletionSource reached) => _reached = reached;

        public ValueTask ExecuteAsync(IAgiChannel channel, IAgiRequest request, CancellationToken cancellationToken = default)
        {
            _reached.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A script that reports it ran, then runs until its token is cancelled, so a stop lands on a
    /// connection that is still being served.
    /// </summary>
    private sealed class ParkedScript : IAgiScript
    {
        private readonly TaskCompletionSource _reached;

        public ParkedScript(TaskCompletionSource reached) => _reached = reached;

        public async ValueTask ExecuteAsync(IAgiChannel channel, IAgiRequest request, CancellationToken cancellationToken = default)
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken));
            _reached.TrySetResult();
            await cancelled.Task;
        }
    }
}
