using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Tests.Shared.Sockets;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// How an ARI audio stream publishes its ending, and whether its server learns it whatever a
/// consumer's state observer does. Every cell drives a real server on a loopback port; every wait is a
/// signal bounded by <see cref="SignalTimeout"/>, and a cell whose signal never comes reads its counts
/// at that bound.
/// </summary>
/// <remarks>
/// A reset is not a cell here: a reset under a live session moves the process-wide transport-failure
/// counter, so that ending is pinned in <c>AudioSocketTransportFailureTests</c>, which runs alone.
/// </remarks>
public sealed class AudioStreamEndingTests
{
    /// <summary>Upper bound on any wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private const int EndingStreams = 20;
    private const int ObserverStreams = 10;

    public enum AudioSocketEnding
    {
        /// <summary>An AudioSocket hangup frame, with the connection left open.</summary>
        HangupFrame,

        /// <summary>FIN without a hangup frame.</summary>
        OrderlyClose,

        /// <summary>No ending from the far end: the server stops.</summary>
        ServerStop,
    }

    public enum WebSocketEnding
    {
        /// <summary>A WebSocket close frame from the far end.</summary>
        CloseFrame,

        /// <summary>The far end aborts the connection without a close frame.</summary>
        PeerAbort,

        /// <summary>No ending from the far end: the server stops.</summary>
        ServerStop,
    }

    // ------------------------------------------------------------------------- one ending (H28)

    [Theory]
    [InlineData(AudioSocketEnding.HangupFrame)]
    [InlineData(AudioSocketEnding.OrderlyClose)]
    [InlineData(AudioSocketEnding.ServerStop)]
    public async Task StateChanges_ShouldPublishDisconnectedOnceAndLast_WhenAnAudioSocketStreamEnds(AudioSocketEnding ending)
    {
        // Arrange — 20 identified streams, each recorded from its announcement to completion
        await using var harness = await AudioSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var calls = await IdentifyAllAsync(harness, EndingStreams, bound.Token);

        // Act
        switch (ending)
        {
            case AudioSocketEnding.HangupFrame:
                await Task.WhenAll(calls.Select(c => c.Peer.SendAsync(AudioSocketFrames.Hangup(), bound.Token)));
                break;
            case AudioSocketEnding.OrderlyClose:
                foreach (var call in calls)
                    call.Peer.ShutdownSend();
                break;
            case AudioSocketEnding.ServerStop:
                await harness.Server.StopAsync().AsTask().WaitAsync(bound.Token);
                break;
        }

        await Task.WhenAll(calls.Select(c => c.Recording.Completed)).WaitAsync(bound.Token);

        // Assert
        var sequences = calls.Select(c => string.Join(",", c.Recording.States)).ToList();
        sequences.Should().AllBe(
            "Connected,Disconnected",
            $"a stream ended by {ending} publishes Disconnected once, last, then completes; recorded: {Summarise(sequences)}");
        Dispose(calls);
    }

    [Fact]
    public async Task StateChanges_ShouldPublishErrorThenDisconnectedOnce_WhenAnErrorFrameEndsAnAudioSocketStream()
    {
        // Arrange
        await using var harness = await AudioSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var calls = await IdentifyAllAsync(harness, EndingStreams, bound.Token);

        // Act
        await Task.WhenAll(calls.Select(c => c.Peer.SendAsync(AudioSocketFrames.Error(), bound.Token)));
        await Task.WhenAll(calls.Select(c => c.Recording.Completed)).WaitAsync(bound.Token);

        // Assert — a pin, green before and after: today's sequence already has each state once, its
        // Disconnected coming from the disposal; after the fix it comes from the pump, in the same step
        var sequences = calls.Select(c => string.Join(",", c.Recording.States)).ToList();
        sequences.Should().AllBe(
            "Connected,Error,Disconnected",
            $"an error frame publishes Error once and then Disconnected once, last; recorded: {Summarise(sequences)}");
        Dispose(calls);
    }

