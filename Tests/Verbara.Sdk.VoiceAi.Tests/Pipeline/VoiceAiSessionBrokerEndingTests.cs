using System.Net;
using System.Net.Sockets;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.Pipeline;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Pipeline;

/// <summary>
/// Who ends an AudioSocket line once the handler the broker started for it is done. The broker does:
/// whether the handler returned or threw, the far end reads one hangup frame and then the close, so on
/// Asterisk 20 and later the dialplan goes on instead of the caller sitting in silence. The ending is
/// idempotent with every other ending — a handler that hung up itself still produces one frame in
/// all, and a session the caller already ended gets none.
/// </summary>
/// <remarks>
/// <para>
/// Real server, real broker, real TCP on <c>127.0.0.1</c> port 0. The far end is a raw socket that
/// sends the identifying frame and then reads everything the server writes until the end of the
/// stream, so the assertion is on the bytes Asterisk would read, frame by frame.
/// </para>
/// <para>
/// No wait here is an ordering device. The read to the end of the stream is bounded by
/// <see cref="SignalTimeout"/>, whose expiry is a failure (a line left open), never a pace. Where a
/// row asserts what the broker logged, it first awaits the broker's stop, which returns only once the
/// handlers it started — and their endings — are done.
/// </para>
/// </remarks>
public sealed class VoiceAiSessionBrokerEndingTests
{
    /// <summary>Upper bound on any single wait below. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>One 20 ms frame of 8 kHz signed-linear silence: what the handlers here "play".</summary>
    private static readonly byte[] OneAudioFrame = new byte[320];

