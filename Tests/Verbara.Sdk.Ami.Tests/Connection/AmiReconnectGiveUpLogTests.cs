using System.Collections.Concurrent;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using static Verbara.Sdk.Ami.Tests.Connection.AmiDispatchTestKit;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// A reconnect loop that gives up at <see cref="AmiConnectionOptions.MaxReconnectAttempts"/> writes exactly one Error
/// line saying the connection will not come back, with the last attempt's exception, before <c>[AMI] Disconnected</c>.
/// No line for a caller's ending recorded before the limit, and none for a loss without
/// <see cref="AmiConnectionOptions.AutoReconnect"/>. Before, the give-up wrote no line of its own: the last line was
/// <c>[AMI] Disconnected</c> at Information.
/// </summary>
/// <remarks>
/// A real <see cref="AmiConnection"/> over the in-memory <see cref="PipedSocket"/> harness, with a logger that keeps
/// every entry's level, line and exception in order. Every wait is bounded by <see cref="AmiDispatchTestKit.Bound"/> and
/// ends on the signal it asserts.
/// </remarks>
public sealed class AmiReconnectGiveUpLogTests : IAsyncLifetime, IDisposable
{
    private const string GaveUp = "Reconnect gave up after";

    private readonly CancellationTokenSource _peerCts = new(Bound * 3);
    private readonly ConcurrentBag<AmiConnection> _connections = [];
    private readonly ConcurrentBag<Task> _peers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
            await connection.DisposeAsync().AsTask().WaitAsync(Bound);

