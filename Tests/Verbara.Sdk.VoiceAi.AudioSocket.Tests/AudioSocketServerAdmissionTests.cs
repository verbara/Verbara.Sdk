using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.VoiceAi.AudioSocket.Internal;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Tests;

/// <summary>
/// What the AudioSocket server admits once a connection has identified itself: never more live
/// sessions than <see cref="AudioSocketOptions.MaxConcurrentSessions"/>, however many connections
/// identify at once; a call that comes back under the id of a session still ending is served, not
/// refused for the limit; and a connection whose registration is attempted once the server's stop has
/// begun is refused with a hangup frame and never registered.
/// </summary>
/// <remarks>
/// <para>
/// The seam is the server's clock. Between a connection's identification and its registration the
/// server reads <see cref="TimeProvider.GetTimestamp"/> exactly once, and a connection that waits for
/// the holder of its id reads it again. <see cref="SeamClock"/> counts those reads per connection (the
/// count lives in the connection's own execution flow) and runs the test's hook on them, so every
/// interleaving below is a sequence the test lays down, not a race it hopes to win. The clock never
/// moves and its timers never fire, so no wait here can end on time: the identification timeout and
/// the same-id grace both stay open until the edge the test raises.
/// </para>
/// <para>
/// The burst row blocks one pool thread per connection inside the hook until all of them have
/// arrived, while the test's own reads need pool threads too, so the class raises the pool's minimum
/// worker threads for its lifetime and restores it in <see cref="Dispose"/>. Every wait is bounded by
/// <see cref="SignalTimeout"/>, whose expiry is a failure, never a pace.
/// </para>
/// </remarks>
public sealed class AudioSocketServerAdmissionTests : IDisposable
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The frame a server writes before it closes a connection it will not serve.</summary>
    private static readonly byte[] HangupFrame = [0x00, 0x00, 0x00];

    /// <summary>The pool minimum the burst needs: every blocked connection plus headroom for the test.</summary>
    private const int PoolThreadsNeeded = 64;

    private readonly int _savedMinWorkers;
    private readonly int _savedMinIo;

    public AudioSocketServerAdmissionTests()
    {
        ThreadPool.GetMinThreads(out _savedMinWorkers, out _savedMinIo);
        if (_savedMinWorkers < PoolThreadsNeeded)
            ThreadPool.SetMinThreads(PoolThreadsNeeded, _savedMinIo);
    }

    public void Dispose() => ThreadPool.SetMinThreads(_savedMinWorkers, _savedMinIo);

    /// <summary>
    /// A burst of K + N connections with distinct ids, every one of which has passed its limit check
    /// before any of them registers, still leaves exactly K sessions: deciding that a connection is
    /// within the limit and taking its place is one step. Before, the check read the registry's count
    /// and the registration came later, so all K + N were announced.
    /// </summary>
    [Fact]
    public async Task HandleConnectionAsync_ShouldAnnounceNoMoreThanTheLimit_WhenABurstPassesTheCheckBeforeAnyRegisters()
    {
        const int limit = 3;
        const int overflow = 3;
        const int burst = limit + overflow;

        var clock = new SeamClock();
        var logger = new CapturingLogger();
        await using var server = NewServer(clock, logger, limit);
        var tally = new BurstTally(burst);

        // Every connection's first read of the clock sits between its limit check and its
        // registration. It waits there until all of the burst has arrived, or until a refusal shows
        // that the limit was enforced before this point, so the hook cannot deadlock a server that
        // refuses earlier.
        var arrivals = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrierExpired = 0;
        clock.OnRead = call =>
        {
            if (call != 1)
                return;

            if (Interlocked.Increment(ref arrivals) == burst)
                barrier.TrySetResult();
            if (!barrier.Task.Wait(SignalTimeout))
                Interlocked.Exchange(ref barrierExpired, 1);
        };
        server.OnSessionStarted += session =>
        {
            tally.Announced(server.ActiveSessionCount);
            return ValueTask.CompletedTask;
        };
        await server.StartAsync(CancellationToken.None);

        var peers = new List<TcpClient>();
        var reads = new List<Task<PeerRead>>();
        try
        {
            for (var i = 0; i < burst; i++)
            {
                var peer = await ConnectAndIdentifyAsync(server, Guid.NewGuid());
                peers.Add(peer);
                reads.Add(ReadUntilClosedAsync(peer, onClosed: () =>
                {
                    barrier.TrySetResult();
                    tally.Refused();
                }));
            }

            // Every connection ends up announced or refused; which, is what the assertions read.
            await tally.Settled.WaitAsync(SignalTimeout);

            using (new AssertionScope())
            {
                Volatile.Read(ref barrierExpired).Should().Be(0, "the burst reached the hook or was refused before it");
                tally.AnnouncedCount.Should().Be(
                    limit, "a burst of {0} distinct ids against a limit of {1} admits exactly the limit", burst, limit);
                tally.MostLive.Should().BeLessThanOrEqualTo(
                    limit, "the number of live sessions never exceeds the limit, not even for an instant");
                reads.Where(r => r.IsCompleted).Select(r => r.Result.Bytes).Should().HaveCount(
                    overflow, "every connection past the limit is refused").And.AllSatisfy(bytes => bytes.Should().Equal(
                    HangupFrame, "a refusal is a hangup frame and then the end of the connection"));
                server.ActiveSessionCount.Should().Be(limit);
            }
        }
        finally
        {
            barrier.TrySetResult();
            await server.DisposeAsync();
            foreach (var peer in peers)
                peer.Dispose();
            await Task.WhenAll(reads).WaitAsync(SignalTimeout);
        }
    }

    /// <summary>
    /// A call that comes back under the id of a session still ending, on a server at its limit, waits
    /// for that session's hangup and is served; a connection with another id that arrives meanwhile is
    /// refused for the limit. The pair counts as one place throughout.
    /// </summary>
    [Fact]
    public async Task HandleConnectionAsync_ShouldServeTheReEntryAndRefuseAnotherId_WhenTheLimitsOnlyHolderIsEnding()
    {
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();
        var clock = new SeamClock();
        var logger = new CapturingLogger();
        await using var server = NewServer(clock, logger, maxConcurrentSessions: 1);
        var announcements = new AnnouncementLog(server);

        // A second read of the clock on one connection is its wait for the holder of its id.
        var reEntryWaits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnRead = call =>
        {
            if (call == 2)
                reEntryWaits.TrySetResult();
        };
        await server.StartAsync(CancellationToken.None);

        using var gate = new ManualResetEventSlim();
        var holderEnding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var holderPeer = await ConnectAndIdentifyAsync(server, x);
        var holder = await announcements.Next(x).WaitAsync(SignalTimeout);

        // The holder's hangup is held open by a consumer still handling it, so the holder keeps its id
        // and its place while the call comes back.
        holder.OnHangup += () =>
        {
            holderEnding.TrySetResult();
            gate.Wait(SignalTimeout);
        };

        try
        {
            await SendFrameAsync(holderPeer, AudioSocketFrameType.Hangup, []);
            await holderEnding.Task.WaitAsync(SignalTimeout);

            using var reEntryPeer = await ConnectAndIdentifyAsync(server, x);
            await reEntryWaits.Task.WaitAsync(SignalTimeout);

            using var otherPeer = await ConnectAndIdentifyAsync(server, y);
            var other = await ReadUntilClosedAsync(otherPeer).WaitAsync(SignalTimeout);

            gate.Set();
            var reEntry = await announcements.Next(x).WaitAsync(SignalTimeout);

            using (new AssertionScope())
            {
                other.Bytes.Should().Equal(HangupFrame, "the other id is refused for the limit, with a hangup frame");
                announcements.CountFor(y).Should().Be(0, "a refused connection is never announced");
                announcements.CountFor(x).Should().Be(2, "the call that came back is served once its holder has ended");
                reEntry.Should().NotBeSameAs(holder);
                server.ActiveSessionCount.Should().Be(1, "the call and its holder count as one place");
                logger.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.EventName).Should().Equal(
                    ["SessionLimitReached"], "only the other id was refused, and for the limit");
            }
        }
        finally
        {
            gate.Set();
        }
    }

    /// <summary>
    /// The holder of an id is released after the call that comes back under that id has passed the
    /// limit check and before it registers. Each place is still counted once: after both sessions end,
    /// a server of limit K admits K fresh sessions and refuses the next.
    /// </summary>
    [Fact]
    public async Task HandleConnectionAsync_ShouldStillAdmitExactlyTheLimit_WhenTheHolderIsReleasedBetweenTheCheckAndTheRegistration()
    {
        const int limit = 2;
        var x = Guid.NewGuid();
        var clock = new SeamClock();
        var logger = new CapturingLogger();
        await using var server = NewServer(clock, logger, limit);
        var announcements = new AnnouncementLog(server);
        await server.StartAsync(CancellationToken.None);

        using var gate = new ManualResetEventSlim();
        var holderEnding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peers = new List<TcpClient>();
        try
        {
            peers.Add(await ConnectAndIdentifyAsync(server, x));
            var holder = await announcements.Next(x).WaitAsync(SignalTimeout);
            var z = Guid.NewGuid();
            peers.Add(await ConnectAndIdentifyAsync(server, z));
            var other = await announcements.Next(z).WaitAsync(SignalTimeout);
            holder.OnHangup += () =>
            {
                holderEnding.TrySetResult();
                gate.Wait(SignalTimeout);
            };
            await SendFrameAsync(peers[0], AudioSocketFrameType.Hangup, []);
            await holderEnding.Task.WaitAsync(SignalTimeout);

            // The call comes back under X. Its check passes because X is held; between that check and
            // its registration, the hook lets the holder's hangup finish and waits for its release.
            var released = 0;
            clock.OnRead = call =>
            {
                if (call != 1 || Interlocked.Exchange(ref released, 1) != 0)
                    return;

                gate.Set();
                holder.HungUp.Wait(SignalTimeout);
            };
            peers.Add(await ConnectAndIdentifyAsync(server, x));
            var reEntry = await announcements.Next(x).WaitAsync(SignalTimeout);
            clock.OnRead = null;

            // Both later end.
            await SendFrameAsync(peers[1], AudioSocketFrameType.Hangup, []);
            await SendFrameAsync(peers[2], AudioSocketFrameType.Hangup, []);
            await Task.WhenAll(other.HungUp, reEntry.HungUp).WaitAsync(SignalTimeout);

            // K fresh sessions are admitted, and the (K+1)-th is refused.
            var fresh = new List<AudioSocketSession>();
            for (var i = 0; i < limit; i++)
            {
                var id = Guid.NewGuid();
                peers.Add(await ConnectAndIdentifyAsync(server, id));
                fresh.Add(await announcements.Next(id).WaitAsync(SignalTimeout));
            }

            var extra = Guid.NewGuid();
            var extraPeer = await ConnectAndIdentifyAsync(server, extra);
            peers.Add(extraPeer);
            var refused = await ReadUntilClosedAsync(extraPeer).WaitAsync(SignalTimeout);

            using (new AssertionScope())
            {
                Volatile.Read(ref released).Should().Be(
                    1, "the holder was released between the re-entry's limit check and its registration");
                fresh.Should().HaveCount(limit, "every place the earlier sessions held was given back");
                refused.Bytes.Should().Equal(HangupFrame, "the place past the limit is refused with a hangup frame");
                announcements.CountFor(extra).Should().Be(0);
                announcements.MostLive.Should().BeLessThanOrEqualTo(limit, "at no instant are more than K sessions live");
                server.ActiveSessionCount.Should().Be(limit);
            }
        }
        finally
        {
            gate.Set();
            await server.DisposeAsync();
            foreach (var peer in peers)
                peer.Dispose();
        }
    }

    /// <summary>
    /// A connection that identified itself before the stop began, and whose registration is attempted
    /// after the stop has completed, is refused with a hangup frame: never registered, never announced,
    /// and nothing at Warning or above. Before, the registration read no stop token before its first
    /// attempt, so the connection was registered and announced after the stop had released everything.
    /// </summary>
    [Fact]
    public async Task HandleConnectionAsync_ShouldRefuseWithAHangupFrameAndRegisterNothing_WhenTheStopLandsBetweenIdentificationAndRegistration()
    {
        var clock = new SeamClock();
        var logger = new CapturingLogger();
        await using var server = NewServer(clock, logger, maxConcurrentSessions: 10);
        var announced = new TaskCompletionSource<AudioSocketSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnSessionStarted += session =>
        {
            announced.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        // The connection's first read of the clock sits between its identification and its
        // registration: the whole stop runs there, start to finish, before the registration.
        var stopRan = 0;
        clock.OnRead = call =>
        {
            if (call != 1)
                return;

            server.StopAsync(CancellationToken.None).WaitAsync(SignalTimeout).GetAwaiter().GetResult();
            Interlocked.Exchange(ref stopRan, 1);
        };
        await server.StartAsync(CancellationToken.None);

        using var peer = await ConnectAndIdentifyAsync(server, Guid.NewGuid());
        var read = ReadUntilClosedAsync(peer);

        // Whichever comes first: the refusal the peer reads, or an announcement that should not exist.
        await Task.WhenAny(read, announced.Task).WaitAsync(SignalTimeout);

        using (new AssertionScope())
        {
            Volatile.Read(ref stopRan).Should().Be(1, "the stop ran to completion before the registration");
            announced.Task.IsCompleted.Should().BeFalse("a connection registered after the stop is never announced");
            server.ActiveSessionCount.Should().Be(0, "nothing is registered once the stop has released the sessions");
            read.IsCompleted.Should().BeTrue("the connection is refused: a hangup frame, then the end of the connection");
            if (read.IsCompleted)
                (await read).Bytes.Should().Equal(HangupFrame, "the refusal still lets the call go on in the dialplan");
            logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning, "a stop is not a refusal the client caused");
        }

        // Releases what a registration after the stop left behind, so the peer's read ends.
        await server.DisposeAsync();
        await read.WaitAsync(SignalTimeout);
    }

    // ---- Harness ----

    private static AudioSocketServer NewServer(SeamClock clock, CapturingLogger logger, int maxConcurrentSessions) =>
        new(
            new AudioSocketOptions
            {
                Port = 0,
                ListenAddress = "127.0.0.1",
                ConnectionTimeout = TimeSpan.FromSeconds(30),
                MaxConcurrentSessions = maxConcurrentSessions,
            },
            logger,
            clock);

    /// <summary>Connects a raw peer to the server's bound port and sends the UUID frame naming <paramref name="channelId"/>.</summary>
    private static async Task<TcpClient> ConnectAndIdentifyAsync(AudioSocketServer server, Guid channelId)
    {
        var peer = new TcpClient();
        try
        {
            // 127.0.0.1, never "localhost": the name resolves ::1 first on this host.
            await peer.ConnectAsync(IPAddress.Loopback, server.BoundPort).WaitAsync(SignalTimeout);
            await SendFrameAsync(peer, AudioSocketFrameType.Uuid, channelId.ToByteArray(bigEndian: true));
            return peer;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            peer.Dispose();
            throw;
        }
    }

    /// <summary>Writes one AudioSocket frame from a peer, in the codec's wire format.</summary>
    private static async Task SendFrameAsync(TcpClient peer, AudioSocketFrameType type, byte[] payload)
    {
        var frame = new ArrayBufferWriter<byte>();
        AudioSocketFrameCodec.WriteFrame(frame, type, payload);
        await peer.GetStream().WriteAsync(frame.WrittenMemory);
    }

    /// <summary>What a peer read before its connection ended.</summary>
    private sealed record PeerRead(byte[] Bytes, bool ClosedByServer);

    /// <summary>
    /// Everything the server writes to <paramref name="peer"/> until the connection ends. Unbounded on
    /// purpose: a connection the server serves stays open until the test releases it, and the test
    /// bounds the wait it cares about itself. A connection the test disposes ends the read too.
    /// </summary>
    private static async Task<PeerRead> ReadUntilClosedAsync(TcpClient peer, Action? onClosed = null)
    {
        using var all = new MemoryStream();
        var buffer = new byte[256];
        try
        {
            int read;
            while ((read = await peer.GetStream().ReadAsync(buffer)) > 0)
                all.Write(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The test disposed its own end: this connection was never closed by the server.
            return new PeerRead(all.ToArray(), ClosedByServer: false);
        }

        onClosed?.Invoke();
        return new PeerRead(all.ToArray(), ClosedByServer: true);
    }

    /// <summary>
    /// The server's clock, as the seam: a timestamp that never moves, timers that never fire, and a
    /// hook on every timestamp read with the count of reads made so far by the same connection.
    /// </summary>
    /// <remarks>
    /// The count lives in an <see cref="AsyncLocal{T}"/> that the first read of a connection creates.
    /// Each accepted connection is handled on a flow of its own, forked from the accept loop, so a
    /// value set there is seen by every later read of that connection and by no other connection.
    /// </remarks>
    private sealed class SeamClock : TimeProvider
    {
        private readonly AsyncLocal<StrongBox<int>?> _readsOnThisConnection = new();

        /// <summary>Runs on every timestamp read, with that read's 1-based number on its connection.</summary>
        public Action<int>? OnRead { get; set; }

        public override long GetTimestamp()
        {
            var reads = _readsOnThisConnection.Value;
            if (reads is null)
            {
                reads = new StrongBox<int>();
                _readsOnThisConnection.Value = reads;
            }

            reads.Value++;
            OnRead?.Invoke(reads.Value);
            return 0;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new InertTimer();

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
                // Nothing was scheduled, so there is nothing to release.
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Counts how a burst settles: every connection is announced or refused.</summary>
    private sealed class BurstTally(int total)
    {
        private int _announced;
        private int _refused;
        private int _mostLive;
        private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Settled => _settled.Task;

        public int AnnouncedCount => Volatile.Read(ref _announced);

        public int MostLive => Volatile.Read(ref _mostLive);

        public void Announced(int liveNow)
        {
            int seen;
            while (liveNow > (seen = Volatile.Read(ref _mostLive))
                   && Interlocked.CompareExchange(ref _mostLive, liveNow, seen) != seen)
            {
                // Another announcement raised the maximum in between; read it again.
            }

            Interlocked.Increment(ref _announced);
            CheckSettled();
        }

        public void Refused()
        {
            Interlocked.Increment(ref _refused);
            CheckSettled();
        }

        private void CheckSettled()
        {
            if (Volatile.Read(ref _announced) + Volatile.Read(ref _refused) >= total)
                _settled.TrySetResult();
        }
    }

    /// <summary>
    /// Every session the server announces, by channel id, in order; and the most sessions live at any
    /// announcement.
    /// </summary>
    private sealed class AnnouncementLog
    {
        private readonly AudioSocketServer _server;
        private readonly Lock _gate = new();
        private readonly Dictionary<Guid, List<AudioSocketSession>> _seen = [];
        private readonly Dictionary<Guid, Queue<TaskCompletionSource<AudioSocketSession>>> _waiting = [];
        private readonly Dictionary<Guid, int> _handed = [];
        private int _mostLive;

        public AnnouncementLog(AudioSocketServer server)
        {
            _server = server;
            server.OnSessionStarted += OnStarted;
        }

        public int MostLive
        {
            get { lock (_gate) return _mostLive; }
        }

        public int CountFor(Guid channelId)
        {
            lock (_gate) return _seen.TryGetValue(channelId, out var list) ? list.Count : 0;
        }

        /// <summary>The next announcement for <paramref name="channelId"/> not yet handed to a caller.</summary>
        public Task<AudioSocketSession> Next(Guid channelId)
        {
            lock (_gate)
            {
                var signal = new TaskCompletionSource<AudioSocketSession>(TaskCreationOptions.RunContinuationsAsynchronously);
                var handed = _handed.GetValueOrDefault(channelId);
                if (_seen.TryGetValue(channelId, out var list) && list.Count > handed)
                {
                    _handed[channelId] = handed + 1;
                    signal.TrySetResult(list[handed]);
                    return signal.Task;
                }

                if (!_waiting.TryGetValue(channelId, out var queue))
                {
                    queue = new Queue<TaskCompletionSource<AudioSocketSession>>();
                    _waiting[channelId] = queue;
                }

                queue.Enqueue(signal);
                return signal.Task;
            }
        }

        private ValueTask OnStarted(AudioSocketSession session)
        {
            lock (_gate)
            {
                _mostLive = Math.Max(_mostLive, _server.ActiveSessionCount);
                if (!_seen.TryGetValue(session.ChannelId, out var list))
                {
                    list = [];
                    _seen[session.ChannelId] = list;
                }

                list.Add(session);
                if (_waiting.TryGetValue(session.ChannelId, out var queue) && queue.Count > 0)
                {
                    _handed[session.ChannelId] = _handed.GetValueOrDefault(session.ChannelId) + 1;
                    queue.Dequeue().TrySetResult(session);
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A server log entry, reduced to what these tests assert on.</summary>
    private sealed record LogEntry(LogLevel Level, string? EventName);

    /// <summary>Records every entry the server logs.</summary>
    private sealed class CapturingLogger : ILogger<AudioSocketServer>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(new LogEntry(logLevel, eventId.Name));
    }
}
