using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// <see cref="AmiConnection.FullyBooted"/>: whether Asterisk has reported <c>FullyBooted</c> on the current AMI
/// session. It completes when the report arrives on that session, including while the connection is still logging in;
/// it is cancelled when the session ends, once the ending has chosen the connection's state; and every session,
/// including each reconnect and each connect attempt, has a task of its own.
/// </summary>
/// <remarks>
/// <para>
/// Asterisk sends <c>FullyBooted</c> when it has finished starting, and only to AMI users with <c>system</c> in their
/// read permissions. An Asterisk that has already started sends it right after the login's response, while the
/// connection is still reading its connect's responses.
/// </para>
/// <para>
/// Each peer is an in-memory <see cref="PipedSocket"/>, a fresh one per connect. Every wait is bounded by
/// <see cref="Bound"/> and ends on the signal it waits for. That a report has <b>not</b> arrived is read after a
/// <c>Ping</c> answered later on the same session: the connection reads a session's messages in order, so by then it
/// has read everything the peer sent before the answer.
/// </para>
/// </remarks>
public sealed class AmiConnectionFullyBootedTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A reconnect backoff that never runs out while a test watches: the reconnect loop waits in it until the test
    /// disposes the connection, which stops the loop.
    /// </summary>
    private static readonly TimeSpan LongBackoff = TimeSpan.FromMinutes(1);

    /// <summary>Where, in a session's connect, the peer sends <c>FullyBooted</c>.</summary>
    public enum ReportAt
    {
        /// <summary>Never: an AMI user without <c>system</c> in its read permissions.</summary>
        Never,

        /// <summary>Before the login's response, while the connection waits for it.</summary>
        BeforeLoginResponse,

        /// <summary>Right after the login's response and before the version probe's: the order Asterisk uses.</summary>
        AfterLoginResponse,

        /// <summary>Once the connect has been answered, when the connection's reader loop is running.</summary>
        AfterConnect,
    }

    [Fact]
    public async Task FullyBooted_ShouldComplete_WhenAsteriskReportsItOnceTheSessionIsConnected()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect: false, LongBackoff);

        await ConnectAsync(connection, factory, ReportAt.AfterConnect, peerCts);
        var completed = await CompletesWithinBoundAsync(connection.FullyBooted);

        using (new AssertionScope())
        {
            completed.Should().BeTrue("Asterisk reported FullyBooted on the session");
            connection.FullyBooted.IsCompletedSuccessfully.Should().BeTrue("a report completes the task, it does not fail it");
        }
    }

    /// <summary>
    /// The report arrives while the connection is still reading its connect's own responses, which is where an
    /// Asterisk that has already started sends it. Read then, or never: the connection's reader loop starts only after
    /// the connect, so a report the connect skips is lost.
    /// </summary>
    [Theory]
    [InlineData(ReportAt.BeforeLoginResponse)]
    [InlineData(ReportAt.AfterLoginResponse)]
    public async Task FullyBooted_ShouldBeCompleteWhenTheConnectReturns_WhenAsteriskReportsItDuringTheLogin(ReportAt reportAt)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect: false, LongBackoff);

        await ConnectAsync(connection, factory, reportAt, peerCts);
        var completedAtConnect = connection.FullyBooted.IsCompletedSuccessfully;

        using (new AssertionScope())
        {
            completedAtConnect.Should().BeTrue(
                "the connect read Asterisk's report before it returned, so the session already counts as booted");
            connection.State.Should().Be(AmiConnectionState.Connected);
        }
    }

    [Fact]
    public async Task FullyBooted_ShouldStayIncomplete_WhenTheUserNeverReceivesTheReport()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect: false, LongBackoff);

        var peer = await ConnectAsync(connection, factory, ReportAt.Never, peerCts);
        (await peer.WriteEventAsync("PeerStatus", [new("Peer", "PJSIP/1001"), new("PeerStatus", "Reachable")]))
            .Should().BeTrue("the peer sends an event that is not the report");
        await PingAsync(connection, peer, peerCts);

        using (new AssertionScope())
        {
            connection.FullyBooted.IsCompleted.Should().BeFalse(
                "a user without system never receives the report, and nothing else completes the task");
            connection.State.Should().Be(AmiConnectionState.Connected);
        }
    }

    /// <summary>
    /// The cancellation is read by a continuation that runs inline, on the thread that cancels the task, so what it
    /// reads is the state at the very moment of the cancellation. That must be the state the ending chose: a load woken
    /// by the end of its session decides from it whether the connection will come back, and must never read the
    /// <c>Connected</c> of the session that just ended.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FullyBooted_ShouldBeCancelledOnceTheEndingHasChosenTheState_WhenTheSessionEnds(bool autoReconnect)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect, LongBackoff);
        var peer = await ConnectAsync(connection, factory, ReportAt.Never, peerCts);
        var fullyBooted = connection.FullyBooted;
        var seen = fullyBooted.ContinueWith(
            task => new Seen(task.Status, connection.State),
            CancellationToken.None, TaskContinuationOptions.None, InlineScheduler.Instance);

        peer.CloseFromPeer();
        var atCancellation = await ResultWithinBoundAsync(seen);

        using (new AssertionScope())
        {
            atCancellation.Should().NotBeNull("the end of the session ends the task");
            atCancellation!.Status.Should().Be(TaskStatus.Canceled,
                "a session that ended will never report FullyBooted, and the end is not a report");
            atCancellation!.State.Should().Be(
                autoReconnect ? AmiConnectionState.Reconnecting : AmiConnectionState.Disconnecting,
                "the ending writes the state it chose before it cancels the session's task");
        }
    }

    [Fact]
    public async Task FullyBooted_ShouldBeANewIncompleteTask_WhenTheConnectionReconnects()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        await using var connection = Create(factory, autoReconnect: true, TimeSpan.FromMilliseconds(1));
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () => reconnected.TrySetResult();
        var first = await ConnectAsync(connection, factory, ReportAt.AfterConnect, peerCts);
        var firstReport = connection.FullyBooted;
        var firstCompleted = await CompletesWithinBoundAsync(firstReport);

        var serving = PlayNextConnectAsync(factory, ReportAt.Never, peerCts);
        first.CloseFromPeer();
        var reconnectedInBound = await CompletesWithinBoundAsync(reconnected.Task);
        var second = await serving.WaitAsync(Bound);
        await PingAsync(connection, second, peerCts);
        var secondReport = connection.FullyBooted;
        var secondCompleteBeforeItsReport = secondReport.IsCompleted;
        (await WriteFullyBootedAsync(second)).Should().BeTrue("Asterisk reports FullyBooted on the second session");
        var secondCompleted = await CompletesWithinBoundAsync(secondReport);

        using (new AssertionScope())
        {
            firstCompleted.Should().BeTrue("Asterisk reported FullyBooted on the first session");
            reconnectedInBound.Should().BeTrue("the connection reconnected to the second session");
            secondReport.Should().NotBeSameAs(firstReport, "each session has a task of its own");
            secondCompleteBeforeItsReport.Should().BeFalse(
                "a report on an earlier session does not count for this one, which has not reported yet");
            secondCompleted.Should().BeTrue("the second session's own report completes its task");
        }
    }

    /// <summary>
    /// A connect attempt that logs in and fails leaves no reader loop behind to end its session's task, so the attempt
    /// ends it itself: nothing may wait on a task that nothing will ever complete or cancel. The reconnect loop's
    /// attempt is followed by a backoff of a minute, so the next attempt cannot replace the task while the test reads it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullyBooted_ShouldBeCancelled_WhenAConnectAttemptFailsAtTheLogin(bool byTheReconnectLoop)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new SignalingLogger<AmiConnection>();
        await using var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = byTheReconnectLoop,
            // The first backoff is a millisecond; the second is a minute.
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(1),
            ReconnectMultiplier = 60_000,
            ReconnectMaxDelay = LongBackoff,
            ConnectionTimeout = TimeSpan.FromMinutes(1),
            DefaultResponseTimeout = TimeSpan.FromMinutes(1),
            DefaultEventTimeout = TimeSpan.Zero,
        }), factory, logger);

        if (byTheReconnectLoop)
        {
            var first = await ConnectAsync(connection, factory, ReportAt.Never, peerCts);
            var rejecting = RejectNextLoginAsync(factory, peerCts);
            var attemptFailed = logger.Logged("[AMI] Reconnect attempt failed");
            first.CloseFromPeer();
            await rejecting.WaitAsync(Bound);
            (await CompletesWithinBoundAsync(attemptFailed)).Should().BeTrue("the reconnect attempt fails at the login");
        }
        else
        {
            var rejecting = RejectNextLoginAsync(factory, peerCts);
            var connect = async () => await connection.ConnectAsync().AsTask().WaitAsync(Bound);
            await connect.Should().ThrowAsync<AmiAuthenticationException>("the peer rejects the login");
            await rejecting.WaitAsync(Bound);
        }

        var ended = await ResultWithinBoundAsync(connection.FullyBooted.ContinueWith(
            task => new Seen(task.Status, connection.State),
            CancellationToken.None, TaskContinuationOptions.None, InlineScheduler.Instance));

        using (new AssertionScope())
        {
            ended.Should().NotBeNull("the failed attempt ended its session's task instead of leaving it pending");
            ended!.Status.Should().Be(TaskStatus.Canceled, "a session that never came up never reported FullyBooted");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static AmiConnection Create(PipedSocketFactory factory, bool autoReconnect, TimeSpan backoff) =>
        new(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = autoReconnect,
            ReconnectInitialDelay = backoff,
            ReconnectMaxDelay = backoff,
            // Limits, never waits: a reconnect attempt the test holds must not give up while it holds it.
            ConnectionTimeout = TimeSpan.FromMinutes(1),
            DefaultResponseTimeout = TimeSpan.FromMinutes(1),
            DefaultEventTimeout = TimeSpan.Zero,
        }), factory, NullLogger<AmiConnection>.Instance);

    /// <summary>Connects, with the next socket's peer playing the connect and reporting at <paramref name="reportAt"/>.</summary>
    private static async Task<PipedSocket> ConnectAsync(AmiConnection connection, PipedSocketFactory factory,
        ReportAt reportAt, CancellationTokenSource peerCts)
    {
        var played = PlayNextConnectAsync(factory, reportAt, peerCts);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        return await played.WaitAsync(Bound);
    }

    /// <summary>
    /// Plays the connect of the next socket the connection creates: the banner, the MD5 challenge, the login and the
    /// version probe, with <c>FullyBooted</c> at <paramref name="reportAt"/>. Returns that socket.
    /// </summary>
    private static Task<PipedSocket> PlayNextConnectAsync(PipedSocketFactory factory, ReportAt reportAt,
        CancellationTokenSource peerCts) =>
        Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.WriteAsync("Asterisk Call Manager/6.0.0\r\n");

            var challenge = await peer.ReadActionAsync(peerCts.Token) ?? throw ClosedDuringConnect();
            await peer.RespondAsync("Success", PipedSocket.ActionIdOf(challenge), [new("Challenge", "840415273")]);

            var login = await peer.ReadActionAsync(peerCts.Token) ?? throw ClosedDuringConnect();
            if (reportAt == ReportAt.BeforeLoginResponse)
                await WriteFullyBootedAsync(peer);

            await peer.RespondAsync("Success", PipedSocket.ActionIdOf(login), [new("Message", "Authentication accepted")]);
            if (reportAt == ReportAt.AfterLoginResponse)
                await WriteFullyBootedAsync(peer);

            var versionProbe = await peer.ReadActionAsync(peerCts.Token) ?? throw ClosedDuringConnect();
            await peer.RespondAsync("Success", PipedSocket.ActionIdOf(versionProbe),
                [new("AMIversion", "9.0.0"), new("AsteriskVersion", "22.9.0")]);
            if (reportAt == ReportAt.AfterConnect)
                await WriteFullyBootedAsync(peer);

            return peer;
        }, peerCts.Token);

    /// <summary>The next socket's peer answers the challenge and rejects the login.</summary>
    private static Task RejectNextLoginAsync(PipedSocketFactory factory, CancellationTokenSource peerCts) =>
        Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.WriteAsync("Asterisk Call Manager/6.0.0\r\n");
            var challenge = await peer.ReadActionAsync(peerCts.Token) ?? throw ClosedDuringConnect();
            await peer.RespondAsync("Success", PipedSocket.ActionIdOf(challenge), [new("Challenge", "840415273")]);
            var login = await peer.ReadActionAsync(peerCts.Token) ?? throw ClosedDuringConnect();
            await peer.RespondAsync("Error", PipedSocket.ActionIdOf(login), [new("Message", "Authentication failed")]);
        }, peerCts.Token);

    /// <summary>
    /// A <c>Ping</c> answered by <paramref name="peer"/>. Once it returns, the connection has read everything the peer
    /// sent on this session before the answer.
    /// </summary>
    private static async Task PingAsync(AmiConnection connection, PipedSocket peer, CancellationTokenSource peerCts)
    {
        var ping = connection.SendActionAsync(new PingAction()).AsTask();
        var action = await peer.ReadActionAsync(peerCts.Token).WaitAsync(Bound)
            ?? throw new InvalidOperationException("The session ended before the connection sent its Ping.");
        (await peer.RespondAsync("Success", PipedSocket.ActionIdOf(action), [new("Ping", "Pong")]))
            .Should().BeTrue("the peer answers the Ping");
        await ping.WaitAsync(Bound);
    }

    private static Task<bool> WriteFullyBootedAsync(PipedSocket peer) =>
        peer.WriteEventAsync("FullyBooted",
            [new("Privilege", "system,all"), new("Uptime", "0"), new("LastReload", "0"), new("Status", "Fully Booted")]);

    private static InvalidOperationException ClosedDuringConnect() =>
        new("The connection closed the socket before the connect finished.");

    private static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Bound);
            return true;
        }
        catch (TimeoutException)
        {
            // Reaching the hang bound is the failure the caller asserts on, not an observation.
            return false;
        }
    }

    private static async Task<T?> ResultWithinBoundAsync<T>(Task<T> task) where T : class
    {
        try
        {
            return await task.WaitAsync(Bound);
        }
        catch (TimeoutException)
        {
            // Reaching the hang bound is the failure the caller asserts on, not an observation.
            return null;
        }
    }

    /// <summary>What a continuation of the session's task saw when it ran: how the task ended, and the state.</summary>
    private sealed record Seen(TaskStatus Status, AmiConnectionState State);

    /// <summary>
    /// Runs every task it is given at once, on the thread that queues it. A continuation scheduled on it runs inside
    /// the call that completes or cancels its antecedent, even when that antecedent runs its continuations
    /// asynchronously: such a task still hands each continuation to its scheduler, synchronously, and this scheduler
    /// runs it there and then.
    /// </summary>
    private sealed class InlineScheduler : TaskScheduler
    {
        public static readonly InlineScheduler Instance = new();

        protected override void QueueTask(Task task) => TryExecuteTask(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => TryExecuteTask(task);

        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }
}
