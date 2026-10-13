using System.Collections.Concurrent;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Ari.Audio;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// Which identification frame an ARI AudioSocket stream keeps, and when the server's wait for the
/// first one ends. Real server on a loopback port; the deadline cells run on a fake clock.
/// </summary>
public sealed class AudioSocketIdentificationTests
{
    /// <summary>Upper bound on any wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The deadline of the fake-clock cells: short enough that the system timer would fire inside the real-time window.</summary>
    private static readonly TimeSpan ShortIdleTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>Real time a silent connection must survive while the fake clock stands still: well past <see cref="ShortIdleTimeout"/>.</summary>
    private static readonly TimeSpan RealTimeWindow = TimeSpan.FromSeconds(1);

    // ------------------------------------------------------------------ the first id wins

    [Fact]
    public async Task ReadPump_ShouldKeepTheFirstId_WhenASecondIdentificationFrameArrivesMidCall()
    {
        // Arrange — identified as X, recording
        await using var harness = await AudioSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();
        var (peer, recording) = await harness.IdentifyAsync(x, bound.Token);
        using var _ = peer;

        // Act — a second id, three audio frames, read by the consumer (the pump processed the second
        // id before the frames behind it), then a hangup
        await peer.SendAsync(AudioSocketFrames.Uuid(y), bound.Token);
        await peer.SendAsync([.. AudioSocketFrames.Audio(1), .. AudioSocketFrames.Audio(2), .. AudioSocketFrames.Audio(3)], bound.Token);
        var read = new List<byte>();
        for (var i = 0; i < 3; i++)
            read.Add((await recording.Stream.ReadFrameAsync(bound.Token)).Span[0]);
        var channelId = recording.Stream.ChannelId;
        var foundByX = harness.Server.GetStream(x.ToString());
        var foundByY = harness.Server.GetStream(y.ToString());
        await peer.SendAsync(AudioSocketFrames.Hangup(), bound.Token);
        await recording.Completed.WaitAsync(bound.Token);
        var warnings = harness.Logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();

        // Assert
        using (new AssertionScope())
        {
            channelId.Should().Be(x.ToString(), "a stream keeps the id of its first identification frame");
            foundByX.Should().BeSameAs(recording.Stream);
            foundByY.Should().BeNull();
            read.Should().Equal([1, 2, 3], "the stream goes on delivering audio");
            recording.States.Should().Equal(
                [AudioStreamState.Connected, AudioStreamState.Disconnected],
                "a later identification frame does not publish Connected again");
            warnings.Should().ContainSingle("the first ignored frame of a stream writes one Warning");
            warnings.Should().OnlyContain(w => w.Message.Contains(x.ToString(), StringComparison.Ordinal) && w.Message.Contains(y.ToString(), StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ReadPump_ShouldLeaveTheOtherCallAlone_WhenASecondIdNamesAnotherLiveCall()
    {
        // Arrange — a live stream under Y, and a second stream under X
        await using var harness = await AudioSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();
        var (holderPeer, holder) = await harness.IdentifyAsync(y, bound.Token);
        var (peer, second) = await harness.IdentifyAsync(x, bound.Token);
        using var holderConnection = holderPeer;
        using var connection = peer;

        // Act — the second stream's far end names Y, then one audio frame the consumer reads
        await peer.SendAsync([.. AudioSocketFrames.Uuid(y), .. AudioSocketFrames.Audio(7)], bound.Token);
        await second.Stream.ReadFrameAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            harness.Server.ActiveStreams.Count(s => s.ChannelId == y.ToString()).Should().Be(1, "exactly one live stream reports Y");
            harness.Server.GetStream(y.ToString()).Should().BeSameAs(holder.Stream, "the server still finds the first stream by Y");
            second.Stream.ChannelId.Should().Be(x.ToString());
        }
    }

    [Fact]
    public async Task ReadPump_ShouldWriteOneWarningAndPublishNothing_WhenTheSameIdIsRepeatedTwice()
    {
        // Arrange
        await using var harness = await AudioSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var x = Guid.NewGuid();
        var (peer, recording) = await harness.IdentifyAsync(x, bound.Token);
        using var _ = peer;

        // Act
        await peer.SendAsync([.. AudioSocketFrames.Uuid(x), .. AudioSocketFrames.Uuid(x), .. AudioSocketFrames.Audio(9)], bound.Token);
        var frame = await recording.Stream.ReadFrameAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            frame.Span[0].Should().Be(9, "the stream goes on");
            recording.States.Should().Equal([AudioStreamState.Connected], "Connected is not published again");
            harness.Logger.Entries.Count(e => e.Level == LogLevel.Warning).Should().Be(1, "one Warning per stream, however many repeats");
        }
    }

    [Fact]
    public async Task HandleConnection_ShouldRegisterUnderTheFirstId_WhenTwoIdsArriveInOneWrite()
    {
        // Arrange — 200 peers, each writing X then Y in a single write
        const int peers = 200;
        var announced = new ConcurrentQueue<string>();
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await AudioSocketEndingHarness.StartAsync(recording =>
        {
            announced.Enqueue(recording.Stream.ChannelId);
            if (announced.Count == peers)
                all.TrySetResult();
        });
        using var bound = new CancellationTokenSource(SignalTimeout);
        var pairs = Enumerable.Range(0, peers).Select(_ => (X: Guid.NewGuid(), Y: Guid.NewGuid())).ToList();

        // Act
        using var connected = new DisposableSet();
        foreach (var peer in await Task.WhenAll(pairs.Select(async pair =>
        {
            var peer = await AudioSocketTestPeer.ConnectAsync(harness.Port, bound.Token);
            await peer.SendAsync([.. AudioSocketFrames.Uuid(pair.X), .. AudioSocketFrames.Uuid(pair.Y)], bound.Token);
            return peer;
        })))
        {
            connected.Add(peer);
        }

        await all.Task.WaitAsync(bound.Token);

        // Assert
        var xs = pairs.Select(p => p.X.ToString()).ToHashSet(StringComparer.Ordinal);
        using (new AssertionScope())
        {
            announced.Count(xs.Contains).Should().Be(peers, "every stream is announced with its first id");
            pairs.Count(p => harness.Server.GetStream(p.X.ToString()) is not null).Should().Be(peers, "and found by it");
            pairs.Count(p => harness.Server.GetStream(p.Y.ToString()) is not null).Should().Be(0, "and none by the second");
        }
    }

    // -------------------------------------------------------- the wait ends with the connection

    [Fact]
    public async Task IdentificationWait_ShouldKeepASilentConnectionOpen_WhileTheFakeClockStandsStill()
    {
        // Arrange — a 300 ms deadline on a clock that never moves
        var time = new FakeTimeProvider();
        await using var harness = await AudioSocketEndingHarness.StartAsync(
            _ => { }, maxStreams: 1, idleTimeout: ShortIdleTimeout, timeProvider: time);
        using var bound = new CancellationTokenSource(SignalTimeout);

        // Act — a peer that connects and says nothing, given well over 300 ms of real time
        using var peer = await AudioSocketTestPeer.ConnectAsync(harness.Port, bound.Token);
        var closedInWindow = await ClosedWithin(peer, RealTimeWindow);

        // Assert
        using (new AssertionScope())
        {
            closedInWindow.Should().BeFalse("the deadline is measured on the injected clock, which has not moved");
            harness.Options.Admission.Held.Should().Be(1, "so the connection still holds its place");
        }
    }

    [Fact]
    public async Task IdentificationWait_ShouldCloseTheSilentConnection_WhenTheFakeClockPassesTheDeadline()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var announcements = 0;
        await using var harness = await AudioSocketEndingHarness.StartAsync(
            _ => Interlocked.Increment(ref announcements), maxStreams: 1, idleTimeout: ShortIdleTimeout, timeProvider: time);
        using var bound = new CancellationTokenSource(SignalTimeout);
        using var peer = await AudioSocketTestPeer.ConnectAsync(harness.Port, bound.Token);
        var deadline = await NextTimerAsync(time, bound.Token);

        // Act
        time.Advance(ShortIdleTimeout + TimeSpan.FromMilliseconds(1));
        await peer.Closed.WaitAsync(bound.Token);
        var (probe, _) = await harness.IdentifyUntilAdmittedAsync(Guid.NewGuid(), bound.Token);
        using var admitted = probe;

        // Assert
        using (new AssertionScope())
        {
            deadline.DueTime.Should().Be(ShortIdleTimeout, "the wait asked the injected clock for IdleTimeout");
            Volatile.Read(ref announcements).Should().Be(1, "the silent connection was never announced; only the probe was");
            harness.Logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        }
    }

    [Fact]
    public async Task IdentificationWait_ShouldServeTheFrame_WhenItArrivesJustBeforeTheDeadline()
    {
        // Arrange
        var time = new FakeTimeProvider();
        await using var harness = await AudioSocketEndingHarness.StartAsync(
            _ => { }, idleTimeout: ShortIdleTimeout, timeProvider: time);
        using var bound = new CancellationTokenSource(SignalTimeout);
        using var peer = await AudioSocketTestPeer.ConnectAsync(harness.Port, bound.Token);
        await NextTimerAsync(time, bound.Token);
        var id = Guid.NewGuid();
        var announced = harness.AnnouncedAs(id.ToString());

        // Act
        time.Advance(ShortIdleTimeout - TimeSpan.FromMilliseconds(1));
        await peer.SendAsync(AudioSocketFrames.Uuid(id), bound.Token);

        // Assert
        var recording = await announced.WaitAsync(bound.Token);
        recording.Stream.ChannelId.Should().Be(id.ToString(), "a frame inside the deadline is served");
    }

    [Fact]
    public async Task IdentificationWait_ShouldGiveThePlaceBackAtOnce_WhenPeersLeaveBeforeIdentifying()
    {
        // Arrange — a limit of 10 and a 30 s deadline on a clock that does not move, so the deadline
        // can never be what ends a wait here
        var time = new FakeTimeProvider();
        await using var harness = await AudioSocketEndingHarness.StartAsync(
            _ => { }, maxStreams: 10, idleTimeout: TimeSpan.FromSeconds(30), timeProvider: time);
        using var bound = new CancellationTokenSource(SignalTimeout);
        var closedByServer = 0;
        var placesBack = false;
        var admittedAtFirstAttempt = false;

        // Act — 10 peers connect and leave without identifying: each closes its sending half and reads
        // the server's close, which proves the server accepted it and ended its wait. Then the count
        // must come back to zero, and an 11th peer that identifies itself is admitted at its one attempt.
        try
        {
            for (var i = 0; i < 10; i++)
            {
                using var leaver = await AudioSocketTestPeer.ConnectAsync(harness.Port, bound.Token);
                leaver.ShutdownSend();
                await leaver.Closed.WaitAsync(bound.Token);
                closedByServer++;
            }

            placesBack = await HeldReachesAsync(harness.Options.Admission, 0, bound.Token);
            admittedAtFirstAttempt = await IdentifyOnceAsync(harness, Guid.NewGuid(), bound.Token);
        }
        catch (OperationCanceledException) when (bound.IsCancellationRequested)
        {
            // Reaching the bound is the failure the assertions below describe
        }

        // Assert
        using (new AssertionScope())
        {
            closedByServer.Should().Be(10, "a peer that leaves before identifying is closed when its read ends, not at a deadline the clock never reaches");
            placesBack.Should().BeTrue(
                $"each of them gives its place back at once (held at the bound: {harness.Options.Admission.Held})");
            admittedAtFirstAttempt.Should().BeTrue("with every place back, the next peer is admitted at its first attempt");
            harness.Logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning, "no connection was refused and none failed");
        }
    }

