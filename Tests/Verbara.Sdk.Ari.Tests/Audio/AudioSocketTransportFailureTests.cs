using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Tests.TestSupport;
using Verbara.Sdk.Tests.Shared.Sockets;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// A transport failure under a live AudioSocket session is written to the log at Warning and counted
/// on <c>audio.transport.failures</c>, and the stream still ends as a hangup does.
/// </summary>
/// <remarks>
/// <para>
/// Every test here reads <c>audio.transport.failures</c>, which is process-wide and carries no tags,
/// so the class runs in <see cref="AudioStreamMetricsGroup"/>, alone.
/// </para>
/// <para>
/// The server-level tests drive a real loopback connection through the public server. The server
/// listens on port 0 and is never dialled: its accept seam hands it the one connection this test
/// accepted on its own 127.0.0.1:0 listener, wrapped so the test learns the moment the server's
/// handler releases it. That release is the handler's last step, after the session has been disposed,
/// and disposing the session waits for both of its loops, so every wait below ends on the signal
/// that nothing more can be logged or counted for that connection.
/// </para>
/// </remarks>
[Collection(AudioStreamMetricsGroup.Name)]
public sealed class AudioSocketTransportFailureTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How the far end ends an identified session in the server-level tests.</summary>
    public enum PeerEnding
    {
        /// <summary>An AudioSocket hangup frame, with the connection left open.</summary>
        HangupFrame,

        /// <summary>A shutdown and close, without a hangup frame: FIN, then end of stream.</summary>
        OrderlyClose,

        /// <summary>An AudioSocket error frame, with the connection left open.</summary>
        ErrorFrame,

        /// <summary>A hangup frame, then at once an abortive close.</summary>
        HangupThenReset,

        /// <summary>No ending from the far end: the server that owns the session stops.</summary>
        ServerStop,
    }

    // ------------------------------------------------------------ server level, real loopback

    [Fact]
    public async Task HandleConnectionAsync_ShouldLogAWarningAndCountIt_WhenTheTransportResetsAnIdentifiedSession()
    {
        // Arrange — an identified session, so the connection is a live call
        using var counter = new TransportFailureCounter();
        await using var served = await ServedConnection.StartAsync();
        var uuid = Guid.NewGuid();
        await served.IdentifyAsync(uuid);

        // Act — a linger-zero close puts an RST on the wire, which the server's read surfaces as an
        // IOException over SocketException(ConnectionReset)
        served.ResetPeer();
        await served.WaitUntilReleasedAsync();

        // Assert
        using (new AssertionScope())
        {
            var warnings = served.Logger.Entries.Where(entry => entry.Level == LogLevel.Warning).ToList();
            warnings.Should().ContainSingle(
                "a reset under a live session is the transport failing, not the caller hanging up, and " +
                "the operator must be able to tell the two apart in the log");
            if (warnings.Count == 1)
            {
                var warning = warnings[0];
                warning.Value("ChannelId").Should().Be(uuid.ToString(), "the line names the call it ended");
                warning.Exception.Should().BeOfType<IOException>("the line carries the failure itself")
                    .Which.InnerException.Should().BeOfType<SocketException>()
                    .Which.SocketErrorCode.Should().Be(SocketError.ConnectionReset);
            }

            counter.Total.Should().Be(1, "one session ended on a transport failure, and the meter counts sessions");
            counter.Measurements.Should().Be(1, "the failure is counted once, at the ending, never per read");
            served.States.Should().Contain(AudioStreamState.Disconnected, "the ending is still the one a hangup produces")
                .And.NotContain(AudioStreamState.Error, "publishing Error is a separate, breaking decision");
        }
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldPublishTheSameStatesForAResetAsForAHangup()
    {
        // Arrange and act — the same server-level path twice, ended once each way
        var hangup = await RecordStatesAsync(end: served => served.SendHangupAsync());
        var reset = await RecordStatesAsync(end: served =>
        {
            served.ResetPeer();
            return Task.CompletedTask;
        });

        // Assert — the two recordings against each other, not against a literal sequence, so a later
        // fix of the duplicate Disconnected changes both sides alike
        hangup.Should().Contain(AudioStreamState.Connected).And.Contain(
            AudioStreamState.Disconnected,
            "each recording spans the whole session, or comparing them proves nothing");
        reset.Should().Equal(
            hangup,
            "a consumer watching StateChanges sees a reset end exactly as a hangup does; the difference " +
            "is only in the log and on the meter");
    }

    [Theory]
    [InlineData(PeerEnding.HangupFrame, AudioStreamState.Disconnected)]
    [InlineData(PeerEnding.OrderlyClose, AudioStreamState.Disconnected)]
    [InlineData(PeerEnding.ErrorFrame, AudioStreamState.Error)]
    [InlineData(PeerEnding.HangupThenReset, AudioStreamState.Disconnected)]
    [InlineData(PeerEnding.ServerStop, AudioStreamState.Disconnected)]
    public async Task HandleConnectionAsync_ShouldLogNoWarningAndCountNothing_WhenTheSessionEndsWithoutATransportFailure(
        PeerEnding ending,
        AudioStreamState expectedEnding)
    {
        // Arrange
        using var counter = new TransportFailureCounter();
        await using var served = await ServedConnection.StartAsync();
        await served.IdentifyAsync(Guid.NewGuid());

        // Act
        switch (ending)
        {
            case PeerEnding.HangupFrame:
                await served.SendHangupAsync();
                break;
            case PeerEnding.OrderlyClose:
                served.ClosePeer();
                break;
            case PeerEnding.ErrorFrame:
                await served.SendErrorAsync();
                break;
            case PeerEnding.HangupThenReset:
                await served.SendHangupAsync();
                served.ResetPeer();
                break;
            case PeerEnding.ServerStop:
                await served.Server.StopAsync().AsTask().WaitAsync(SignalTimeout);
                break;
        }

        await served.WaitUntilReleasedAsync();

        // Assert
        served.States.First(state => state is AudioStreamState.Disconnected or AudioStreamState.Error)
            .Should().Be(expectedEnding, "the session keeps the ending it has today");
        served.Logger.Entries.Should().NotContain(
            entry => entry.Level == LogLevel.Warning,
            $"a session ended by {ending} did not lose its transport");
        counter.Total.Should().Be(0, $"a session ended by {ending} is not a transport failure");
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldLogNoWarningAndCountNothing_WhenTheTransportResetsBeforeIdentification()
    {
        // Arrange — the idle deadline is far beyond any wait here, so the handler lets the connection
        // go only when the server stops, after the test knows the read has failed
        using var counter = new TransportFailureCounter();
        using var readFailed = new ResetWitness();
        await using var served = await ServedConnection.StartAsync(idleTimeout: TimeSpan.FromMinutes(5));

        // Act — the connection resets before it ever sends its identification frame
        served.ResetPeer();
        await readFailed.Seen.WaitAsync(SignalTimeout);
        await served.Server.StopAsync().AsTask().WaitAsync(SignalTimeout);
        await served.WaitUntilReleasedAsync();

        // Assert
        served.WasAnnounced.Should().BeFalse("the connection never identified itself, so it never became a session");
        served.Logger.Entries.Should().NotContain(
            entry => entry.Level == LogLevel.Warning,
            "a connection that fails before it identifies itself is no call ending: a TCP health probe " +
            "or a scanner that resets would otherwise write a Warning each time");
        counter.Total.Should().Be(0, "only a session that started can end on a transport failure");
    }

    // ---------------------------------------------------------------- session level, scripted

    [Fact]
    public async Task ReadPumpAsync_ShouldLogAWarningCountItAndEndDisconnected_WhenTheTransportFailsAfterIdentification()
    {
        // Arrange — the stream plays the identification frame, and its next read fails the way a
        // reset connection's read does
        using var counter = new TransportFailureCounter();
        var logger = new CapturingLogger<AudioSocketSession>();
        var uuid = Guid.NewGuid();
        var failure = ResetFailure();
        await using var stream = new ScriptedStream(BuildUuidFrame(uuid), failure);
        var session = new AudioSocketSession(stream, "slin16", logger);
        using var recording = new SessionRecording(session);

        // Act
        session.Start();
        var statesAtTheEnding = await recording.Ended.WaitAsync(SignalTimeout);
        await session.DisposeAsync();

        // Assert
        statesAtTheEnding.Should().Equal(
            [AudioStreamState.Connecting, AudioStreamState.Connected, AudioStreamState.Disconnected],
            "the session ends as a hangup does, and never publishes Error");
        var warning = logger.Entries.Where(entry => entry.Level == LogLevel.Warning).Should().ContainSingle(
            "one session ended on its transport, so one line says so").Subject;
        warning.EventName.Should().Be("TransportFailed");
        warning.Value("ChannelId").Should().Be(uuid.ToString(), "the line names the call it ended");
        warning.Exception.Should().BeSameAs(failure, "the line carries the failure the read raised");
        counter.Total.Should().Be(1, "one session ended on a transport failure");
        counter.Measurements.Should().Be(1);
    }

    [Fact]
    public async Task ReadFrameAsync_ShouldReturnEveryFrameReceivedBeforeTheFailure_WhenTheTransportFails()
    {
        // Arrange — three audio frames arrive after the identification, and then the transport fails.
        // The consumer reads nothing until the session has ended, so all three are audio received and
        // not yet read when the failure lands.
        using var counter = new TransportFailureCounter();
        byte[][] audio = [Payload(0x11), Payload(0x22), Payload(0x33)];
        await using var stream = new ScriptedStream(
            [
                BuildUuidFrame(Guid.NewGuid()),
                [.. BuildFrame(AudioFrameType.Audio, audio[0]),
                    .. BuildFrame(AudioFrameType.Audio, audio[1]),
                    .. BuildFrame(AudioFrameType.Audio, audio[2])],
            ],
            ResetFailure());
        var session = new AudioSocketSession(stream, "slin16", new CapturingLogger<AudioSocketSession>());
        using var recording = new SessionRecording(session);

        // Act
        session.Start();
        var statesAtTheEnding = await recording.Ended.WaitAsync(SignalTimeout);
        List<byte[]> read = [];
        for (var i = 0; i < 4; i++)
            read.Add((await session.ReadFrameAsync().AsTask().WaitAsync(SignalTimeout)).ToArray());
        await session.DisposeAsync();

        // Assert
        statesAtTheEnding[^1].Should().Be(AudioStreamState.Disconnected, "the transport failure ended the session");
        read.Take(3).Should().BeEquivalentTo(
            audio,
            options => options.WithStrictOrdering(),
            "making the failure visible must not cost the audio that already arrived");
        read[3].Should().BeEmpty("after the frames that arrived, the stream reports that it has ended");
        counter.Total.Should().Be(1, "it is still one transport failure");
    }

    [Fact]
    public async Task StateChanges_ShouldFindTheWarningAndTheCountAlready_WhenASubscriberReadsThemOnDisconnected()
    {
        // Arrange — a consumer that reacts to the ending by reading the log and the meter
        using var counter = new TransportFailureCounter();
        var logger = new CapturingLogger<AudioSocketSession>();
        await using var stream = new ScriptedStream(BuildUuidFrame(Guid.NewGuid()), ResetFailure());
        var session = new AudioSocketSession(stream, "slin16", logger);
        var seenOnDisconnected = new TaskCompletionSource<(int Warnings, long Count)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = session.StateChanges.Subscribe(state =>
        {
            if (state == AudioStreamState.Disconnected)
            {
                seenOnDisconnected.TrySetResult((
                    logger.Entries.Count(entry => entry.Level == LogLevel.Warning),
                    counter.Total));
            }
        });

        // Act
        session.Start();
        var seen = await seenOnDisconnected.Task.WaitAsync(SignalTimeout);
        await session.DisposeAsync();

        // Assert
        seen.Warnings.Should().Be(1, "the line is written before the ending is published");
        seen.Count.Should().Be(1, "the count is added before the ending is published");
    }

    [Fact]
    public async Task ReadFrameAsync_ShouldFindTheWarningAndTheCountAlready_WhenTheConsumerReadsTheEndOfTheAudio()
    {
        // Arrange — the failure is held until the consumer is already waiting for audio, so the end of
        // the audio is what wakes it
        using var counter = new TransportFailureCounter();
        var logger = new CapturingLogger<AudioSocketSession>();
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = new ScriptedStream([BuildUuidFrame(Guid.NewGuid())], ResetFailure(), fail.Task);
        var session = new AudioSocketSession(stream, "slin16", logger);
        using var recording = new SessionRecording(session);
        session.Start();
        await recording.Connected.WaitAsync(SignalTimeout);
        var endOfAudio = session.ReadFrameAsync().AsTask();

        // Act
        fail.SetResult();
        var frame = await endOfAudio.WaitAsync(SignalTimeout);
        var seen = (Warnings: logger.Entries.Count(entry => entry.Level == LogLevel.Warning), Count: counter.Total);
        await session.DisposeAsync();

        // Assert
        frame.IsEmpty.Should().BeTrue("the audio ended with the transport");
        seen.Warnings.Should().Be(1, "the line is written before the audio channel is completed");
        seen.Count.Should().Be(1, "the count is added before the audio channel is completed");
    }

    [Fact]
    public async Task ReadPumpAsync_ShouldLogNoWarningAndCountNothing_WhenTheTransportFailsBeforeIdentification()
    {
        // Arrange — the very first read fails: the connection never sends its identification frame
        using var counter = new TransportFailureCounter();
        var logger = new CapturingLogger<AudioSocketSession>();
        await using var stream = new ScriptedStream([], ResetFailure());
        var session = new AudioSocketSession(stream, "slin16", logger);
        using var recording = new SessionRecording(session);

        // Act — the audio channel is completed by the read pump's last step, after the fill loop has
        // handled the failure and completed the pipe, so an empty read means both loops are done
        session.Start();
        var frame = await session.ReadFrameAsync().AsTask().WaitAsync(SignalTimeout);
        var states = recording.States;
        await session.DisposeAsync();

        // Assert
        frame.IsEmpty.Should().BeTrue("the connection ended");
        states.Should().Equal([AudioStreamState.Connecting], "the connection never became a session");
        logger.Entries.Should().NotContain(
            entry => entry.Level == LogLevel.Warning,
            "a connection that never identified itself is no call ending, so its failure is no Warning");
        counter.Total.Should().Be(0, "only a session that started can end on a transport failure");
    }

    // --------------------------------------------------------------------------------- helpers

    private static async Task<IReadOnlyList<AudioStreamState>> RecordStatesAsync(Func<ServedConnection, Task> end)
    {
        await using var served = await ServedConnection.StartAsync();
        await served.IdentifyAsync(Guid.NewGuid());
        await end(served);
        await served.WaitUntilReleasedAsync();
        return served.States;
    }

    /// <summary>One byte of type, two of big-endian length, then the payload.</summary>
    private static byte[] BuildFrame(AudioFrameType type, byte[] payload)
    {
        var frame = new byte[3 + payload.Length];
        frame[0] = (byte)type;
        frame[1] = (byte)(payload.Length >> 8);
        frame[2] = (byte)payload.Length;
        payload.CopyTo(frame.AsSpan(3));
        return frame;
    }

    /// <summary>The UUID travels in RFC 4122 order, most significant byte first, as Asterisk sends it.</summary>
    private static byte[] BuildUuidFrame(Guid uuid) =>
        BuildFrame(AudioFrameType.Uuid, uuid.ToByteArray(bigEndian: true));

    /// <summary>320 bytes, 10 ms of slin16, every one of them <paramref name="fill"/>.</summary>
    private static byte[] Payload(byte fill) => Enumerable.Repeat(fill, 320).ToArray();

    /// <summary>What <c>NetworkStream.ReadAsync</c> raises when the peer resets the connection.</summary>
    private static IOException ResetFailure() =>
        new(
            "Unable to read data from the transport connection: Connection reset by peer.",
            new SocketException((int)SocketError.ConnectionReset));

    /// <summary>
    /// Records a session's state notifications from its first, the replayed Connecting, and completes
    /// <see cref="Ended"/> with what it recorded when the first ending arrives.
    /// </summary>
    private sealed class SessionRecording : IDisposable
    {
        private readonly ConcurrentQueue<AudioStreamState> _states = new();
        private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<AudioStreamState>> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IDisposable _subscription;

        public SessionRecording(IAudioStream session) =>
            _subscription = session.StateChanges.Subscribe(state =>
            {
                _states.Enqueue(state);
                if (state == AudioStreamState.Connected)
                    _connected.TrySetResult();
                if (state is AudioStreamState.Disconnected or AudioStreamState.Error)
                    _ended.TrySetResult([.. _states]);
            });

        public IReadOnlyList<AudioStreamState> States => [.. _states];

        public Task Connected => _connected.Task;

        /// <summary>The states up to and including the first ending, taken as that ending arrived.</summary>
        public Task<IReadOnlyList<AudioStreamState>> Ended => _ended.Task;

        public void Dispose() => _subscription.Dispose();
    }

    /// <summary>
    /// Completes when the process first raises what a reset connection's read raises: an
    /// <see cref="IOException"/> over <see cref="SocketError.ConnectionReset"/>. The first-chance
    /// notification comes before any catch runs, so it orders the test after the read failed and
    /// before whatever the session does about it.
    /// </summary>
    private sealed class ResetWitness : IDisposable
    {
        private readonly TaskCompletionSource _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ResetWitness() => AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;

        public Task Seen => _seen.Task;

        public void Dispose() => AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;

        private void OnFirstChance(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is IOException { InnerException: SocketException { SocketErrorCode: SocketError.ConnectionReset } })
                _seen.TrySetResult();
        }
    }

    /// <summary>
    /// A server serving exactly one real loopback connection, the far end of it, and what the server
    /// logged and published for it.
    /// </summary>
    private sealed class ServedConnection : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<ReleaseSignallingClient> _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _announced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<AudioStreamState> _states = new();
        private readonly ConcurrentQueue<IDisposable> _subscriptions = new();
        private int _accepts;

        private ServedConnection(TimeSpan idleTimeout)
        {
            Server = new AudioSocketServer(
                new AudioServerOptions
                {
                    AudioSocketPort = 0,
                    ListenAddress = "127.0.0.1",
                    DefaultFormat = "slin16",
                    IdleTimeout = idleTimeout,
                },
                Logger);
            Peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }

        public AudioSocketServer Server { get; }

        public CapturingLogger<AudioSocketServer> Logger { get; } = new();

        /// <summary>The far end: what Asterisk would be.</summary>
        public Socket Peer { get; }

        /// <summary>What the session published, from its identification to its completion.</summary>
        public IReadOnlyList<AudioStreamState> States => [.. _states];

        public bool WasAnnounced => _announced.Task.IsCompleted;

        public static async Task<ServedConnection> StartAsync(TimeSpan? idleTimeout = null)
        {
            var served = new ServedConnection(idleTimeout ?? TimeSpan.FromSeconds(30));
            await served.StartCoreAsync();
            return served;
        }

        private async Task StartCoreAsync()
        {
            _listener.Start();
            Server.AcceptOverride = AcceptAsync;
            _subscriptions.Enqueue(Server.OnStreamConnected.Subscribe(stream =>
            {
                // Subscribed inside the announcement, before the handler waits on the session, so the
                // recording starts at Connected (the subject replays it) and misses nothing after.
                _subscriptions.Enqueue(stream.StateChanges.Subscribe(
                    state => _states.Enqueue(state),
                    () => _completed.TrySetResult()));
                _announced.TrySetResult();
            }));

            await Server.StartAsync();
            await Peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
            await _accepted.Task.WaitAsync(SignalTimeout);
        }

        /// <summary>Sends the identification frame and waits until the server announces the session.</summary>
        public async Task IdentifyAsync(Guid uuid)
        {
            await Peer.SendAsync(BuildUuidFrame(uuid));
            await _announced.Task.WaitAsync(SignalTimeout);
        }

        public async Task SendHangupAsync() => await Peer.SendAsync(BuildFrame(AudioFrameType.Hangup, []));

        public async Task SendErrorAsync() => await Peer.SendAsync(BuildFrame(AudioFrameType.Error, [0x01]));

        /// <summary>An abortive close: with a zero linger, closing sends RST instead of FIN.</summary>
        public void ResetPeer()
        {
            Peer.LingerState = new LingerOption(enable: true, seconds: 0);
            Peer.Dispose();
        }

        /// <summary>An orderly close: FIN, so the server's next read returns end of stream.</summary>
        public void ClosePeer()
        {
            Peer.Shutdown(SocketShutdown.Both);
            Peer.Dispose();
        }

        /// <summary>
        /// Waits until the server's handler has released the connection, and, for a session it
        /// announced, until the session has completed its state notifications. Both come after the
        /// session's loops have ended.
        /// </summary>
        public async Task WaitUntilReleasedAsync()
        {
            var client = await _accepted.Task.WaitAsync(SignalTimeout);
            await client.Released.WaitAsync(SignalTimeout);
            if (WasAnnounced)
                await _completed.Task.WaitAsync(SignalTimeout);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var subscription in _subscriptions.ToArray())
                subscription.Dispose();
            await Server.DisposeAsync();
            Peer.Dispose();
            _listener.Stop();
            if (_accepted.Task.IsCompletedSuccessfully)
                (await _accepted.Task).Dispose();
        }

        /// <summary>
        /// The server's accept seam: the first call hands over the one connection the peer made to
        /// this test's listener, and every later call parks until the server stops.
        /// </summary>
        private async ValueTask<TcpClient> AcceptAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref _accepts) > 1)
                return await AcceptedClients.ParkUntilCancelledAsync(token);

            var socket = await _listener.AcceptSocketAsync(token);
            var client = new ReleaseSignallingClient(socket);
            _accepted.TrySetResult(client);
            return client;
        }
    }
}
