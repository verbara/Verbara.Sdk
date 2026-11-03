using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Tests.TestSupport;
using Verbara.Sdk.Tests.Shared.Sockets;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// How a WebSocket audio stream ends: what it publishes on <c>StateChanges</c> for each way of ending, and the closing
/// handshake it owes its peer. Every test drives the real server, or the real session, over a real loopback socket,
/// whose far end is a <see cref="RawWebSocketPeer"/> that records every frame the server sends and answers nothing by
/// itself.
/// </summary>
/// <remarks>
/// Several tests read <c>audio.transport.failures</c>, which is process-wide and tagged only with the transport, and
/// several end connections with a reset, which moves it: the class runs in <see cref="AudioStreamMetricsGroup"/>, alone.
/// </remarks>
[Collection(AudioStreamMetricsGroup.Name)]
public sealed class WebSocketSessionEndingTests
{
    /// <summary>Upper bound on any wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The closing handshake's bound: at most 2 seconds, by the stream's contract.</summary>
    private static readonly TimeSpan CloseBound = TimeSpan.FromSeconds(2);

    /// <summary>Scheduling slack over <see cref="CloseBound"/> for a wait that may spend the whole bound.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(1);

    private const string TransportFailedEvent = "TransportFailed";

    private static readonly AudioStreamState[] PeerOrLocalEnding = [AudioStreamState.Connected, AudioStreamState.Disconnected];

    private static readonly AudioStreamState[] FailedEnding =
        [AudioStreamState.Connected, AudioStreamState.Error, AudioStreamState.Disconnected];

    // ============================================================ a failed transport says so

