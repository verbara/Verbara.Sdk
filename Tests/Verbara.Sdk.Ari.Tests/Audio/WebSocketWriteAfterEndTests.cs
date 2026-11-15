using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Tests.TestSupport;
using Verbara.Sdk.TestInfrastructure.Wire;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// A write to a WebSocket audio stream that has already ended. Every audio write and every command that finds the
/// stream ended throws <see cref="ObjectDisposedException"/>, whichever step of the ending it meets: the peer's close
/// received, the answer sent, the transport failed, or the server's disposal done; a write waiting for the send when the
/// stream is disposed finds it disposed too, and never hangs. A transport failure that cuts short a write already under
/// way keeps its own type; a disposal that cuts one short ends it with <see cref="ObjectDisposedException"/>. A token
/// already cancelled is read before the ending on a stream that has ended, and after the disposal on a disposed one.
/// Real sessions over real loopback sockets, each take counted by the outcome it ended on, so a failure prints the
/// census of the run.
/// </summary>
/// <remarks>
/// Several tests reset connections, which moves the process-wide <c>audio.transport.failures</c>: the class runs in
/// <see cref="AudioStreamMetricsGroup"/>, alone.
/// </remarks>
[Collection(AudioStreamMetricsGroup.Name)]
public sealed class WebSocketWriteAfterEndTests
{
    /// <summary>Upper bound on any wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Takes per racing test: enough that a mixed outcome cannot pass by luck.</summary>
    private const int Takes = 50;

    /// <summary>Takes of the writer racing a reset: the window it probes is a few instructions wide, and a take costs milliseconds.</summary>
    private const int ResetRaceTakes = 200;

    /// <summary>Takes of the cut-short write, each of which fills the connection first.</summary>
    private const int CutShortTakes = 20;

    /// <summary>Takes of the write queued behind a blocked one, each of which fills the connection first.</summary>
    private const int QueuedTakes = 5;

    /// <summary>How long the queued-write test lets the read loop take the peer's close: a tenth of the close bound.</summary>
    private static readonly TimeSpan LateAnswer = TimeSpan.FromMilliseconds(100);

    /// <summary>How long after its send failed the scripted socket delivers the peer's close: a fifth of the write's wait for the ending.</summary>
    private static readonly TimeSpan LateClose = TimeSpan.FromMilliseconds(10);

    private const string Disposed = nameof(ObjectDisposedException);

    private static readonly byte[] Frame = new byte[640];

    // ============================================================ a write after the ending

    [Fact]
    public async Task WriteFrameAsync_ShouldThrowObjectDisposed_WhenCalledRightAfterThePeersCloseEndedTheStream()
    {
        // Arrange — the real server, which disposes each stream once it ended, racing the consumer's write
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        var outcomes = new List<string>();

        for (var take = 0; take < Takes; take++)
        {
            using var bound = new CancellationTokenSource(SignalTimeout);
            var (peer, recording) = await harness.ConnectRawAsync($"after-{take}-{Guid.NewGuid():N}", bound.Token);
            using var _ = peer;

            // Act — the peer hangs up; the consumer writes as soon as it sees Disconnected
            await peer.SendCloseAsync();
            await recording.Ended.WaitAsync(bound.Token);
            outcomes.Add(await OutcomeAsync(() => recording.Stream.WriteFrameAsync(Frame)));
        }

        // Assert
        Census(outcomes).Should().Be(AllDisposed(outcomes), "{0}; the takes ended on: {1}", "a write that finds the stream ended throws ObjectDisposedException, whether or not the server has disposed it yet", Census(outcomes));
    }

    [Fact]
    public async Task WriteFrameAsync_ShouldEndOnObjectDisposed_WhenABackToBackWriterRacesThePeersClose()
    {
        // Arrange — a writer that never pauses, and a peer that closes after a different number of frames each take
        var outcomes = new List<string>();

        for (var take = 0; take < Takes; take++)
        {
            using var bound = new CancellationTokenSource(SignalTimeout);
            await using var pair = await LoopbackSession.StartAsync(bound.Token);
            var closeAfter = 1 + (take % 10);
            var received = pair.PeerReceived(closeAfter);
            var writer = WriteUntilThrowAsync(pair.Session, bound.Token);

            // Act
            await received.WaitAsync(bound.Token);
            await pair.CloseFromPeerAsync(bound.Token);
            outcomes.Add(await writer.WaitAsync(bound.Token));
            await pair.Ended.WaitAsync(bound.Token);
        }

        // Assert
        Census(outcomes).Should().Be(AllDisposed(outcomes), "{0}; the takes ended on: {1}", "the writer's first failed write is the one that found the stream ended, so it throws ObjectDisposedException, never the transport's type", Census(outcomes));
    }

