using System.Reflection;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// What ends the reconnect loop, and what it leaves behind. When it gives up at
/// <see cref="AmiConnectionOptions.MaxReconnectAttempts"/>, every socket the connection created is released
/// exactly once, the last one included, and <see cref="AmiConnectionState.Disconnected"/> is reported only
/// after that release. When the caller ends the connection with <c>DisconnectAsync</c> or
/// <c>DisposeAsync</c>, in the backoff delay or while a connect attempt is in flight, the loop stops: the
/// connection dials no more, logs in no more, never raises <c>Reconnected</c>, and stays
/// <see cref="AmiConnectionState.Disconnected"/>.
/// </summary>
/// <remarks>
/// <para>
/// How many connects the loop makes is ruling C3 of the 2026-09-26 decision audit (ADR-0008 addendum):
/// <c>MaxReconnectAttempts = N</c> makes N reconnect connects, every <c>[AMI] Reconnecting</c> line is followed by a
/// connect, and the give-up comes right after the last failed attempt, with no further backoff.
/// <see cref="ReconnectLoop_ShouldMakeExactlyNConnects_WhenEveryReconnectIsRefused"/> counts them on the client side,
/// from the sockets the factory created; the other tests read those sockets instead of predicting how many there are.
/// </para>
/// <para>
/// Each peer is an in-memory <see cref="PipedSocket"/>, a fresh one per connect. The first accepts its
/// connect and logs in. A later one refuses its connect, as an Asterisk that is down does, or logs in, as one
/// that came back does. Every wait is bounded by <see cref="Bound"/> and ends on the signal it asserts; none of
/// them sleeps. The one exception is <see cref="DialWindow"/>, an observation window for an absence (the loop
/// dials no more), paired with its positive control: a loop that nothing ended dials inside the same window.
/// </para>
/// </remarks>
public sealed class AmiReconnectLoopTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The backoff of the tests whose caller ends the connection. The loop writes its <c>[AMI] Reconnecting</c>
    /// line right before this delay, so an ending issued on that line lands inside it.
    /// </summary>
    private static readonly TimeSpan Backoff = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How long a test watches for a dial once the caller's ending has returned: five backoff delays. It is
    /// the observation, not a hang bound, and
    /// <see cref="ReconnectLoop_ShouldDialWithinTheObservationWindow_WhenNothingEndsTheConnection"/> is its
    /// positive control.
    /// </summary>
    private static readonly TimeSpan DialWindow = Backoff * 5;

    [Theory]
    // Gives up after its one refused connect: what is left is the socket that connect created.
    [InlineData(1)]
    // Gives up after three refused connects: what is left is the last socket it created.
    [InlineData(3)]
    public async Task ReconnectLoop_ShouldDisposeEverySocket_WhenItGivesUpAtMaxReconnectAttempts(int maxReconnectAttempts)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new SignalingLogger<AmiConnection>();
        var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            // Only the peer's close ends the connection.
            EnableHeartbeat = false,
            AutoReconnect = true,
            MaxReconnectAttempts = maxReconnectAttempts,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(10),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(10),
        }), factory, logger);
        // Nothing else ends this connection, so the line is the loop giving up; it is written once the
        // loop has released what it held.
        var gaveUp = logger.Logged("[AMI] Disconnected");

        var loggedIn = Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.CompleteLoginAsync(peerCts.Token);
            return peer;
        }, peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        var firstPeer = await loggedIn.WaitAsync(Bound);

        firstPeer.CloseFromPeer();
        var ended = await CompletesWithinBoundAsync(gaveUp);

        var sockets = factory.Created;
        using (new AssertionScope())
        {
            Unreleased(sockets).Should().BeEmpty(
                $"the connection created {sockets.Count} socket(s) and must release every one exactly once, the last included, " +
                $"when the reconnect loop gives up at MaxReconnectAttempts = {maxReconnectAttempts}");
            ended.Should().BeTrue("the loop gives up and reports Disconnected once it has released what it held");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
        }

        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue(
            "a DisposeAsync after the give-up completes");
        Unreleased(sockets).Should().BeEmpty(
            "a DisposeAsync that finds the connection already Disconnected disposes no socket a second time");
    }

    [Theory]
    [InlineData(nameof(AmiConnection.DisposeAsync))]
    [InlineData(nameof(AmiConnection.DisconnectAsync))]
    public async Task Ending_ShouldStopTheReconnectLoop_WhenCalledDuringTheBackoff(string ending)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        // Only the first socket accepts its connect: every reconnect is refused, as by an Asterisk that is down.
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new SignalingLogger<AmiConnection>();
        var connection = CreateReconnecting(factory, logger);
        var first = await ConnectAsync(connection, factory, peerCts);
        (await LoseAsync(first, logger.Logged)).Should().BeTrue("the peer's close starts the reconnect loop");

        var returned = await CompletesWithinBoundAsync(EndAsync(connection, ending));
        var stateAtReturn = connection.State;
        var socketsAtReturn = factory.Created.Count;
        var dialled = await factory.NextWithinAsync(DialWindow);

        var sockets = factory.Created;
        using (new AssertionScope())
        {
            returned.Should().BeTrue($"{ending} returns");
            stateAtReturn.Should().Be(AmiConnectionState.Disconnected, $"{ending} has ended the connection when it returns");
            dialled.Should().BeNull(
                $"{ending} stops the reconnect loop, so it dials no more in {DialWindow.TotalMilliseconds} ms, five of its backoff delays");
            sockets.Should().HaveCount(socketsAtReturn, $"the connection creates no socket after {ending} returns");
            Unreleased(sockets).Should().BeEmpty(
                $"every socket the connection created is released exactly once when {ending} ends it during a reconnect");
            connection.State.Should().Be(AmiConnectionState.Disconnected, "nothing the loop does afterwards overrides the caller's ending");
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldNotLogInAgain_WhenAsteriskComesBackAfterTheDispose()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new SignalingLogger<AmiConnection>();
        var connection = CreateReconnecting(factory, logger);
        var reconnects = new Counter();
        connection.Reconnected += reconnects.Increment;
        var first = await ConnectAsync(connection, factory, peerCts);
        // Asterisk is back: the peer of every socket created from here on completes the login.
        var logins = new Counter();
        var peers = LogInEveryNewSocketAsync(factory, logins, peerCts.Token);
        (await LoseAsync(first, logger.Logged)).Should().BeTrue("the peer's close starts the reconnect loop");

        var returned = await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask());
        var socketsAtReturn = factory.Created.Count;
        var dialled = await factory.NextWithinAsync(DialWindow);

        using (new AssertionScope())
        {
            returned.Should().BeTrue("DisposeAsync returns");
            dialled.Should().BeNull(
                $"DisposeAsync stops the reconnect loop, so it dials no more in {DialWindow.TotalMilliseconds} ms, although Asterisk is back");
            factory.Created.Should().HaveCount(socketsAtReturn, "the connection creates no socket after DisposeAsync returns");
            logins.Count.Should().Be(0, "a disposed connection sends no login");
            reconnects.Count.Should().Be(0, "a disposed connection never raises Reconnected");
            connection.State.Should().Be(AmiConnectionState.Disconnected, "a disposed connection stays Disconnected");
        }

        await peerCts.CancelAsync();
        (await CompletesWithinBoundAsync(peers)).Should().BeTrue("the peers stop with the test");
    }

    /// <summary>
    /// The ending lands between the loop's backoff delay and its connect. It is issued from inside the lost
    /// socket's first disposal, which the loop makes itself as it releases what the lost connection held
    /// before it dials again, so the order is fixed by construction: the delay is over, the caller's ending is
    /// recorded, and only then could the loop dial. It must not: no socket is created once the ending has been
    /// issued, not only once it has returned.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldCreateNoSocket_WhenItLandsBetweenTheBackoffAndTheConnect()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        // Only the first socket accepts its connect: every reconnect is refused, as by an Asterisk that is down.
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new SignalingLogger<AmiConnection>();
        var connection = CreateReconnecting(factory, logger);
        var first = await ConnectAsync(connection, factory, peerCts);
        var issued = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.DuringFirstDispose = () => issued.TrySetResult(connection.DisposeAsync().AsTask());

        first.CloseFromPeer();
        var dispose = await ResultWithinBoundAsync(issued.Task);
        dispose.Should().NotBeNull("the loop releases the lost socket once its backoff is over, and that release issues DisposeAsync");
        var returned = await CompletesWithinBoundAsync(dispose!);
        var socketsAtReturn = factory.Created.Count;
        var dialled = await factory.NextWithinAsync(DialWindow);

        var sockets = factory.Created;
        using (new AssertionScope())
        {
            returned.Should().BeTrue("DisposeAsync returns");
            socketsAtReturn.Should().Be(1,
                "the loop dials no socket once the caller's ending has been issued between its backoff and its connect");
            dialled.Should().BeNull($"DisposeAsync stops the reconnect loop, so it dials no more in {DialWindow.TotalMilliseconds} ms");
            Unreleased(sockets).Should().BeEmpty("the lost socket is released exactly once");
            connection.State.Should().Be(AmiConnectionState.Disconnected, "a disposed connection stays Disconnected");
        }
    }

    /// <summary>
    /// The ending lands while the loop's connect attempt waits for the banner its peer withholds. The
    /// attempt's socket reports the state at the moment the ending releases it, which is where a failed
    /// attempt that wrote <see cref="AmiConnectionState.Reconnecting"/> over the caller's ending would show.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldStopTheReconnectLoop_WhenAConnectAttemptIsInFlight()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new SignalingLogger<AmiConnection>();
        var connection = CreateReconnecting(factory, logger);
        var reconnects = new Counter();
        connection.Reconnected += reconnects.Increment;
        var first = await ConnectAsync(connection, factory, peerCts);

        first.CloseFromPeer();
        // The loop's attempt. Its peer withholds the banner, so the attempt stays in flight until something
        // ends it.
        var attempt = await factory.NextAsync(peerCts.Token).AsTask().WaitAsync(Bound);
        AmiConnectionState? stateAtRelease = null;
        attempt.DuringFirstDispose = () => stateAtRelease = connection.State;

        var returned = await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask());
        var socketsAtReturn = factory.Created.Count;
        // Asterisk is back from here on: the peer of every new socket completes the login.
        var logins = new Counter();
        var peers = LogInEveryNewSocketAsync(factory, logins, peerCts.Token);
        var dialled = await factory.NextWithinAsync(DialWindow);

        using (new AssertionScope())
        {
            returned.Should().BeTrue("DisposeAsync returns while the loop's connect attempt is in flight");
            attempt.DisposeCount.Should().Be(1, "the in-flight attempt's socket is released exactly once");
            stateAtRelease.Should().Be(AmiConnectionState.Disconnecting,
                "the caller's ending owns the state while it releases the attempt's socket: the attempt it cut short must not write Reconnecting over it");
            dialled.Should().BeNull(
                $"DisposeAsync stops the reconnect loop, so it dials no more in {DialWindow.TotalMilliseconds} ms, although Asterisk is back");
            factory.Created.Should().HaveCount(socketsAtReturn, "the connection creates no socket after DisposeAsync returns");
            logins.Count.Should().Be(0, "a disposed connection sends no login");
            reconnects.Count.Should().Be(0, "a disposed connection never raises Reconnected");
            connection.State.Should().Be(AmiConnectionState.Disconnected, "a disposed connection stays Disconnected");
        }

        await peerCts.CancelAsync();
        (await CompletesWithinBoundAsync(peers)).Should().BeTrue("the peers stop with the test");
    }

    /// <summary>
    /// The ending lands after the loop's attempt has logged in and before the loop reports the reconnect. It
    /// is issued from inside the attempt's own <c>[AMI] Connected</c> line, which the attempt writes on the
    /// loop's task as its last step, so the order is fixed by construction: the login succeeded, the caller's
    /// ending was recorded, and only then could the loop raise <c>Reconnected</c>.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldNotRaiseReconnected_WhenItLandsAfterTheAttemptLoggedIn()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        // The first "[AMI] Connected" line is the test's own connect; the second is the loop's attempt.
        var logger = new ActingLogger("[AMI] Connected", occurrence: 2);
        var connection = CreateReconnecting(factory, logger);
        var reconnects = new Counter();
        connection.Reconnected += reconnects.Increment;
        var issued = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        logger.Act = () => issued.TrySetResult(connection.DisposeAsync().AsTask());
        var first = await ConnectAsync(connection, factory, peerCts);
        // Asterisk is back: the peer of every socket created from here on completes the login.
        var logins = new Counter();
        var peers = LogInEveryNewSocketAsync(factory, logins, peerCts.Token);
        (await LoseAsync(first, logger.Logged)).Should().BeTrue("the peer's close starts the reconnect loop");

        var dispose = await ResultWithinBoundAsync(issued.Task);
        dispose.Should().NotBeNull("the loop's attempt logs in, and its [AMI] Connected line issues DisposeAsync");
        var returned = await CompletesWithinBoundAsync(dispose!);
        var socketsAtReturn = factory.Created.Count;
        var dialled = await factory.NextWithinAsync(DialWindow);

        var sockets = factory.Created;
        using (new AssertionScope())
        {
            returned.Should().BeTrue("DisposeAsync returns");
            reconnects.Count.Should().Be(0,
                "the caller's ending was recorded before the loop reported the reconnect, so the loop never raises Reconnected");
            dialled.Should().BeNull($"DisposeAsync stops the reconnect loop, so it dials no more in {DialWindow.TotalMilliseconds} ms");
            sockets.Should().HaveCount(socketsAtReturn, "the connection creates no socket after DisposeAsync returns");
            Unreleased(sockets).Should().BeEmpty(
                "every socket the connection created is released exactly once, the one the attempt logged in on included");
            connection.State.Should().Be(AmiConnectionState.Disconnected, "a disposed connection stays Disconnected");
        }

        await peerCts.CancelAsync();
        (await CompletesWithinBoundAsync(peers)).Should().BeTrue("the peers stop with the test");
    }

    /// <summary>
    /// The positive control of <see cref="DialWindow"/>, green before and after the loop learned to stop: the
    /// same connection, with nothing ending it, dials inside the window. A socket absent from the window in
    /// the tests above therefore means the loop stopped, not that the window was too short to see a dial.
    /// </summary>
    [Fact]
    public async Task ReconnectLoop_ShouldDialWithinTheObservationWindow_WhenNothingEndsTheConnection()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new SignalingLogger<AmiConnection>();
        var connection = CreateReconnecting(factory, logger);
        var first = await ConnectAsync(connection, factory, peerCts);
        (await LoseAsync(first, logger.Logged)).Should().BeTrue("the peer's close starts the reconnect loop");

        var dialled = await factory.NextWithinAsync(DialWindow);

        dialled.Should().NotBeNull(
            $"with nothing ending the connection, the loop dials within {DialWindow.TotalMilliseconds} ms of starting its backoff");
        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue();
    }

    /// <summary>
    /// A pin, green before and after the loop learned to stop: a connection its caller ended during a
    /// reconnect refuses a connect, as one its caller ended while connected does. It holds the guard at
    /// <c>ConnectAsync</c>'s entry, which the loop no longer passes through.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_ShouldThrowObjectDisposed_WhenTheCallerEndedTheConnectionDuringAReconnect()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new SignalingLogger<AmiConnection>();
        var connection = CreateReconnecting(factory, logger);
        var first = await ConnectAsync(connection, factory, peerCts);
        (await LoseAsync(first, logger.Logged)).Should().BeTrue("the peer's close starts the reconnect loop");
        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue("DisposeAsync returns");
        var socketsBeforeConnect = factory.Created.Count;

        var connect = async () => await connection.ConnectAsync().AsTask().WaitAsync(Bound);

        await connect.Should().ThrowAsync<ObjectDisposedException>(
            "a connection its caller ended during a reconnect stays ended, as one ended while connected does");
        factory.Created.Should().HaveCount(socketsBeforeConnect, "the refused connect creates no socket");
    }

    /// <summary>
    /// The loop's backoff cannot be computed: the options were valid when the connection was built, and were changed
    /// afterwards, through the object the connection holds, to a multiplier below 1. The loop must not die where nobody
    /// sees it. It writes one <c>ReconnectBackoffFailed</c> entry with the failure, makes no connect, releases what the
    /// lost connection held and ends <see cref="AmiConnectionState.Disconnected"/>. The entry is matched by its event
    /// name, never by counting <c>Error</c> entries: the loss writes its own.
    /// </summary>
    [Fact]
    public async Task ReconnectLoop_ShouldEndDisconnectedWithOneBackoffError_WhenTheBackoffCannotBeComputed()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new RecordingLogger();
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            ReconnectInitialDelay = Backoff,
            ReconnectMaxDelay = Backoff,
        };
        var connection = new AmiConnection(Options.Create(options), factory, logger);
        // Changed after construction, so the constructor's check never saw it.
        options.ReconnectMultiplier = 0.5;
        var first = await ConnectAsync(connection, factory, peerCts);
        var backoffFailedBeforeTheLoss = logger.Named("ReconnectBackoffFailed").Count;
        var disconnected = logger.Logged("[AMI] Disconnected");

        first.CloseFromPeer();
        var ended = await CompletesWithinBoundAsync(disconnected);

        var sockets = factory.Created;
        var backoffFailed = logger.Named("ReconnectBackoffFailed");
        using (new AssertionScope())
        {
            ended.Should().BeTrue("a loop whose backoff cannot be computed ends the connection instead of staying Reconnecting");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
            backoffFailedBeforeTheLoss.Should().Be(0, "nothing failed before the loss");
            backoffFailed.Should().ContainSingle("the failure is logged exactly once, not once per iteration");
            if (backoffFailed.Count == 1)
            {
                backoffFailed[0].Level.Should().Be(LogLevel.Error);
                backoffFailed[0].Exception.Should().BeOfType<ArgumentOutOfRangeException>("the entry carries what the backoff threw");
            }

            sockets.Should().HaveCount(1, "the failure is not a failed connect attempt: the loop makes no connect");
            Unreleased(sockets).Should().BeEmpty("the lost socket is released exactly once when the loop ends");
        }

        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue();
    }

    /// <summary>
    /// The options were valid when the connection was built, and <see cref="AmiConnectionOptions.ConnectionTimeout"/> was
    /// changed afterwards, through the object the connection holds, to a negative value, which cannot bound a connect.
    /// The loop must not count that as a failed attempt and retry it forever (with <c>MaxReconnectAttempts = 0</c> it
    /// would never end): it ends the connection once, as a backoff that cannot be computed does, with one
    /// <c>ReconnectBackoffFailed</c> entry whose exception names the option, no connect, the lost socket released, and a
    /// final <see cref="AmiConnectionState.Disconnected"/> that carries that exception.
    /// </summary>
    [Fact]
    public async Task ReconnectLoop_ShouldEndOnceCarryingTheRejection_WhenTheConnectionTimeoutIsMadeUnusableAfterConstruction()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new RecordingLogger();
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            MaxReconnectAttempts = 0,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(5),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(5),
        };
        var connection = new AmiConnection(Options.Create(options), factory, logger);
        var changes = new List<AmiConnectionStateChange>();
        var changesGate = new Lock();
        connection.StateChanged += change =>
        {
            lock (changesGate)
                changes.Add(change);
        };
        var first = await ConnectAsync(connection, factory, peerCts);
        // Changed after construction, so the constructor's check never saw it.
        options.ConnectionTimeout = TimeSpan.FromSeconds(-5);
        var disconnected = logger.Logged("[AMI] Disconnected");

        first.CloseFromPeer();
        var ended = await CompletesWithinBoundAsync(disconnected);

        var sockets = factory.Created;
        var backoffFailed = logger.Named("ReconnectBackoffFailed");
        var attemptsFailed = logger.Named("ReconnectAttemptFailed");
        AmiConnectionStateChange? last;
        lock (changesGate)
            last = changes.LastOrDefault();
        using (new AssertionScope())
        {
            ended.Should().BeTrue("a loop whose ConnectionTimeout cannot bound a connect ends the connection instead of retrying it");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
            attemptsFailed.Should().BeEmpty("an unusable option is not a failed attempt to retry");
            backoffFailed.Should().ContainSingle("the loop ends once, loudly");
            if (backoffFailed.Count == 1)
            {
                backoffFailed[0].Exception.Should().BeOfType<ArgumentOutOfRangeException>()
                    .Which.ParamName.Should().Be(nameof(AmiConnectionOptions.ConnectionTimeout), "the entry names the option to fix");
            }

            last.Should().NotBeNull();
            last?.Current.Should().Be(AmiConnectionState.Disconnected);
            last?.Cause.Should().BeOfType<ArgumentOutOfRangeException>("the give-up carries the rejection")
                .Which.ParamName.Should().Be(nameof(AmiConnectionOptions.ConnectionTimeout));
            sockets.Should().HaveCount(1, "the loop makes no connect with an option that cannot bound it");
            Unreleased(sockets).Should().BeEmpty("the lost socket is released exactly once when the loop ends");
        }

        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue();
    }

    /// <summary>
    /// <c>MaxReconnectAttempts = N</c> makes exactly N reconnect connects when every one is refused, then gives up
    /// <see cref="AmiConnectionState.Disconnected"/>. Counted on the client side: the factory's sockets, read after the
    /// loop's own task has completed, so a connect the loop made after the give-up would be counted too.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task ReconnectLoop_ShouldMakeExactlyNConnects_WhenEveryReconnectIsRefused(int maxReconnectAttempts)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        // Only the first socket accepts its connect: every reconnect is refused, as by an Asterisk that is down.
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new SignalingLogger<AmiConnection>();
        var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            MaxReconnectAttempts = maxReconnectAttempts,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(5),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(5),
        }), factory, logger);
        var first = await ConnectAsync(connection, factory, peerCts);
        // Nothing else ends this connection, so the line is the loop giving up.
        var gaveUp = logger.Logged("[AMI] Disconnected");

        first.CloseFromPeer();
        var ended = await CompletesWithinBoundAsync(gaveUp);
        var loop = ReconnectLoopOf(connection);
        var loopCompleted = loop is not null && await CompletesWithinBoundAsync(loop);

        var reconnectConnects = factory.Created.Count - 1;
        using (new AssertionScope())
        {
            ended.Should().BeTrue("the loop gives up once its attempts are used");
            loopCompleted.Should().BeTrue("the loop's task completes at the give-up");
            reconnectConnects.Should().Be(maxReconnectAttempts,
                $"MaxReconnectAttempts = {maxReconnectAttempts} makes exactly that many reconnect connects, and none after the give-up");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
            logger.Entries.Count(e => e.Line.Contains("[AMI] Reconnecting", StringComparison.Ordinal)).Should().Be(maxReconnectAttempts,
                "every [AMI] Reconnecting line is followed by a connect");
            logger.Entries.Count(e => e.Line.Contains("[AMI] Reconnect attempt failed", StringComparison.Ordinal)).Should().Be(maxReconnectAttempts,
                "each refused connect is one failed attempt");
        }

        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue();
    }

    /// <summary>
    /// Values inside the rule must keep the loop reconnecting past the attempt where the multiplier's power overflows a
    /// <see cref="double"/> (about the 100th with a multiplier of 2000): a zero initial delay times an infinite power is
    /// not a number, which the backoff must not turn into an exception that ends the reconnect. 120 refused connects,
    /// then the give-up at <c>MaxReconnectAttempts</c>, and no <c>ReconnectBackoffFailed</c>.
    /// </summary>
    [Fact]
    public async Task ReconnectLoop_ShouldKeepReconnecting_WhenAZeroInitialDelayMeetsAMultiplierWhosePowerOverflows()
    {
        const int maxReconnectAttempts = 120;
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new RecordingLogger();
        var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            MaxReconnectAttempts = maxReconnectAttempts,
            ReconnectInitialDelay = TimeSpan.Zero,
            ReconnectMultiplier = 2000.0,
            ReconnectMaxDelay = TimeSpan.Zero,
        }), factory, logger);
        var first = await ConnectAsync(connection, factory, peerCts);
        var disconnected = logger.Logged("[AMI] Disconnected");

        first.CloseFromPeer();
        var ended = await CompletesWithinBoundAsync(disconnected);

        using (new AssertionScope())
        {
            ended.Should().BeTrue("the loop ends, at its give-up");
            logger.Named("ReconnectBackoffFailed").Should().BeEmpty("values the rule accepted never fail the backoff");
            (factory.Created.Count - 1).Should().Be(maxReconnectAttempts, "every attempt up to the limit is made");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
        }

        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue();
    }

    /// <summary>
    /// The count's second failure mode: every reconnect's TCP connect is accepted and the peer closes the socket before
    /// the banner, as an Asterisk restarting behind a listener does. Each such connect is one failed attempt, so
    /// <c>MaxReconnectAttempts = N</c> still makes exactly N reconnect connects, then gives up
    /// <see cref="AmiConnectionState.Disconnected"/> with every socket released once.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ReconnectLoop_ShouldMakeExactlyNConnects_WhenEveryReconnectIsAcceptedAndClosed(int maxReconnectAttempts)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new SignalingLogger<AmiConnection>();
        var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            MaxReconnectAttempts = maxReconnectAttempts,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(5),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(5),
        }), factory, logger);
        var first = await ConnectAsync(connection, factory, peerCts);
        var gaveUp = logger.Logged("[AMI] Disconnected");
        // Every socket the loop creates is accepted and closed by its peer before the banner.
        var closer = Task.Run(async () =>
        {
            try
            {
                while (true)
                    (await factory.NextAsync(peerCts.Token)).CloseFromPeer();
            }
            catch (OperationCanceledException) when (peerCts.IsCancellationRequested)
            {
                // The test is over.
            }
        });

        first.CloseFromPeer();
        var ended = await CompletesWithinBoundAsync(gaveUp);
        var loop = ReconnectLoopOf(connection);
        var loopCompleted = loop is not null && await CompletesWithinBoundAsync(loop);

        var sockets = factory.Created;
        using (new AssertionScope())
        {
            ended.Should().BeTrue("the loop gives up once its attempts are used");
            loopCompleted.Should().BeTrue("the loop's task completes at the give-up");
            (sockets.Count - 1).Should().Be(maxReconnectAttempts,
                $"MaxReconnectAttempts = {maxReconnectAttempts} makes exactly that many reconnect connects when each is accepted and closed");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
            logger.Entries.Count(e => e.Line.Contains("[AMI] Reconnect attempt failed", StringComparison.Ordinal)).Should().Be(maxReconnectAttempts,
                "each accepted-and-closed connect is one failed attempt");
            Unreleased(sockets).Should().BeEmpty("every socket the connection created is released exactly once");
        }

        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue();
        await peerCts.CancelAsync();
        (await CompletesWithinBoundAsync(closer)).Should().BeTrue("the peer stops with the test");
    }

    /// <summary>
    /// A caller's ending racing the give-up, from both sides (the #360 ending paths). <c>before</c>: the ending is issued
    /// on the loop's own last <c>[AMI] Reconnect attempt failed</c> line, so it is recorded before the loop reaches its
    /// attempt check. <c>during</c>: the ending is issued from inside the give-up's own release of the last socket, so it
    /// joins the connection's ending under way. Either way the caller's call returns, nothing is dialled after the
    /// N-th connect, every socket is released exactly once and the connection ends <see cref="AmiConnectionState.Disconnected"/>.
    /// </summary>
    [Theory]
    [InlineData(nameof(AmiConnection.DisposeAsync), "before")]
    [InlineData(nameof(AmiConnection.DisposeAsync), "during")]
    [InlineData(nameof(AmiConnection.DisconnectAsync), "before")]
    [InlineData(nameof(AmiConnection.DisconnectAsync), "during")]
    public async Task Ending_ShouldReleaseEverySocketOnce_WhenTheCallerEndsTheConnectionAsTheLoopGivesUp(string ending, string when)
    {
        const int maxReconnectAttempts = 2;
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new ActingLogger("[AMI] Reconnect attempt failed", occurrence: maxReconnectAttempts);
        var connection = new AmiConnection(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            MaxReconnectAttempts = maxReconnectAttempts,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(5),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(5),
        }), factory, logger);
        var issued = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        logger.Act = when == "before"
            ? () => issued.TrySetResult(EndAsync(connection, ending))
            // The last failed connect's socket is released by the give-up's own ending: issue the caller's there.
            : () => factory.Created[^1].DuringFirstDispose = () => issued.TrySetResult(EndAsync(connection, ending));
        var first = await ConnectAsync(connection, factory, peerCts);

        first.CloseFromPeer();
        var callerEnding = await ResultWithinBoundAsync(issued.Task);
        callerEnding.Should().NotBeNull("the caller's ending is issued at the give-up");
        var returned = await CompletesWithinBoundAsync(callerEnding!);
        var loop = ReconnectLoopOf(connection);
        var loopCompleted = loop is not null && await CompletesWithinBoundAsync(loop);

        var sockets = factory.Created;
        using (new AssertionScope())
        {
            returned.Should().BeTrue($"{ending} returns when it lands on the give-up ({when})");
            loopCompleted.Should().BeTrue("the reconnect loop's task completes");
            (sockets.Count - 1).Should().Be(maxReconnectAttempts, "nothing is dialled after the last attempt");
            Unreleased(sockets).Should().BeEmpty("every socket is released exactly once, whichever ending releases it");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
            logger.Entries.Count(e => e.Line.Contains("[AMI] Disconnected", StringComparison.Ordinal)).Should().Be(1,
                "the two endings are one ending: Disconnected is reported once");
        }

        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue();
    }

    /// <summary>
    /// A caller's ending issued on the loop's own <c>ReconnectBackoffFailed</c> line, right before the loop ends the
    /// connection itself: the loop's ending yields to the caller's, which returns, releases the lost socket once and
    /// leaves <see cref="AmiConnectionState.Disconnected"/>.
    /// </summary>
    [Theory]
    [InlineData(nameof(AmiConnection.DisposeAsync))]
    [InlineData(nameof(AmiConnection.DisconnectAsync))]
    public async Task Ending_ShouldReleaseTheLostSocketOnce_WhenTheCallerEndsTheConnectionAsTheBackoffFails(string ending)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new ActingLogger("[AMI] Reconnect backoff failed", occurrence: 1);
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            ReconnectInitialDelay = Backoff,
            ReconnectMaxDelay = Backoff,
        };
        var connection = new AmiConnection(Options.Create(options), factory, logger);
        // Changed after construction, so the constructor's check never saw it.
        options.ReconnectMultiplier = 0.5;
        var issued = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        logger.Act = () => issued.TrySetResult(EndAsync(connection, ending));
        var first = await ConnectAsync(connection, factory, peerCts);

        first.CloseFromPeer();
        var callerEnding = await ResultWithinBoundAsync(issued.Task);
        callerEnding.Should().NotBeNull("the backoff fails and its line issues the caller's ending");
        var returned = await CompletesWithinBoundAsync(callerEnding!);
        var loop = ReconnectLoopOf(connection);
        var loopCompleted = loop is not null && await CompletesWithinBoundAsync(loop);

        var sockets = factory.Created;
        using (new AssertionScope())
        {
            returned.Should().BeTrue($"{ending} returns when it lands on the backoff failure");
            loopCompleted.Should().BeTrue("the reconnect loop's task completes");
            sockets.Should().HaveCount(1, "the loop makes no connect");
            Unreleased(sockets).Should().BeEmpty("the lost socket is released exactly once");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
            logger.Entries.Count(e => e.Line.Contains("[AMI] Disconnected", StringComparison.Ordinal)).Should().Be(1,
                "the two endings are one ending: Disconnected is reported once");
        }
    }

    /// <summary>The connection's private field that holds the reconnect loop's task.</summary>
    private static readonly FieldInfo ReconnectLoopField =
        typeof(AmiConnection).GetField("_reconnectLoop", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("AmiConnection has no _reconnectLoop field: the test's seam moved.");

    /// <summary>
    /// The reconnect loop's task, which the connection keeps private. Read by reflection, in the test only, so a test can
    /// wait for the loop itself to end; <c>src/</c> exposes nothing for it.
    /// </summary>
    private static Task? ReconnectLoopOf(AmiConnection connection) => (Task?)ReconnectLoopField.GetValue(connection);

    /// <summary>Each socket not disposed exactly once, described by its place in creation order.</summary>
    private static List<string> Unreleased(IReadOnlyList<PipedSocket> sockets) =>
        [.. sockets
            .Select((socket, index) => (socket.DisposeCount, Place: index + 1))
            .Where(s => s.DisposeCount != 1)
            .Select(s => $"socket {s.Place} of {sockets.Count}: disposed {s.DisposeCount} time(s)")];

    /// <summary>
    /// A connection that reconnects forever (<c>MaxReconnectAttempts = 0</c>, the default) after
    /// <see cref="Backoff"/>, and that only the peer's close or the test ends.
    /// </summary>
    private static AmiConnection CreateReconnecting(PipedSocketFactory factory, ILogger<AmiConnection> logger) =>
        new(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            MaxReconnectAttempts = 0,
            ReconnectInitialDelay = Backoff,
            ReconnectMaxDelay = Backoff,
        }), factory, logger);

    /// <summary>
    /// Connects, with the next socket's peer completing the login; returns that socket. The peer's reads end
    /// with <paramref name="peerCts"/>.
    /// </summary>
    private static async Task<PipedSocket> ConnectAsync(AmiConnection connection, PipedSocketFactory factory,
        CancellationTokenSource peerCts)
    {
        var loggedIn = Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.CompleteLoginAsync(peerCts.Token);
            return peer;
        }, peerCts.Token);
        await connection.ConnectAsync().AsTask().WaitAsync(Bound);
        return await loggedIn.WaitAsync(Bound);
    }

    /// <summary>
    /// The peer closes the socket; completes once the reconnect loop has written its <c>[AMI] Reconnecting</c>
    /// line, right before its backoff delay.
    /// </summary>
    private static Task<bool> LoseAsync(PipedSocket peer, Func<string, Task> logged)
    {
        var reconnecting = logged("[AMI] Reconnecting");
        peer.CloseFromPeer();
        return CompletesWithinBoundAsync(reconnecting);
    }

    private static Task EndAsync(AmiConnection connection, string ending) => ending switch
    {
        nameof(AmiConnection.DisposeAsync) => connection.DisposeAsync().AsTask(),
        nameof(AmiConnection.DisconnectAsync) => connection.DisconnectAsync().AsTask(),
        _ => throw new ArgumentOutOfRangeException(nameof(ending), ending, "Not a caller's ending."),
    };

    /// <summary>
    /// Plays an Asterisk that is up for every socket the connection creates from now on: its peer completes
    /// the login, which <paramref name="logins"/> counts. Ends when <paramref name="ct"/> is cancelled.
    /// </summary>
    private static async Task LogInEveryNewSocketAsync(PipedSocketFactory factory, Counter logins, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var peer = await factory.NextAsync(ct);
                try
                {
                    await peer.CompleteLoginAsync(ct);
                    logins.Increment();
                }
                catch (InvalidOperationException)
                {
                    // The connection closed this socket before the login finished: no login was sent.
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The test is over.
        }
    }

    private static async Task<bool> CompletesWithinBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Bound);
            return true;
        }
        catch (TimeoutException)
        {
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
            return null;
        }
    }

    private sealed class Counter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }

    /// <summary>
    /// A <see cref="SignalingLogger{T}"/> that also runs <see cref="Act"/> inside the
    /// <c>occurrence</c>-th line containing <c>fragment</c>: on the thread that writes the line, before the
    /// write returns. It places a call at a fixed point of the connection's own work by construction.
    /// </summary>
    private sealed class ActingLogger(string fragment, int occurrence) : ILogger<AmiConnection>
    {
        private readonly SignalingLogger<AmiConnection> _signals = new();
        private int _seen;

        public Action? Act { get; set; }

        public IReadOnlyList<(LogLevel Level, string Line)> Entries => _signals.Entries;

        public Task Logged(string lineFragment) => _signals.Logged(lineFragment);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _signals.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _signals.Log(logLevel, eventId, state, exception, formatter);
            if (formatter(state, exception).Contains(fragment, StringComparison.Ordinal)
                && Interlocked.Increment(ref _seen) == occurrence)
            {
                Act?.Invoke();
            }
        }
    }

    /// <summary>
    /// Keeps every entry with its event name, level and exception, and signals a line as <see cref="SignalingLogger{T}"/>
    /// does, so a test can match an entry by its <see cref="EventId.Name"/> instead of its text.
    /// </summary>
    private sealed class RecordingLogger : ILogger<AmiConnection>
    {
        private readonly SignalingLogger<AmiConnection> _signals = new();
        private readonly Lock _gate = new();
        private readonly List<(string? Name, LogLevel Level, Exception? Exception)> _entries = [];

        public Task Logged(string fragment) => _signals.Logged(fragment);

        public List<(string? Name, LogLevel Level, Exception? Exception)> Named(string eventName)
        {
            lock (_gate)
            {
                return [.. _entries.Where(e => e.Name == eventName)];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _entries.Add((eventId.Name, logLevel, exception));
            }

            _signals.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
