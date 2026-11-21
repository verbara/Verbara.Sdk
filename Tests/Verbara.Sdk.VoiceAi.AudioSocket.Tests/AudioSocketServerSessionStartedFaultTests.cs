using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.VoiceAi.AudioSocket.Diagnostics;
using Verbara.Sdk.VoiceAi.AudioSocket.Internal;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Tests;

/// <summary>
/// What an <see cref="AudioSocketServer.OnSessionStarted"/> subscriber's failure does to the other subscribers and to the
/// connection: every subscriber is invoked, all of them are awaited together, each failure is counted and logged at
/// Warning when it happens, and the connection is closed, with a hangup frame, only when every subscriber failed.
/// </summary>
/// <remarks>
/// Real server, raw peer over the IPv4 loopback literal. Every wait a subscriber makes is on a gate the test opens, so no
/// outcome depends on a delay. The fault counter is process-wide and carries no tag, so the class runs in the
/// connection-metrics collection, which is not parallelised with the rest of the assembly. "The server has decided" is
/// the end of the connection's <c>audiosocket.session</c> activity, which the server stops only after it has awaited every
/// subscriber and, when it closes, after the close.
/// </remarks>
[Collection(AudioSocketConnectionMetricsGroup.Name)]
public sealed class AudioSocketServerSessionStartedFaultTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private const string FaultCounterName = "audiosocket.subscriber_faults";

    private const string FaultEventName = "SessionStartedSubscriberFailed";

    private static readonly byte[] HangupFrame = [0x00, 0x00, 0x00];

    // ── (a) the first throws synchronously ──

    [Fact]
    public async Task OnSessionStarted_ShouldRunTheSecondAndKeepTheConnection_WhenTheFirstThrowsSynchronously()
    {
        // Arrange
        await using var rig = await Rig.StartAsync();
        var secondGate = Gate();
        var second = new Subscriber(async session =>
        {
            await secondGate.Task;
        });
        rig.Server.OnSessionStarted += _ => throw new InvalidOperationException("the first subscriber's own failure");
        rig.Server.OnSessionStarted += second.Handle;

        // Act — the second is let go only once the first's failure is reported, so the close (if any) comes after it
        await using var peer = await rig.ConnectAsync();
        var entered = await Task.WhenAny(second.Entered, peer.Read).WaitAsync(SignalTimeout);
        entered.Should().BeSameAs(second.Entered, "a subscriber after one that throws synchronously is still invoked");
        await rig.Faults.ReachedAsync(1);
        secondGate.SetResult();
        await rig.DecidedAsync(peer);

        // Assert
        using (new AssertionScope())
        {
            second.Completed.IsCompletedSuccessfully.Should().BeTrue("the second subscriber runs to completion");
            rig.Faults.Total.Should().Be(1, "the first subscriber's failure is counted once");
            rig.Logger.FaultWarnings.Should().ContainSingle("the first subscriber's failure is logged once at Warning")
                .Which.ExceptionType.Should().Be(nameof(InvalidOperationException));
            rig.Logger.Errors.Should().BeEmpty("a subscriber's failure is not a connection error");
        }

        await Rig.ShouldStayOpenAndCarryAudioAsync(peer, await second.Session);
    }

    // ── (b) the first returns a faulted task; regression case of spec audit F2 ──

    [Theory]
    [InlineData(FaultShape.AtOnce)]
    [InlineData(FaultShape.Later)]
    public async Task OnSessionStarted_ShouldCountAndLogTheFirstsFaultAndKeepTheConnection_WhenTheFirstFaultsAndTheSecondSucceeds(
        FaultShape shape)
    {
        // Arrange
        await using var rig = await Rig.StartAsync();
        var firstGate = Gate();
        var secondGate = Gate();
        var second = new Subscriber(async _ => await secondGate.Task);
        rig.Server.OnSessionStarted += Faulting(shape, firstGate);
        rig.Server.OnSessionStarted += second.Handle;

        // Act
        await using var peer = await rig.ConnectAsync();
        await second.Entered.WaitAsync(SignalTimeout);
        firstGate.SetResult();
        await rig.Faults.ReachedAsync(1);
        secondGate.SetResult();
        await rig.DecidedAsync(peer);

        // Assert
        using (new AssertionScope())
        {
            rig.Faults.Total.Should().Be(1, "an earlier subscriber's faulted task is observed and counted once");
            rig.Logger.FaultWarnings.Should().ContainSingle("and logged once at Warning");
            rig.Logger.Errors.Should().BeEmpty();
        }

        await Rig.ShouldStayOpenAndCarryAudioAsync(peer, await second.Session);
    }

    // ── (c) the first succeeds, the last faults ──

    [Fact]
    public async Task OnSessionStarted_ShouldKeepTheConnection_WhenTheLastFaultsAfterTheFirstSucceeded()
    {
        // Arrange
        await using var rig = await Rig.StartAsync();
        var lastGate = Gate();
        var first = new Subscriber(_ => Task.CompletedTask);
        rig.Server.OnSessionStarted += first.Handle;
        rig.Server.OnSessionStarted += Faulting(FaultShape.Later, lastGate);

        // Act
        await using var peer = await rig.ConnectAsync();
        await first.Completed.WaitAsync(SignalTimeout);
        lastGate.SetResult();
        await rig.DecidedAsync(peer);

        // Assert
        using (new AssertionScope())
        {
            rig.Faults.Total.Should().Be(1, "the last subscriber's failure is counted once");
            rig.Logger.FaultWarnings.Should().ContainSingle();
            rig.Logger.Errors.Should().BeEmpty();
        }

        await Rig.ShouldStayOpenAndCarryAudioAsync(peer, await first.Session);
    }

    // ── (d) every subscriber failed ──

    [Theory]
    [InlineData(new[] { FaultShape.Throws })]
    [InlineData(new[] { FaultShape.AtOnce })]
    [InlineData(new[] { FaultShape.Later })]
    [InlineData(new[] { FaultShape.Later, FaultShape.Later })]
    [InlineData(new[] { FaultShape.Throws, FaultShape.Later })]
    public async Task OnSessionStarted_ShouldHangUpAndCloseAndReportEachFault_WhenEverySubscriberFails(FaultShape[] shapes)
    {
        // Arrange
        await using var rig = await Rig.StartAsync();
        var gate = Gate();
        foreach (var shape in shapes)
            rig.Server.OnSessionStarted += Faulting(shape, gate);

        // Act
        await using var peer = await rig.ConnectAsync();
        gate.SetResult();
        var read = await peer.Read.WaitAsync(SignalTimeout);
        await rig.DecidedAsync(peer);

        // Assert
        using (new AssertionScope())
        {
            read.ClosedByServer.Should().BeTrue("a connection no subscriber completed on is closed");
            read.Bytes.Should().Equal(HangupFrame, "the server says it is hanging up before it closes, so the call can go on in the dialplan");
            rig.Faults.Total.Should().Be(shapes.Length, "each failure is counted once");
            rig.Logger.FaultWarnings.Should().HaveCount(shapes.Length, "each failure is logged once at Warning");
            rig.Logger.Errors.Should().BeEmpty("a subscriber's failure is not logged as a connection error");
        }
    }

    [Fact]
    public async Task OnSessionStarted_ShouldLogOneWarningCountItAndHangUp_WhenTheOnlySubscriberFaultsWithOperationCanceledException()
    {
        // Re-pinned from AudioSocketServerEdgeCaseTests.Server_ShouldLogHandleConnectionError_WhenOnSessionStartedThrowsOperationCanceledException
        // (up to 2.10.x: one Error HandleConnectionError and a close with no frame). A subscriber runs only once the UUID
        // frame has arrived, so an OperationCanceledException it raises is the consumer's own failure, not the end of the
        // wait for the UUID; the UUID timeout runs on a fake clock that never moves, so it cannot fire.
        await using var rig = await Rig.StartAsync();
        rig.Server.OnSessionStarted += _ => ValueTask.FromException(new OperationCanceledException());

        // Act
        await using var peer = await rig.ConnectAsync();
        var read = await peer.Read.WaitAsync(SignalTimeout);
        await rig.DecidedAsync(peer);

        // Assert
        using (new AssertionScope())
        {
            read.Bytes.Should().Equal(HangupFrame, "the only subscriber failed, so the server hangs up and closes");
            rig.Logger.FaultWarnings.Should().ContainSingle("the subscriber's failure is logged once")
                .Which.ExceptionType.Should().Be(nameof(OperationCanceledException));
            rig.Logger.Entries.Should().NotContain(e => e.EventName == "NoUuidFrame", "the client sent its UUID frame");
            rig.Logger.Errors.Should().BeEmpty("a cancellation raised by a subscriber is that subscriber's failure, not a connection error");
            rig.Faults.Total.Should().Be(1);
        }
    }

    // ── (e) a subscriber that serves the call in the background, next to one that fails ──

    [Theory]
    [InlineData(BrokerOrder.BrokerThenFault)]
    [InlineData(BrokerOrder.FaultThenBroker)]
    [InlineData(BrokerOrder.ThrowThenBroker)]
    public async Task OnSessionStarted_ShouldLeaveTheCallToTheSubscriberServingIt_WhenASideSubscriberFails(BrokerOrder order)
    {
        // Arrange — the broker shape: start the handler, return at once (VoiceAiSessionBroker.Dispatch)
        await using var rig = await Rig.StartAsync();
        var sideGate = Gate();
        using var broker = new BackgroundReader();
        Func<AudioSocketSession, ValueTask> side = order == BrokerOrder.ThrowThenBroker
            ? Faulting(FaultShape.Throws, sideGate)
            : Faulting(FaultShape.Later, sideGate);
        if (order == BrokerOrder.BrokerThenFault)
        {
            rig.Server.OnSessionStarted += broker.Handle;
            rig.Server.OnSessionStarted += side;
        }
        else
        {
            rig.Server.OnSessionStarted += side;
            rig.Server.OnSessionStarted += broker.Handle;
        }

        // Act
        await using var peer = await rig.ConnectAsync();
        var started = await Task.WhenAny(broker.Started, peer.Read).WaitAsync(SignalTimeout);
        started.Should().BeSameAs(broker.Started, "the subscriber after a side subscriber that throws is still invoked");
        sideGate.SetResult();
        await rig.DecidedAsync(peer);
        const int frames = 10;
        for (var i = 0; i < frames; i++)
            await peer.SendAudioAsync(AudioPayload(i));

        // Assert
        var read = await broker.ReadAtLeastAsync(frames, peer.Read);
        using (new AssertionScope())
        {
            read.Should().Be(frames, "the side subscriber's failure does not end the call the broker is serving");
            peer.Read.IsCompleted.Should().BeFalse("the server did not close the connection");
            rig.Faults.Total.Should().Be(1);
            rig.Logger.Errors.Should().BeEmpty();
        }

        await peer.SendHangupAsync();
        (await peer.Read.WaitAsync(SignalTimeout)).Bytes.Should().BeEmpty("the server wrote nothing: no hangup of its own");
    }

    // ── (f) a failure is reported when it happens, not when its turn in the await comes ──

    [Fact]
    public async Task OnSessionStarted_ShouldReportTheSideFaultWhileTheLongSubscriberStillServes_WhenTheSideFaultsFirst()
    {
        // Arrange — the first subscriber serves the whole call: its task completes only when the test lets it
        await using var rig = await Rig.StartAsync();
        var callGate = Gate();
        var sideGate = Gate();
        var longOne = new Subscriber(async _ => await callGate.Task);
        rig.Server.OnSessionStarted += longOne.Handle;
        rig.Server.OnSessionStarted += Faulting(FaultShape.Later, sideGate);

        // Act
        await using var peer = await rig.ConnectAsync();
        await longOne.Entered.WaitAsync(SignalTimeout);
        sideGate.SetResult();
        var reported = await Task.WhenAny(
            Task.WhenAll(rig.Faults.ReachedAsync(1), rig.Logger.FaultWarningLogged), peer.Read).WaitAsync(SignalTimeout);

        // Assert — reported while the long subscriber's task is still running, and the call is not cut
        using (new AssertionScope())
        {
            peer.Read.IsCompleted.Should().BeFalse("a side subscriber's failure does not cut the call the first one serves");
            reported.Should().NotBeSameAs(peer.Read);
            rig.Faults.Total.Should().Be(1, "the side subscriber's fault is counted when it happens");
            longOne.Completed.IsCompleted.Should().BeFalse("the long subscriber is still serving the call");
            rig.Logger.FaultWarnings.Should().ContainSingle("the Warning is written when the fault happens, too");
        }

        callGate.SetResult();
        await rig.DecidedAsync(peer);
        await Rig.ShouldStayOpenAndCarryAudioAsync(peer, await longOne.Session);
    }

    // ── invocation shape: every subscriber entered before any is awaited ──

    [Fact]
    public async Task OnSessionStarted_ShouldEnterTheSecondSubscriber_WhileTheFirstHasNotCompleted()
    {
        // Arrange — a pin against invoke → await → next, which would start the second only after the call
        await using var rig = await Rig.StartAsync();
        var firstGate = Gate();
        var first = new Subscriber(async _ => await firstGate.Task);
        var second = new Subscriber(_ => Task.CompletedTask);
        rig.Server.OnSessionStarted += first.Handle;
        rig.Server.OnSessionStarted += second.Handle;

        // Act
        await using var peer = await rig.ConnectAsync();
        await second.Completed.WaitAsync(SignalTimeout);

        // Assert
        first.Completed.IsCompleted.Should().BeFalse("the second subscriber is entered while the first is still running");
        firstGate.SetResult();
        await rig.DecidedAsync(peer);
        rig.Faults.Total.Should().Be(0);
        await Rig.ShouldStayOpenAndCarryAudioAsync(peer, await first.Session);
    }

    // ── a throwing logger and OutOfMemoryException ──

    [Fact]
    public async Task OnSessionStarted_ShouldCountTheFaultAndRunTheSecond_WhenTheLoggerThrowsOnTheReport()
    {
        // Arrange
        var logger = new ReportThrowingLogger<AudioSocketServer>("OnSessionStarted subscriber failed");
        await using var rig = await Rig.StartAsync(logger);
        var second = new Subscriber(_ => Task.CompletedTask);
        rig.Server.OnSessionStarted += _ => throw new InvalidOperationException("the first subscriber's own failure");
        rig.Server.OnSessionStarted += second.Handle;

        // Act
        await using var peer = await rig.ConnectAsync();
        var entered = await Task.WhenAny(second.Completed, peer.Read).WaitAsync(SignalTimeout);
        await rig.DecidedAsync(peer);

        // Assert
        using (new AssertionScope())
        {
            entered.Should().BeSameAs(second.Completed, "the logger's failure does not leave the report");
            rig.Faults.Total.Should().Be(1, "the fault is counted before the log is attempted");
            logger.Thrown.Should().Be(1, "the report reached the logger, which threw");
        }

        await Rig.ShouldStayOpenAndCarryAudioAsync(peer, await second.Session);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleConnectionAsync_ShouldLetOutOfMemoryEscape_WhenASubscriberRaisesIt(bool asFaultedTask)
    {
        // Arrange — the handler directly, so the escape is observable
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0, ConnectionTimeout = TimeSpan.FromSeconds(30) },
            new CapturingLogger(),
            new FakeTimeProvider());
        // The runtime's own exception, thrown here on purpose: the test is that the server does not catch it.
