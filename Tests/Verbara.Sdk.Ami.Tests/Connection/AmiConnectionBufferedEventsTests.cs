using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Internal;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// What happens to the AMI events still buffered in the event pump when a connection ends. The caller's
/// ending — <c>DisposeAsync</c> or <c>DisconnectAsync</c> — delivers none of them: it waits for the dispatch
/// in progress only, counts the rest as dropped with <c>reason=caller_ending</c> and logs the count once at
/// Warning, and it cuts a loss's drain that is still delivering. A loss alone still delivers the whole buffer,
/// in order, before the reconnect dials or before the lost connection reports Disconnected.
/// </summary>
/// <remarks>
/// <para>
/// Every test holds a dispatch with a gate and buffers events behind it through <see cref="PipedSocketFactory"/>:
/// the peer writes them before it answers a Ping, so they wait in the pump once the Ping returns. The ending is
/// asked without being awaited, then the gate opens, and the held handler returns at once. What is asserted is how
/// many events the handler received after the ending was asked: a count, never a clock. Time enters only as
/// <see cref="Bound"/>, a hang bound every wait ends long before, and as <see cref="LaterEventWindow"/>, the one
/// observation window for an absence, whose positive controls are the two loss pins here and
/// <see cref="AmiConnectionEndingTests.EventPump_ShouldDispatchTheBufferedEvent_WhenTheHandlerReturnsWithoutEndingTheConnection"/>.
/// </para>
/// <para>
/// The class runs in <see cref="AmiEventsDroppedMetricGroup"/> because one test listens on the process-wide
/// <c>ami.events.dropped</c> counter.
/// </para>
/// </remarks>
[Collection(AmiEventsDroppedMetricGroup.Name)]
public sealed class AmiConnectionBufferedEventsTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a test watches for a buffered event after an ending from inside the dispatch. It is the
    /// observation, not a hang bound; the loss pins below deliver every buffered event well inside it.
    /// </summary>
    private static readonly TimeSpan LaterEventWindow = TimeSpan.FromSeconds(1);

    /// <summary>How many events are buffered behind the held dispatch.</summary>
    private const int Buffered = 20;

    /// <summary>A count no other test in the process records on <c>ami.events.dropped</c>.</summary>
    private const int DistinctiveBuffered = 37;

    /// <summary>A second count no other test records on <c>ami.events.dropped</c>: the faulted loss drain's.</summary>
    private const int FaultedDrainBuffered = 41;

    /// <summary>The loss-drain tests hold the k-th buffered event's dispatch; k of the N are delivered by then.</summary>
    private const int HeldBufferedEvent = 5;

    private const string DiscardedFormat = "[AMI_EVENT] Discarded on caller ending: count={Count}";

    // ── The caller's ending ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(AmiConnection.DisposeAsync))]
    [InlineData(nameof(AmiConnection.DisconnectAsync))]
    public async Task DisposeAsync_ShouldDeliverNoBufferedEvent_WhenTheCallerEndsTheConnection(string ending)
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory, autoReconnect: false);
        var dispatches = new Dispatches(connection);
        var socket = await ConnectAsync(connection, factory, peerCts);
        await BufferBehindTheHeldDispatchAsync(connection, socket, dispatches, Buffered, peerCts);

        var asked = dispatches.Count;
        var end = EndAsync(connection, ending);
        dispatches.First.Open();
        var ended = await CompletesWithinBoundAsync(end);

        using (new AssertionScope())
        {
            ended.Should().BeTrue($"the caller's {ending} returns once the dispatch in progress has");
            (dispatches.Count - asked).Should().Be(0,
                $"the caller's {ending} delivers none of the {Buffered} events still buffered when it was asked");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldCallNoObserverForABufferedEvent_WhenTheCallerEndsTheConnection()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory, autoReconnect: false);
        var observer = new HoldingObserver();
        using var subscription = connection.Subscribe(observer);
        var socket = await ConnectAsync(connection, factory, peerCts);
        (await socket.WriteEventAsync("FullyBooted")).Should().BeTrue("the peer sends the first event");
        (await CompletesWithinBoundAsync(observer.First.Entered)).Should().BeTrue("the observer receives it and holds its OnNext");
        await BufferAsync(connection, socket, Buffered, peerCts);

        var asked = observer.Count;
        var end = connection.DisposeAsync().AsTask();
        observer.First.Open();
        var ended = await CompletesWithinBoundAsync(end);

        using (new AssertionScope())
        {
            ended.Should().BeTrue("the caller's DisposeAsync returns once the OnNext in progress has");
            (observer.Count - asked).Should().Be(0,
                $"the caller's DisposeAsync calls no observer for any of the {Buffered} events still buffered when it was asked");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
        }
    }

    // ── The caller's ending while a loss is ending the session ───────────────────────────────────────

    /// <summary>
    /// The peer closes with N events buffered behind the held first dispatch. The reconnect loop's release, before
    /// its first attempt, disposes the lost socket and waits for the pump; the gate then opens and the loss's drain
    /// delivers the buffered events until the k-th, which is held on a second gate. The caller disposes there.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldCutTheLossDrain_WhenTheCallerEndsTheConnectionWhileAReconnectingLossDelivers()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory, autoReconnect: true, backoff: TimeSpan.FromMilliseconds(1));
        var dispatches = new Dispatches(connection, holdAtBuffered: HeldBufferedEvent);
        var socket = await ConnectAsync(connection, factory, peerCts);
        await BufferBehindTheHeldDispatchAsync(connection, socket, dispatches, Buffered, peerCts);

        socket.CloseFromPeer();
        (await CompletesWithinBoundAsync(socket.Disposed)).Should().BeTrue(
            "the reconnect loop's release disposes the lost socket, before it waits for the pump");
        dispatches.First.Open();
        (await CompletesWithinBoundAsync(dispatches.Kth.Entered)).Should().BeTrue(
            $"the loss's drain delivers the buffered events up to the {HeldBufferedEvent}th, whose dispatch is held");

        var asked = dispatches.Count;
        var end = connection.DisposeAsync().AsTask();
        dispatches.Kth.Open();
        var ended = await CompletesWithinBoundAsync(end);

        using (new AssertionScope())
        {
            ended.Should().BeTrue("the caller's DisposeAsync returns once the dispatch in progress has");
            (dispatches.Count - asked).Should().Be(0,
                $"the caller's DisposeAsync cuts the loss's drain: none of the {Buffered - HeldBufferedEvent} events " +
                $"after the {HeldBufferedEvent}th is delivered");
            factory.Created.Should().HaveCount(1, "the reconnect loop dials no socket once the caller has ended the connection");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
        }
    }

    /// <summary>
    /// With AutoReconnect off, the peer's close runs the lost connection's own ending, whose release disposes the
    /// lost socket and waits for the pump's drain. The caller's DisposeAsync, asked while the k-th buffered event's
    /// dispatch is held, joins that ending.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldCutTheLossDrain_WhenItJoinsTheEndingOfALossWithoutAutoReconnect()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory, autoReconnect: false);
        var dispatches = new Dispatches(connection, holdAtBuffered: HeldBufferedEvent);
        var socket = await ConnectAsync(connection, factory, peerCts);
        await BufferBehindTheHeldDispatchAsync(connection, socket, dispatches, Buffered, peerCts);

        socket.CloseFromPeer();
        (await CompletesWithinBoundAsync(socket.Disposed)).Should().BeTrue(
            "the lost connection's ending disposes the lost socket, before it waits for the pump");
        dispatches.First.Open();
        (await CompletesWithinBoundAsync(dispatches.Kth.Entered)).Should().BeTrue(
            $"the loss's drain delivers the buffered events up to the {HeldBufferedEvent}th, whose dispatch is held");

        var asked = dispatches.Count;
        var end = connection.DisposeAsync().AsTask();
        dispatches.Kth.Open();
        var ended = await CompletesWithinBoundAsync(end);

        using (new AssertionScope())
        {
            ended.Should().BeTrue("the caller's DisposeAsync joins the lost connection's ending and returns once it has finished");
            (dispatches.Count - asked).Should().Be(0,
                $"the caller's DisposeAsync cuts the drain of the ending it joins: none of the {Buffered - HeldBufferedEvent} " +
                $"events after the {HeldBufferedEvent}th is delivered");
            factory.Created.Should().HaveCount(1, "nothing dials after a loss without AutoReconnect");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
        }
    }

    /// <summary>
    /// The peer closes with N events buffered behind the held first dispatch, and the reconnect loop waits out a
    /// backoff far longer than the test: the lost session's pump is still attached, and nothing has released it.
    /// The caller disposes there, then the gate opens.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldDeliverNoBufferedEvent_WhenTheCallerEndsTheConnectionDuringTheReconnectBackoff()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new CapturingLogger<AmiConnection>();
        var connection = Create(factory, autoReconnect: true, logger, backoff: TimeSpan.FromMinutes(5));
        var dispatches = new Dispatches(connection);
        var reconnecting = logger.Logged("[AMI] Reconnecting");
        var socket = await ConnectAsync(connection, factory, peerCts);
        await BufferBehindTheHeldDispatchAsync(connection, socket, dispatches, Buffered, peerCts);

        socket.CloseFromPeer();
        (await CompletesWithinBoundAsync(reconnecting)).Should().BeTrue(
            "the peer's close starts the reconnect loop, which logs Reconnecting right before its backoff delay");

        var asked = dispatches.Count;
        var end = connection.DisposeAsync().AsTask();
        dispatches.First.Open();
        var ended = await CompletesWithinBoundAsync(end);

        using (new AssertionScope())
        {
            ended.Should().BeTrue("the caller's DisposeAsync stops the backoff and returns once the dispatch in progress has");
            (dispatches.Count - asked).Should().Be(0,
                $"the caller's DisposeAsync during the backoff delivers none of the {Buffered} events of the lost session");
            factory.Created.Should().HaveCount(1, "the reconnect loop dials no socket once the caller has ended the connection");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
        }
    }

    /// <summary>
    /// The session the reconnect loop connects gets its own event pump. The peer closes the first session, the loop
    /// dials and logs in again, and only then does the second session's peer buffer N events behind a held dispatch.
    /// The caller disposes there, then the gate opens.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldDeliverNoBufferedEvent_WhenTheCallerEndsTheSessionTheReconnectLoopConnected()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory, autoReconnect: true, backoff: TimeSpan.FromMilliseconds(1));
        var dispatches = new Dispatches(connection);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += () => reconnected.TrySetResult();
        var lost = await ConnectAsync(connection, factory, peerCts);
        var relogin = Task.Run(async () =>
        {
            var peer = await factory.NextAsync(peerCts.Token);
            await peer.CompleteLoginAsync(peerCts.Token);
            return peer;
        }, peerCts.Token);

        lost.CloseFromPeer();
        var socket = await relogin.WaitAsync(Bound);
        (await CompletesWithinBoundAsync(reconnected.Task)).Should().BeTrue(
            "the reconnect loop logs in on the second socket and reports the reconnect");
        await BufferBehindTheHeldDispatchAsync(connection, socket, dispatches, Buffered, peerCts);

        var asked = dispatches.Count;
        var end = connection.DisposeAsync().AsTask();
        dispatches.First.Open();
        var ended = await CompletesWithinBoundAsync(end);

        using (new AssertionScope())
        {
            ended.Should().BeTrue("the caller's DisposeAsync returns once the dispatch in progress has");
            (dispatches.Count - asked).Should().Be(0,
                $"the caller's DisposeAsync delivers none of the {Buffered} events buffered in the session the " +
                "reconnect loop connected: that session's pump observes the caller's ending too");
            factory.Created.Should().HaveCount(2, "one socket per session, and nothing dials after the caller's ending");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
        }
    }

    // ── What the drop leaves behind: one Warning and one tagged measurement ──────────────────────────

    [Fact]
    public async Task DisposeAsync_ShouldLogTheDiscardedCountOnceAtWarning_WhenBufferedEventsAreDiscarded()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new CapturingLogger<AmiConnection>();
        var connection = Create(factory, autoReconnect: false, logger);
        var dispatches = new Dispatches(connection);
        var socket = await ConnectAsync(connection, factory, peerCts);
        await BufferBehindTheHeldDispatchAsync(connection, socket, dispatches, Buffered, peerCts);

        var end = connection.DisposeAsync().AsTask();
        dispatches.First.Open();
        (await CompletesWithinBoundAsync(end)).Should().BeTrue("the caller's DisposeAsync returns once the dispatch in progress has");

        var discarded = logger.Entries.Where(e => e.Format == DiscardedFormat).ToList();
        using (new AssertionScope())
        {
            discarded.Should().ContainSingle(
                $"the caller's DisposeAsync logs \"{DiscardedFormat}\" exactly once when it discards buffered events");
            discarded.Should().OnlyContain(e => e.Level == LogLevel.Warning, "the discard is logged at Warning");
            discarded.Select(e => CountOf(e)).Should().Equal([(long)Buffered],
                $"the Warning's Count is the {Buffered} events still buffered when the caller ended the connection");
        }
    }

    /// <summary>
    /// The listener sees every measurement on the process-wide counter, including those of connections other tests
    /// end. This class is not run in parallel with other test classes, but a release that finishes after an
    /// earlier test's ending returned can still record inside this listener's window. So the test asserts that
    /// exactly one measurement equals a count no other test uses, never a summed delta.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldCountTheDiscardedEventsAsCallerEnding_WhenBufferedEventsAreDiscarded()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        using var dropped = new CallerEndingDrops();
        var factory = new PipedSocketFactory();
        var connection = Create(factory, autoReconnect: false);
        var dispatches = new Dispatches(connection);
        var socket = await ConnectAsync(connection, factory, peerCts);
        await BufferBehindTheHeldDispatchAsync(connection, socket, dispatches, DistinctiveBuffered, peerCts);

        var end = connection.DisposeAsync().AsTask();
        dispatches.First.Open();
        (await CompletesWithinBoundAsync(end)).Should().BeTrue("the caller's DisposeAsync returns once the dispatch in progress has");

        dropped.Measurements.Where(m => m == DistinctiveBuffered).Should().ContainSingle(
            $"the caller's DisposeAsync records the {DistinctiveBuffered} events it discarded as one measurement on " +
            "ami.events.dropped tagged reason=caller_ending");
    }

    [Fact]
    public async Task DisposeAsync_ShouldLogNoDiscardedEvents_WhenTheBufferIsEmpty()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new CapturingLogger<AmiConnection>();
        var connection = Create(factory, autoReconnect: false, logger);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnEvent += _ =>
        {
            delivered.TrySetResult();
            return ValueTask.CompletedTask;
        };
        var socket = await ConnectAsync(connection, factory, peerCts);
        (await socket.WriteEventAsync("FullyBooted")).Should().BeTrue("the peer sends one event");
        (await CompletesWithinBoundAsync(delivered.Task)).Should().BeTrue("the handler receives it, and nothing is buffered behind it");

        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue();

        using (new AssertionScope())
        {
            logger.Entries.Where(e => e.Format == DiscardedFormat).Should().BeEmpty(
                "a caller's DisposeAsync with nothing buffered discards nothing, so it logs no discard");
            logger.Entries.Where(e => e.Level >= LogLevel.Warning).Should().BeEmpty("a clean close logs no Warning");
        }
    }

    // ── Pins, green before and after: a loss alone delivers the whole buffer, and #335's in-dispatch ending ──

    [Fact]
    public async Task ReconnectLoop_ShouldDeliverEveryBufferedEventInOrder_WhenThePeerClosesWithAutoReconnect()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        // Only the first socket accepts its connect, so the test reads the loop's first dial and nothing logs in.
        var factory = new PipedSocketFactory { ConnectsAccepted = 1 };
        var logger = new CapturingLogger<AmiConnection>();
        var connection = Create(factory, autoReconnect: true, logger, backoff: TimeSpan.FromMilliseconds(1));
        var dispatches = new Dispatches(connection);
        var socket = await ConnectAsync(connection, factory, peerCts);
        await BufferBehindTheHeldDispatchAsync(connection, socket, dispatches, Buffered, peerCts);

        socket.CloseFromPeer();
        dispatches.First.Open();
        var redial = await ResultWithinBoundAsync(factory.NextAsync(peerCts.Token).AsTask());
        var deliveredBeforeRedial = dispatches.BufferedPeers();
        (await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask())).Should().BeTrue();

        using (new AssertionScope())
        {
            redial.Should().NotBeNull("the reconnect loop dials once the loss's drain is over");
            deliveredBeforeRedial.Should().Equal(ExpectedPeers(Buffered),
                $"a loss delivers all {Buffered} buffered events, in order, before the reconnect loop dials");
            logger.Entries.Where(e => e.Format == DiscardedFormat).Should().BeEmpty("a loss discards nothing");
        }
    }

    [Fact]
    public async Task LostConnection_ShouldDeliverEveryBufferedEventInOrder_BeforeDisconnected_WhenAutoReconnectIsOff()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var logger = new CapturingLogger<AmiConnection>();
        var connection = Create(factory, autoReconnect: false, logger);
        var dispatches = new Dispatches(connection);
        var disconnected = logger.Logged("[AMI] Disconnected");
        var socket = await ConnectAsync(connection, factory, peerCts);
        await BufferBehindTheHeldDispatchAsync(connection, socket, dispatches, Buffered, peerCts);

        socket.CloseFromPeer();
        dispatches.First.Open();
        var ended = await CompletesWithinBoundAsync(disconnected);

        using (new AssertionScope())
        {
            ended.Should().BeTrue("the peer's close ends the connection");
            dispatches.BufferedPeers().Should().Equal(ExpectedPeers(Buffered),
                $"a loss without AutoReconnect delivers all {Buffered} buffered events, in order");
            dispatches.States().Should().NotContain(AmiConnectionState.Disconnected,
                "every buffered event is delivered before the lost connection reports Disconnected");
            logger.Entries.Where(e => e.Format == DiscardedFormat).Should().BeEmpty("a loss discards nothing");
            connection.State.Should().Be(AmiConnectionState.Disconnected);
        }
    }

    /// <summary>
    /// A pump whose consumer stopped with <see cref="FaultedDrainBuffered"/> events still buffered, released by a
    /// connection nobody ended: a loss alone, so the release reports no caller's discard — no <c>Discarded on caller
    /// ending</c> Warning and no <c>reason=caller_ending</c> measurement — although the pump counts every one of them
    /// as undelivered.
    /// </summary>
    /// <remarks>
    /// Pinned at pump level. A connection's dispatch guards every <c>OnEvent</c> handler, so no handler fault ends the
    /// consumer of a connection's pump any more and a loss through a real connection leaves nothing undelivered: there
    /// the release's check of the caller's ending could not be shown red. Here the pump's own handler, which the pump
    /// does not guard, throws on its first event once the rest are buffered; the release under test is the
    /// connection's, reached through <see cref="ReleasePump"/>. Removing the check of the lifetime token from it turns
    /// this test red.
    /// </remarks>
    [Fact]
    public async Task ReleasePump_ShouldReportNoCallerEndingDiscard_WhenAPumpStoppedWithEventsBufferedAndNobodyEndedTheConnection()
    {
        using var dropped = new CallerEndingDrops();
        var logger = new CapturingLogger<AmiConnection>();
        var connection = Create(new PipedSocketFactory(), autoReconnect: false, logger);
        var pump = new AsyncEventPump(FaultedDrainBuffered + 1);
        var first = new HeldDispatch();
        var dispatchCount = 0;
        pump.Start(async _ =>
        {
            if (Interlocked.Increment(ref dispatchCount) != 1)
                return;

            first.Enter();
            await first.Gate;
            throw new InvalidOperationException("The pump's handler fails with events still buffered.");
        });
        pump.TryEnqueue(new PeerStatusEvent { Peer = PeerName(0) }).Should().BeTrue();
        (await CompletesWithinBoundAsync(first.Entered)).Should().BeTrue("the handler receives the first event and holds it");
        for (var i = 1; i <= FaultedDrainBuffered; i++)
            pump.TryEnqueue(new PeerStatusEvent { Peer = PeerName(i) }).Should().BeTrue();

        first.Open();
        var released = await CompletesWithinBoundAsync(ReleasePump(connection, pump));

        using (new AssertionScope())
        {
            released.Should().BeTrue("the release returns once the stopped consumer has");
            pump.DroppedOnDispose.Should().Be(FaultedDrainBuffered,
                $"the consumer stopped at the fault, so the release finds all {FaultedDrainBuffered} buffered events undelivered");
            Volatile.Read(ref dispatchCount).Should().Be(1, "the fault stopped the pump's consumer after its first event");
            logger.Entries.Where(e => e.Format == DiscardedFormat).Should().BeEmpty(
                "nobody ended this connection: a release without a caller's ending never logs a caller's discard");
            dropped.Measurements.Should().NotContain(FaultedDrainBuffered,
                "a release without a caller's ending records nothing on ami.events.dropped with reason=caller_ending");
        }
    }

    /// <summary>
    /// The #335 path with a full buffer: the handler holding the first dispatch ends the connection itself, with
    /// <see cref="Buffered"/> events behind it, and the pump dispatches none of them.
    /// </summary>
    [Fact]
    public async Task OnEventHandler_ShouldDeliverNoBufferedEvent_WhenItEndsTheConnectionWithEventsBuffered()
    {
        using var peerCts = new CancellationTokenSource(Bound * 2);
        var factory = new PipedSocketFactory();
        var connection = Create(factory, autoReconnect: true);
        var first = new HeldDispatch();
        var ended = new TaskCompletionSource<AmiConnectionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var later = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchCount = 0;
        var laterCount = 0;
        connection.OnEvent += async evt =>
        {
            if (Interlocked.Increment(ref dispatchCount) > 1)
            {
                Interlocked.Increment(ref laterCount);
                later.TrySetResult((evt as PeerStatusEvent)?.Peer);
                return;
            }

            first.Enter();
            await first.Gate;
            await connection.DisposeAsync();
            ended.TrySetResult(connection.State);
        };
        var socket = await ConnectAsync(connection, factory, peerCts);
        (await socket.WriteEventAsync("FullyBooted")).Should().BeTrue("the peer sends the first event");
        (await CompletesWithinBoundAsync(first.Entered)).Should().BeTrue("the handler receives it and holds its dispatch");
        await BufferAsync(connection, socket, Buffered, peerCts);

        first.Open();
        var state = await ValueWithinAsync(ended.Task, Bound);
        var laterEvent = await ResultWithinAsync(later.Task, LaterEventWindow);
        var laterDispose = await CompletesWithinBoundAsync(connection.DisposeAsync().AsTask());

        using (new AssertionScope())
        {
            state.Should().Be(AmiConnectionState.Disconnected,
                "the handler's DisposeAsync returns without waiting for the dispatch it runs in");
            laterEvent.Should().BeNull(
                $"the pump dispatches none of the {Buffered} buffered events after the handler ended the connection, " +
                $"so none arrives in {LaterEventWindow.TotalMilliseconds} ms");
            Volatile.Read(ref laterCount).Should().Be(0);
            laterDispose.Should().BeTrue("a DisposeAsync after the handler's ending completes");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static AmiConnection Create(PipedSocketFactory factory, bool autoReconnect,
        ILogger<AmiConnection>? logger = null, TimeSpan? backoff = null) =>
        new(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            // Only the test ends the connection.
            EnableHeartbeat = false,
            AutoReconnect = autoReconnect,
            ReconnectInitialDelay = backoff ?? TimeSpan.FromMilliseconds(200),
            ReconnectMaxDelay = backoff ?? TimeSpan.FromMilliseconds(200),
        }), factory, logger ?? new CapturingLogger<AmiConnection>());

    /// <summary>
    /// Connects, with the next socket's peer completing the login; returns that socket. The peer's reads
    /// end with <paramref name="peerCts"/>.
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
    /// The peer sends a first event, whose dispatch <paramref name="dispatches"/> holds, then buffers
    /// <paramref name="count"/> events behind it.
    /// </summary>
    private static async Task BufferBehindTheHeldDispatchAsync(AmiConnection connection, PipedSocket socket,
        Dispatches dispatches, int count, CancellationTokenSource peerCts)
    {
        (await socket.WriteEventAsync("FullyBooted")).Should().BeTrue("the peer sends the first event");
        (await CompletesWithinBoundAsync(dispatches.First.Entered)).Should().BeTrue("the handler receives it and holds its dispatch");
        await BufferAsync(connection, socket, count, peerCts);
    }

    /// <summary>
    /// The peer writes <paramref name="count"/> PeerStatus events, <c>SIP/0</c> onwards, then answers a Ping. The
    /// reader reads them before the Ping's answer, so when the Ping returns they all wait in the pump's buffer.
    /// </summary>
    private static async Task BufferAsync(AmiConnection connection, PipedSocket socket, int count,
        CancellationTokenSource peerCts)
    {
        for (var i = 0; i < count; i++)
            (await socket.WriteEventAsync("PeerStatus", [new("Peer", PeerName(i))])).Should().BeTrue();

        var ping = connection.SendActionAsync(new PingAction()).AsTask();
        var action = await socket.ReadActionAsync(peerCts.Token).WaitAsync(Bound);
        PipedSocket.IsPing(action).Should().BeTrue("the connection sends the Ping");
        (await socket.RespondAsync("Success", PipedSocket.ActionIdOf(action!))).Should().BeTrue();
        (await CompletesWithinBoundAsync(ping)).Should().BeTrue(
            $"the Ping is answered while the dispatch is held, so the {count} events before its answer are buffered");
    }

    private static string PeerName(int i) => "SIP/" + i.ToString(CultureInfo.InvariantCulture);

    private static string[] ExpectedPeers(int count) => [.. Enumerable.Range(0, count).Select(PeerName)];

    private static Task EndAsync(AmiConnection connection, string ending) => ending switch
    {
        nameof(AmiConnection.DisposeAsync) => connection.DisposeAsync().AsTask(),
        nameof(AmiConnection.DisconnectAsync) => connection.DisconnectAsync().AsTask(),
        _ => throw new ArgumentOutOfRangeException(nameof(ending), ending, "Not a caller's ending."),
    };

    private static long? CountOf(LogEntry entry) =>
        entry.State.TryGetValue("Count", out var value) && value is not null
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : null;

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

    private static Task<T?> ResultWithinBoundAsync<T>(Task<T> task) where T : class =>
        ResultWithinAsync(task, Bound);

    /// <summary>
    /// The task's result if it completes within <paramref name="limit"/>, else <see langword="null"/>. With
    /// <see cref="Bound"/> it is a hang bound; with <see cref="LaterEventWindow"/>, the absence it observes.
    /// </summary>
    private static async Task<T?> ResultWithinAsync<T>(Task<T> task, TimeSpan limit) where T : class?
    {
        try
        {
            return await task.WaitAsync(limit);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static async Task<T?> ValueWithinAsync<T>(Task<T> task, TimeSpan limit) where T : struct
    {
        try
        {
            return await task.WaitAsync(limit);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// An <c>OnEvent</c> handler that counts every dispatch and records, in order, each PeerStatus's peer and the
    /// connection's state when it was dispatched. It holds the first dispatch on <see cref="First"/>, and, when
    /// asked, the dispatch of the k-th buffered event on <see cref="Kth"/>. A held dispatch returns at once when its
    /// gate opens.
    /// </summary>
    private sealed class Dispatches
    {
        private readonly Lock _gate = new();
        private readonly List<string?> _peers = [];
        private readonly List<AmiConnectionState> _states = [];
        private readonly int _holdAt;
        private int _count;

        public Dispatches(AmiConnection connection, int holdAtBuffered = 0)
        {
            // Dispatch 1 is the first event; the k-th buffered event is dispatch k + 1.
            _holdAt = holdAtBuffered > 0 ? holdAtBuffered + 1 : 0;
            connection.OnEvent += evt => OnEventAsync(connection, evt);
        }

        public HeldDispatch First { get; } = new();

        public HeldDispatch Kth { get; } = new();

        /// <summary>How many dispatches have started.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>The peers of the buffered events dispatched so far, in order.</summary>
        public IReadOnlyList<string?> BufferedPeers()
        {
            lock (_gate)
            {
                return [.. _peers.Skip(1)];
            }
        }

        /// <summary>The connection's state at each dispatch, in order.</summary>
        public IReadOnlyList<AmiConnectionState> States()
        {
            lock (_gate)
            {
                return [.. _states];
            }
        }

        private async ValueTask OnEventAsync(AmiConnection connection, ManagerEvent evt)
        {
            var n = Interlocked.Increment(ref _count);
            lock (_gate)
            {
                _peers.Add((evt as PeerStatusEvent)?.Peer);
                _states.Add(connection.State);
            }

            if (n == 1)
            {
                First.Enter();
                await First.Gate;
            }
            else if (n == _holdAt)
            {
                Kth.Enter();
                await Kth.Gate;
            }
        }
    }

    /// <summary>
    /// An observer that counts its <c>OnNext</c> calls and blocks the first one until <see cref="First"/> opens, or
    /// <see cref="Bound"/> passes.
    /// </summary>
    private sealed class HoldingObserver : IObserver<ManagerEvent>
    {
        private int _count;

        public HeldDispatch First { get; } = new();

        public int Count => Volatile.Read(ref _count);

        public void OnNext(ManagerEvent value)
        {
            if (Interlocked.Increment(ref _count) != 1)
                return;

            First.Enter();
            First.Gate.Wait(Bound);
        }

        public void OnError(Exception error)
        {
            // The connection never reports one; nothing to observe.
        }

        public void OnCompleted()
        {
            // The connection never reports one; nothing to observe.
        }
    }

    private sealed class HeldDispatch
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the dispatch has started and is being held.</summary>
        public Task Entered => _entered.Task;

        public Task Gate => _gate.Task;

        public void Enter() => _entered.TrySetResult();

        public void Open() => _gate.TrySetResult();
    }

    /// <summary>
    /// Every measurement recorded on the <c>Verbara.Sdk.Ami</c> meter's <c>ami.events.dropped</c> counter with the
    /// tag <c>reason=caller_ending</c> while this capture is alive, each one kept, never summed.
    /// </summary>
    private sealed class CallerEndingDrops : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<long> _measurements = new();

        public CallerEndingDrops()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Verbara.Sdk.Ami" && instrument.Name == "ami.events.dropped")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "reason" && tag.Value as string == "caller_ending")
                        _measurements.Enqueue(value);
                }
            });
            _listener.Start();
        }

        public IReadOnlyList<long> Measurements => [.. _measurements];

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>The connection's release of a detached event pump (<c>AmiConnection.ReleasePumpAsync</c>).</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ReleasePumpAsync")]
    private static extern Task ReleasePump(AmiConnection connection, AsyncEventPump pump);

    private sealed record LogEntry(LogLevel Level, string? Format, string Line, IReadOnlyDictionary<string, object?> State);

    /// <summary>
    /// A logger that keeps every line with its level, its message template and its structured state, and whose
    /// lines a test can wait on.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly Lock _gate = new();
        private readonly List<LogEntry> _entries = [];
        private readonly List<(string Fragment, TaskCompletionSource Signal)> _waiters = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        /// <summary>Completes once a line containing <paramref name="fragment"/> has been logged (already or later).</summary>
        public Task Logged(string fragment)
        {
            lock (_gate)
            {
                if (_entries.Exists(e => e.Line.Contains(fragment, StringComparison.Ordinal)))
                    return Task.CompletedTask;

                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((fragment, signal));
                return signal.Task;
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                    values[pair.Key] = pair.Value;
            }

            var line = formatter(state, exception);
            var format = values.TryGetValue("{OriginalFormat}", out var f) ? f as string : null;
            List<TaskCompletionSource> fired = [];
            lock (_gate)
            {
                _entries.Add(new LogEntry(logLevel, format, line, values));
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (line.Contains(_waiters[i].Fragment, StringComparison.Ordinal))
                    {
                        fired.Add(_waiters[i].Signal);
                        _waiters.RemoveAt(i);
                    }
                }
            }

            foreach (var signal in fired)
                signal.TrySetResult();
        }
    }
}

/// <summary>
/// Test classes that listen on the process-wide <c>ami.events.dropped</c> counter. They run apart from every
/// other test class, so no connection another test ends while the listener is alive records on it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AmiEventsDroppedMetricGroup
{
    public const string Name = "ami.events.dropped listeners";
}
