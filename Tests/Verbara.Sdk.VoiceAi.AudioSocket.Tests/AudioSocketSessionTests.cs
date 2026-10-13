using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Tests;

[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class AudioSocketSessionTests : IAsyncLifetime
{
    private readonly AudioSocketServer _server;

    public AudioSocketSessionTests()
    {
        var options = new AudioSocketOptions
        {
            Port = 0,
            ConnectionTimeout = TimeSpan.FromSeconds(5),
        };
        _server = new AudioSocketServer(options, NullLogger<AudioSocketServer>.Instance);
    }

    private async Task<(AudioSocketSession session, AudioSocketClient client)> CreateSessionAsync()
    {
        var tcs = new TaskCompletionSource<AudioSocketSession>();
        _server.OnSessionStarted += session =>
        {
            tcs.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        await _server.StartAsync(CancellationToken.None);

        var channelId = Guid.NewGuid();
        var client = new AudioSocketClient("127.0.0.1", _server.BoundPort, channelId);
        await client.ConnectAsync();

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        return (session, client);
    }

    // ── HangupAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HangupAsync_ShouldSendHangupFrameAndDispose()
    {
        var (session, client) = await CreateSessionAsync();

        await session.HangupAsync();

        session.IsConnected.Should().BeFalse();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task HangupAsync_ShouldCompleteAsANoOp_WhenAlreadyDisposed()
    {
        var (session, client) = await CreateSessionAsync();

        await session.DisposeAsync();

        var act = async () => await session.HangupAsync();

        // A hangup on a session that already ended has nothing left to do. What reaches the far end
        // is asserted in AudioSocketSessionHangupTests.
        await act.Should().NotThrowAsync();

        await client.DisposeAsync();
    }

    // ── FireHangup (concurrent safety) ───────────────────────────────────────

    [Fact]
    public async Task OnHangup_ShouldFireOnlyOnce_WhenClientSendsHangup()
    {
        var (session, client) = await CreateSessionAsync();

        int hangupCount = 0;
        session.OnHangup += () => Interlocked.Increment(ref hangupCount);

        await client.SendHangupAsync();
        await Task.Delay(500); // give time for read loop to process

        hangupCount.Should().Be(1);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task OnHangup_ShouldFireOnlyOnce_WhenDisposedAfterHangup()
    {
        var (session, client) = await CreateSessionAsync();

        int hangupCount = 0;
        session.OnHangup += () => Interlocked.Increment(ref hangupCount);

        await client.SendHangupAsync();
        await Task.Delay(500);

        // Disposing again should not fire hangup a second time
        await session.DisposeAsync();
        await Task.Delay(100);

        hangupCount.Should().Be(1);

        await client.DisposeAsync();
    }

    // ── WriteAudioAsync with different frame types ───────────────────────────

    [Fact]
    public async Task WriteAudioAsync_ShouldSendDefaultAudioFrame()
    {
        var (session, client) = await CreateSessionAsync();

        byte[] audio = new byte[160];
        Random.Shared.NextBytes(audio);
        await session.WriteAudioAsync(audio);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        ReadOnlyMemory<byte>? received = null;
        await foreach (var chunk in client.ReadAudioAsync(cts.Token))
        {
            received = chunk;
            break;
        }

        received.Should().NotBeNull();
        received!.Value.ToArray().Should().BeEquivalentTo(audio);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task WriteAudioAsync_WithFrameType_ShouldNotThrow()
    {
        var (session, client) = await CreateSessionAsync();

        byte[] audio = new byte[320];
        Random.Shared.NextBytes(audio);

        // Write with explicit Slin16 frame type
        await session.WriteAudioAsync(audio, AudioSocketFrameType.AudioSlin16);

        // Session should still be connected after writing
        session.IsConnected.Should().BeTrue();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task WriteAudioAsync_ShouldThrow_WhenDisposed()
    {
        var (session, client) = await CreateSessionAsync();

        await session.DisposeAsync();

        byte[] audio = new byte[160];
        var act = async () => await session.WriteAudioAsync(audio);

        await act.Should().ThrowAsync<ObjectDisposedException>();

        await client.DisposeAsync();
    }

    // ── IsConnected state transitions ────────────────────────────────────────

    [Fact]
    public async Task IsConnected_ShouldBeTrue_AfterSessionCreated()
    {
        var (session, client) = await CreateSessionAsync();

        session.IsConnected.Should().BeTrue();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task IsConnected_ShouldBeFalse_AfterDispose()
    {
        var (session, client) = await CreateSessionAsync();

        await session.DisposeAsync();

        session.IsConnected.Should().BeFalse();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task IsConnected_ShouldBeFalse_AfterHangup()
    {
        var (session, client) = await CreateSessionAsync();

        await session.HangupAsync();

        session.IsConnected.Should().BeFalse();

        await client.DisposeAsync();
    }

    // ── ChannelId and RemoteEndpoint ─────────────────────────────────────────

    [Fact]
    public async Task ChannelId_ShouldMatchClientChannelId()
    {
        var tcs = new TaskCompletionSource<AudioSocketSession>();
        _server.OnSessionStarted += session =>
        {
            tcs.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        await _server.StartAsync(CancellationToken.None);

        var expectedId = Guid.NewGuid();
        var client = new AudioSocketClient("127.0.0.1", _server.BoundPort, expectedId);
        await client.ConnectAsync();

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        session.ChannelId.Should().Be(expectedId);
        session.RemoteEndpoint.Should().NotBeNullOrEmpty();

        await client.DisposeAsync();
    }

    // ── DisposeAsync idempotency ─────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_ShouldBeIdempotent()
    {
        var (session, client) = await CreateSessionAsync();

        // Calling DisposeAsync multiple times should not throw
        await session.DisposeAsync();
        await session.DisposeAsync();
        await session.DisposeAsync();

        session.IsConnected.Should().BeFalse();

        await client.DisposeAsync();
    }

    // ── ReadAudioAsync teardown orderings (ADR-0053) ─────────────────────────

    /// <summary>
    /// Ordering (a): the hangup is fully processed before the consumer's first <c>MoveNextAsync</c>.
    /// Ordered by construction, not by delay — the client's read ends on EOF, and the FIN that
    /// produces it is emitted by <c>_client.Dispose()</c>, the last statement of the session's
    /// teardown. When the drain loop returns, the teardown has provably completed.
    /// </summary>
    [Fact]
    public async Task ReadAudioAsync_ShouldDeliverBufferedAudioThenEnd_WhenTheHangupCompletesBeforeTheFirstRead()
    {
        var (session, client) = await CreateSessionAsync();
        var payload = new byte[160];
        payload[0] = 0x42;

        await client.SendAudioAsync(payload);   // lands in the 256-slot channel …
        await client.SendHangupAsync();         // … and the hangup follows it on the same stream

        await foreach (var _ in client.ReadAudioAsync(CancellationToken.None)) { /* drain to EOF */ }

        var frames = new List<ReadOnlyMemory<byte>>();
        await foreach (var frame in session.ReadAudioAsync())
            frames.Add(frame);

        frames.Should().ContainSingle("audio received before the hangup is still the caller's audio")
            .Which.Span[0].Should().Be(0x42);

        await client.DisposeAsync();
    }

    /// <summary>Ordering (b-variant): the owner disposes while a consumer is enumerating. A routine
    /// host shutdown must end the sequence, not fault it — see <c>AudioSocketServer.StopAsync</c>.</summary>
    [Fact]
    public async Task ReadAudioAsync_ShouldEndTheSequence_WhenTheSessionIsDisposedMidEnumeration()
    {
        var (session, client) = await CreateSessionAsync();
        var firstFrameSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new List<ReadOnlyMemory<byte>>();

        var consumer = Task.Run(async () =>
        {
            await foreach (var frame in session.ReadAudioAsync())
            {
                frames.Add(frame);
                firstFrameSeen.TrySetResult();
            }
        });

        await client.SendAudioAsync(new byte[160]);
        await firstFrameSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await session.DisposeAsync();

        var fault = await Record.ExceptionAsync(() => consumer.WaitAsync(TimeSpan.FromSeconds(5)));

        fault.Should().BeNull("disposing the session is how a host shuts it down, not a failure");
        frames.Should().ContainSingle();

        await client.DisposeAsync();
    }

    /// <summary>Ordering (c) / R2: a read issued after the owner disposed throws from the call
    /// itself, naming the type — as every other member of this session already does.</summary>
    [Fact]
    public async Task ReadAudioAsync_ShouldThrowObjectDisposedException_WhenTheOwnerDisposedTheSession()
    {
        var (session, client) = await CreateSessionAsync();

        await session.DisposeAsync();

        var act = () => session.ReadAudioAsync();

        act.Should().Throw<ObjectDisposedException>()
            .Which.ObjectName.Should().Contain(nameof(AudioSocketSession));

        await client.DisposeAsync();
    }

    /// <summary>R1: the consumer's own cancellation still faults, and takes precedence over the
    /// sequence ending quietly. The cancelled token goes to the subject only (ADR-0052 F3).</summary>
    [Fact]
    public async Task ReadAudioAsync_ShouldThrowOperationCanceled_WhenTheCallersTokenIsCancelled()
    {
        var (session, client) = await CreateSessionAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () =>
        {
            await foreach (var _ in session.ReadAudioAsync(cts.Token)) { /* never reached */ }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();

        await client.DisposeAsync();
    }

    /// <summary>Ordering (d): a bare EOF with no hangup frame. The session must release its
    /// transport — the server has already dropped it from its registry by the time OnHangup returns,
    /// so nothing else can ever reclaim it.</summary>
    [Fact]
    public async Task ReadLoop_ShouldReleaseTheTransport_WhenTheSocketEndsWithoutAHangupFrame()
    {
        var (session, client) = await CreateSessionAsync();
        var tornDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnHangup += () => tornDown.TrySetResult();

        await client.DisposeAsync();   // FIN, no hangup frame

        await tornDown.Task.WaitAsync(TimeSpan.FromSeconds(5));

        session.IsConnected.Should().BeFalse(
            "a session whose socket is gone is not connected, and its owner has already forgotten it");
    }

    /// <summary>
    /// A session torn down before its read loop starts (a server stop that reaches it right after its
    /// registration) still runs the loop's ending once the loop starts: its hangup fires and its release
    /// runs, so the server's registry entry and its place against the limit are given back.
    /// </summary>
    [Fact]
    public async Task StartReadLoop_ShouldFireTheHangupAndTheRelease_WhenTheSessionWasDisposedBeforeTheLoopStarted()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new System.Net.Sockets.TcpClient();
        await peer.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();
        var session = new AudioSocketSession(
            Guid.NewGuid(),
            accepted,
            System.IO.Pipelines.PipeReader.Create(accepted.GetStream()),
            new AudioSocketOptions().DefaultFormat,
            NullLogger.Instance);
        var hangups = 0;
        var releases = 0;
        session.OnHangup += () => Interlocked.Increment(ref hangups);
        session.Released = () => Interlocked.Increment(ref releases);

        await session.DisposeAsync();
        session.StartReadLoop();
        await session.HungUp.WaitAsync(TimeSpan.FromSeconds(5));

        Volatile.Read(ref hangups).Should().Be(1, "the loop's ending raises the hangup once, even for a session already torn down");
        Volatile.Read(ref releases).Should().Be(1, "the release runs, so the server gives back the entry and the place");
    }

    // ── A write that fails on its transport ends the session ─────────────────

    /// <summary>
    /// A write that fails because the far end reset the connection ends the session before the exception
    /// reaches the caller: by the time <see cref="AudioSocketSession.WriteAudioAsync(ReadOnlyMemory{byte}, CancellationToken)"/>
    /// throws, the session reports itself not connected and has raised its hangup once, and the transport
    /// failure is logged once at Warning. The exception type is unchanged.
    /// </summary>
    /// <remarks>
    /// Deterministic: the session is built on a pipe reader the test holds and never completes, and its
    /// read loop is never started, so nothing but the write itself can notice the reset.
    /// </remarks>
    [Fact]
    public async Task WriteAudioAsync_ShouldEndTheSessionBeforeThrowing_WhenTheTransportFails()
    {
        var logger = new RecordingSessionLogger();
        await using var pair = await SessionPair.CreateAsync(logger);
        var hangups = 0;
        pair.Session.OnHangup += () => Interlocked.Increment(ref hangups);

        pair.ResetPeer();
        var fault = await WriteUntilItThrowsAsync(pair.Session);

        // Read right after the throw, before anything else can run on the session.
        var connectedAtTheThrow = pair.Session.IsConnected;
        var hangupsAtTheThrow = Volatile.Read(ref hangups);

        using (new AssertionScope())
        {
            fault.Should().BeAssignableTo<IOException>("the exception type the caller sees does not change");
            connectedAtTheThrow.Should().BeFalse("the session ended itself before the exception reached the caller");
            hangupsAtTheThrow.Should().Be(1, "the session's hangup was raised once, before the throw");
            logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle(
                "a transport that failed while the session was live is reported once at Warning");
        }
    }

    /// <summary>
    /// A write cut short by the session's own ending (here its owner's disposal, which closes the transport
    /// under a flush) throws, and logs nothing: the session was not live when the write failed, so nothing
    /// broke.
    /// </summary>
    [Fact]
    public async Task WriteAudioAsync_ShouldThrowWithoutAWarning_WhenTheSessionEndsUnderTheWrite()
    {
        var logger = new RecordingSessionLogger();
        await using var pair = await SessionPair.CreateAsync(logger);
        var frame = new byte[32_000];

        // The far end does not read, so the socket's buffers fill and one write stays in flight. The loop
        // bound is a failure bound: loopback buffers hold far less than this many frames.
        Task? inFlight = null;
        for (var i = 0; i < 2_000 && inFlight is null; i++)
        {
            var write = pair.Session.WriteAudioAsync(frame, AudioSocketFrameType.Audio);
            if (write.IsCompleted)
                await write;
            else
                inFlight = write.AsTask();
        }

        var pending = inFlight
            ?? throw new Xunit.Sdk.XunitException("the far end reads nothing, so a write eventually waits for the transport");

        await pair.Session.DisposeAsync();
        var fault = await Record.ExceptionAsync(() => pending.WaitAsync(SignalTimeout));

        using (new AssertionScope())
        {
            fault.Should().Match<Exception>(
                e => e is IOException || e is ObjectDisposedException,
                "the write in flight fails once the transport under it is closed (got {0})", fault?.GetType().Name);
            logger.Entries.Where(e => e.Level >= LogLevel.Warning).Should().BeEmpty(
                "a write the session's own ending cut short is not a transport failure");
        }
    }

    /// <summary>
    /// A hangup subscriber that throws neither replaces the write's transport failure nor escapes the
    /// ending: the write still throws its <see cref="IOException"/>, the subscriber's exception is logged
    /// once at Warning, and the release and the hung-up signal still run.
    /// </summary>
    [Fact]
    public async Task WriteAudioAsync_ShouldStillThrowTheTransportFailure_WhenAHangupSubscriberThrows()
    {
        var logger = new RecordingSessionLogger();
        await using var pair = await SessionPair.CreateAsync(logger);
        var subscriberFault = new InvalidOperationException("A hangup subscriber failed.");
        var released = 0;
        pair.Session.OnHangup += () => throw subscriberFault;
        pair.Session.Released = () => Interlocked.Increment(ref released);

        pair.ResetPeer();
        var fault = await WriteUntilItThrowsAsync(pair.Session);
        var hungUp = await Record.ExceptionAsync(() => pair.Session.HungUp.WaitAsync(SignalTimeout));

        using (new AssertionScope())
        {
            fault.Should().BeAssignableTo<IOException>(
                "the write's own failure reaches the caller, not the subscriber's exception");
            logger.Entries.Should().ContainSingle(
                e => e.Level == LogLevel.Warning && ReferenceEquals(e.Exception, subscriberFault),
                "the subscriber's exception is logged once at Warning and goes no further");
            hungUp.Should().BeNull("the hung-up signal still completes after a throwing subscriber");
            Volatile.Read(ref released).Should().Be(1, "the release still runs after a throwing subscriber");
        }
    }

    /// <summary>Upper bound on any single wait in the transport-failure tests. Reaching it is a failure.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Writes small frames until one throws, and returns what it threw. The bound is a failure bound: on
    /// loopback the reset is in the kernel before the first write after it.
    /// </summary>
    private static async Task<Exception?> WriteUntilItThrowsAsync(AudioSocketSession session)
    {
        var frame = new byte[320];
        for (var i = 0; i < 10_000; i++)
        {
            var fault = await Record.ExceptionAsync(async () => await session.WriteAudioAsync(frame));
            if (fault is not null)
                return fault;
        }

        return null;
    }

    /// <summary>
    /// A session over a real loopback connection whose read side is a pipe the test never completes, so
    /// the read loop (never started) cannot end the session; and the raw far end of that connection.
    /// </summary>
    private sealed class SessionPair : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly System.Net.Sockets.TcpClient _peer;
        private readonly System.IO.Pipelines.Pipe _heldReadSide = new();

        private SessionPair(
            System.Net.Sockets.TcpListener listener, System.Net.Sockets.TcpClient peer, System.Net.Sockets.TcpClient accepted, ILogger logger)
        {
            _listener = listener;
            _peer = peer;
            Session = new AudioSocketSession(
                Guid.NewGuid(), accepted, _heldReadSide.Reader, new AudioSocketOptions().DefaultFormat, logger);
        }

        public AudioSocketSession Session { get; }

        public static async Task<SessionPair> CreateAsync(ILogger logger)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var peer = new System.Net.Sockets.TcpClient { ReceiveBufferSize = 4096 };
            await peer.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
            var accepted = await listener.AcceptTcpClientAsync();
            return new SessionPair(listener, peer, accepted, logger);
        }

        /// <summary>The far end resets the connection: <c>SO_LINGER</c> 0, then close.</summary>
        public void ResetPeer()
        {
            _peer.Client.LingerState = new System.Net.Sockets.LingerOption(true, 0);
            _peer.Close();
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            _peer.Dispose();
            _listener.Dispose();
        }
    }

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        await _server.DisposeAsync();
    }
}

/// <summary>One entry a session logged: its level, its message and the exception it carried.</summary>
file sealed record SessionLogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>Records what a session logs, so a test can say at which level and with which exception.</summary>
file sealed class RecordingSessionLogger : ILogger
{
    private readonly Lock _gate = new();
    private readonly List<SessionLogEntry> _entries = [];

    /// <summary>A snapshot of everything logged so far, oldest first.</summary>
    public IReadOnlyList<SessionLogEntry> Entries
    {
        get { lock (_gate) return [.. _entries]; }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
            _entries.Add(new SessionLogEntry(logLevel, formatter(state, exception), exception));
    }
}
