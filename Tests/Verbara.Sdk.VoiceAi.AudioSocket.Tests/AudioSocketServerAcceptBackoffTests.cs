using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Tests;

/// <summary>
/// The accept loop's backoff: a failed accept is logged and retried after a wait that starts at
/// 100 ms, doubles with each consecutive failure up to 5 s, and starts over after an accept succeeds.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here waits on a real clock. The loop waits through <see cref="FakeTimeProvider"/>, which
/// publishes each wait as the loop asks for it, and the loop cannot retry until the test advances
/// the clock past that wait. That is also what makes the attempt counts exact rather than racy.
/// </para>
/// <para>
/// Failures come from <see cref="AudioSocketServer.AcceptOverride"/> rather than from a real
/// listener. Getting a real accept to fail on demand means exhausting file descriptors or stopping
/// the listener under the loop, and the second surfaces as different exceptions depending on where
/// the stop lands. The tests drive <see cref="AudioSocketServer.AcceptLoopAsync"/> directly, with their
/// own token, which is the method <see cref="AudioSocketServer.StartAsync"/> runs.
/// </para>
/// </remarks>
public sealed class AudioSocketServerAcceptBackoffTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AcceptLoopAsync_ShouldDoubleTheWaitUpToFiveSeconds_WhenAcceptsKeepFailing()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var logger = new AcceptErrorCounter();
        await using var server = new AudioSocketServer(new AudioSocketOptions { Port = 0 }, logger, time);
        var attempts = 0;
        server.AcceptOverride = _ =>
        {
            Interlocked.Increment(ref attempts);
            return FailedAccept();
        };
        using var cts = new CancellationTokenSource();

        // Act — the loop runs on the pool; the test only reads each wait and moves the clock past it
        var loop = Task.Run(() => server.AcceptLoopAsync(cts.Token));
        var waits = new List<TimeSpan>();
        for (var failure = 1; failure <= 8; failure++)
        {
            var timer = await NextTimerAsync(time);
            waits.Add(timer.DueTime);
            Volatile.Read(ref attempts).Should().Be(failure, "nothing is retried before the clock reaches the wait");

            if (failure < 8)
                time.Advance(timer.DueTime);
        }

        await cts.CancelAsync();
        await loop.WaitAsync(SignalTimeout);

        // Assert
        waits.Should().Equal(Milliseconds(100, 200, 400, 800, 1600, 3200, 5000, 5000));
        logger.AcceptErrors.Should().Be(8, "each failed accept is still logged, once");
    }

    [Fact]
    public async Task AcceptLoopAsync_ShouldStartTheWaitOver_WhenAnAcceptSucceeds()
    {
        // Arrange — three failures, then a real loopback connection, then failures again
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();

        var time = new FakeTimeProvider();
        await using var server = new AudioSocketServer(new AudioSocketOptions { Port = 0 }, new AcceptErrorCounter(), time);
        var attempts = 0;
        server.AcceptOverride = _ =>
            Interlocked.Increment(ref attempts) == 4 ? ValueTask.FromResult(accepted) : FailedAccept();
        using var cts = new CancellationTokenSource();

        // Act
        var loop = Task.Run(() => server.AcceptLoopAsync(cts.Token));
        var waits = new List<TimeSpan>();
        for (var wait = 1; wait <= 4; wait++)
        {
            var timer = await NextTimerAsync(time);
            waits.Add(timer.DueTime);

            if (wait < 4)
                time.Advance(timer.DueTime);
        }

        var attemptsAtLastWait = Volatile.Read(ref attempts);
        await cts.CancelAsync();
        await loop.WaitAsync(SignalTimeout);

        // Assert — the success is attempt 4, so the wait after attempt 5 is the first of a new run
        attemptsAtLastWait.Should().Be(5);
        waits.Should().Equal(Milliseconds(100, 200, 400, 100));
    }

    [Fact]
    public async Task AcceptLoopAsync_ShouldEndWithoutWaitingOutTheBackoff_WhenCancelledDuringIt()
    {
        // Arrange — park the loop in its first wait. Which wait does not matter on a clock that never
        // moves, and the first keeps this test independent of how the waits grow.
        var time = new FakeTimeProvider();
        await using var server = new AudioSocketServer(new AudioSocketOptions { Port = 0 }, new AcceptErrorCounter(), time);
        var attempts = 0;
        server.AcceptOverride = _ =>
        {
            Interlocked.Increment(ref attempts);
            return FailedAccept();
        };
        using var cts = new CancellationTokenSource();

        var loop = Task.Run(() => server.AcceptLoopAsync(cts.Token));
        await NextTimerAsync(time);
        var attemptsBeforeCancel = Volatile.Read(ref attempts);

        // Act — the clock is never moved past the pending wait, so only the token can end it
        await cts.CancelAsync();
        var fault = await Record.ExceptionAsync(() => loop.WaitAsync(SignalTimeout));

        // Assert
        fault.Should().BeNull("a cancelled wait ends the loop; it neither hangs nor escapes as an exception");
        loop.Status.Should().Be(TaskStatus.RanToCompletion);
        Volatile.Read(ref attempts).Should().Be(attemptsBeforeCancel, "no accept is attempted after cancellation");
    }

    private static ValueTask<TcpClient> FailedAccept() =>
        ValueTask.FromException<TcpClient>(new SocketException((int)SocketError.TooManyOpenSockets));

    private static Task<FakeTimeProvider.FakeTimer> NextTimerAsync(FakeTimeProvider time) =>
        time.TimersCreated.ReadAsync().AsTask().WaitAsync(SignalTimeout);

    private static TimeSpan[] Milliseconds(params int[] values) =>
        [.. values.Select(ms => TimeSpan.FromMilliseconds(ms))];

    /// <summary>Counts the server's <c>AcceptError</c> entries and ignores everything else.</summary>
    private sealed class AcceptErrorCounter : ILogger<AudioSocketServer>
    {
        private int _acceptErrors;

        public int AcceptErrors => Volatile.Read(ref _acceptErrors);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == "AcceptError")
                Interlocked.Increment(ref _acceptErrors);
        }
    }
}