    [Fact]
    public async Task HandleSessionAsync_ShouldBeFollowedByOneHangupFrameAndTheClose_WhenTheHandlerReturnsWithoutHangingUp()
    {
        var handler = new PlayingHandler(HandlerEnding.Return);
        await using var rig = await EndingRig.StartAsync(handler);

        using var peer = await RawPeer.ConnectAsync(rig.Server.BoundPort);
        var read = await peer.ReadToEndAsync(SignalTimeout);

        using (new AssertionScope())
        {
            AssertOneHangupThenClose(read, expectedAudioFrames: 1, "the handler returned without hanging up");
            handler.Returned.IsCompleted.Should().BeTrue(
                "the line ends because the handler is done, never while it still runs");
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldBeFollowedByOneHangupFrameAndTheClose_WhenTheHandlerThrows()
    {
        var handler = new PlayingHandler(HandlerEnding.Throw);
        await using var rig = await EndingRig.StartAsync(handler);

        using var peer = await RawPeer.ConnectAsync(rig.Server.BoundPort);
        var read = await peer.ReadToEndAsync(SignalTimeout);
        await rig.Logger.FirstError.WaitAsync(SignalTimeout);
        await rig.StopBrokerAsync();

        using (new AssertionScope())
        {
            AssertOneHangupThenClose(read, expectedAudioFrames: 1, "the handler threw");
            rig.Logger.Entries.Count(e => e.Level == LogLevel.Error).Should().Be(
                1, "the handler's failure is still logged at Error, exactly once");
            rig.Logger.Entries.Single(e => e.Level == LogLevel.Error).Exception.Should().NotBeNull(
                "the Error entry carries the handler's exception");
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldLeaveExactlyOneHangupFrame_WhenTheHandlerHangsUpAndThenReturns()
    {
        var handler = new PlayingHandler(HandlerEnding.HangUpThenReturn);
        await using var rig = await EndingRig.StartAsync(handler);

        using var peer = await RawPeer.ConnectAsync(rig.Server.BoundPort);
        var read = await peer.ReadToEndAsync(SignalTimeout);
        await handler.Returned.WaitAsync(SignalTimeout);
        await rig.StopBrokerAsync();

        using (new AssertionScope())
        {
            AssertOneHangupThenClose(
                read, expectedAudioFrames: 1, "the handler hung up itself, so the broker's ending adds no second frame");
            rig.Logger.Entries.Where(e => e.Level >= LogLevel.Warning).Should().BeEmpty(
                "ending a session the handler already ended is not a fault");
        }
    }

    [Fact]
    public async Task HandleSessionAsync_ShouldWriteNothingAndRaiseNothing_WhenTheCallerHungUpBeforeTheHandlerReturned()
    {
        var handler = new WaitForCallerHandler();
        await using var rig = await EndingRig.StartAsync(handler);

        using var peer = await RawPeer.ConnectAsync(rig.Server.BoundPort);
        await handler.Watching.WaitAsync(SignalTimeout);
        await peer.SendHangupAsync();
        var read = await peer.ReadToEndAsync(SignalTimeout);
        await handler.Returned.WaitAsync(SignalTimeout);
        await rig.StopBrokerAsync();

        using (new AssertionScope())
        {
            read.ReachedEnd.Should().BeTrue("the session tears its transport down when the caller hangs up");
            read.Frames.Should().BeEmpty(
                "the session had processed the caller's ending before the handler returned, so the broker's " +
                "ending writes no hangup frame");
            handler.Fault.Should().BeNull("nothing the ending does reaches the handler");
            rig.Logger.Entries.Where(e => e.Level >= LogLevel.Warning).Should().BeEmpty(
                "a line the caller already ended is not a fault for the broker");
        }
    }

    private static void AssertOneHangupThenClose(PeerRead read, int expectedAudioFrames, string because)
    {
        using var scope = new AssertionScope();
        read.ReachedEnd.Should().BeTrue(
            $"{because}, so the broker ends the line: the far end reads the end of the connection within " +
            $"{SignalTimeout.TotalSeconds:0} s instead of a line left open in silence (read so far: {read.Describe()})");
        read.Frames.Count(f => f.Type == AudioSocketFrameType.Hangup).Should().Be(
            1, $"{because}: exactly one hangup frame in all (read: {read.Describe()})");
        read.Frames.Should().HaveCount(
            expectedAudioFrames + 1, $"the handler's audio and then the hangup frame (read: {read.Describe()})");
        if (read.Frames.Count > 0)
            read.Frames[^1].Should().Be(
                new PeerFrame(AudioSocketFrameType.Hangup, 0), "the hangup frame is the last thing the far end reads");
    }

    private enum HandlerEnding
    {
        Return,
        Throw,
        HangUpThenReturn,
    }

    /// <summary>Plays one audio frame, then returns, throws, or hangs up and returns.</summary>
    private sealed class PlayingHandler(HandlerEnding ending) : ISessionHandler
    {
        private readonly TaskCompletionSource _returned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Returned => _returned.Task;

        public async ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default)
        {
            try
            {
                await session.WriteAudioAsync(OneAudioFrame, AudioSocketFrameType.Audio, ct);
                switch (ending)
                {
                    case HandlerEnding.Throw:
                        throw new InvalidOperationException("The handler failed after playing its audio.");
                    case HandlerEnding.HangUpThenReturn:
                        await session.HangupAsync(ct);
                        break;
                    case HandlerEnding.Return:
                    default:
                        break;
                }
            }
            finally
            {
                _returned.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Watches for the caller's hangup and returns once the session has processed it; records any
    /// exception that reaches it.
    /// </summary>
    private sealed class WaitForCallerHandler : ISessionHandler
    {
        private readonly TaskCompletionSource _watching = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _returned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Watching => _watching.Task;

        public Task Returned => _returned.Task;

        public Exception? Fault { get; private set; }

        public async ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default)
        {
            var hungUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.OnHangup += () => hungUp.TrySetResult();
            if (!session.IsConnected)
                hungUp.TrySetResult();
            _watching.TrySetResult();
            try
            {
                await hungUp.Task.WaitAsync(SignalTimeout, ct);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Recorded, not rethrown: the row asserts that nothing reached the handler.
                Fault = ex;
            }
            finally
            {
                _returned.TrySetResult();
            }
        }
    }

    /// <summary>A started server and a started broker over it, with the broker's log recorded.</summary>
    private sealed class EndingRig : IAsyncDisposable
    {
        private int _brokerStopped;

        private EndingRig(AudioSocketServer server, VoiceAiSessionBroker broker, RecordingLogger logger)
        {
            Server = server;
            Broker = broker;
            Logger = logger;
        }

        public AudioSocketServer Server { get; }

        public VoiceAiSessionBroker Broker { get; }

        public RecordingLogger Logger { get; }

        public static async Task<EndingRig> StartAsync(ISessionHandler handler)
        {
            var server = new AudioSocketServer(
                new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0 },
                NullLogger<AudioSocketServer>.Instance);
            var logger = new RecordingLogger();
            var broker = new VoiceAiSessionBroker(server, handler, logger);
            await server.StartAsync(CancellationToken.None);
            await broker.StartAsync(CancellationToken.None);
            return new EndingRig(server, broker, logger);
        }

        /// <summary>
        /// A graceful stop. Bounded, because a stop that waits for its handlers must find them done
        /// here; reaching the bound is a failure.
        /// </summary>
        public async Task StopBrokerAsync()
        {
            if (Interlocked.Exchange(ref _brokerStopped, 1) == 0)
                await Broker.StopAsync(CancellationToken.None).WaitAsync(SignalTimeout);
        }

        public async ValueTask DisposeAsync()
        {
            Broker.Dispose();
            await Server.DisposeAsync();
        }
    }

    /// <summary>A far end that speaks the AudioSocket wire directly, as Asterisk does.</summary>
    private sealed class RawPeer : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        private RawPeer(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public static async Task<RawPeer> ConnectAsync(int port)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                var peer = new RawPeer(client);
                var uuid = new byte[19];
                uuid[0] = (byte)AudioSocketFrameType.Uuid;
                uuid[2] = 16;
                Guid.NewGuid().TryWriteBytes(uuid.AsSpan(3), bigEndian: true, out _);
                await peer._stream.WriteAsync(uuid);
                return peer;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The peer never reached the caller: release its socket before reporting the failure.
                client.Dispose();
                throw;
            }
        }

        public async Task SendHangupAsync() =>
            await _stream.WriteAsync(new byte[] { (byte)AudioSocketFrameType.Hangup, 0, 0 });

        /// <summary>
        /// Reads until the server ends the stream or <paramref name="bound"/> runs out; what was read is
        /// returned either way, so a line left open fails on its assertion rather than on a timeout.
        /// </summary>
        public async Task<PeerRead> ReadToEndAsync(TimeSpan bound)
        {
            var received = new List<byte>();
            var reachedEnd = false;
            var reading = ReadAllAsync(_stream, received);
            try
            {
                await reading.WaitAsync(bound);
                reachedEnd = true;
            }
            catch (TimeoutException)
            {
                // The line is still open: reported by the caller's assertion, with what was read.
            }

            byte[] bytes;
            lock (received)
                bytes = [.. received];
            return new PeerRead(reachedEnd, PeerFrame.Parse(bytes), bytes.Length);
        }

        private static async Task ReadAllAsync(NetworkStream stream, List<byte> received)
        {
            var buffer = new byte[4096];
            try
            {
                int n;
                while ((n = await stream.ReadAsync(buffer)) > 0)
                {
                    lock (received)
                        received.AddRange(buffer.AsSpan(0, n).ToArray());
                }
            }
            catch (IOException)
            {
                // A reset is also an end of the connection; the bytes before it are kept.
            }
            catch (ObjectDisposedException)
            {
                // The peer was disposed while a read was still pending on a line left open.
            }
        }

        public void Dispose()
        {
            _stream.Dispose();
            _client.Dispose();
        }
    }

    private sealed record PeerFrame(AudioSocketFrameType Type, int Length)
    {
        public static List<PeerFrame> Parse(byte[] bytes)
        {
            var frames = new List<PeerFrame>();
            var offset = 0;
            while (bytes.Length - offset >= 3)
            {
                var length = (bytes[offset + 1] << 8) | bytes[offset + 2];
                if (bytes.Length - offset - 3 < length)
                    break;
                frames.Add(new PeerFrame((AudioSocketFrameType)bytes[offset], length));
                offset += 3 + length;
            }

            return frames;
        }
    }

    private sealed record PeerRead(bool ReachedEnd, IReadOnlyList<PeerFrame> Frames, int ByteCount)
    {
        public string Describe() =>
            ByteCount == 0
                ? "no bytes"
                : $"{ByteCount} bytes: " + string.Join(", ", Frames.Select(f => $"{f.Type}({f.Length})"));
    }

    /// <summary>The broker's log, kept so a row can count its Error entries and see nothing at Warning.</summary>
    private sealed class RecordingLogger : ILogger<VoiceAiSessionBroker>
    {
        private readonly List<Entry> _entries = [];
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _firstError = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstError => _firstError.Task;

        public IReadOnlyList<Entry> Entries
        {
            get { lock (_gate) return [.. _entries]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
                _entries.Add(new Entry(logLevel, formatter(state, exception), exception));
            if (logLevel == LogLevel.Error)
                _firstError.TrySetResult();
        }

        internal sealed record Entry(LogLevel Level, string Message, Exception? Exception);
    }
}