    [Theory]
    [InlineData(WebSocketEnding.CloseFrame)]
    [InlineData(WebSocketEnding.PeerAbort)]
    [InlineData(WebSocketEnding.ServerStop)]
    public async Task StateChanges_ShouldPublishDisconnectedOnceAndLast_WhenAWebSocketStreamEnds(WebSocketEnding ending)
    {
        // Arrange
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var calls = new List<(WebSocketTestPeer Peer, StateRecording Recording)>();
        for (var i = 0; i < EndingStreams; i++)
            calls.Add(await harness.ConnectAsync($"ending-{ending}-{i}-{Guid.NewGuid():N}", bound.Token));

        // Act
        switch (ending)
        {
            case WebSocketEnding.CloseFrame:
                await Task.WhenAll(calls.Select(c => c.Peer.SendCloseAsync()));
                break;
            case WebSocketEnding.PeerAbort:
                foreach (var call in calls)
                    call.Peer.Abort();
                break;
            case WebSocketEnding.ServerStop:
                await harness.Server.StopAsync().AsTask().WaitAsync(bound.Token);
                break;
        }

        await Task.WhenAll(calls.Select(c => c.Recording.Completed)).WaitAsync(bound.Token);

        // Assert
        var sequences = calls.Select(c => string.Join(",", c.Recording.States)).ToList();
        sequences.Should().AllBe(
            "Connected,Disconnected",
            $"a WebSocket stream ended by {ending} publishes Disconnected once, last, then completes; recorded: {Summarise(sequences)}");
        foreach (var call in calls)
            call.Peer.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_ShouldPublishNothingAndNotThrow_WhenAConsumerDisposesAnEndedAudioSocketStream()
    {
        // Arrange — each consumer disposes its stream as soon as it has seen the ending, racing the
        // handler's own disposal of the same session
        await using var harness = await AudioSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var calls = await IdentifyAllAsync(harness, EndingStreams, bound.Token);

        // Act
        await Task.WhenAll(calls.Select(c => c.Peer.SendAsync(AudioSocketFrames.Hangup(), bound.Token)));
        var disposals = calls.Select(async c =>
        {
            await c.Recording.Ended.WaitAsync(bound.Token);
            return await Record.ExceptionAsync(async () => await c.Recording.Stream.DisposeAsync());
        }).ToList();
        var thrown = await Task.WhenAll(disposals).WaitAsync(bound.Token);
        await Task.WhenAll(calls.Select(c => c.Recording.Completed)).WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            thrown.Where(ex => ex is not null).Select(ex => ex!.GetType().Name).Should().BeEmpty(
                "a disposal of a stream that has ended, by its consumer and by its server, throws nothing");
            var sequences = calls.Select(c => string.Join(",", c.Recording.States)).ToList();
            sequences.Should().AllBe(
                "Connected,Disconnected",
                $"no disposal after the ending publishes anything; recorded: {Summarise(sequences)}");
        }

        Dispose(calls);
    }

    [Fact]
    public async Task DisposeAsync_ShouldPublishNothingAndNotThrow_WhenAConsumerDisposesAnEndedWebSocketStream()
    {
        // Arrange
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var calls = new List<(WebSocketTestPeer Peer, StateRecording Recording)>();
        for (var i = 0; i < EndingStreams; i++)
            calls.Add(await harness.ConnectAsync($"dispose-{i}-{Guid.NewGuid():N}", bound.Token));

        // Act
        await Task.WhenAll(calls.Select(c => c.Peer.SendCloseAsync()));
        var thrown = await Task.WhenAll(calls.Select(async c =>
        {
            await c.Recording.Ended.WaitAsync(bound.Token);
            return await Record.ExceptionAsync(async () => await c.Recording.Stream.DisposeAsync());
        })).WaitAsync(bound.Token);
        await Task.WhenAll(calls.Select(c => c.Recording.Completed)).WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            thrown.Where(ex => ex is not null).Select(ex => ex!.GetType().Name).Should().BeEmpty();
            var sequences = calls.Select(c => string.Join(",", c.Recording.States)).ToList();
            sequences.Should().AllBe(
                "Connected,Disconnected",
                $"no disposal after the ending publishes anything; recorded: {Summarise(sequences)}");
        }