    [Fact]
    public async Task WriteFrameAsync_ShouldThrowObjectDisposed_WhenAResetEndedTheStream()
    {
        // Arrange — a session nobody disposes, so its socket stays aborted after the failure
        var outcomes = new List<string>();

        for (var take = 0; take < 10; take++)
        {
            using var bound = new CancellationTokenSource(SignalTimeout);
            await using var pair = await LoopbackSession.StartAsync(bound.Token);

            // Act — the peer resets; the consumer writes once it sees the ending
            pair.ResetFromPeer();
            await pair.Ended.WaitAsync(bound.Token);
            outcomes.Add(await OutcomeAsync(() => pair.Session.WriteFrameAsync(Frame)));
        }

        // Assert
        Census(outcomes).Should().Be(AllDisposed(outcomes), "{0}; the takes ended on: {1}", "the stream had ended before the write, so the write throws ObjectDisposedException", Census(outcomes));
    }

    [Fact]
    public async Task WriteFrameAsync_ShouldThrowObjectDisposed_WhenItWaitedBehindABlockedWriteWhileThePeerClosed()
    {
        // Arrange — the peer stops reading, so one write blocks and holds the send; a second write waits behind it
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        var outcomes = new List<string>();

        for (var take = 0; take < QueuedTakes; take++)
        {
            using var bound = new CancellationTokenSource(SignalTimeout);
            var (peer, recording) = await harness.ConnectRawAsync($"queued-{take}-{Guid.NewGuid():N}", bound.Token, readPaused: true);
            using var _ = peer;
            var blocked = await WriteUntilOneBlocksAsync(recording.Stream, bound.Token);
            var queued = OutcomeAsync(() => recording.Stream.WriteFrameAsync(Frame));

            // Act — the peer closes while both writes wait; once it reads again the blocked write goes out, and the
            // queued one takes the send after the peer's close was received
            await peer.SendCloseAsync();
            await Task.Delay(LateAnswer, bound.Token); // fence-allow: SETTLE — lets the read loop take the peer's close, so the queued write gets the send on a stream that has ended
            peer.ResumeReading();
            await blocked.WaitAsync(bound.Token);
            outcomes.Add(await queued.WaitAsync(bound.Token));
            await recording.Ended.WaitAsync(bound.Token);
        }

        // Assert
        Census(outcomes).Should().Be(AllDisposed(outcomes), "{0}; the takes ended on: {1}", "the ending is read when the write takes the send, not when it was asked for, so a write that gets the send after the peer's close throws ObjectDisposedException", Census(outcomes));
    }

    [Theory]
    [InlineData(ChanWebSocketCaptureFormat.PlainText)]
    [InlineData(ChanWebSocketCaptureFormat.Json)]
    public async Task SendCommands_ShouldThrowObjectDisposed_WhenThePeersCloseEndedTheStream(ChanWebSocketCaptureFormat format)
    {
        // Arrange — a stream whose MEDIA_START was read, so every command has a format to be written in
        var commands = CommandsFor(format);
        var outcomes = new List<string>();

        for (var take = 0; take < 10; take++)
        {
            using var bound = new CancellationTokenSource(SignalTimeout);
            await using var pair = await LoopbackSession.StartAsync(bound.Token);
            await pair.NegotiateAsync(format, bound.Token);

            // Act
            await pair.CloseFromPeerAsync(bound.Token);
            await pair.Ended.WaitAsync(bound.Token);
            foreach (var (name, send) in commands)
                outcomes.Add($"{name}:{await OutcomeAsync(() => send(pair.Session))}");
        }

        // Assert
        using (new AssertionScope())
        {
            foreach (var (name, _) in commands)
            {
                List<string> calls = [.. outcomes.Where(o => o.StartsWith(name + ":", StringComparison.Ordinal)).Select(o => o[(name.Length + 1)..])];
                Census(calls).Should().Be(AllDisposed(calls), "{0} finds the stream ended and throws ObjectDisposedException; the takes ended on: {1}", name, Census(calls));
            }
        }
    }

