using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Sdk;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Tests;

/// <summary>
/// A session's hangup is idempotent and never interleaves with an audio write. On a session that has
/// already ended — the far end hung up, the session was hung up before, or its owner disposed it — the
/// hangup completes and writes nothing. On a live session it writes one hangup frame and ends the
/// session, and a hangup issued while an audio write is in flight waits for that write, so the far end
/// reads whole frames only.
/// </summary>
/// <remarks>
/// Real server on <c>127.0.0.1</c> port 0, and a raw socket as the far end that reads only when the
/// test tells it to, so an audio write can be held in flight by the far end not reading. Every bound
/// below is a failure bound, never a pace.
/// </remarks>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class AudioSocketSessionHangupTests : IAsyncLifetime
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private readonly AudioSocketServer _server = new(
        new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0 },
        NullLogger<AudioSocketServer>.Instance);

    private readonly List<RawPeer> _peers = [];

    [Fact]
    public async Task HangupAsync_ShouldCompleteAndWriteNothing_WhenTheCallerAlreadyHungUp()
    {
        var (session, peer) = await ConnectAsync();
        var ended = SessionEnded(session);
        await peer.SendHangupAsync();
        await ended.WaitAsync(SignalTimeout);

        var hangup = await Record.ExceptionAsync(async () => await session.HangupAsync());
        var read = await peer.ReadToEndAsync(SignalTimeout);

        using (new AssertionScope())
        {
            hangup.Should().BeNull("a hangup on a session the far end already ended has nothing left to do");
            read.ReachedEnd.Should().BeTrue("the session tore its transport down when the caller hung up");
            read.ByteCount.Should().Be(0, $"nothing is written to a session that already ended (read: {read.Describe()})");
        }
    }

    [Fact]
    public async Task HangupAsync_ShouldLeaveOneHangupFrameInAll_WhenCalledTwice()
    {
        var (session, peer) = await ConnectAsync();
        await session.HangupAsync();

        var second = await Record.ExceptionAsync(async () => await session.HangupAsync());
        var read = await peer.ReadToEndAsync(SignalTimeout);

        using (new AssertionScope())
        {
            second.Should().BeNull("a second hangup on a session already hung up completes as a no-op");
            read.ReachedEnd.Should().BeTrue("the first hangup ended the session");
            read.Frames.Should().Equal(
                [new PeerFrame(AudioSocketFrameType.Hangup, 0)],
                $"the far end reads exactly one hangup frame in all (read: {read.Describe()})");
        }
    }

    [Fact]
    public async Task HangupAsync_ShouldCompleteAndWriteNothing_WhenTheOwnerDisposedTheSession()
    {
        var (session, peer) = await ConnectAsync();
        await session.DisposeAsync();

        var hangup = await Record.ExceptionAsync(async () => await session.HangupAsync());
        var read = await peer.ReadToEndAsync(SignalTimeout);

        using (new AssertionScope())
        {
            hangup.Should().BeNull("a hangup on a session its owner already disposed has nothing left to do");
            read.ReachedEnd.Should().BeTrue("the disposal released the transport");
            read.ByteCount.Should().Be(0, $"nothing is written once the session ended (read: {read.Describe()})");
        }
    }

    [Fact]
    public async Task HangupAsync_ShouldWaitForTheAudioWriteInFlight_WhenCalledBeforeThatWriteHasFinished()
    {
        var (session, peer) = await ConnectAsync();
        var frame = new byte[32_000];

        // The far end does not read yet, so the socket's buffers fill and one write stays in flight.
        // The loop bound is a failure bound: loopback buffers hold far less than this many frames.
        Task? inFlight = null;
        var written = 0;
        for (var i = 0; i < 2_000 && inFlight is null; i++)
        {
            var write = session.WriteAudioAsync(frame, AudioSocketFrameType.Audio);
            written++;
            if (write.IsCompleted)
                await write;
            else
                inFlight = write.AsTask();
        }

        var pending = inFlight
            ?? throw new XunitException("the far end reads nothing, so a write eventually waits for the transport");

        var hangup = Record.ExceptionAsync(async () => await session.HangupAsync());
        peer.StartReading();
        var read = await peer.ReadToEndAsync(SignalTimeout);
        var writeFault = await Record.ExceptionAsync(() => pending.WaitAsync(SignalTimeout));
        var hangupFault = await hangup.WaitAsync(SignalTimeout);

        using (new AssertionScope())
        {
            writeFault.Should().BeNull("the hangup waits for the audio write in flight instead of cutting it");
            hangupFault.Should().BeNull("a hangup on a live session completes once its frame is out");
            read.ReachedEnd.Should().BeTrue("the hangup ends the session after its frame");
            read.LeftoverBytes.Should().Be(
                0, $"every frame the far end reads is whole, never parts of two interleaved (read: {read.Describe()})");
            read.Frames.Count(f => f.Type == AudioSocketFrameType.Audio && f.Length == frame.Length).Should().Be(
                written, $"every audio frame written arrives whole (read: {read.Describe()})");
            read.Frames.Should().HaveCount(written + 1, $"the audio frames and then the hangup frame (read: {read.Describe()})");
            if (read.Frames.Count > 0)
                read.Frames[^1].Should().Be(
                    new PeerFrame(AudioSocketFrameType.Hangup, 0), "the hangup frame follows the audio, whole");
        }
    }

    public async Task InitializeAsync() => await _server.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        foreach (var peer in _peers)
            peer.Dispose();
        await _server.DisposeAsync();
    }

    private async Task<(AudioSocketSession Session, RawPeer Peer)> ConnectAsync()
    {
        var started = new TaskCompletionSource<AudioSocketSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.OnSessionStarted += session =>
        {
            started.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        var peer = await RawPeer.ConnectAsync(_server.BoundPort);
        _peers.Add(peer);
        var session = await started.Task.WaitAsync(SignalTimeout);
        return (session, peer);
    }

    /// <summary>Completes once the session has processed its ending (torn down and raised its hangup).</summary>
    private static Task SessionEnded(AudioSocketSession session)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnHangup += () => ended.TrySetResult();
        if (!session.IsConnected)
            ended.TrySetResult();
        return ended.Task;
    }

    /// <summary>
    /// A far end that speaks the AudioSocket wire directly, as Asterisk does, and reads only once told
    /// to (<see cref="StartReading"/>, or the first <see cref="ReadToEndAsync"/>).
    /// </summary>
    private sealed class RawPeer : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly List<byte> _received = [];
        private readonly Lock _gate = new();
        private Task? _reading;

        private RawPeer(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public static async Task<RawPeer> ConnectAsync(int port)
        {
            var client = new TcpClient { ReceiveBufferSize = 4096 };
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                var uuid = new byte[19];
                uuid[0] = (byte)AudioSocketFrameType.Uuid;
                uuid[2] = 16;
                Guid.NewGuid().TryWriteBytes(uuid.AsSpan(3), bigEndian: true, out _);
                await client.GetStream().WriteAsync(uuid);
                return new RawPeer(client);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The peer never reached the server: release its socket before reporting the failure.
                client.Dispose();
                throw;
            }
        }

        public async Task SendHangupAsync() =>
            await _stream.WriteAsync(new byte[] { (byte)AudioSocketFrameType.Hangup, 0, 0 });

        public void StartReading()
        {
            lock (_gate)
                _reading ??= ReadAllAsync();
        }

        /// <summary>
        /// Reads until the server ends the stream or <paramref name="bound"/> runs out; what was read is
        /// returned either way, so a line left open fails on its assertion rather than on a timeout.
        /// </summary>
        public async Task<PeerRead> ReadToEndAsync(TimeSpan bound)
        {
            StartReading();
            var reachedEnd = false;
            try
            {
                await _reading!.WaitAsync(bound);
                reachedEnd = true;
            }
            catch (TimeoutException)
            {
                // The line is still open: reported by the caller's assertion, with what was read.
            }

            byte[] bytes;
            lock (_gate)
                bytes = [.. _received];
            return PeerRead.Parse(reachedEnd, bytes);
        }

        private async Task ReadAllAsync()
        {
            var buffer = new byte[16_384];
            try
            {
                int n;
                while ((n = await _stream.ReadAsync(buffer)) > 0)
                {
                    lock (_gate)
                        _received.AddRange(buffer.AsSpan(0, n).ToArray());
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

    private sealed record PeerFrame(AudioSocketFrameType Type, int Length);

    private sealed record PeerRead(bool ReachedEnd, IReadOnlyList<PeerFrame> Frames, int ByteCount, int LeftoverBytes)
    {
        public static PeerRead Parse(bool reachedEnd, byte[] bytes)
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

            return new PeerRead(reachedEnd, frames, bytes.Length, bytes.Length - offset);
        }

        public string Describe()
        {
            if (ByteCount == 0)
                return "no bytes";

            var types = Frames.GroupBy(f => (f.Type, f.Length)).Select(g => $"{g.Key.Type}({g.Key.Length})x{g.Count()}");
            return $"{ByteCount} bytes: {string.Join(", ", types)}, last {(Frames.Count > 0 ? Frames[^1].Type : "none")}, leftover {LeftoverBytes}";
        }
    }
}
