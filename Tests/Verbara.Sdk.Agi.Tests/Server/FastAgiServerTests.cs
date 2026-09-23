using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Verbara.Sdk;
using Verbara.Sdk.Agi.Mapping;
using Verbara.Sdk.Agi.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Agi.Tests.Server;

public sealed class FastAgiServerTests : IAsyncDisposable
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The minimal AGI request header block, which is what makes a connection a script run.</summary>
    private const string AgiRequestHeaders =
        "agi_network: yes\nagi_network_script: probe\nagi_channel: SIP/test\nagi_uniqueid: 1.1\n\n";

    private FastAgiServer? _sut;

    public async ValueTask DisposeAsync()
    {
        if (_sut is not null)
            await _sut.DisposeAsync();
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task StartAsync_ShouldSetIsRunningTrue()
    {
        var port = GetAvailablePort();
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(port, strategy, NullLogger<FastAgiServer>.Instance);

        _sut.IsRunning.Should().BeFalse();
        await _sut.StartAsync();
        _sut.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsync_ShouldSetIsRunningFalse()
    {
        var port = GetAvailablePort();
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(port, strategy, NullLogger<FastAgiServer>.Instance);

        await _sut.StartAsync();
        _sut.IsRunning.Should().BeTrue();

        await _sut.StopAsync();
        _sut.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_ShouldAcceptTcpConnections()
    {
        var port = GetAvailablePort();
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(port, strategy, NullLogger<FastAgiServer>.Instance);
        await _sut.StartAsync();

        // Attempt to connect a TCP client
        using var client = new TcpClient();
        var act = async () => await client.ConnectAsync(IPAddress.Loopback, port);
        await act.Should().NotThrowAsync();

        client.Connected.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsync_ShouldRejectNewConnections()
    {
        var port = GetAvailablePort();
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(port, strategy, NullLogger<FastAgiServer>.Instance);

        await _sut.StartAsync();
        await _sut.StopAsync();

        // After stop, new connections should fail
        using var client = new TcpClient();
        var act = async () => await client.ConnectAsync(IPAddress.Loopback, port);
        await act.Should().ThrowAsync<SocketException>();
    }

    [Fact]
    public async Task ConnectionTimeout_ShouldDefaultToFiveMinutes()
    {
        var port = GetAvailablePort();
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(port, strategy, NullLogger<FastAgiServer>.Instance);

        _sut.ConnectionTimeout.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task ConnectionTimeout_ShouldBeConfigurable()
    {
        var port = GetAvailablePort();
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(port, strategy, NullLogger<FastAgiServer>.Instance)
        {
            ConnectionTimeout = TimeSpan.FromSeconds(30)
        };

        _sut.ConnectionTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task HandleConnection_ShouldTimeout_WhenScriptHangs()
    {
        var port = GetAvailablePort();
        var hangingScript = new HangingScript();
        var strategy = Substitute.For<IMappingStrategy>();
        strategy.Resolve(Arg.Any<AgiRequest>()).Returns(hangingScript);

        _sut = new FastAgiServer(port, strategy, NullLogger<FastAgiServer>.Instance)
        {
            ConnectionTimeout = TimeSpan.FromMilliseconds(200)
        };
        await _sut.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);

        // Send minimal AGI request headers
        var stream = client.GetStream();
        var headers = "agi_network: yes\nagi_network_script: hang\nagi_channel: SIP/test\nagi_uniqueid: 1.1\n\n";
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(headers));

        // Wait for connection timeout + margin
        await Task.Delay(500);

        // The hanging script should have been cancelled via timeout
        hangingScript.WasCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_ShouldStopIfRunning()
    {
        var port = GetAvailablePort();
        var strategy = Substitute.For<IMappingStrategy>();
        _sut = new FastAgiServer(port, strategy, NullLogger<FastAgiServer>.Instance);

        await _sut.StartAsync();
        _sut.IsRunning.Should().BeTrue();

        await _sut.DisposeAsync();
        _sut.IsRunning.Should().BeFalse();
        _sut = null; // Prevent double dispose in DisposeAsync
    }

    // ------------------------------------------------------- accept failures that are not the stop

    [Fact]
    public async Task AcceptLoopAsync_ShouldReportItAndKeepAccepting_WhenAnAcceptFailsWhileRunning()
    {
        // Arrange — the first accept fails with the shape a descriptor exhaustion has, the second
        // hands over a real connection, and every later one parks. The loop's own behaviour is the
        // only thing that can produce the second and third attempts, so reaching the third is the
        // evidence that a survivable failure did not end it.
        using var pair = new TcpListener(IPAddress.Loopback, 0);
        pair.Start();
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)pair.LocalEndpoint).Port);
        using var accepted = await pair.AcceptTcpClientAsync();

        var logger = new CapturingLogger();
        var strategy = Substitute.For<IMappingStrategy>();
        var server = new FastAgiServer(GetAvailablePort(), strategy, logger);

        var attempts = 0;
        var thirdAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AcceptOverride = token =>
        {
            switch (Interlocked.Increment(ref attempts))
            {
                case 1:
                    throw new SocketException((int)SocketError.TooManyOpenSockets);
                case 2:
                    return ValueTask.FromResult(accepted);
                default:
                    thirdAttempt.TrySetResult();
                    return ParkUntilCancelledAsync(token);
            }
        };

        await server.StartAsync();
        try
        {
            // Act — the third attempt can only happen after the loop survived the first failure and
            // then finished serving the second accept, so this one wait orders the whole sequence.
            var keptAccepting = await ReachedAsync(thirdAttempt.Task);

            // Assert
            keptAccepting.Should().BeTrue(
                "an accept that failed while the server is still running must not end the loop — " +
                "after {0} attempt(s) all the server had logged was [{1}]",
                Volatile.Read(ref attempts),
                string.Join(", ", logger.Entries.Select(entry => entry.EventName)));
            logger.Entries.Should().ContainSingle(
                entry => entry.Level == LogLevel.Error
                    && entry.EventName == "AcceptLoopFailed"
                    && entry.ExceptionType == nameof(SocketException),
                "an accept that failed while the server is still running is the loss of every " +
                "connection Asterisk will open, so it is reported at Error rather than swallowed");
            logger.Entries.Should().Contain(
                entry => entry.EventName == "ConnectionAccepted",
                "the connection after the failure was accepted and served, not merely attempted");
            server.IsRunning.Should().BeTrue(
                "IsRunning means bound and accepting, and after a survived failure the server is " +
                "still both — today it reports true over a loop that is already dead");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcceptLoopAsync_ShouldBackOffFurther_WhenAcceptsKeepFailing()
    {
        // Arrange — every accept fails, so the loop is a pure backoff generator and each wait it asks
        // for is read off the fake clock without any of it being spent. Nothing here touches a real
        // clock: the waits below are the due times the loop asked for, not time this test sat out.
        var time = new FakeTimeProvider();
        var strategy = Substitute.For<IMappingStrategy>();
        var server = new FastAgiServer(
            GetAvailablePort(),
            strategy,
            NullLogger<FastAgiServer>.Instance,
            time);
        server.AcceptOverride = _ => throw new SocketException((int)SocketError.TooManyOpenSockets);

        await server.StartAsync();
        try
        {
            // Act — read each wait as the loop asks for it, then release it to reach the next
            var waits = new List<TimeSpan>();
            for (var i = 0; i < 8; i++)
            {
                var timer = await NextTimerAsync(time);
                waits.Add(timer.DueTime);
                time.Advance(timer.DueTime);
            }

            // Assert
            waits.Should().Equal(
                [
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(200),
                    TimeSpan.FromMilliseconds(400),
                    TimeSpan.FromMilliseconds(800),
                    TimeSpan.FromMilliseconds(1600),
                    TimeSpan.FromMilliseconds(3200),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5)
                ],
                "the wait doubles with each consecutive failure and then holds at the cap, so a " +
                "failure that persists neither spins the loop nor walks away from the socket");
            waits[0].Should().Be(FastAgiServer.InitialAcceptBackoff);
            waits[^1].Should().Be(FastAgiServer.MaxAcceptBackoff);
            server.IsRunning.Should().BeTrue("the server is still bound and still accepting");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcceptLoopAsync_ShouldResetTheBackoff_WhenAnAcceptSucceedsBetweenFailures()
    {
        // Arrange — a real loopback pair supplies the one connection the middle accept returns, so the
        // success is a real hand-off and not a stub. Accepts one and three fail; the wait the loop asks
        // for after the third is what says whether the success reset the run.
        using var pair = new TcpListener(IPAddress.Loopback, 0);
        pair.Start();
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)pair.LocalEndpoint).Port);
        using var accepted = await pair.AcceptTcpClientAsync();

        var time = new FakeTimeProvider();
        var strategy = Substitute.For<IMappingStrategy>();
        var server = new FastAgiServer(
            GetAvailablePort(),
            strategy,
            NullLogger<FastAgiServer>.Instance,
            time);
        var attempts = 0;
        server.AcceptOverride = token => Interlocked.Increment(ref attempts) switch
        {
            2 => ValueTask.FromResult(accepted),
            >= 4 => ParkUntilCancelledAsync(token),
            _ => throw new SocketException((int)SocketError.TooManyOpenSockets)
        };

        await server.StartAsync();
        try
        {
            // Act — the first failure's wait, then the success, then the second failure's wait
            var afterFirstFailure = await NextTimerAsync(time);
            time.Advance(afterFirstFailure.DueTime);
            var afterSuccess = await NextTimerAsync(time);

            // Assert
            afterFirstFailure.DueTime.Should().Be(FastAgiServer.InitialAcceptBackoff);
            afterSuccess.DueTime.Should().Be(
                FastAgiServer.InitialAcceptBackoff,
                "a successful accept starts the run over, so the next failure waits the initial " +
                "backoff again rather than the doubled one");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopAsync_ShouldEndTheLoopWithoutReportingAFailure_WhenTheServerIsStopped()
    {
        // Arrange — the real listener and a real accepted connection, so the loop is known to be
        // parked on its next accept when the stop lands. That is the ending StopAsync produces:
        // SetState(Stopping) first, then Stop(), which aborts the pending accept. No override here —
        // the accept under test is the real one, because the abort is what the real one raises.
        var port = GetAvailablePort();
        var scriptReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var strategy = Substitute.For<IMappingStrategy>();
        strategy.Resolve(Arg.Any<AgiRequest>()).Returns(new SignallingScript(scriptReached));

        var logger = new CapturingLogger();
        var server = new FastAgiServer(port, strategy, logger);
        await server.StartAsync();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(AgiRequestHeaders));
            await stream.FlushAsync();

            (await ReachedAsync(scriptReached.Task)).Should().BeTrue(
                "the loop accepted the connection and ran its script, so it is back on the next " +
                "accept and the stop below lands on a pending one");

            // Act — StopAsync awaits the accept loop, so it returns only once the loop has ended
            await server.StopAsync().AsTask().WaitAsync(SignalTimeout);

            // Assert
            server.IsRunning.Should().BeFalse();
            logger.Entries.Should().NotContain(
                entry => entry.EventName == "AcceptLoopFailed",
                "an accept aborted by the stop is the stop, not a failure — this is the case that " +
                "catches a filter transplanted without checking that StopAsync clears the running " +
                "state before it touches the listener");
            logger.Entries.Should().Contain(
                entry => entry.EventName == "ServerStopped",
                "the stop ran to the end rather than being left behind by a loop still waiting");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>
    /// Whether <paramref name="signal"/> completes within <see cref="SignalTimeout"/>. Returning a
    /// bool rather than letting the timeout escape is what turns "the loop never got there" into an
    /// assertion failure carrying the reason instead of a bare timeout.
    /// </summary>
    private static async Task<bool> ReachedAsync(Task signal)
    {
        try
        {
            await signal.WaitAsync(SignalTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// An accept that never returns a connection and ends only when <paramref name="token"/> does, so
    /// a loop parked on it attempts nothing further and puts no wait on any clock.
    /// </summary>
    private static async ValueTask<TcpClient> ParkUntilCancelledAsync(CancellationToken token)
    {
        var parked = new TaskCompletionSource<TcpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => parked.TrySetCanceled(token));
        return await parked.Task;
    }

    /// <summary>
    /// The next timer the code under test creates on <paramref name="time"/> — the wait it asked for,
    /// read without any of it being spent, and the proof it is parked on that wait before the clock
    /// is moved.
    /// </summary>
    private static Task<FakeTimeProvider.FakeTimer> NextTimerAsync(FakeTimeProvider time) =>
        time.TimersCreated.ReadAsync().AsTask().WaitAsync(SignalTimeout);

    /// <summary>A server log entry, reduced to what these tests assert on.</summary>
    private sealed record LogEntry(LogLevel Level, string? EventName, string? ExceptionType);

    /// <summary>Records every entry the server logs.</summary>
    private sealed class CapturingLogger : ILogger<FastAgiServer>
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

    private sealed class HangingScript : IAgiScript
    {
        public bool WasCancelled { get; private set; }

        public async ValueTask ExecuteAsync(IAgiChannel channel, IAgiRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
        }
    }
}