    // -------------------------------------------------------------------------------- helpers

    private static async Task<bool> ClosedWithin(AudioSocketTestPeer peer, TimeSpan window)
    {
        try
        {
            await peer.Closed.WaitAsync(window);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits until <paramref name="admission"/> holds <paramref name="value"/> places, or the bound passes.
    /// The place goes back in the handler's last step, after the peer has already read the close, and
    /// nothing signals it, so the wait reads the count itself. Nothing in the server's own work is
    /// timed by it: it only ends when the count is the one asked for.
    /// </summary>
    private static async Task<bool> HeldReachesAsync(AudioStreamAdmission admission, int value, CancellationToken token)
    {
        while (admission.Held != value)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), token); // fence-allow: LOOP-DRIVER — paces reading a count nothing signals; the bound ends the loop
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return admission.Held == value;
            }
        }

        return true;
    }

    /// <summary>One attempt to identify: true when the server announces it, false when it closes the connection instead.</summary>
    private static async Task<bool> IdentifyOnceAsync(AudioSocketEndingHarness harness, Guid id, CancellationToken token)
    {
        var announced = harness.AnnouncedAs(id.ToString());
        using var peer = await AudioSocketTestPeer.ConnectAsync(harness.Port, token);
        try
        {
            await peer.SendAsync(AudioSocketFrames.Uuid(id), token);
        }
        catch (IOException)
        {
            return false;
        }

        return await Task.WhenAny(announced, peer.Closed).WaitAsync(token) == announced;
    }

    private static Task<FakeTimeProvider.FakeTimer> NextTimerAsync(FakeTimeProvider time, CancellationToken token) =>
        time.TimersCreated.ReadAsync(token).AsTask();
}