    [Fact]
    public async Task StateChanges_ShouldPublishErrorThenDisconnected_WhenTheConnectionIsResetUnderALiveStream()
    {
        // Arrange — a second observer reads the count and the log at the moment Error is delivered
        using var counter = new TransportFailureCounter();
        WebSocketEndingHarness? started = null;
        (long Count, int Warnings)? atError = null;
        await using var harness = await WebSocketEndingHarness.StartAsync(recording =>
        {
            recording.Subscribe();
            recording.Stream.StateChanges.Subscribe(state =>
            {
                if (state == AudioStreamState.Error)
                    atError = (counter.Total, TransportWarnings(started!).Count);
            });
        });
        started = harness;
        using var bound = new CancellationTokenSource(SignalTimeout);
        var (peer, recording) = await harness.ConnectRawAsync($"reset-{Guid.NewGuid():N}", bound.Token);
        using var _ = peer;

        // Act
        peer.Reset();
        await recording.Completed.WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            recording.States.Should().Equal(FailedEnding, "a reset under a live stream is a failed transport, published as Error, then Disconnected, once each");
            counter.Total.Should().Be(1, "one stream ended on a transport failure");
            TransportWarnings(harness).Should().ContainSingle("the failure is written once");
            atError.Should().Be((1L, 1), "the count and the Warning exist before Error is published");
        }
    }

    [Fact]
    public async Task StateChanges_ShouldPublishErrorThenDisconnected_WhenTheConnectionEndsWithoutACloseFrame()
    {
        // Arrange
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var (peer, recording) = await harness.ConnectRawAsync($"fin-{Guid.NewGuid():N}", bound.Token);
        using var _ = peer;

        // Act — FIN with no close frame, as Asterisk's side ends after a failed write
        peer.ShutdownSend();
        await recording.Completed.WaitAsync(bound.Token);

        // Assert
        recording.States.Should().Equal(FailedEnding, "a connection that ends without a close frame failed");
    }

    [Fact]
    public async Task ReadFrameAsync_ShouldReturnEveryHeldFrameThenTheEmptyFrame_WhenTheConnectionFails()
    {
        // Arrange — five frames received and not yet read
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var (peer, recording) = await harness.ConnectRawAsync($"held-{Guid.NewGuid():N}", bound.Token);
        using var _ = peer;
        var sent = Enumerable.Range(1, 5).Select(i => Enumerable.Repeat((byte)i, 320).ToArray()).ToList();
        foreach (var payload in sent)
            await peer.SendBinaryAsync(payload);

        // Act — the connection fails behind them (FIN keeps the bytes ahead of it readable)
        peer.ShutdownSend();
        await recording.Ended.WaitAsync(bound.Token);
        var read = new List<byte[]>();
        for (var i = 0; i <= sent.Count; i++)
            read.Add((await recording.Stream.ReadFrameAsync(bound.Token).AsTask().WaitAsync(bound.Token)).ToArray());

        // Assert
        read.Take(sent.Count).Should().BeEquivalentTo(sent, o => o.WithStrictOrdering(), "every frame received before the failure reaches the consumer");
        read[^1].Should().BeEmpty("then the stream ends with the empty frame");
    }

    // ============================================================ a peer's close ends without an error

    [Theory]
    [InlineData((ushort)1000)]
    [InlineData((ushort)1011)]
    public async Task StateChanges_ShouldPublishDisconnectedOnceWithNoError_WhenThePeerSendsACloseFrame(ushort status)
    {
        // Arrange
        using var counter = new TransportFailureCounter();
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var (peer, recording) = await harness.ConnectRawAsync($"close-{status}-{Guid.NewGuid():N}", bound.Token);
        using var _ = peer;

        // Act
        await peer.SendCloseAsync(status);
        await recording.Completed.WaitAsync(bound.Token);
        await peer.Ended.WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            recording.States.Should().Equal(PeerOrLocalEnding, "a close frame with status {0} is the peer ending the stream, whatever its code", status);
            counter.Total.Should().Be(0, "a close frame is not a transport failure");
            TransportWarnings(harness).Should().BeEmpty("a close frame writes no transport Warning");
        }
    }

    [Fact]
    public async Task StateChanges_ShouldEndAsAPeerCloseWithNoError_WhenThePeerClosesAndDropsTheConnectionWhileAudioIsWritten()
    {
        // Arrange — the consumer writes audio in a loop; the peer reads it
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var (peer, recording) = await harness.ConnectRawAsync($"writing-{Guid.NewGuid():N}", bound.Token);
        using var _ = peer;
        using var stopWriting = new CancellationTokenSource();
        var writer = WriteUntilFailureAsync(recording.Stream, new byte[640], stopWriting.Token);
        await WaitUntilAsync(() => peer.Frames.Count(f => f.Opcode == RawWebSocketPeer.OpcodeBinary) >= 5, bound.Token);

        // Act — the close frame, then the connection dropped at once
        await peer.SendCloseAsync();
        peer.Reset();
        await recording.Completed.WaitAsync(bound.Token);
        await stopWriting.CancelAsync();
        await writer.WaitAsync(bound.Token);

        // Assert
        recording.States.Should().Equal(PeerOrLocalEnding, "the close frame was received, so the peer ended the stream");
    }

    // ============================================================ an ending on this side ends without an error

    [Fact]
    public async Task StateChanges_ShouldPublishDisconnectedOnceWithNoError_WhenTheConsumerDisposesALiveStream()
    {
        // Arrange
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var (peer, recording) = await harness.ConnectRawAsync($"dispose-{Guid.NewGuid():N}", bound.Token);
        using var _ = peer;
        peer.AnswerCloseWhenReceived();

        // Act
        await recording.Stream.DisposeAsync().AsTask().WaitAsync(bound.Token);
        await recording.Completed.WaitAsync(bound.Token);

        // Assert
        recording.States.Should().Equal(PeerOrLocalEnding, "the consumer ended the stream");
    }

    [Fact]
    public async Task StateChanges_ShouldEndDisconnectedWithNoErrorWarningOrCount_WhenThePeerResetsInsteadOfAnsweringTheClose()
    {
        // Arrange — a live stream with a read pending; the peer resets the connection when it reads the close
        using var counter = new TransportFailureCounter();
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var (peer, recording) = await harness.ConnectRawAsync($"reset-answer-{Guid.NewGuid():N}", bound.Token);
        using var _ = peer;
        await MakeReadPendingAsync(peer, recording.Stream, bound.Token);
        peer.ResetWhenCloseReceived();

        // Act
        await recording.Stream.DisposeAsync().AsTask().WaitAsync(bound.Token);
        await recording.Completed.WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            peer.Closes.Should().ContainSingle("the disposal sends its close frame, which the peer answers with a reset");
            recording.States.Should().Equal(PeerOrLocalEnding, "the first ending was this side's, and a reset during it changes nothing");
            counter.Total.Should().Be(0, "a reset during this side's ending is not a transport failure");
            TransportWarnings(harness).Should().BeEmpty("nor is it written as one");
        }
    }

    // ============================================================ a peer's close is answered

    [Fact]
    public async Task ReadPump_ShouldAnswerWithExactlyOneCloseBeforeDisconnected_WhenThePeerClosesFirst()
    {
        // Arrange — the Disconnected observer holds the publishing thread until the answer reaches the peer, so the
        // answer can arrive in that window only if it was sent before the ending was published
        RawWebSocketPeer? farEnd = null;
        bool? answeredBeforeDisconnected = null;
        await using var harness = await WebSocketEndingHarness.StartAsync(recording =>
        {
            recording.Subscribe();
            recording.Stream.StateChanges.Subscribe(state =>
            {
                if (state == AudioStreamState.Disconnected)
                    answeredBeforeDisconnected = Volatile.Read(ref farEnd)!.WaitForClose(CloseBound);
            });
        });
        using var bound = new CancellationTokenSource(SignalTimeout);
        var (peer, recording) = await harness.ConnectRawAsync($"answer-{Guid.NewGuid():N}", bound.Token);
        using var _ = peer;
        Volatile.Write(ref farEnd, peer);

        // Act
        await peer.SendCloseAsync();
        await recording.Completed.WaitAsync(bound.Token);
        await peer.Ended.WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            peer.Closes.Should().ContainSingle("a close from the peer is answered by exactly one close frame");
            answeredBeforeDisconnected.Should().BeTrue("the answer is sent before Disconnected is published");
            recording.States.Should().Equal(PeerOrLocalEnding);
        }
    }

    [Fact]
    public async Task StateChanges_ShouldEndDisconnectedWithNoErrorAndNoException_WhenTheAnswerCannotReachThePeer()
    {
        // Arrange
        using var unobserved = new UnobservedServerFaults(nameof(WebSocketAudioSession));
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var (peer, recording) = await harness.ConnectRawAsync($"unreachable-{Guid.NewGuid():N}", bound.Token);
        using var _ = peer;

        // Act — the close frame, and the connection closed at once
        await peer.SendCloseAsync();
        peer.Reset();
        await recording.Completed.WaitAsync(bound.Token);
        var thrown = await Record.ExceptionAsync(() => recording.Stream.DisposeAsync().AsTask().WaitAsync(bound.Token));
        await harness.Server.StopAsync().AsTask().WaitAsync(bound.Token);
        UnobservedServerFaults.CollectDiscardedTasks();

        // Assert
        using (new AssertionScope())
        {
            recording.States.Should().Equal(PeerOrLocalEnding, "a failed answer is not a second ending");
            thrown.Should().BeNull("a failed answer raises nothing to a caller");
            unobserved.Faults.Should().BeEmpty("a failed answer leaves no exception unobserved");
        }
    }

    [Fact]
    public async Task StateChanges_ShouldPublishTheEndingWithinTheBound_WhenAWriteIsBlockedAsThePeerCloses()
    {
        // Arrange — the peer stops reading, so the consumer's writes fill the connection until one blocks
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var (peer, recording) = await harness.ConnectRawAsync($"blocked-{Guid.NewGuid():N}", bound.Token, readPaused: true);
        using var _ = peer;
        var blocked = await WriteUntilOneBlocksAsync(recording.Stream, bound.Token);

        // Act
        var clock = Stopwatch.StartNew();
        await peer.SendCloseAsync();
        await recording.Ended.WaitAsync(bound.Token);
        var elapsed = clock.Elapsed;
        peer.Dispose();
        await Record.ExceptionAsync(() => blocked.WaitAsync(bound.Token));

        // Assert
        elapsed.Should().BeLessThan(CloseBound + Slack, "a blocked write holds the ending at most the bound");
    }

    [Fact]
    public async Task HandleConnection_ShouldAnswerTheCloseAndNeverAnnounce_WhenAPeerClosesBeforeNamingItsCall()
    {
        // Arrange — a server that waits to be named, far longer than this test runs
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe(), mediaStartTimeout: SignalTimeout * 2);
        using var bound = new CancellationTokenSource(SignalTimeout);
        var id = $"unnamed-{Guid.NewGuid():N}";
        using var peer = await RawWebSocketPeer.ConnectAsync(harness.Port, bound.Token);
        await peer.UpgradeAsync(id, bound.Token);

        // Act — a close frame is the connection's first frame
        await peer.SendCloseAsync();
        await peer.Ended.WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            peer.Closes.Should().ContainSingle("a close before the call is named is answered too");
            harness.Announcements.Should().Be(0, "a connection that ended before its first frame is never announced");
            harness.AnnouncedAs(id).IsCompleted.Should().BeFalse();
        }
    }

    // ============================================================ an ending on this side sends its close first

    [Fact]
    public async Task DisposeAsync_ShouldSendOneClose1000BeforeReleasing_WhenAReadIsPending()
    {
        // Arrange
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var (peer, recording) = await harness.ConnectRawAsync($"pending-{Guid.NewGuid():N}", bound.Token);
        using var _ = peer;
        await MakeReadPendingAsync(peer, recording.Stream, bound.Token);
        peer.AnswerCloseWhenReceived();

        // Act
        await recording.Stream.DisposeAsync().AsTask().WaitAsync(bound.Token);
        var ended = await peer.Ended.WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            peer.Closes.Should().ContainSingle("a disposal sends its close frame, whether or not a read is pending")
                .Which.CloseStatus.Should().Be(1000);
            ended.Should().Be(RawConnectionEnd.Fin, "the connection is released after the close frame");
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldSendExactlyOneClose1000BeforeReleasing_WhenNoReadIsPending()
    {
        // Arrange — the real session over a real socket, never started, so no read was ever issued
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var bound = new CancellationTokenSource(SignalTimeout);
        var accept = listener.AcceptTcpClientAsync(bound.Token);
        using var peer = await RawWebSocketPeer.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port, bound.Token);
        using var accepted = await accept;
        var stream = accepted.GetStream();
        var upgrade = peer.UpgradeAsync("no-start", bound.Token);
        var (wsKey, _) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, bound.Token);
        await WebSocketAudioServer.SendUpgradeResponseAsync(stream, wsKey!, bound.Token);
        await upgrade;
        var session = new WebSocketAudioSession(
            WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true }),
            "no-start",
            "slin16",
            new CapturingLogger<WebSocketAudioSession>());
        peer.AnswerCloseWhenReceived();

        // Act
        await session.DisposeAsync().AsTask().WaitAsync(bound.Token);
        accepted.Dispose();
        var ended = await peer.Ended.WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            peer.Closes.Should().ContainSingle("a disposal with no read pending sends exactly one close frame")
                .Which.CloseStatus.Should().Be(1000);
            ended.Should().Be(RawConnectionEnd.Fin, "the connection is released after the close frame");
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldReadTheAnswerAndReleaseBeforeTheBound_WhenThePeerSendsFramesBeforeItsAnswer()
    {
        // Arrange — the peer answers the close with audio first, then its close
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var (peer, recording) = await harness.ConnectRawAsync($"frames-first-{Guid.NewGuid():N}", bound.Token);
        using var _ = peer;
        await MakeReadPendingAsync(peer, recording.Stream, bound.Token);
        var answer = AnswerWithFramesFirstAsync(peer);

        // Act
        var clock = Stopwatch.StartNew();
        await recording.Stream.DisposeAsync().AsTask().WaitAsync(bound.Token);
        var disposal = clock.Elapsed;
        await peer.Ended.WaitAsync(bound.Token);
        var answered = await answer.WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            peer.Closes.Should().ContainSingle("the disposal sends its close frame").Which.CloseStatus.Should().Be(1000);
            answered.Should().BeTrue("the peer read that close and sent its frames and its answer");
            disposal.Should().BeLessThan(CloseBound / 4, "the answer is read, so the connection is released without waiting for the bound");
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldReturnWithinTheBoundAndRelease_WhenThePeerNeverAnswers()
    {
        // Arrange
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var (peer, recording) = await harness.ConnectRawAsync($"mute-{Guid.NewGuid():N}", bound.Token);
        using var _ = peer;
        await MakeReadPendingAsync(peer, recording.Stream, bound.Token);

        // Act
        var clock = Stopwatch.StartNew();
        await recording.Stream.DisposeAsync().AsTask().WaitAsync(bound.Token);
        var disposal = clock.Elapsed;
        var ended = await peer.Ended.WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            disposal.Should().BeLessThan(CloseBound + Slack, "a mute peer costs the disposal at most the bound");
            ended.Should().Be(RawConnectionEnd.Fin, "and the connection is released");
        }
    }

    // ============================================================ a server stop closes its streams together

    [Fact]
    public async Task StopAsync_ShouldSendEveryPeerACloseWithinOneBound_WhenTheServerStopsWithManyLiveStreams()
    {
        // Arrange — six live streams, each with a read pending; half the peers never answer
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var calls = new List<(RawWebSocketPeer Peer, StateRecording Recording)>();
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var call = await harness.ConnectRawAsync($"stop-{i}-{Guid.NewGuid():N}", bound.Token);
                calls.Add(call);
                await MakeReadPendingAsync(call.Peer, call.Recording.Stream, bound.Token);
                if (i % 2 == 0)
                    call.Peer.AnswerCloseWhenReceived();
            }

            // Act
            var clock = Stopwatch.StartNew();
            await harness.Server.StopAsync().AsTask().WaitAsync(bound.Token);
            var stop = clock.Elapsed;
            await Task.WhenAll(calls.Select(c => c.Peer.Ended)).WaitAsync(bound.Token);

            // Assert
            using (new AssertionScope())
            {
                calls.Select(c => c.Peer.Closes.Count).Should().AllBeEquivalentTo(1, "every stream's peer receives one close frame");
                stop.Should().BeLessThan(CloseBound * 2, "the streams close together: one bound however many mute peers");
            }
        }
        finally
        {
            foreach (var call in calls)
                call.Peer.Dispose();
        }
    }

    [Fact]
    public async Task StopAsync_ShouldKeepTheConnectionUntilTheAnswer_WhenADisposalIsInProgress()
    {
        // Arrange — the consumer's disposal has sent its close and reads for the answer
        await using var harness = await WebSocketEndingHarness.StartAsync(recording => recording.Subscribe());
        using var bound = new CancellationTokenSource(SignalTimeout);
        var (peer, recording) = await harness.ConnectRawAsync($"in-progress-{Guid.NewGuid():N}", bound.Token);
        using var _ = peer;
        await MakeReadPendingAsync(peer, recording.Stream, bound.Token);
        var disposal = recording.Stream.DisposeAsync().AsTask();
        var closeSeen = await WaitForCloseAsync(peer, CloseBound + Slack);

        // Act — the server stops, and the peer answers inside the bound
        var stop = harness.Server.StopAsync().AsTask();
        var stopReturnedAt = stop.ContinueWith(t => Stopwatch.GetTimestamp(), TaskScheduler.Default);
        await Task.Delay(CloseBound / 4, bound.Token); // fence-allow: SIMULATED-WORK — the peer's answer comes a quarter of the bound after the stop starts, so a stop that releases the connection early is seen
        var answeredAt = Stopwatch.GetTimestamp();
        var answerError = await Record.ExceptionAsync(() => peer.SendCloseAsync());
        await stop.WaitAsync(bound.Token);
        await disposal.WaitAsync(bound.Token);
        await peer.Ended.WaitAsync(bound.Token);

        // Assert
        using (new AssertionScope())
        {
            closeSeen.Should().BeTrue("the consumer's disposal sends its close frame first");
            answerError.Should().BeNull("the connection is still open when the peer answers");
            peer.EndedAt.Should().BeGreaterThan(answeredAt, "the stop does not release a connection whose close is in progress before its answer");
            (await stopReturnedAt).Should().BeGreaterThan(answeredAt, "the stop does not return before the answer");
        }
    }

    [Fact]
    public async Task StopAsync_ShouldSendEveryPeerACloseWithinOneBound_WhenTheStopsWaitIsCancelled()
    {
        // Arrange — three announced streams whose handlers a subscriber holds, and peers that never answer. Each peer
        // sends its upgrade after its accept, so its handler is parked off the accept loop when the subscriber holds it.
        using var release = new ManualResetEventSlim();
        var entered = 0;
        await using var harness = await WebSocketEndingHarness.StartWithAcceptSignalsAsync(recording =>
        {
            recording.Subscribe();
            Interlocked.Increment(ref entered);
            release.Wait();
        });
        using var bound = new CancellationTokenSource(SignalTimeout);
        using var owned = new DisposableSet();
        var peers = new List<RawWebSocketPeer>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var peer = owned.Add(await RawWebSocketPeer.ConnectAsync(harness.Port, bound.Token));
                peers.Add(peer);
                await harness.NextAcceptedAsync(bound.Token);
                await peer.UpgradeAsync($"held-{i}-{Guid.NewGuid():N}", bound.Token);
                var expected = i + 1;
                await WaitUntilAsync(() => Volatile.Read(ref entered) >= expected, bound.Token);
            }

            // Act
            using var stopToken = new CancellationTokenSource();
            var clock = Stopwatch.StartNew();
            var stop = harness.Server.StopAsync(stopToken.Token).AsTask();
            await stopToken.CancelAsync();
            await stop.WaitAsync(bound.Token);
            var elapsed = clock.Elapsed;
            var closed = await Task.WhenAll(peers.Select(p => WaitForCloseAsync(p, Slack)));

            // Assert
            using (new AssertionScope())
            {
                closed.Should().AllBeEquivalentTo(true, "every stream's peer receives a close frame");
                elapsed.Should().BeLessThan(CloseBound * 2, "the streams close together: one bound however many mute peers");
            }
        }
        finally
        {
            release.Set();
        }
    }

    // ============================================================ helpers

    private static List<CountedLogEntry> TransportWarnings(WebSocketEndingHarness harness) =>
        [.. harness.Logger.Entries.Where(e => e.Level == LogLevel.Warning && e.EventName == TransportFailedEvent)];

    /// <summary>
    /// The peer sends one frame and the consumer reads it, so the session's read loop has run and is back in a
    /// receive; a short pause lets it get there.
    /// </summary>
    private static async Task MakeReadPendingAsync(RawWebSocketPeer peer, IAudioStream stream, CancellationToken token)
    {
        await peer.SendBinaryAsync(new byte[320]);
        await stream.ReadFrameAsync(token).AsTask().WaitAsync(token);
        await Task.Delay(50, token); // fence-allow: SETTLE — lets the read loop return to its receive after the frame the consumer read; the GIVEN is a pending read
    }

    private static async Task<bool> WaitForCloseAsync(RawWebSocketPeer peer, TimeSpan timeout)
    {
        try
        {
            await peer.FirstClose.WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task<bool> AnswerWithFramesFirstAsync(RawWebSocketPeer peer)
    {
        if (!await WaitForCloseAsync(peer, CloseBound + Slack))
            return false;

        try
        {
            for (var i = 0; i < 3; i++)
                await peer.SendBinaryAsync(new byte[320]);
            await peer.SendCloseAsync();
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
            await Task.Delay(10, token); // fence-allow: LOOP-DRIVER — polls a condition under the caller's bounded token
    }

    private static async Task WriteUntilFailureAsync(IAudioStream stream, byte[] frame, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await stream.WriteFrameAsync(frame, token);
                await Task.Delay(5, token); // fence-allow: SIMULATED-WORK — paces the consumer's audio writes
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            // The connection ended under the writer, or the test stopped it: either way the writing is over.
        }
    }

    /// <summary>Writes large frames until one has not completed after 250 ms, and returns that write.</summary>
    private static async Task<Task> WriteUntilOneBlocksAsync(IAudioStream stream, CancellationToken token)
    {
        var frame = new byte[64 * 1024];
        for (var i = 0; i < 4000; i++)
        {
            var write = stream.WriteFrameAsync(frame, CancellationToken.None).AsTask();
            if (await Task.WhenAny(write, Task.Delay(250, token)) != write) // fence-allow: SETTLE — the negative "this write has not completed" needs a bounded window; a write that has not completed in it counts as blocked
                return write;
            await write;
        }

        throw new InvalidOperationException("No write blocked: the peer's connection never filled.");
    }
}