    [Fact]
    public async Task WriteFrameAsync_ShouldEndOnObjectDisposed_WhenABackToBackWriterRacesTheDisposal()
    {
        // Arrange — a writer that never pauses, and a disposal after a different number of frames each take
        var outcomes = new List<string>();

        for (var take = 0; take < Takes; take++)
        {
            using var bound = new CancellationTokenSource(SignalTimeout);
            await using var pair = await LoopbackSession.StartAsync(bound.Token);
            var received = pair.PeerReceived(1 + (take % 10));
            var writer = WriteUntilThrowAsync(pair.Session, bound.Token);

            // Act
            await received.WaitAsync(bound.Token);
            await pair.Session.DisposeAsync().AsTask().WaitAsync(bound.Token);
            outcomes.Add(await writer.WaitAsync(bound.Token));
        }

        // Assert
        Census(outcomes).Should().Be(AllDisposed(outcomes), "{0}; the takes ended on: {1}", "the disposal's close goes out under the send lock, so the writer's first failed write found the stream disposed and throws ObjectDisposedException, never the transport's type", Census(outcomes));
    }

    [Fact]
    public async Task WriteFrameAsync_ShouldNeverThrowInvalidState_WhenABackToBackWriterRacesAReset()
    {
        // Arrange — a writer that never pauses, and a peer that resets after a different number of frames each take
        var outcomes = new List<string>();

        for (var take = 0; take < ResetRaceTakes; take++)
        {
            using var bound = new CancellationTokenSource(SignalTimeout);
            await using var pair = await LoopbackSession.StartAsync(bound.Token);
            var received = pair.PeerReceived(1 + (take % 7));
            var writer = WriteUntilThrowAsync(pair.Session, bound.Token);

            // Act — the reset aborts the socket from the read side, which takes no send lock: it can land between a
            // write's ending check and its send
            await received.WaitAsync(bound.Token);
            pair.ResetFromPeer();
            outcomes.Add(await writer.WaitAsync(bound.Token));
        }

        // Assert — a write that finds the stream ended throws ObjectDisposedException; one under way when the transport
        // failed keeps the transport's own type; a write that wrote nothing never reports the transport's InvalidState
        var census = Census(outcomes);
        outcomes.Should().OnlyContain(
            o => o == Disposed || o == nameof(OperationCanceledException) || o == $"{nameof(WebSocketException)}:{WebSocketError.ConnectionClosedPrematurely}",
            "a write the reset left with nothing sent finds the stream ended and throws ObjectDisposedException, never WebSocketException:InvalidState; the takes ended on: {0}", census);
    }

    [Fact]
    public async Task WriteFrameAsync_ShouldThrowObjectDisposedRatherThanHang_WhenItWaitedBehindABlockedWriteWhileTheStreamWasDisposed()
    {
        // Arrange — the peer stops reading, so one write blocks and holds the send past the disposal's close bound; a
        // second write waits behind it
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        var outcomes = new List<string>();

        for (var take = 0; take < QueuedTakes; take++)
        {
            using var bound = new CancellationTokenSource(SignalTimeout);
            var (peer, recording) = await harness.ConnectRawAsync($"queued-dispose-{take}-{Guid.NewGuid():N}", bound.Token, readPaused: true);
            using var _ = peer;
            var blockedWrite = await WriteUntilOneBlocksAsync(recording.Stream, bound.Token);
            var blocked = OutcomeAsync(() => new ValueTask(blockedWrite));
            var queued = OutcomeAsync(() => recording.Stream.WriteFrameAsync(Frame));

            // Act
            await recording.Stream.DisposeAsync().AsTask().WaitAsync(bound.Token);

            // Assert input — each write's outcome, or `pending` when it had not finished one signal timeout later
            var both = Task.WhenAll(blocked, queued);
            var finished = await Task.WhenAny(both, Task.Delay(SignalTimeout, CancellationToken.None)) == both; // fence-allow: GUARD-TIMEOUT — a write that never completes is the defect; the bound turns the hang into a census entry
            outcomes.Add($"blocked:{(blocked.IsCompleted ? blocked.Result : "pending")}");
            outcomes.Add($"queued:{(queued.IsCompleted ? queued.Result : "pending")}");
            if (!finished)
                break;
        }

        // Assert
        List<string> writes = [.. outcomes.Select(o => o[(o.IndexOf(':', StringComparison.Ordinal) + 1)..])];
        Census(writes).Should().Be(AllDisposed(writes),
            "the disposal ends both writes: the one under way and the one waiting for the send find the stream disposed and throw ObjectDisposedException, and neither hangs; per write: {0}; the takes ended on: {1}",
            string.Join(", ", outcomes), Census(writes));
    }