        await _peerCts.CancelAsync();
        await Task.WhenAll(_peers).WaitAsync(Bound);
    }

    // Called by the runner after DisposeAsync.
    public void Dispose() => _peerCts.Dispose();

    [Fact]
    public async Task GiveUp_ShouldWriteOneErrorBeforeDisconnected_WhenEveryReconnectLoginIsRejected()
    {
        var sockets = new PipedSocketFactory();
        // The first socket logs in; every reconnect attempt's login is rejected, as after a credentials change.
        Serve(sockets, rejectLogin: socket => socket > 1);
        var logger = new DispatchLogger();
        var connection = Track(Create(sockets, logger, o => Reconnecting(o, maxAttempts: 2, delay: TimeSpan.FromMilliseconds(20))));
        var final = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorsWhenFinal = -1;
        connection.StateChanged += change =>
        {
            if (!change.IsFinal)
                return;

            Volatile.Write(ref errorsWhenFinal, GiveUpLines(logger).Count);
            final.TrySetResult();
        };
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        sockets.Created[0].CloseFromPeer();
        var gaveUp = await CompletesWithinBoundAsync(final.Task);
        var drained = await CompletesWithinBoundAsync(connection.PendingNotifications);

        var entries = logger.Entries;
        var lines = GiveUpLines(logger);
        var giveUpIndex = IndexOf(entries, e => e.Level == LogLevel.Error && e.Line.Contains(GaveUp, StringComparison.Ordinal));
        var disconnectedIndex = IndexOf(entries, e => e.Line.Contains("[AMI] Disconnected", StringComparison.Ordinal));
        using (new AssertionScope())
        {
            gaveUp.Should().BeTrue("the reconnect loop gives up once every attempt it makes is rejected");
            drained.Should().BeTrue("every queued change is delivered");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
            lines.Should().ContainSingle("the give-up writes exactly one Error line");
            lines.Should().OnlyContain(e => e.Exception is AmiAuthenticationException,
                "the line carries the last attempt's exception: its login was rejected");
            lines.Should().OnlyContain(e => Equals(e.State.GetValueOrDefault("Attempts"), 2),
                "the line names the attempts made");
            lines.Should().OnlyContain(e => e.Line.Contains("will not come back", StringComparison.Ordinal));
            giveUpIndex.Should().BeGreaterThanOrEqualTo(0);
            giveUpIndex.Should().BeLessThan(disconnectedIndex, "the give-up line is written before [AMI] Disconnected");
            Volatile.Read(ref errorsWhenFinal).Should().Be(1,
                "the give-up line is written before the final change to Disconnected is delivered");
            entries.Count(e => e.Level == LogLevel.Warning && e.Line.Contains("[AMI] Reconnect attempt failed", StringComparison.Ordinal))
                .Should().Be(2, "each failed attempt still writes its Warning");
        }
    }

    [Fact]
    public async Task GiveUp_ShouldWriteNoLine_WhenTheCallerDisposesDuringTheFirstBackoff()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new DispatchLogger();
        // The backoff is a hang bound, never awaited: the dispose lands inside it.
        var connection = Create(factory, logger, o => Reconnecting(o, maxAttempts: 2, delay: TimeSpan.FromSeconds(30)));
        var socket = await ConnectAsync(connection, factory, peer);
        var reconnecting = logger.Logged("[AMI] Reconnecting");

        socket.CloseFromPeer();
        var inBackoff = await CompletesWithinBoundAsync(reconnecting);
        var disposed = await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask());

        using (new AssertionScope())
        {
            inBackoff.Should().BeTrue("the loop logs its first Reconnecting line before its backoff");
            disposed.Should().BeTrue("the caller's dispose ends the connection inside the backoff");
            GiveUpLines(logger).Should().BeEmpty("the caller ended the connection; the loop did not give up");
            logger.Containing("[AMI] Disconnected").Should().ContainSingle();
        }
    }

    /// <summary>
    /// A caller's ending racing the give-up, from both sides. <c>before</c>: issued on the loop's own last
    /// <c>[AMI] Reconnect attempt failed</c> line, so it is recorded before the loop reaches its limit — no give-up line.
    /// <c>during</c>: issued from inside the give-up's own release of the last socket, so the loop gave up first —
    /// exactly one give-up line.
    /// </summary>
    [Theory]
    [InlineData(nameof(AmiConnection.DisposeAsync), "before", 0)]
    [InlineData(nameof(AmiConnection.DisposeAsync), "during", 1)]
    [InlineData(nameof(AmiConnection.DisconnectAsync), "before", 0)]
    [InlineData(nameof(AmiConnection.DisconnectAsync), "during", 1)]
    public async Task GiveUp_ShouldWriteALineOnlyWhenTheLoopGaveUpFirst_WhenTheCallerEndsTheConnectionAsTheLoopGivesUp(
        string ending, string when, int expected)
    {
        const int maxReconnectAttempts = 2;
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new DispatchLogger { ActOn = "[AMI] Reconnect attempt failed", ActOccurrence = maxReconnectAttempts };
        var connection = Create(factory, logger,
            o => Reconnecting(o, maxReconnectAttempts, delay: TimeSpan.FromMilliseconds(5)));
        var issued = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        logger.Act = when == "before"
            ? () => issued.TrySetResult(EndAsync(connection, ending))
            // The last failed connect's socket is released by the give-up's own ending: issue the caller's there.
            : () => factory.Created[^1].DuringFirstDispose = () => issued.TrySetResult(EndAsync(connection, ending));
        var first = await ConnectAsync(connection, factory, peer);

        first.CloseFromPeer();
        var callerEnding = await ResultWithinBoundAsync(issued.Task);
        callerEnding.Should().NotBeNull("the caller's ending is issued at the give-up");
        var returned = await CompletesWithinBoundAsync(callerEnding!);

        using (new AssertionScope())
        {
            returned.Should().BeTrue($"{ending} returns when it lands on the give-up ({when})");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
            GiveUpLines(logger).Should().HaveCount(expected, when == "before"
                ? "a caller's ending recorded before the limit leaves no give-up line"
                : "the loop gave up before the caller's ending was issued");
            logger.Containing("[AMI] Disconnected").Should().ContainSingle("the two endings are one ending");
        }

        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue();
    }

    /// <summary>
    /// A loop whose backoff cannot be computed keeps its own single Error line and writes no give-up line: it did not
    /// reach its limit. The multiplier is changed after construction, through the object the connection holds, so the
    /// constructor's check never saw it.
    /// </summary>
    [Fact]
    public async Task BackoffFailure_ShouldKeepItsOwnErrorAndWriteNoGiveUpLine_WhenTheBackoffCannotBeComputed()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new DispatchLogger();
        var options = new AmiConnectionOptions();
        await using var connection = Create(factory, logger, o =>
        {
            Reconnecting(o, maxAttempts: 2, delay: TimeSpan.FromMilliseconds(20));
            options = o;
        });
        options.ReconnectMultiplier = 0.5;
        var socket = await ConnectAsync(connection, factory, peer);
        var disconnected = logger.Logged("[AMI] Disconnected");

        socket.CloseFromPeer();
        var ended = await CompletesWithinBoundAsync(disconnected);

        using (new AssertionScope())
        {
            ended.Should().BeTrue("a loop whose backoff cannot be computed ends the connection");
            logger.Containing("[AMI] Reconnect backoff failed").Should().ContainSingle("the backoff failure keeps its own line");
            GiveUpLines(logger).Should().BeEmpty("the loop did not give up at its limit: it never made an attempt");
        }
    }

    [Fact]
    public async Task Loss_ShouldWriteNoGiveUpLine_WhenAutoReconnectIsOff()
    {
        using var peer = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new DispatchLogger();
        await using var connection = Create(factory, logger);
        var socket = await ConnectAsync(connection, factory, peer);
        var disconnected = logger.Logged("[AMI] Disconnected");

        socket.CloseFromPeer();
        var ended = await CompletesWithinBoundAsync(disconnected);

        using (new AssertionScope())
        {
            ended.Should().BeTrue("the peer's close ends a connection without AutoReconnect");
            GiveUpLines(logger).Should().BeEmpty("a loss without AutoReconnect is not a reconnect that gave up");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static List<DispatchLogEntry> GiveUpLines(DispatchLogger logger) =>
        [.. logger.Entries.Where(e => e.Level == LogLevel.Error && e.Line.Contains(GaveUp, StringComparison.Ordinal))];

    private static int IndexOf(IReadOnlyList<DispatchLogEntry> entries, Func<DispatchLogEntry, bool> match)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (match(entries[i]))
                return i;
        }

        return -1;
    }

    private static void Reconnecting(AmiConnectionOptions options, int maxAttempts, TimeSpan delay)
    {
        options.AutoReconnect = true;
        options.MaxReconnectAttempts = maxAttempts;
        options.ReconnectInitialDelay = delay;
        options.ReconnectMaxDelay = delay;
        // Limits, never waits: nothing here runs until them.
        options.ConnectionTimeout = TimeSpan.FromMinutes(1);
        options.DefaultResponseTimeout = TimeSpan.FromMinutes(1);
    }

    private AmiConnection Track(AmiConnection connection)
    {
        _connections.Add(connection);
        return connection;
    }

    private static Task EndAsync(AmiConnection connection, string ending) => ending switch
    {
        nameof(AmiConnection.DisposeAsync) => connection.DisposeAsync().AsTask(),
        nameof(AmiConnection.DisconnectAsync) => connection.DisconnectAsync().AsTask(),
        _ => throw new ArgumentOutOfRangeException(nameof(ending), ending, "Not a caller's ending."),
    };

    /// <summary>
    /// Plays the Asterisk peer of every socket the connection creates: the n-th (from 1) rejects the login when
    /// <paramref name="rejectLogin"/> says so, else logs in and answers Pings.
    /// </summary>
    private void Serve(PipedSocketFactory sockets, Func<int, bool> rejectLogin)
    {
        var ct = _peerCts.Token;
        _peers.Add(Task.Run(async () =>
        {
            var served = new List<Task>();
            try
            {
                while (true)
                {
                    var peer = await sockets.NextAsync(ct);
                    var reject = rejectLogin(served.Count + 1);
                    served.Add(Task.Run(() => ServeAsync(peer, reject, ct), CancellationToken.None));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The test is over.
            }

            await Task.WhenAll(served);
        }, CancellationToken.None));
    }

    private static async Task ServeAsync(PipedSocket peer, bool rejectLogin, CancellationToken ct)
    {
        try
        {
            if (rejectLogin)
            {
                await peer.WriteAsync("Asterisk Call Manager/6.0.0\r\n");
                var challenge = await peer.ReadActionAsync(ct) ?? throw new InvalidOperationException("closed");
                await peer.RespondAsync("Success", PipedSocket.ActionIdOf(challenge), [new("Challenge", "abc123")]);
                var login = await peer.ReadActionAsync(ct) ?? throw new InvalidOperationException("closed");
                await peer.RespondAsync("Error", PipedSocket.ActionIdOf(login), [new("Message", "Authentication failed")]);
                return;
            }

            await peer.CompleteLoginAsync(ct);
            while (await peer.ReadActionAsync(ct) is { } action)
            {
                if (PipedSocket.IsPing(action))
                    await peer.RespondAsync("Success", PipedSocket.ActionIdOf(action));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The test is over.
        }
        catch (InvalidOperationException)
        {
            // The connection closed this socket before the login finished.
        }
    }
}