        foreach (var call in calls)
            call.Peer.Dispose();
    }

    // ------------------------------------------------------------- the server learns it (H29)

    [Fact]
    public async Task HandleConnection_ShouldReleaseEveryAudioSocketStream_WhenAConsumerObserverThrowsOnDisconnected()
    {
        var counts = await MeasureAudioSocketObserverCellAsync(throwOnDisconnected: true);

        using (new AssertionScope($"counts: {counts}"))
        {
            counts.Active.Should().Be(0, "the server releases a stream whatever a consumer observer throws");
            counts.Findable.Should().Be(0);
            counts.Held.Should().Be(0, "no place under MaxConcurrentStreams is kept by an ended stream");
            counts.ClosedByServer.Should().Be(ObserverStreams, "the server closes every ended stream's connection");
            counts.ObserverErrors.Should().Be(ObserverStreams, "each throwing observer is logged once at Error");
            counts.Unobserved.Should().Be(0, "no consumer exception surfaces as an unobserved task exception");
        }
    }

    [Fact]
    public async Task HandleConnection_ShouldReleaseEveryAudioSocketStream_WhenTheConsumerObserverDoesNotThrow()
    {
        // The control of the cell above, green before and after
        var counts = await MeasureAudioSocketObserverCellAsync(throwOnDisconnected: false);

        using (new AssertionScope($"counts: {counts}"))
        {
            counts.Active.Should().Be(0);
            counts.Findable.Should().Be(0);
            counts.Held.Should().Be(0);
            counts.ClosedByServer.Should().Be(ObserverStreams);
            counts.ObserverErrors.Should().Be(0);
            counts.Unobserved.Should().Be(0);
        }
    }

    [Fact]
    public async Task HandleConnection_ShouldReleaseEveryWebSocketStream_WhenAConsumerObserverThrowsOnDisconnected()
    {
        var counts = await MeasureWebSocketObserverCellAsync(throwOnDisconnected: true);

        using (new AssertionScope($"counts: {counts}"))
        {
            counts.Active.Should().Be(0);
            counts.Findable.Should().Be(0);
            counts.Held.Should().Be(0);
            counts.ObserverErrors.Should().Be(ObserverStreams);
            counts.Unobserved.Should().Be(0);
        }
    }

    [Fact]
    public async Task HandleConnection_ShouldReleaseEveryWebSocketStream_WhenTheConsumerObserverDoesNotThrow()
    {
        var counts = await MeasureWebSocketObserverCellAsync(throwOnDisconnected: false);

        using (new AssertionScope($"counts: {counts}"))
        {
            counts.Active.Should().Be(0);
            counts.Findable.Should().Be(0);
            counts.Held.Should().Be(0);
            counts.ObserverErrors.Should().Be(0);
            counts.Unobserved.Should().Be(0);
        }
    }

    [Fact]
    public async Task StateChanges_ShouldStillNotifyTheSecondObserver_WhenTheFirstThrowsOnDisconnected()
    {
        // Arrange — two consumer observers, the first throwing on Disconnected
        var token = Guid.NewGuid().ToString("N");
        using var faults = new UnobservedServerFaults(nameof(AudioSocketServer));
        var harness = await AudioSocketEndingHarness.StartAsync(recording =>
        {
            recording.Stream.StateChanges.Subscribe(state =>
            {
                if (state == AudioStreamState.Disconnected)
                    throw new InvalidOperationException($"first observer {token}");
            });
            recording.Subscribe();
        });
        int unobserved;
        string[] second;
        bool completed;
        bool released;
        int errors;
        try
        {
            using var bound = new CancellationTokenSource(SignalTimeout);
            var (peer, recording) = await harness.IdentifyAsync(Guid.NewGuid(), bound.Token);
            using (peer)
            {
                // Act
                await peer.SendAsync(AudioSocketFrames.Hangup(), bound.Token);
                completed = await Signalled(recording.Completed, bound.Token);
                released = await Signalled(peer.Closed, bound.Token);
                await Signalled(harness.Logger.WhenCount(e => e.Level == LogLevel.Error, 1), bound.Token);
                second = [.. recording.States.Select(s => s.ToString())];
                errors = harness.Logger.Entries.Count(e => e.Level == LogLevel.Error);
            }
        }
        finally
        {
            await harness.DisposeAsync().AsTask().WaitAsync(SignalTimeout);
        }

        UnobservedServerFaults.CollectDiscardedTasks();
        unobserved = faults.All.Count(f => f.Contains(token, StringComparison.Ordinal));

        // Assert
        using (new AssertionScope($"second={string.Join(",", second)} completed={completed} released={released} errors={errors} unobserved={unobserved}"))
        {
            second.Should().Equal(["Connected", "Disconnected"], "the second observer sees the ending once");
            completed.Should().BeTrue("and then the completion");
            released.Should().BeTrue("the server released and closed the stream");
            errors.Should().Be(1, "the first observer's throw is logged once at Error");
            unobserved.Should().Be(0);
        }
    }

    [Fact]
    public async Task OnStreamConnected_ShouldPropagateTheReplayThrowToTheHandler_WhenAnObserverThrowsOnEveryState()
    {
        // A pin, green before and after: the throw on the state replayed inside Subscribe reaches the
        // subscriber's caller, and the server reports the failed announcement as a connection error
        var cell = await MeasureReplayThrowCellAsync();

        using (new AssertionScope($"cell: {cell}"))
        {
            cell.SubscribeThrewIntoHandler.Should().Be(ObserverStreams);
            cell.SecondSubscribed.Should().Be(0, "the handler did not get past the throwing subscription");
            cell.ClosedByServer.Should().Be(ObserverStreams);
            cell.ConnectionErrors.Should().Be(ObserverStreams, "counted by event name, not by level");
        }
    }

    [Fact]
    public async Task StateChanges_ShouldNotifyNothingMore_WhenTheObserversSubscriptionCallThrew()
    {
        var cell = await MeasureReplayThrowCellAsync();

        using (new AssertionScope($"cell: {cell}"))
        {
            cell.ExtraCalls.Should().Be(0, "an observer whose subscription call threw is no longer subscribed");
            cell.Unobserved.Should().Be(0, "so the disposal's ending cannot throw out of it into a discarded task");
            cell.OtherErrors.Should().Be(0, "no Error line besides the connection errors");
        }
    }

    [Fact]
    public async Task AcceptLoop_ShouldAdmitAThirdPeer_WhenTwoStreamsWhoseObserversThrowHaveHungUp()
    {
        // Arrange — a limit of two, filled by two calls whose consumer observers throw on Disconnected
        await using var harness = await AudioSocketEndingHarness.StartAsync(
            recording =>
            {
                recording.Subscribe();
                recording.Stream.StateChanges.Subscribe(state =>
                {
                    if (state == AudioStreamState.Disconnected)
                        throw new InvalidOperationException("consumer observer");
                });
            },
            maxStreams: 2);
        using var bound = new CancellationTokenSource(SignalTimeout);
        var calls = await IdentifyAllAsync(harness, 2, bound.Token);
        await Task.WhenAll(calls.Select(c => c.Peer.SendAsync(AudioSocketFrames.Hangup(), bound.Token)));

        // Act — a third peer identifies itself
        var third = Guid.NewGuid();
        var admitted = await Signalled(harness.IdentifyUntilAdmittedAsync(third, bound.Token), bound.Token);

        // Assert
        admitted.Should().BeTrue(
            $"the two hung-up streams gave their places back, so the third is announced (held now: {harness.Options.Admission.Held})");
        Dispose(calls);
    }

    // ---------------------------------------------------------------------------------- cells

    private sealed record ObserverCellCounts(int Active, int Findable, int Held, int ClosedByServer, int ObserverErrors, int Unobserved);

    private sealed record ReplayThrowCell(
        int SubscribeThrewIntoHandler,
        int SecondSubscribed,
        int ClosedByServer,
        int ConnectionErrors,
        int OtherErrors,
        int ExtraCalls,
        int Unobserved);

    /// <summary>
    /// 10 AudioSocket streams under a limit of 10, each with a sentinel recording and then a consumer
    /// observer that throws on Disconnected (or not, for the control); each far end hangs up. The
    /// unobserved count is read after the server's DisposeAsync and a forced collection: read after the
    /// hangup alone it is 0 on the defective code too.
    /// </summary>
    private static async Task<ObserverCellCounts> MeasureAudioSocketObserverCellAsync(bool throwOnDisconnected)
    {
        var token = Guid.NewGuid().ToString("N");
        using var faults = new UnobservedServerFaults(nameof(AudioSocketServer));
        var harness = await AudioSocketEndingHarness.StartAsync(
            recording =>
            {
                recording.Subscribe();
                recording.Stream.StateChanges.Subscribe(state =>
                {
                    if (throwOnDisconnected && state == AudioStreamState.Disconnected)
                        throw new InvalidOperationException($"consumer observer {token}");
                });
            },
            maxStreams: ObserverStreams);
        List<(AudioSocketTestPeer Peer, StateRecording Recording)> calls = [];
        int active, findable, held, closed, errors;
        try
        {
            using var bound = new CancellationTokenSource(SignalTimeout);
            calls = await IdentifyAllAsync(harness, ObserverStreams, bound.Token);
            var ids = calls.Select(c => c.Recording.Stream.ChannelId).ToList();

            await Task.WhenAll(calls.Select(c => c.Peer.SendAsync(AudioSocketFrames.Hangup(), bound.Token)));
            var allClosed = await Signalled(Task.WhenAll(calls.Select(c => c.Peer.Closed)), bound.Token);
            if (throwOnDisconnected)
                await Signalled(harness.Logger.WhenCount(e => e.Level == LogLevel.Error, ObserverStreams), bound.Token);

            closed = calls.Count(c => c.Peer.Closed.IsCompleted);
            active = harness.Server.ActiveStreamCount;
            findable = ids.Count(id => harness.Server.GetStream(id) is not null);
            errors = harness.Logger.Entries.Count(e => e.Level == LogLevel.Error);
            held = await PlacesHeldAsync(allClosed, harness);
        }
        finally
        {
            await harness.DisposeAsync().AsTask().WaitAsync(SignalTimeout);
        }

        // The stuck connections of the defective code end at the stop; their handlers' tasks are the
        // ones whose exception goes unobserved, so the read waits for every connection to be closed
        using (var afterStop = new CancellationTokenSource(SignalTimeout))
            await Signalled(Task.WhenAll(calls.Select(c => c.Peer.Closed)), afterStop.Token);
        Dispose(calls);
        UnobservedServerFaults.CollectDiscardedTasks();
        var unobserved = faults.All.Count(f => f.Contains(token, StringComparison.Ordinal));
        return new ObserverCellCounts(active, findable, held, closed, errors, unobserved);
    }

    /// <summary>
    /// The places still held by the cell's ten streams. When every connection was closed, it is proven
    /// by admission: under the limit of ten, ten new peers must each be announced, which they can only
    /// if no ended stream still holds a place; the count is ten minus the probes admitted. One probe
    /// would prove only that one place came back, and the read after it would race the last releases.
    /// When the connections were not closed, nothing is racing the read any more and the count is read
    /// as it stands.
    /// </summary>
    private static async Task<int> PlacesHeldAsync(bool allClosed, AudioSocketEndingHarness harness)
    {
        if (!allClosed)
            return harness.Options.Admission.Held;

        using var probeBound = new CancellationTokenSource(SignalTimeout);
        using var probes = new DisposableSet();
        for (var i = 0; i < ObserverStreams; i++)
        {
            var probe = harness.IdentifyUntilAdmittedAsync(Guid.NewGuid(), probeBound.Token);
            if (!await Signalled(probe, probeBound.Token))
                break;
            probes.Add((await probe).Peer);
        }

        return ObserverStreams - probes.Count;
    }

    private static async Task<ObserverCellCounts> MeasureWebSocketObserverCellAsync(bool throwOnDisconnected)
    {
        var token = Guid.NewGuid().ToString("N");
        using var faults = new UnobservedServerFaults(nameof(WebSocketAudioServer));
        var harness = await WebSocketEndingHarness.StartAsync(
            recording =>
            {
                recording.Subscribe();
                recording.Stream.StateChanges.Subscribe(state =>
                {
                    if (throwOnDisconnected && state == AudioStreamState.Disconnected)
                        throw new InvalidOperationException($"consumer observer {token}");
                });
            },
            maxStreams: ObserverStreams);
        var calls = new List<(WebSocketTestPeer Peer, StateRecording Recording)>();
        int active, findable, held, released, errors;
        try
        {
            using var bound = new CancellationTokenSource(SignalTimeout);
            for (var i = 0; i < ObserverStreams; i++)
                calls.Add(await harness.ConnectAsync($"observer-{i}-{token}", bound.Token));
            var ids = calls.Select(c => c.Recording.Stream.ChannelId).ToList();

            await Task.WhenAll(calls.Select(c => c.Peer.SendCloseAsync()));

            // The handler disposes the session after its release, and the disposal completes the state
            // sequence: the sentinel recording's completion is the release's signal
            var allReleased = await Signalled(Task.WhenAll(calls.Select(c => c.Recording.Completed)), bound.Token);
            if (throwOnDisconnected)
                await Signalled(harness.Logger.WhenCount(e => e.Level == LogLevel.Error, ObserverStreams), bound.Token);

            released = calls.Count(c => c.Recording.Completed.IsCompleted);
            active = harness.Server.ActiveStreamCount;
            findable = ids.Count(id => harness.Server.GetStream(id) is not null);
            errors = harness.Logger.Entries.Count(e => e.Level == LogLevel.Error);
            if (allReleased)
            {
                // Proven by admission, as for the AudioSocket cell: ten new connections under the limit
                // of ten must each be announced
                using var probeBound = new CancellationTokenSource(SignalTimeout);
                using var probes = new DisposableSet();
                for (var i = 0; i < ObserverStreams; i++)
                {
                    var probe = harness.ConnectUntilAdmittedAsync($"probe-{i}-{token}", probeBound.Token);
                    if (!await Signalled(probe, probeBound.Token))
                        break;
                    probes.Add((await probe).Peer);
                }

                held = ObserverStreams - probes.Count;
            }
            else
            {
                held = harness.Options.Admission.Held;
            }
        }
        finally
        {
            await harness.DisposeAsync().AsTask().WaitAsync(SignalTimeout);
        }

        foreach (var call in calls)
            call.Peer.Dispose();
        UnobservedServerFaults.CollectDiscardedTasks();
        var unobserved = faults.All.Count(f => f.Contains(token, StringComparison.Ordinal));
        return new ObserverCellCounts(active, findable, held, released, errors, unobserved);
    }

    /// <summary>
    /// 10 AudioSocket streams, each announced to a handler that subscribes an observer throwing on every
    /// state, then a second observer. Every count is read after the connections were closed by the
    /// server and the server was disposed, and the unobserved count after a forced collection.
    /// </summary>
    private static async Task<ReplayThrowCell> MeasureReplayThrowCellAsync()
    {
        var token = Guid.NewGuid().ToString("N");
        var threwIntoHandler = 0;
        var secondSubscribed = 0;
        var calls = 0;
        using var faults = new UnobservedServerFaults(nameof(AudioSocketServer));
        var harness = await AudioSocketEndingHarness.StartAsync(recording =>
        {
            try
            {
                recording.Stream.StateChanges.Subscribe(_ =>
                {
                    Interlocked.Increment(ref calls);
                    throw new InvalidOperationException($"replay observer {token}");
                });
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref threwIntoHandler);
                throw;
            }

            recording.Subscribe();
            Interlocked.Increment(ref secondSubscribed);
        });
        var peers = new List<AudioSocketTestPeer>();
        int closed, connectionErrors, otherErrors;
        try
        {
            using var bound = new CancellationTokenSource(SignalTimeout);
            for (var i = 0; i < ObserverStreams; i++)
                peers.Add((await harness.IdentifyAsync(Guid.NewGuid(), bound.Token)).Peer);

            await Signalled(Task.WhenAll(peers.Select(p => p.Closed)), bound.Token);
            await Signalled(harness.Logger.WhenCount(e => e.EventName == "ConnectionError", ObserverStreams), bound.Token);
        }
        finally
        {
            await harness.DisposeAsync().AsTask().WaitAsync(SignalTimeout);
        }

        closed = peers.Count(p => p.Closed.IsCompleted);
        connectionErrors = harness.Logger.Entries.Count(e => e.EventName == "ConnectionError");
        otherErrors = harness.Logger.Entries.Count(e => e.Level >= LogLevel.Error && e.EventName != "ConnectionError");
        foreach (var peer in peers)
            peer.Dispose();
        UnobservedServerFaults.CollectDiscardedTasks();
        var unobserved = faults.All.Count(f => f.Contains(token, StringComparison.Ordinal));
        return new ReplayThrowCell(
            threwIntoHandler,
            secondSubscribed,
            closed,
            connectionErrors,
            otherErrors,
            Volatile.Read(ref calls) - ObserverStreams,
            unobserved);
    }

    // -------------------------------------------------------------------------------- helpers

    private static async Task<List<(AudioSocketTestPeer Peer, StateRecording Recording)>> IdentifyAllAsync(
        AudioSocketEndingHarness harness, int count, CancellationToken token)
    {
        var calls = new List<(AudioSocketTestPeer Peer, StateRecording Recording)>();
        for (var i = 0; i < count; i++)
            calls.Add(await harness.IdentifyAsync(Guid.NewGuid(), token));
        return calls;
    }

    /// <summary>Whether <paramref name="task"/> completed before <paramref name="token"/>; never throws for the bound.</summary>
    private static async Task<bool> Signalled(Task task, CancellationToken token)
    {
        try
        {
            await task.WaitAsync(token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string Summarise(IEnumerable<string> sequences) =>
        string.Join("; ", sequences.GroupBy(s => s).Select(g => $"{g.Count()}× [{g.Key}]"));

    private static void Dispose(IEnumerable<(AudioSocketTestPeer Peer, StateRecording Recording)> calls)
    {
        foreach (var call in calls)
            call.Peer.Dispose();
    }
}
