using System.Threading.Channels;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// A second loss while the first reconnect's <see cref="AmiConnection.Reconnected"/> still waits in the notification
/// queue: whether that notification is still current is decided when it is delivered, not when it was queued.
/// </summary>
public sealed partial class AmiConnectionReconnectedDeliveryTests
{
    /// <summary>
    /// The first loss's <c>Lost</c> handler holds the queue, so Reconnected(2) waits behind it; the second session is lost
    /// too, and the third session logs in and queues Reconnected(3). Once the queue is released, the <c>Reconnected</c>
    /// handlers run once, for the third session; the second session's notification is dropped at delivery with one Debug
    /// line; and the connection reports a reconnect pending from the third login until that handler has returned.
    /// </summary>
    /// <remarks>
    /// Reconnected(3) is known to be queued by the queue's tail moving past the tail read while the third session's
    /// <c>[AMI] Connected</c> line is written: that line is the connect's last statement, written on the reconnect loop's
    /// thread before it queues the notification, so the read cannot already include it.
    /// </remarks>
    [Fact]
    public async Task Reconnected_ShouldBeDeliveredOnceForTheSurvivingSession_WhenASecondLossLandsWhileTheFirstReconnectedIsQueued()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var logins = Channel.CreateUnbounded<PipedSocket>();
        var serving = ServeAndSignalEveryLoginAsync(sockets, logins.Writer, peerCts.Token);
        AmiConnection? connection = null;
        var tailAtThirdConnect = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = 0;
        var logger = new TailReadingLogger(line =>
        {
            if (line.StartsWith("[AMI] Connected", StringComparison.Ordinal) && Interlocked.Increment(ref connected) == 3)
                tailAtThirdConnect.TrySetResult(connection!.PendingNotifications.Id);
        });
        connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(20),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(20),
        }), sockets, logger);
        using var gate = new ManualResetEventSlim(false);
        var lostEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lostNotification = NotAQueueTask;
        var lostCalls = 0;
        connection.Lost += _ =>
        {
            if (Interlocked.Increment(ref lostCalls) != 1)
                return;

            Volatile.Write(ref lostNotification, Task.CurrentId ?? NotAQueueTask);
            lostEntered.TrySetResult();
            gate.Wait();
        };
        var deliveries = new List<(long Session, AmiConnectionState State, bool PendingInside)>();
        connection.Reconnected += () =>
        {
            lock (deliveries)
                deliveries.Add((connection.EventLossMark.Session, connection.State, connection.ReconnectPending));
        };

        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        await logins.Reader.ReadAsync(peerCts.Token).AsTask().WaitAsync(Bound);

        sockets.Created[0].CloseFromPeer();
        await lostEntered.Task.WaitAsync(Bound);
        await logins.Reader.ReadAsync(peerCts.Token).AsTask().WaitAsync(Bound);
        await TailMovedPastAsync(connection, Volatile.Read(ref lostNotification)).WaitAsync(Bound);

        sockets.Created[1].CloseFromPeer();
        await logins.Reader.ReadAsync(peerCts.Token).AsTask().WaitAsync(Bound);
        var tail = await tailAtThirdConnect.Task.WaitAsync(Bound);
        await TailMovedPastAsync(connection, tail).WaitAsync(Bound);
        var pendingAtThirdLogin = connection.ReconnectPending;

        gate.Set();
        await connection.PendingNotifications.WaitAsync(Bound);
        var pendingAfter = connection.ReconnectPending;
        List<(long Session, AmiConnectionState State, bool PendingInside)> delivered;
        lock (deliveries)
            delivered = [.. deliveries];
        await connection.DisposeAsync().AsTask().WaitAsync(Bound);
        await peerCts.CancelAsync();
        await serving;

        using (new AssertionScope())
        {
            delivered.Select(d => d.Session).Should().Equal([3L],
                "the Reconnected handler runs exactly once, for the third session: Reconnected(2) reaches delivery after its " +
                "session was lost and is not delivered");
            delivered.Select(d => d.State).Should().Equal([AmiConnectionState.Connected]);
            logger.Lines.Should().Contain(
                l => l.Level == LogLevel.Debug && l.Line.Contains("[AMI] Reconnected not delivered: session 2", StringComparison.Ordinal),
                "the stale Reconnected(2) is dropped at delivery with one Debug line naming its session");
            pendingAtThirdLogin.Should().BeTrue("a reconnect is pending from the third session's login");
            delivered.Select(d => d.PendingInside).Should().Equal([true],
                "it is still pending while the third session's Reconnected handlers run");
            pendingAfter.Should().BeFalse("it is cleared once the third session's Reconnected handlers have returned");
        }
    }

    /// <summary>Logs in every socket the connection creates, writes each to <paramref name="logins"/>, and answers its Pings.</summary>
    private static async Task ServeAndSignalEveryLoginAsync(PipedSocketFactory sockets, ChannelWriter<PipedSocket> logins,
        CancellationToken ct)
    {
        var peers = new List<Task>();
        try
        {
            while (true)
            {
                var peer = await sockets.NextAsync(ct);
                peers.Add(Task.Run(async () =>
                {
                    try
                    {
                        await peer.CompleteLoginAsync(ct);
                        logins.TryWrite(peer);
                        await peer.AnswerPingsAsync(ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // The test is over.
                    }
                    catch (InvalidOperationException)
                    {
                        // The connection closed this socket before the login finished.
                    }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The test is over.
        }

        await Task.WhenAll(peers);
    }

    /// <summary>Keeps every line at every level, and runs a callback on each, on the thread that logs it.</summary>
    private sealed class TailReadingLogger(Action<string> onLine) : ILogger<AmiConnection>
    {
        private readonly Lock _gate = new();
        private readonly List<(LogLevel Level, string Line)> _lines = [];

        public IReadOnlyList<(LogLevel Level, string Line)> Lines
        {
            get
            {
                lock (_gate)
                    return [.. _lines];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            lock (_gate)
                _lines.Add((logLevel, line));
            onLine(line);
        }
    }
}