#pragma warning disable CA2201, MA0012
        server.OnSessionStarted += asFaultedTask
            ? _ => ValueTask.FromException(new OutOfMemoryException())
            : _ => throw new OutOfMemoryException();
#pragma warning restore CA2201, MA0012
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port).WaitAsync(SignalTimeout);
        using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(SignalTimeout);
        await SendFrameAsync(peer, AudioSocketFrameType.Uuid, Guid.NewGuid().ToByteArray(bigEndian: true));

        // Act
        var escaped = await Record.ExceptionAsync(
            () => server.HandleConnectionAsync(accepted, CancellationToken.None).WaitAsync(SignalTimeout));

        // Assert
        escaped.Should().BeOfType<OutOfMemoryException>("ADR-0075 rule 1: OutOfMemoryException is never caught");
    }

    // ── fixtures ──

    public enum FaultShape
    {
        /// <summary>Throws from the delegate itself, before returning a task.</summary>
        Throws,

        /// <summary>Returns a task that has already faulted.</summary>
        AtOnce,

        /// <summary>Returns a running task that faults once the gate opens.</summary>
        Later,
    }

    public enum BrokerOrder { BrokerThenFault, FaultThenBroker, ThrowThenBroker }

    /// <summary>
    /// The close after every subscriber failed is bounded as the stop's endings are. The only subscriber leaves an audio
    /// write in flight that the far end never lets complete (it reads nothing), then fails, so the hangup frame waits
    /// behind that write. The server waits no longer than <see cref="AudioSocketServer.StopEndingBound"/> on its own
    /// clock, a fake here that the test moves past the bound once it is armed, then closes the connection without a frame.
    /// </summary>
    [Fact]
    public async Task OnSessionStarted_ShouldCloseWithoutAFrameOnceTheBoundRunsOut_WhenEverySubscriberFailedBehindAWriteThatNeverCompletes()
    {
        // Arrange
        await using var rig = await Rig.StartAsync();
        Task? inFlight = null;
        rig.Server.OnSessionStarted += session =>
        {
            // The loop bound is a failure bound: loopback buffers hold far less than this many frames.
            var frame = new byte[32_000];
            for (var i = 0; i < 2_000 && inFlight is null; i++)
            {
                var write = session.WriteAudioAsync(frame, AudioSocketFrameType.Audio);
                if (write.IsCompleted)
                    write.GetAwaiter().GetResult();
                else
                    inFlight = write.AsTask();
            }

            return ValueTask.FromException(new InvalidOperationException("the only subscriber's own failure"));
        };

        // Act
        using var peer = await StalledPeer.ConnectAsync(rig.Server.BoundPort);
        var decided = rig.DecidedAsync(peer.ChannelId);
        var armed = await NextTimerAsync(rig.Clock, AudioSocketServer.StopEndingBound);
        var heldBeforeTheBound = !decided.IsCompleted;
        rig.Clock.Advance(AudioSocketServer.StopEndingBound);
        var decidedFault = await Record.ExceptionAsync(() => decided);
        var frames = await peer.ReadFramesToEndAsync();
        var writeFault = await Record.ExceptionAsync(() => (inFlight ?? Task.CompletedTask).WaitAsync(SignalTimeout));

        // Assert
        using (new AssertionScope())
        {
            inFlight.Should().NotBeNull("the far end reads nothing, so a write eventually waits for the transport");
            armed.Should().BeTrue("the close arms its bound on the server's clock");
            heldBeforeTheBound.Should().BeTrue("the hangup frame waits behind the write in flight until the bound runs out");
            decidedFault.Should().BeNull("the server closes the connection once its bound has run out");
            frames.ReachedEnd.Should().BeTrue("the connection is closed");
            frames.Types.Should().NotContain(AudioSocketFrameType.Hangup, "no hangup frame is written once the bound ran out");
            writeFault.Should().NotBeNull("the write in flight fails when the connection is closed under it");
            rig.Faults.Total.Should().Be(1, "the subscriber's failure is counted once");
            rig.Logger.Errors.Should().BeEmpty("a bounded close is not a connection error");
        }
    }

    /// <summary>
    /// Waits until <paramref name="clock"/> has a timer due after <paramref name="dueTime"/>: the moment the code under
    /// test is parked on it. False when no such timer is created within the signal bound.
    /// </summary>
    private static async Task<bool> NextTimerAsync(FakeTimeProvider clock, TimeSpan dueTime)
    {
        using var bound = new CancellationTokenSource(SignalTimeout);
        try
        {
            while (true)
            {
                var timer = await clock.TimersCreated.ReadAsync(bound.Token);
                if (timer.DueTime == dueTime)
                    return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Func<AudioSocketSession, ValueTask> Faulting(FaultShape shape, TaskCompletionSource gate) => shape switch
    {
        FaultShape.Throws => _ => throw new InvalidOperationException("a subscriber's own failure"),
        FaultShape.AtOnce => _ => ValueTask.FromException(new InvalidOperationException("a subscriber's own failure")),
        _ => async _ =>
        {
            await gate.Task;
            throw new InvalidOperationException("a subscriber's own failure");
        },
    };

    private static byte[] AudioPayload(int seed)
    {
        var payload = new byte[320];
        payload.AsSpan().Fill((byte)(seed + 1));
        return payload;
    }

    private static async Task SendFrameAsync(TcpClient peer, AudioSocketFrameType type, byte[] payload)
    {
        var frame = new ArrayBufferWriter<byte>();
        AudioSocketFrameCodec.WriteFrame(frame, type, payload);
        await peer.GetStream().WriteAsync(frame.WrittenMemory);
    }

    /// <summary>A subscriber that records the session and signals entry and completion.</summary>
    private sealed class Subscriber(Func<AudioSocketSession, Task> body)
    {
        private readonly TaskCompletionSource<AudioSocketSession> _session = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AudioSocketSession> Session => _session.Task;

        public Task Entered => _session.Task;

        public Task Completed => _completed.Task;

        public async ValueTask Handle(AudioSocketSession session)
        {
            _session.TrySetResult(session);
            await body(session);
            _completed.TrySetResult();
        }
    }

    /// <summary>The broker's shape: start reading the call in the background, return at once.</summary>
    private sealed class BackgroundReader : IDisposable
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _frames = new(0);
        private int _count;

        public Task Started => _started.Task;

        public ValueTask Handle(AudioSocketSession session)
        {
            _started.TrySetResult();
            _ = Task.Run(async () =>
            {
                await foreach (var _ in session.ReadAudioAsync())
                {
                    Interlocked.Increment(ref _count);
                    _frames.Release();
                }
            });
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Waits until the reader has read <paramref name="expected"/> frames, or the connection ended, and returns how
        /// many it read.
        /// </summary>
        public async Task<int> ReadAtLeastAsync(int expected, Task connectionEnded)
        {
            for (var i = 0; i < expected; i++)
            {
                var next = _frames.WaitAsync(SignalTimeout);
                if (await Task.WhenAny(next, connectionEnded) != next || !await next)
                    break;
            }

            return Volatile.Read(ref _count);
        }

        public void Dispose() => _frames.Dispose();
    }

    /// <summary>What a peer read before its connection ended.</summary>
    private sealed record PeerRead(byte[] Bytes, bool ClosedByServer);

    /// <summary>A raw peer playing Asterisk's part: sends the UUID frame and reads everything until the close.</summary>
    private sealed class Peer : IAsyncDisposable
    {
        private readonly TcpClient _client;

        private Peer(TcpClient client, Guid channelId)
        {
            _client = client;
            ChannelId = channelId;
            Read = ReadUntilClosedAsync(client);
        }

        public Guid ChannelId { get; }

        /// <summary>Everything the server wrote, once the connection ended.</summary>
        public Task<PeerRead> Read { get; }

        public static async Task<Peer> ConnectAsync(int port)
        {
            var client = new TcpClient();
            try
            {
                // 127.0.0.1, never "localhost": the name resolves ::1 first on this host (ADR-0044).
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SignalTimeout);
                var channelId = Guid.NewGuid();
                await SendFrameAsync(client, AudioSocketFrameType.Uuid, channelId.ToByteArray(bigEndian: true));
                return new Peer(client, channelId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                client.Dispose();
                throw;
            }
        }

        public Task SendAudioAsync(byte[] payload) => SendFrameAsync(_client, AudioSocketFrameType.Audio, payload);

        public Task SendHangupAsync() => SendFrameAsync(_client, AudioSocketFrameType.Hangup, []);

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            return ValueTask.CompletedTask;
        }

        private static async Task<PeerRead> ReadUntilClosedAsync(TcpClient peer)
        {
            using var all = new MemoryStream();
            var buffer = new byte[1024];
            try
            {
                int read;
                while ((read = await peer.GetStream().ReadAsync(buffer)) > 0)
                    all.Write(buffer, 0, read);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return new PeerRead(all.ToArray(), ClosedByServer: false);
            }

            return new PeerRead(all.ToArray(), ClosedByServer: true);
        }
    }

    /// <summary>The frame types a <see cref="StalledPeer"/> read, and whether the server ended the stream.</summary>
    private sealed record FramesRead(bool ReachedEnd, IReadOnlyList<AudioSocketFrameType> Types);

    /// <summary>
    /// A far end that sends its UUID frame and then reads nothing until asked, with a small receive buffer, so that the
    /// server's writes stall; disposed, it resets the connection, which ends any write still in flight.
    /// </summary>
    private sealed class StalledPeer : IDisposable
    {
        private readonly TcpClient _client;

        private StalledPeer(TcpClient client, Guid channelId)
        {
            _client = client;
            ChannelId = channelId;
        }

        public Guid ChannelId { get; }

        public static async Task<StalledPeer> ConnectAsync(int port)
        {
            var client = new TcpClient { ReceiveBufferSize = 4096 };
            try
            {
                // 127.0.0.1, never "localhost": the name resolves ::1 first on this host (ADR-0044).
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SignalTimeout);
                var channelId = Guid.NewGuid();
                await SendFrameAsync(client, AudioSocketFrameType.Uuid, channelId.ToByteArray(bigEndian: true));
                return new StalledPeer(client, channelId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                client.Dispose();
                throw;
            }
        }

        /// <summary>Reads until the server ends the stream or the signal bound runs out, and parses the whole frames.</summary>
        public async Task<FramesRead> ReadFramesToEndAsync()
        {
            using var all = new MemoryStream();
            var buffer = new byte[4096];
            var reachedEnd = false;
            using var bound = new CancellationTokenSource(SignalTimeout);
            try
            {
                int read;
                while ((read = await _client.GetStream().ReadAsync(buffer, bound.Token)) > 0)
                    all.Write(buffer, 0, read);
                reachedEnd = true;
            }
            catch (IOException)
            {
                reachedEnd = true; // a reset is also an end of the connection; the bytes before it are kept
            }
            catch (OperationCanceledException)
            {
                // The line is still open: reported by the caller's assertion.
            }

            var bytes = all.ToArray();
            var types = new List<AudioSocketFrameType>();
            var offset = 0;
            while (bytes.Length - offset >= 3)
            {
                var length = (bytes[offset + 1] << 8) | bytes[offset + 2];
                if (bytes.Length - offset - 3 < length)
                    break;
                types.Add((AudioSocketFrameType)bytes[offset]);
                offset += 3 + length;
            }

            return new FramesRead(reachedEnd, types);
        }

        public void Dispose() => _client.Dispose();
    }

    /// <summary>One server, its logger, its fault counter and its per-connection "decided" signal.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ActivityListener _activities;
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _decided = new();

        private Rig(AudioSocketServer server, CapturingLogger logger, FakeTimeProvider clock)
        {
            Server = server;
            Logger = logger;
            Clock = clock;
            Faults = new FaultCounter();
            _activities = new ActivityListener
            {
                ShouldListenTo = source => ReferenceEquals(source, AudioSocketActivitySource.Source),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.GetTagItem("asterisk.channel.id") is string id)
                        DecidedSignal(id).TrySetResult();
                },
            };
            ActivitySource.AddActivityListener(_activities);
        }

        public AudioSocketServer Server { get; }

        public CapturingLogger Logger { get; }

        public FaultCounter Faults { get; }

        /// <summary>The server's clock: it moves only when a test advances it.</summary>
        public FakeTimeProvider Clock { get; }

        public static Task<Rig> StartAsync() => StartAsync(null);

        public static async Task<Rig> StartAsync(ILogger<AudioSocketServer>? logger)
        {
            var capturing = new CapturingLogger();
            var clock = new FakeTimeProvider();
            var server = new AudioSocketServer(
                new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0, ConnectionTimeout = TimeSpan.FromSeconds(30) },
                logger ?? capturing,
                clock);
            var rig = new Rig(server, capturing, clock);
            await server.StartAsync(CancellationToken.None);
            return rig;
        }

        public Task<Peer> ConnectAsync() => Peer.ConnectAsync(Server.BoundPort);

        /// <summary>Completes once the server has awaited every subscriber of <paramref name="peer"/>'s session and acted.</summary>
        public Task DecidedAsync(Peer peer) => DecidedAsync(peer.ChannelId);

        /// <summary>Completes once the server has awaited every subscriber of <paramref name="channelId"/>'s session and acted.</summary>
        public Task DecidedAsync(Guid channelId) =>
            DecidedSignal(channelId.ToString()).Task.WaitAsync(SignalTimeout);

        /// <summary>
        /// The connection is still served after the server decided: a frame the peer sends reaches the session, a frame
        /// the session writes reaches the peer, and the server writes nothing else (no hangup) until the peer hangs up.
        /// </summary>
        public static async Task ShouldStayOpenAndCarryAudioAsync(Peer peer, AudioSocketSession session)
        {
            peer.Read.IsCompleted.Should().BeFalse("a connection a subscriber completed on stays open");
            var inbound = AudioPayload(7);
            await peer.SendAudioAsync(inbound);
            await using var frames = session.ReadAudioAsync().GetAsyncEnumerator();
            (await frames.MoveNextAsync().AsTask().WaitAsync(SignalTimeout)).Should().BeTrue("the session still reads audio");
            frames.Current.ToArray().Should().Equal(inbound);

            var outbound = AudioPayload(9);
            await session.WriteAudioAsync(outbound);
            await peer.SendHangupAsync();
            var read = await peer.Read.WaitAsync(SignalTimeout);
            var expected = new ArrayBufferWriter<byte>();
            AudioSocketFrameCodec.WriteFrame(expected, AudioSocketFrameType.Audio, outbound);
            read.Bytes.Should().Equal(expected.WrittenSpan.ToArray(), "the server wrote the session's audio and no hangup of its own");
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            _activities.Dispose();
            Faults.Dispose();
        }

        private TaskCompletionSource DecidedSignal(string channelId) =>
            _decided.GetOrAdd(channelId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>Totals <c>audiosocket.subscriber_faults</c>, found by name so the test builds before it exists.</summary>
    private sealed class FaultCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Lock _gate = new();
        private readonly List<(long Target, TaskCompletionSource Signal)> _waiters = [];
        private long _total;

        public FaultCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, AudioSocketMetrics.Meter) && instrument.Name == FaultCounterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
            {
                lock (_gate)
                {
                    _total += value;
                    foreach (var (target, signal) in _waiters)
                    {
                        if (_total >= target)
                            signal.TrySetResult();
                    }
                }
            });
            _listener.Start();
        }

        public long Total
        {
            get
            {
                lock (_gate)
                    return _total;
            }
        }

        /// <summary>Completes once the total reached <paramref name="target"/>; fails the test at the bound.</summary>
        public Task ReachedAsync(long target)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_total >= target)
                    signal.TrySetResult();
                else
                    _waiters.Add((target, signal));
            }

            return signal.Task.WaitAsync(SignalTimeout);
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>A server log entry, reduced to what these tests assert on.</summary>
    private sealed record LogEntry(LogLevel Level, string? EventName, string? ExceptionType);

    private sealed class CapturingLogger : ILogger<AudioSocketServer>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        private readonly TaskCompletionSource _faultWarningLogged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the first subscriber-fault Warning is written.</summary>
        public Task FaultWarningLogged => _faultWarningLogged.Task;

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public IReadOnlyCollection<LogEntry> FaultWarnings =>
            _entries.Where(e => e.Level == LogLevel.Warning && e.EventName == FaultEventName).ToArray();

        public IReadOnlyCollection<LogEntry> Errors => _entries.Where(e => e.Level >= LogLevel.Error).ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _entries.Enqueue(new LogEntry(logLevel, eventId.Name, exception?.GetType().Name));
            if (logLevel == LogLevel.Warning && eventId.Name == FaultEventName)
                _faultWarningLogged.TrySetResult();
        }
    }
}