    [Fact]
    public async Task WriteFrameAsync_ShouldThrowTheSessionsOwnObjectDisposed_WhenTheStreamWasDisposed()
    {
        // Arrange
        using var bound = new CancellationTokenSource(SignalTimeout);
        await using var pair = await LoopbackSession.StartAsync(bound.Token);
        await pair.Session.DisposeAsync();

        // Act
        var thrown = await Record.ExceptionAsync(() => pair.Session.WriteFrameAsync(Frame).AsTask());

        // Assert — the session's own exception, which says the stream ended, not one of a helper it released
        var disposed = thrown.Should().BeOfType<ObjectDisposedException>().Subject;
        using (new AssertionScope())
        {
            disposed.ObjectName.Should().Be(nameof(WebSocketAudioSession));
            disposed.Message.Should().Contain("The stream has ended");
        }
    }

    // ============================================================ a token cancelled before the write

    [Fact]
    public async Task WriteFrameAsync_ShouldThrowOperationCanceledNotObjectDisposed_WhenItsTokenIsAlreadyCancelledOnAnEndedStream()
    {
        // Arrange — a stream the peer's close ended and nobody has disposed yet
        using var bound = new CancellationTokenSource(SignalTimeout);
        await using var pair = await LoopbackSession.StartAsync(bound.Token);
        await pair.CloseFromPeerAsync(bound.Token);
        await pair.Ended.WaitAsync(bound.Token);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // Act
        var thrown = await Record.ExceptionAsync(() => pair.Session.WriteFrameAsync(Frame, cancelled.Token).AsTask());

        // Assert — the caller's cancellation is read before the ending, as every .NET wait reads its token first
        thrown.Should().BeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task WriteFrameAsync_ShouldThrowOperationCanceledNotObjectDisposed_WhenItsCallerCancelsItWhileTheDisposalWaitsForIt()
    {
        // Arrange — the peer stops reading, so a write blocks holding the send; the disposal then waits for it
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var (peer, recording) = await harness.ConnectRawAsync($"cancel-under-dispose-{Guid.NewGuid():N}", bound.Token, readPaused: true);
        using var _ = peer;
        using var caller = new CancellationTokenSource();
        var blocked = await WriteUntilOneBlocksAsync(recording.Stream, bound.Token, caller.Token);
        var disposing = recording.Stream.DisposeAsync().AsTask();

        // Act — the caller gives up on its write while the disposal is still waiting for the send
        await caller.CancelAsync();
        var thrown = await Record.ExceptionAsync(() => blocked.WaitAsync(bound.Token));
        await disposing.WaitAsync(bound.Token);

        // Assert — the caller's own cancellation, not the disposal's ending
        thrown.Should().BeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task WriteFrameAsync_ShouldThrowObjectDisposedNotOperationCanceled_WhenItsTokenIsAlreadyCancelledOnADisposedStream()
    {
        // Arrange
        using var bound = new CancellationTokenSource(SignalTimeout);
        await using var pair = await LoopbackSession.StartAsync(bound.Token);
        await pair.Session.DisposeAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // Act
        var thrown = await Record.ExceptionAsync(() => pair.Session.WriteFrameAsync(Frame, cancelled.Token).AsTask());

        // Assert — a disposed object refuses every call first, as it did before the session read the ending itself
        thrown.Should().BeOfType<ObjectDisposedException>();
    }

    // ============================================================ a failure under a live write keeps its type

    [Fact]
    public async Task WriteFrameAsync_ShouldThrowTheTransportsOwnFailure_WhenAResetCutsShortAWriteUnderWay()
    {
        // Arrange — the peer stops reading, so a write blocks in the transport with the stream still live
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        var outcomes = new List<string>();

        for (var take = 0; take < CutShortTakes; take++)
        {
            using var bound = new CancellationTokenSource(SignalTimeout);
            var (peer, recording) = await harness.ConnectRawAsync($"cut-{take}-{Guid.NewGuid():N}", bound.Token, readPaused: true);
            using var _ = peer;
            var blocked = await WriteUntilOneBlocksAsync(recording.Stream, bound.Token);

            // Act
            peer.Reset();
            outcomes.Add(await OutcomeAsync(() => new ValueTask(blocked)).WaitAsync(bound.Token));
            await recording.Ended.WaitAsync(bound.Token);
        }

        // Assert — the transport reports the reset as WebSocketException when the send fails first, and as
        // OperationCanceledException when the read's failure aborted the socket first; both are its own, and the
        // write had passed the ending check, so neither becomes ObjectDisposedException
        var census = Census(outcomes);
        using (new AssertionScope())
        {
            outcomes.Should().OnlyContain(
                o => o.StartsWith(nameof(WebSocketException) + ":", StringComparison.Ordinal) || o == nameof(OperationCanceledException),
                "a write under way when the transport failed throws the transport's own failure; the takes ended on: {0}", census);
            outcomes.Should().Contain(
                o => o.StartsWith(nameof(WebSocketException) + ":", StringComparison.Ordinal),
                "at least one take sees the send itself fail, so a failure rewritten as another type shows here; the takes ended on: {0}", census);
        }
    }

    // ============================================================ a write the peer's close cuts short ends as the ending the session declares

    [Fact]
    public async Task WriteFrameAsync_ShouldThrowObjectDisposedWithTheTransportsFailureInside_WhenThePeerClosesThenResetsUnderAWriteUnderWay()
    {
        // Arrange — the peer stops reading, so a write blocks in the transport with audio the peer never reads
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        var outcomes = new List<string>();
        var endings = new List<string>();

        for (var take = 0; take < QueuedTakes; take++)
        {
            using var bound = new CancellationTokenSource(SignalTimeout);
            var (peer, recording) = await harness.ConnectRawAsync($"close-then-reset-{take}-{Guid.NewGuid():N}", bound.Token, readPaused: true);
            using var _ = peer;
            var blocked = await WriteUntilOneBlocksAsync(recording.Stream, bound.Token);
            var cut = ExceptionAsync(blocked);

            // Act — Asterisk's hangup under a burst: its close, read by the session, then the connection cut with our
            // audio still unread
            await peer.SendCloseAsync();
            while (recording.Stream.IsConnected && !bound.IsCancellationRequested)
                await Task.Delay(1, CancellationToken.None); // fence-allow: LOOP-DRIVER — the stream exposes no signal for having read the peer's close, only IsConnected turning false; bounded by SignalTimeout
            peer.Reset();
            var thrown = await cut.WaitAsync(bound.Token);
            await recording.Ended.WaitAsync(bound.Token);
            outcomes.Add(thrown is ObjectDisposedException { InnerException: WebSocketException or IOException }
                ? $"{Disposed}+inner"
                : thrown is null ? "succeeded" : Name(thrown));
            endings.Add(string.Join(",", recording.States));
        }

        // Assert — the session read the peer's close first and declares an orderly ending, so the write says the same
        using (new AssertionScope())
        {
            Census(outcomes).Should().Be($"{Disposed}+inner={outcomes.Count}",
                "a write cut short after the peer's close throws ObjectDisposedException with the transport's failure inside, never WebSocketException; the takes ended on: {0}", Census(outcomes));
            Census(endings).Should().Be($"Connected,Disconnected={endings.Count}",
                "the session ended on the peer's close, not on a failed transport; the endings were: {0}", Census(endings));
        }
    }

    [Fact]
    public async Task WriteFrameAsync_ShouldThrowObjectDisposed_WhenItsSendFailsBeforeTheReadLoopHasThePeersClose()
    {
        // Arrange — a socket whose send fails while the peer's close is still on its way to the read loop
        using var bound = new CancellationTokenSource(SignalTimeout);
        await using var scripted = ScriptedSession.Start();
        var write = ExceptionAsync(scripted.Session.WriteFrameAsync(Frame).AsTask());
        await scripted.Socket.SendFailed.WaitAsync(bound.Token);

        // Act — the close arrives after the send failed, within the time the write waits for the ending
        await Task.Delay(LateClose, bound.Token); // fence-allow: SETTLE — lets the failed write start waiting for the read loop's verdict before the close reaches it, a fifth of that wait
        scripted.Socket.DeliverClose();
        var thrown = await write.WaitAsync(bound.Token);
        await scripted.Ended.WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            thrown.Should().BeOfType<ObjectDisposedException>("the read loop then declares the peer's close, so the write ends as that ending");
            thrown?.InnerException.Should().BeOfType<WebSocketException>("the transport's failure stays inside");
            scripted.States.Should().Equal([AudioStreamState.Connected, AudioStreamState.Disconnected], "the session ended on the peer's close");
        }
    }

    [Fact]
    public async Task WriteFrameAsync_ShouldThrowTheTransportsOwnFailureWithinTheCloseBound_WhenItsSendFailsAndNoEndingFollows()
    {
        // Arrange — a socket whose send fails, and whose read loop never learns how the stream ends
        using var bound = new CancellationTokenSource(SignalTimeout);
        await using var scripted = ScriptedSession.Start();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        // Act
        var thrown = await ExceptionAsync(scripted.Session.WriteFrameAsync(Frame).AsTask()).WaitAsync(bound.Token);
        var waited = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        // Assert — the write waits a bounded time for an ending, then keeps the transport's type; the bound is well
        // inside the close's own, so the answer to a close the write delayed still goes out in time
        using (new AssertionScope())
        {
            thrown.Should().BeOfType<WebSocketException>("no ending was declared, so the failure is the transport's own")
                .Which.WebSocketErrorCode.Should().Be(WebSocketError.ConnectionClosedPrematurely);
            waited.Should().BeLessThan(WebSocketAudioSession.CloseBound, "the wait for the ending is bounded, and shorter than the close's bound");
            scripted.States.Should().Equal([AudioStreamState.Connected], "nothing has ended the stream yet");
        }
    }

    // ============================================================ helpers

    /// <summary>Every command a <see cref="IChanWebSocketSession"/> writes, in the format the connection negotiated.</summary>
    private static List<(string Name, Func<IChanWebSocketSession, ValueTask> Send)> CommandsFor(ChanWebSocketCaptureFormat format)
    {
        List<(string, Func<IChanWebSocketSession, ValueTask>)> commands =
        [
            ("WriteFrameAsync", s => s.WriteFrameAsync(Frame)),
            ("SendMarkAsync", s => s.SendMarkAsync("m")),
            ("SendFlushMediaAsync", s => s.SendFlushMediaAsync()),
            ("SendPauseMediaAsync", s => s.SendPauseMediaAsync()),
            ("SendContinueMediaAsync", s => s.SendContinueMediaAsync()),
            ("SendGetStatusAsync", s => s.SendGetStatusAsync()),
            ("SendStartMediaBufferingAsync", s => s.SendStartMediaBufferingAsync()),
            ("SendReportQueueDrainedAsync", s => s.SendReportQueueDrainedAsync()),
            ("SendStopMediaBufferingAsync", s => s.SendStopMediaBufferingAsync("c1")),
        ];
        if (format == ChanWebSocketCaptureFormat.Json)
            commands.Add(("SendSetMediaDirectionAsync", s => s.SendSetMediaDirectionAsync(ChanWebSocketMediaDirection.In)));
        return commands;
    }

    /// <summary>What one call ended on: <c>succeeded</c>, the WebSocket error code, or the exception's type.</summary>
    private static async Task<string> OutcomeAsync(Func<ValueTask> call)
    {
        try
        {
            await call();
            return "succeeded";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) // every outcome is recorded, not judged here
        {
            return Name(ex);
        }
    }

    /// <summary>What <paramref name="call"/> threw, or <see langword="null"/> when it completed.</summary>
    private static async Task<Exception?> ExceptionAsync(Task call)
    {
        try
        {
            await call;
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) // every outcome is recorded, not judged here
        {
            return ex;
        }
    }

    private static string Name(Exception ex) =>
        ex is WebSocketException ws ? $"{nameof(WebSocketException)}:{ws.WebSocketErrorCode}" : ex.GetType().Name;

    /// <summary>The census of <paramref name="outcomes"/> when every take ended on <see cref="ObjectDisposedException"/>.</summary>
    private static string AllDisposed(List<string> outcomes) => $"{Disposed}={outcomes.Count}";

    /// <summary>The outcomes counted, in ordinal order: <c>A=3, B=1</c>.</summary>
    private static string Census(IEnumerable<string> outcomes) =>
        string.Join(", ", outcomes.GroupBy(o => o, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Count()}"));

    /// <summary>Writes with no pause until a write throws, and returns what it threw.</summary>
    private static Task<string> WriteUntilThrowAsync(WebSocketAudioSession stream, CancellationToken token) =>
        Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await stream.WriteFrameAsync(Frame, CancellationToken.None);
                    await Task.Yield();
                }

                return "never-threw";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) // every outcome is recorded, not judged here
            {
                return Name(ex);
            }
        }, CancellationToken.None);

    /// <summary>
    /// Writes large frames until one has not completed after 250 ms, and returns that write; each write is given
    /// <paramref name="writeToken"/>.
    /// </summary>
    private static async Task<Task> WriteUntilOneBlocksAsync(IAudioStream stream, CancellationToken token, CancellationToken writeToken = default)
    {
        var frame = new byte[64 * 1024];
        for (var i = 0; i < 4000; i++)
        {
            var write = stream.WriteFrameAsync(frame, writeToken).AsTask();
            if (await Task.WhenAny(write, Task.Delay(250, token)) != write) // fence-allow: SETTLE — the negative "this write has not completed" needs a bounded window; a write that has not completed in it counts as blocked
                return write;
            await write;
        }

        throw new InvalidOperationException("No write blocked: the peer's connection never filled.");
    }

    /// <summary>
    /// A started <see cref="WebSocketAudioSession"/> over a real loopback socket, with no server: nothing disposes it
    /// when it ends, so its socket stays in the state the ending left. The peer is a client <see cref="WebSocket"/>
    /// that reads every frame the session sends until the session's close, and answers that close when it was the first.
    /// </summary>
    private sealed class LoopbackSession : IAsyncDisposable
    {
        private readonly TcpClient _peerTcp;
        private readonly TcpClient _accepted;
        private readonly WebSocket _peer;
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IDisposable _subscription;
        private readonly Lock _gate = new();
        private readonly List<(int Count, TaskCompletionSource Signal)> _waits = [];
        private Task? _peerRead;
        private int _received;

        private LoopbackSession(TcpClient peerTcp, TcpClient accepted)
        {
            _peerTcp = peerTcp;
            _accepted = accepted;
            _peer = WebSocket.CreateFromStream(peerTcp.GetStream(), new WebSocketCreationOptions { IsServer = false });
            var serverSocket = WebSocket.CreateFromStream(accepted.GetStream(), new WebSocketCreationOptions { IsServer = true });
            Session = new WebSocketAudioSession(serverSocket, $"loop-{Guid.NewGuid():N}", "slin16", new CapturingLogger<WebSocketAudioSession>());
            _subscription = Session.StateChanges.Subscribe(state =>
            {
                if (state == AudioStreamState.Disconnected)
                    _ended.TrySetResult();
            });
        }

        public WebSocketAudioSession Session { get; }

        /// <summary>Completes when the session publishes <see cref="AudioStreamState.Disconnected"/>.</summary>
        public Task Ended => _ended.Task;

        public static async Task<LoopbackSession> StartAsync(CancellationToken token)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var peerTcp = new TcpClient();
            var accepting = listener.AcceptTcpClientAsync(token).AsTask();
            await peerTcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token);
            var pair = new LoopbackSession(peerTcp, await accepting);
            pair.Session.Start();
            pair._peerRead = pair.ReadUntilCloseAsync();
            return pair;
        }

        /// <summary>Completes once the peer has received <paramref name="count"/> binary frames.</summary>
        public Task PeerReceived(int count)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_received >= count)
                    signal.TrySetResult();
                else
                    _waits.Add((count, signal));
            }

            return signal.Task;
        }

        /// <summary>The peer sends the captured MEDIA_START of <paramref name="format"/>, and the session has read it.</summary>
        public async Task NegotiateAsync(ChanWebSocketCaptureFormat format, CancellationToken token)
        {
            var mediaStart = ChanWebSocketWireCapture.MediaStart.First(f =>
                f.AsteriskVersion == "24.0.0-rc2" && f.Producer == ChanWebSocketCaptureProducer.ExternalMedia && f.Format == format && f.Option == "none");
            await _peer.SendAsync(mediaStart.Payload(), WebSocketMessageType.Text, endOfMessage: true, token);
            await _peer.SendAsync(Frame, WebSocketMessageType.Binary, endOfMessage: true, token);

            // The pump handles frames in order, so the MEDIA_START ahead of this frame has been read.
            var frame = await Session.ReadFrameAsync(token).AsTask().WaitAsync(token);
            frame.Length.Should().Be(Frame.Length);
            Session.MediaStart.Should().NotBeNull();
        }

        /// <summary>The peer's orderly hangup: one close frame; the peer keeps reading until the session's answer.</summary>
        public Task CloseFromPeerAsync(CancellationToken token) =>
            _peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, token);

        /// <summary>The peer's connection reset, with no close frame.</summary>
        public void ResetFromPeer()
        {
            _peerTcp.LingerState = new LingerOption(enable: true, seconds: 0);
            _peerTcp.Client.Close();
        }

        private async Task ReadUntilCloseAsync()
        {
            var buffer = new byte[8192];
            try
            {
                while (true)
                {
                    var result = await _peer.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // The session's own close, from a disposal: answered, so the disposal does not wait out its bound.
                        if (_peer.State == WebSocketState.CloseReceived)
                            await _peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, CancellationToken.None);
                        return;
                    }
                    if (result.MessageType == WebSocketMessageType.Binary && result.EndOfMessage)
                        OnFrameReceived();
                }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The connection went away under the peer: its reading is over.
            }
        }

        private void OnFrameReceived()
        {
            lock (_gate)
            {
                _received++;
                for (var i = _waits.Count - 1; i >= 0; i--)
                {
                    if (_received < _waits[i].Count)
                        continue;
                    _waits[i].Signal.TrySetResult();
                    _waits.RemoveAt(i);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _subscription.Dispose();
            await Session.DisposeAsync();
            _peer.Abort();
            _peer.Dispose();
            if (_peerRead is not null)
                await _peerRead.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _peerTcp.Dispose();
            _accepted.Dispose();
        }
    }

    /// <summary>A started <see cref="WebSocketAudioSession"/> over a <see cref="ScriptedWebSocket"/>, with every state it published.</summary>
    private sealed class ScriptedSession : IAsyncDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<AudioStreamState> _states = new();
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IDisposable _subscription;

        private ScriptedSession()
        {
            Session = new WebSocketAudioSession(Socket, $"scripted-{Guid.NewGuid():N}", "slin16", new CapturingLogger<WebSocketAudioSession>());
            _subscription = Session.StateChanges.Subscribe(state =>
            {
                _states.Enqueue(state);
                if (state == AudioStreamState.Disconnected)
                    _ended.TrySetResult();
            });
        }

        public ScriptedWebSocket Socket { get; } = new();

        public WebSocketAudioSession Session { get; }

        public IReadOnlyList<AudioStreamState> States => [.. _states];

        /// <summary>Completes when the session publishes <see cref="AudioStreamState.Disconnected"/>.</summary>
        public Task Ended => _ended.Task;

        public static ScriptedSession Start()
        {
            var scripted = new ScriptedSession();
            scripted.Session.Start();
            return scripted;
        }

        public async ValueTask DisposeAsync()
        {
            _subscription.Dispose();
            await Session.DisposeAsync();
            Socket.Dispose();
        }
    }

    /// <summary>
    /// A WebSocket whose every send fails as a reset does, <see cref="WebSocketException"/>
    /// <see cref="WebSocketError.ConnectionClosedPrematurely"/> with an <see cref="IOException"/> inside, and whose one
    /// received frame is the peer's close, delivered when the test says so or as the answer to this side's own close.
    /// </summary>
    private sealed class ScriptedWebSocket : WebSocket
    {
        private readonly TaskCompletionSource _close = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _sendFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile WebSocketState _state = WebSocketState.Open;

        /// <summary>Completes when a send has failed.</summary>
        public Task SendFailed => _sendFailed.Task;

        /// <summary>The peer's close reaches the read loop.</summary>
        public void DeliverClose() => _close.TrySetResult();

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => _state is WebSocketState.CloseReceived or WebSocketState.Closed ? WebSocketCloseStatus.NormalClosure : null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            await _close.Task.WaitAsync(ct);
            _state = _state == WebSocketState.CloseSent ? WebSocketState.Closed : WebSocketState.CloseReceived;
            return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
        {
            _sendFailed.TrySetResult();
            return ValueTask.FromException(new WebSocketException(WebSocketError.ConnectionClosedPrematurely, new IOException("Connection reset by peer")));
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        {
            if (_state == WebSocketState.CloseReceived)
            {
                _state = WebSocketState.Closed;
            }
            else
            {
                // This side's close, from a disposal: the peer answers it.
                _state = WebSocketState.CloseSent;
                DeliverClose();
            }

            return Task.CompletedTask;
        }

        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) =>
            CloseOutputAsync(status, description, ct);

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
            => throw new NotSupportedException("Use the Memory overload");

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
            => SendAsync(buffer.AsMemory(), type, endOfMessage, ct).AsTask();

        public override void Abort() => _state = WebSocketState.Aborted;

#pragma warning disable IDISP010 // WebSocket.Dispose is abstract — no base to call
        public override void Dispose() => _state = WebSocketState.Closed;
#pragma warning restore IDISP010
    }
}
