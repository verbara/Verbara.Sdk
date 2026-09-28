using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Tests.TestSupport;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.Ari.Tests.Audio;

public class AudioSocketServerTests : IAsyncDisposable
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private AudioSocketServer? _server;

    [Fact]
    public async Task PublicConstructor_ShouldBindAndAccept_WhenGivenOnlyOptionsAndALogger()
    {
        // The two-argument constructor is the one a consumer resolves from DI. Every other test in
        // this file reaches past it for the internal three-argument overload that takes a clock, so
        // until this test nothing exercised the delegation and nothing said that a server built the
        // way consumers build it accepts at all. A real UUID handshake is the assertion, because
        // registering a stream is the whole of what the delegated construction has to produce.
        var port = GetFreePort();
        await using var server = new AudioSocketServer(
            new AudioServerOptions
            {
                AudioSocketPort = port,
                ListenAddress = "127.0.0.1",
                DefaultFormat = "slin16",
                IdleTimeout = TimeSpan.FromSeconds(5)
            },
            NullLogger<AudioSocketServer>.Instance);

        await server.StartAsync();

        var uuid = Guid.NewGuid();
        using var client = await ConnectAndSendUuidAsync(port, uuid, server);

        server.IsRunning.Should().BeTrue("the delegated construction produced a running server");
        server.ActiveStreamCount.Should().Be(1, "and one that registers what it accepted");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>One byte of type, two of big-endian length, then the payload.</summary>
    private static byte[] BuildFrame(AudioFrameType type, byte[] payload)
    {
        var frame = new byte[3 + payload.Length];
        frame[0] = (byte)type;
        frame[1] = (byte)(payload.Length >> 8);
        frame[2] = (byte)(payload.Length);
        payload.CopyTo(frame.AsSpan(3));
        return frame;
    }

    /// <summary>The UUID travels in RFC 4122 order, most significant byte first, as Asterisk sends it.</summary>
    private static byte[] BuildUuidFrame(Guid uuid) =>
        BuildFrame(AudioFrameType.Uuid, uuid.ToByteArray(bigEndian: true));

    private static byte[] BuildHangupFrame() =>
        BuildFrame(AudioFrameType.Hangup, []);

    /// <summary>
    /// Port 0, so the OS picks the port when the server binds. It is for a server the test never
    /// dials, because its accepts come from <c>AcceptOverride</c>. A port probed with
    /// <see cref="GetFreePort"/> is released before the server binds it, and any process on the
    /// machine can take it in between; the start then fails with "Address already in use".
    /// </summary>
    private const int PortTheOsPicks = 0;

    private AudioSocketServer CreateServer(
        int port,
        int maxStreams = 1000,
        TimeSpan? idleTimeout = null,
        ILogger<AudioSocketServer>? logger = null,
        TimeProvider? timeProvider = null)
    {
        var options = new AudioServerOptions
        {
            AudioSocketPort = port,
            ListenAddress = "127.0.0.1",
            MaxConcurrentStreams = maxStreams,
            DefaultFormat = "slin16",
            IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(5)
        };
        _server = new AudioSocketServer(
            options,
            logger ?? NullLogger<AudioSocketServer>.Instance,
            timeProvider ?? TimeProvider.System);
        return _server;
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    /// <summary>Connects, sends a UUID frame, and waits until the server registers the stream.</summary>
    private static async Task<TcpClient> ConnectAndSendUuidAsync(int port, Guid uuid, AudioSocketServer server)
    {
        var client = await ConnectAsync(port);
        var stream = client.GetStream();
        await stream.WriteAsync(BuildUuidFrame(uuid));
        await stream.FlushAsync();

        // Wait for server to register the stream
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (server.GetStream(uuid.ToString()) is null && !cts.Token.IsCancellationRequested)
            await Task.Delay(20, cts.Token);

        return client;
    }

    [Fact]
    public async Task StartAsync_ShouldSetIsRunning()
    {
        var port = GetFreePort();
        var server = CreateServer(port);

        server.IsRunning.Should().BeFalse();

        await server.StartAsync();

        server.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsync_ShouldClearIsRunning()
    {
        var port = GetFreePort();
        var server = CreateServer(port);

        await server.StartAsync();
        server.IsRunning.Should().BeTrue();

        await server.StopAsync();
        server.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_ShouldNotThrow_WhenNotStarted()
    {
        var server = CreateServer(GetFreePort());

        var act = async () => await server.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ActiveStreamCount_ShouldBeZero_WhenNoConnections()
    {
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();

        server.ActiveStreamCount.Should().Be(0);
        server.ActiveStreams.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStream_ShouldReturnNull_WhenNoStreams()
    {
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();

        server.GetStream("nonexistent-channel-id").Should().BeNull();
    }

    [Fact]
    public async Task HandleConnection_ShouldRegisterStream_WhenUuidReceived()
    {
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();

        var uuid = Guid.NewGuid();
        using var client = await ConnectAndSendUuidAsync(port, uuid, server);

        server.ActiveStreamCount.Should().Be(1);
        var registeredStream = server.GetStream(uuid.ToString());
        registeredStream.Should().NotBeNull();
        registeredStream!.ChannelId.Should().Be(uuid.ToString());
        registeredStream.Format.Should().Be("slin16");
    }

    [Fact]
    public async Task GetStream_ShouldMiss_WhenIdentifierIsNotCanonicalLowercase()
    {
        // The negative control behind ExternalMediaActivity minting its identifier with
        // Guid.ToString(). The fixture is a measurement, not an argument: probe-capture.txt RUN U
        // sent this exact UUID to a real Asterisk — 22.9.0 and 23.4.1, identically — as both
        // channelId and data:
        //     -> HTTP 200  id=EC30994A-B0E1-4EBE-99BD-19CA35669F7A
        //     HEX: 01 00 10 ec 30 99 4a b0 e1 4e be 99 bd 19 ca 35 66 9f 7a
        // Asterisk accepted the uppercase spelling and echoed Channel.Id back in it, while the
        // identification frame carried sixteen raw bytes. This test puts those sixteen bytes into
        // the real server and then asks it for the stream BOTH ways — the way a consumer holding
        // Channel.Id would, and the way the table is actually keyed.
        const string asSentToAsterisk = "EC30994A-B0E1-4EBE-99BD-19CA35669F7A";
        var wireUuid = Guid.Parse(asSentToAsterisk);
        var canonical = wireUuid.ToString();
        canonical.Should().NotBe(asSentToAsterisk,
            "the fixture has to differ in spelling from its canonical form, or this test controls nothing");

        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();

        using var client = await ConnectAndSendUuidAsync(port, wireUuid, server);

        server.ActiveStreamCount.Should().Be(1, "the identification frame was accepted");
        server.GetStream(canonical).Should().NotBeNull(
            "ParseUuid renders the sixteen wire bytes with new Guid(bytes, bigEndian: true).ToString(), "
            + "so the table is keyed by the canonical lowercase hyphenated form");
        server.GetStream(asSentToAsterisk).Should().BeNull(
            "the table is a ConcurrentDictionary<string, ...> with the default ORDINAL comparer. A "
            + "channel created with a non-canonical channelId comes back with Channel.Id in that "
            + "spelling, so GetStream(Channel.Id) misses — HTTP 200, a live stream, and no error on "
            + "any hop. That silence is why the identifier is minted rather than accepted");
    }

    [Fact]
    public async Task HandleConnection_ShouldEmitOnStreamConnected_WhenUuidReceived()
    {
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();

        IAudioStream? emittedStream = null;
        var streamReceived = new TaskCompletionSource();
        using var sub = server.OnStreamConnected.Subscribe(s =>
        {
            emittedStream = s;
            streamReceived.TrySetResult();
        });

        var uuid = Guid.NewGuid();
        using var client = await ConnectAndSendUuidAsync(port, uuid, server);

        await streamReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));

        emittedStream.Should().NotBeNull();
        emittedStream!.ChannelId.Should().Be(uuid.ToString());
    }

    [Fact]
    public async Task HandleConnection_ShouldRemoveStream_WhenHangupReceived()
    {
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();

        var uuid = Guid.NewGuid();
        using var client = await ConnectAndSendUuidAsync(port, uuid, server);

        // Verify stream is registered
        server.GetStream(uuid.ToString()).Should().NotBeNull();

        // Send hangup frame
        var stream = client.GetStream();
        await stream.WriteAsync(BuildHangupFrame());
        await stream.FlushAsync();

        // Wait for server to remove the stream
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (server.GetStream(uuid.ToString()) is not null && !cts.Token.IsCancellationRequested)
            await Task.Delay(20, cts.Token);

        server.GetStream(uuid.ToString()).Should().BeNull();
        server.ActiveStreamCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleConnection_ShouldDisposeSession_WhenNoUuidReceived()
    {
        var port = GetFreePort();
        var server = CreateServer(port, idleTimeout: TimeSpan.FromMilliseconds(300));
        await server.StartAsync();

        // Connect but never send a UUID frame
        using var client = await ConnectAsync(port);

        // Wait longer than the idle timeout
        await Task.Delay(TimeSpan.FromMilliseconds(600));

        // No stream should have been registered
        server.ActiveStreamCount.Should().Be(0);
    }

    [Fact]
    public async Task MaxConcurrentStreams_ShouldDropExcessConnections()
    {
        var port = GetFreePort();
        var server = CreateServer(port, maxStreams: 1);
        await server.StartAsync();

        // First connection — should be accepted
        var uuid1 = Guid.NewGuid();
        using var client1 = await ConnectAndSendUuidAsync(port, uuid1, server);
        server.ActiveStreamCount.Should().Be(1);

        // Second connection — should be dropped by the server (MaxConcurrentStreams=1)
        var uuid2 = Guid.NewGuid();
        using var client2 = await ConnectAsync(port);
        var stream2 = client2.GetStream();
        await stream2.WriteAsync(BuildUuidFrame(uuid2));
        await stream2.FlushAsync();

        // Give the server time to process and drop
        await Task.Delay(300);

        // Only the first stream should be registered
        server.ActiveStreamCount.Should().Be(1);
        server.GetStream(uuid1.ToString()).Should().NotBeNull();
        server.GetStream(uuid2.ToString()).Should().BeNull();
    }

    [Fact]
    public async Task StopAsync_ShouldDisposeAllActiveSessions()
    {
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();

        var uuid = Guid.NewGuid();
        using var client = await ConnectAndSendUuidAsync(port, uuid, server);
        server.ActiveStreamCount.Should().Be(1);

        await server.StopAsync();

        server.ActiveStreamCount.Should().Be(0);
        server.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_ShouldStopRunningServer()
    {
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();
        server.IsRunning.Should().BeTrue();

        await server.DisposeAsync();

        server.IsRunning.Should().BeFalse();
        // Set to null so the fixture cleanup does not double-dispose
        _server = null;
    }

    // ------------------------------------------------- the running flag: one listener, one teardown

    [Fact]
    public async Task StartAsync_ShouldBindNoSecondListener_WhenTheServerIsAlreadyRunning()
    {
        // Arrange — a server already bound and accepting.
        var port = GetFreePort();
        var logger = new CapturingLogger();
        var server = CreateServer(port, logger: logger);
        await server.StartAsync();

        try
        {
            // Act — the second start. Without a reentrancy guard this builds a second TcpListener
            // on the port the first one already holds and overwrites _cts, _listener and
            // _acceptLoop with it, so the first loop is left running against a source nothing can
            // cancel.
            await server.StartAsync();

            // Assert — the log is the observable these servers have. Neither carries a bound-port
            // property, so "no second listener" is read as "the start did not happen twice", and
            // then as "the listener the first start bound is still the one accepting".
            logger.Entries.Should().ContainSingle(
                entry => entry.EventName == "ServerStarted",
                "a start that finds the server already running returns before it binds, logs or " +
                "replaces anything");
            server.IsRunning.Should().BeTrue("the server the first start bound is still running");

            var uuid = Guid.NewGuid();
            using var client = await ConnectAndSendUuidAsync(port, uuid, server);

            server.GetStream(uuid.ToString()).Should().NotBeNull(
                "the accept loop the first start began is still the one serving the port");
        }
        finally
        {
            // Bounded, and that bound is part of what this test is about. A second start that got
            // as far as replacing _cts leaves the FIRST accept loop running against a source
            // nothing can cancel, and StopAsync then awaits that loop forever — so an unbounded
            // teardown would hang the whole run instead of failing this one test. The timeout is
            // swallowed rather than raised so it cannot stand in front of the assertion that
            // already failed.
            _server = null;
            try
            {
                await server.DisposeAsync().AsTask().WaitAsync(SignalTimeout);
            }
            catch (TimeoutException)
            {
                /* Unrecoverable — which is the state this test exists to keep out of the tree */
            }
        }
    }

    [Fact]
    public async Task StopAsync_ShouldTearDownOnce_WhenCalledTwice()
    {
        // Arrange
        var port = GetFreePort();
        var logger = new CapturingLogger();
        var server = CreateServer(port, logger: logger);
        await server.StartAsync();

        // Act — the second stop finds the server already stopped. Without the guard it walks the
        // whole teardown again: Stop() on a listener that is already down, a second CancelAsync,
        // and a second pass over the session table.
        await server.StopAsync();
        await server.StopAsync();

        // Assert
        logger.Entries.Should().ContainSingle(
            entry => entry.EventName == "ServerStopped",
            "a stop that finds the server already stopped returns instead of repeating the teardown");
        server.IsRunning.Should().BeFalse("the server is stopped, and stopping it twice does not " +
            "make it more stopped");
    }

    [Fact]
    public async Task StopAsync_ShouldReportNotRunning_WhenTheStopHasBegunButHasNotFinished()
    {
        // Arrange — a registered session whose connection handler is parked inside the
        // OnStreamConnected emission. The handler registers the session BEFORE it publishes it, so
        // the session is still in the server's table when StopAsync walks it, and a parked handler
        // cannot dispose it first. That is what keeps the stop in flight while this test reads the
        // flag: the stop's own teardown of that session awaits the session's read pump, which is
        // parked on a socket read on another thread and so cannot complete inline.
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();

        var handlerParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var parking = server.OnStreamConnected.Subscribe(_ =>
        {
            handlerParked.TrySetResult();
            release.Wait();
        });

        bool stopWasStillRunningWhenRead;
        bool runningWhileStopping;
        try
        {
            using var client = await ConnectAsync(port);
            var stream = client.GetStream();
            await stream.WriteAsync(BuildUuidFrame(Guid.NewGuid()));
            await stream.FlushAsync();
            await handlerParked.Task.WaitAsync(SignalTimeout);

            // Act — StopAsync runs synchronously as far as its first await, so by the time it
            // hands back its ValueTask the listener is already down: the stop has unambiguously
            // begun. Both reads are taken before anything is released, so neither waits on a clock.
            var stop = server.StopAsync().AsTask();
            stopWasStillRunningWhenRead = !stop.IsCompleted;
            runningWhileStopping = server.IsRunning;

            release.Set();
            await stop.WaitAsync(SignalTimeout);
        }
        finally
        {
            release.Set();
        }

        // Assert
        stopWasStillRunningWhenRead.Should().BeTrue(
            "the premise of this test is a stop caught mid-teardown; a stop that had already " +
            "finished would report false for the old reason and prove nothing");
        runningWhileStopping.Should().BeFalse(
            "IsRunning describes the server's intent, not the completion of its teardown — a stop " +
            "that has begun is a stop, and the accept loop's classification of its own shutdown " +
            "depends on reading it that way");
    }

    // ------------------------------------------------------- accept failures that are not the stop

    [Fact]
    public async Task AcceptLoopAsync_ShouldLogErrorAndKeepAccepting_WhenAnAcceptFails()
    {
        // Arrange — the accept fails once with the shape a descriptor exhaustion has, then parks, so
        // the loop's own behaviour is the only thing that can produce a second attempt. The backoff
        // runs on a fake clock, so the loop resumes when this test moves it and at no other moment.
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();
        var server = CreateServer(GetFreePort(), logger: logger, timeProvider: time);
        var attempts = 0;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AcceptOverride = token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new SocketException((int)SocketError.TooManyOpenSockets);

            secondAttempt.TrySetResult();
            return ParkUntilCancelledAsync(token);
        };

        await server.StartAsync();
        try
        {
            // Act — the loop reaches its backoff, which is what creates the only timer on this clock
            var backoff = await NextTimerAsync(time);

            // Assert — the failure was reported, and the server still says it is accepting
            backoff.DueTime.Should().Be(
                AudioSocketServer.InitialAcceptBackoff,
                "the first of a run of failed accepts waits the initial backoff");
            logger.Entries.Should().ContainSingle(
                entry => entry.Level == LogLevel.Error
                    && entry.EventName == "AcceptLoopFailed"
                    && entry.ExceptionType == nameof(SocketException),
                "an accept that failed while the server is still running is the loss of every " +
                "connection, so it is logged at Error rather than vanishing into an unobserved task");
            server.IsRunning.Should().BeTrue(
                "IsRunning means bound and accepting, and after a backed-off failure the server is " +
                "still both");
            secondAttempt.Task.IsCompleted.Should().BeFalse(
                "the loop waits the backoff out before it accepts again, instead of spinning");

            // Act — the backoff is the only thing holding the loop, so moving the clock releases it
            time.Advance(AudioSocketServer.InitialAcceptBackoff);
            await secondAttempt.Task.WaitAsync(SignalTimeout);

            // Assert
            Volatile.Read(ref attempts).Should().Be(
                2,
                "the loop keeps accepting after a failure rather than ending with the port bound");
            server.IsRunning.Should().BeTrue();
        }
        finally
        {
            _server = null;
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcceptLoopAsync_ShouldDoubleTheBackoffToTheCap_WhenAcceptsKeepFailing()
    {
        // Arrange — every accept fails, so the loop is a pure backoff generator and each wait it asks
        // for is read off the fake clock without any of it being spent.
        var time = new FakeTimeProvider();
        var server = CreateServer(GetFreePort(), logger: new CapturingLogger(), timeProvider: time);
        server.AcceptOverride = _ => throw new SocketException((int)SocketError.TooManyOpenSockets);

        await server.StartAsync();
        try
        {
            // Act — read each wait as the loop asks for it, then release it to reach the next
            var waits = new List<TimeSpan>();
            for (var i = 0; i < 8; i++)
            {
                var timer = await NextTimerAsync(time);
                waits.Add(timer.DueTime);
                time.Advance(timer.DueTime);
            }

            // Assert
            waits.Should().Equal(
                [
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(200),
                    TimeSpan.FromMilliseconds(400),
                    TimeSpan.FromMilliseconds(800),
                    TimeSpan.FromMilliseconds(1600),
                    TimeSpan.FromMilliseconds(3200),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5)
                ],
                "the wait doubles with each consecutive failure and then holds at the cap, so a " +
                "failure that persists neither spins nor walks away");
            waits[0].Should().Be(AudioSocketServer.InitialAcceptBackoff);
            waits[^1].Should().Be(AudioSocketServer.MaxAcceptBackoff);
            server.IsRunning.Should().BeTrue("the server is still bound and still accepting");
        }
        finally
        {
            _server = null;
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcceptLoopAsync_ShouldResetTheBackoff_WhenAnAcceptSucceedsBetweenFailures()
    {
        // Arrange — a real loopback pair supplies the one connection the middle accept returns, so the
        // success is a real hand-off and not a stub. Accepts one and three fail; the wait the loop asks
        // for after the third is what says whether the success reset the run.
        using var pair = new TcpListener(IPAddress.Loopback, 0);
        pair.Start();
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)pair.LocalEndpoint).Port);
        using var accepted = await pair.AcceptTcpClientAsync();

        var time = new FakeTimeProvider();
        var server = CreateServer(GetFreePort(), logger: new CapturingLogger(), timeProvider: time);
        var attempts = 0;
        server.AcceptOverride = token => Interlocked.Increment(ref attempts) switch
        {
            2 => ValueTask.FromResult(accepted),
            >= 4 => ParkUntilCancelledAsync(token),
            _ => throw new SocketException((int)SocketError.TooManyOpenSockets)
        };

        await server.StartAsync();
        try
        {
            // Act — the first failure's wait, then the success, then the second failure's wait
            var afterFirstFailure = await NextTimerAsync(time);
            time.Advance(afterFirstFailure.DueTime);
            var afterSuccess = await NextTimerAsync(time);

            // Assert
            afterFirstFailure.DueTime.Should().Be(AudioSocketServer.InitialAcceptBackoff);
            afterSuccess.DueTime.Should().Be(
                AudioSocketServer.InitialAcceptBackoff,
                "a successful accept starts the run over, so the next failure waits the initial " +
                "backoff again rather than the doubled one");
        }
        finally
        {
            _server = null;
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopAsync_ShouldReportNoAcceptFailure_WhenTheStopAbortsThePendingAccept()
    {
        // Arrange — THE case that separates this change from the two that preceded it. The accept is
        // parked and ends as SocketException(OperationAborted), which is the shape a Linux stop
        // produces on a pending accept — measured at 7 of 10 in a sibling change's probe, against 3
        // of 10 for OperationCanceledException. Driving that abort from the stop's own cancellation
        // instead of waiting for the real 7-in-10 makes it certain rather than likely, and it is the
        // WEAKER of the two moments: the real abort comes from _listener.Stop(), which StopAsync runs
        // EARLIER still. Either way the running flag is already down by then — unless it is cleared
        // last, which is what this test exists to keep out of the tree.
        //
        // The clock is fake and never moves, so any backoff the loop took would be a timer it created
        // and would never be waited out by accident.
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();
        var server = CreateServer(GetFreePort(), logger: logger, timeProvider: time);
        var accepting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AcceptOverride = token =>
        {
            accepting.TrySetResult();
            return AbortOnCancellationAsync(token);
        };

        await server.StartAsync();
        _server = null;
        await accepting.Task.WaitAsync(SignalTimeout);

        // Act — StopAsync awaits the accept loop, so it returns only once the loop has ended
        await server.StopAsync().AsTask().WaitAsync(SignalTimeout);

        // Assert
        logger.Entries.Should().NotContain(
            entry => entry.EventName == "AcceptLoopFailed",
            "an accept aborted by the stop is the stop, not a failure — reporting it would put an " +
            "Error in the log on every ordinary shutdown and make the report worthless exactly " +
            "when a real failure needs to stand out");
        logger.Entries.Should().Contain(
            entry => entry.EventName == "ServerStopped",
            "the stop ran to the end rather than being left behind by a loop still backing off");
        server.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task StopAsync_ShouldReportNoAcceptFailure_WhenARealListenerStopAbortsTheAccept()
    {
        // Arrange — the same ending as the case above, but produced by the real listener rather than
        // fabricated: a real connection is accepted first, so the loop is known to be parked on its
        // next accept when _listener.Stop() aborts it. Which of the two shutdown shapes the platform
        // raises is not deterministic, so this case cannot be the guard — it is the evidence that the
        // guarded path is the one production actually takes.
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();
        var port = GetFreePort();
        var server = CreateServer(port, logger: logger, timeProvider: time);
        await server.StartAsync();

        using var client = await ConnectAndSendUuidAsync(port, Guid.NewGuid(), server);
        server.ActiveStreamCount.Should().Be(1, "the loop has accepted once and is parked on the next accept");

        // Act
        _server = null;
        await server.StopAsync().AsTask().WaitAsync(SignalTimeout);

        // Assert
        logger.Entries.Should().NotContain(
            entry => entry.EventName == "AcceptLoopFailed",
            "a real stop aborting a real pending accept is the stop, not a failure");
        logger.Entries.Should().Contain(
            entry => entry.EventName == "ServerStopped",
            "the stop ran to the end");
        server.IsRunning.Should().BeFalse();
    }

    // ------------------------------------- a connection whose configuration fails after the accept

    [Fact]
    public async Task AcceptLoopAsync_ShouldCloseTheAcceptedSocket_WhenConfiguringItThrows()
    {
        // Arrange — the accept seam hands over a client whose socket is a UDP socket, so setting
        // TCP_NODELAY on it throws a SocketException (setsockopt answers ENOPROTOOPT) while the socket
        // is still open. The clock is fake and never moves. A loop that takes the failure for an
        // accept failure asks it for a backoff timer, and that timer is the signal the test ends on
        // in that case; otherwise the handler releasing the client is.
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();
        var server = CreateServer(PortTheOsPicks, logger: logger, timeProvider: time);
        using var accepted = AcceptedClients.UdpBacked();
        using var client = new ReleaseSignallingClient(accepted.Socket);
        var attempts = 0;
        server.AcceptOverride = token => Interlocked.Increment(ref attempts) == 1
            ? ValueTask.FromResult<TcpClient>(client)
            : AcceptedClients.ParkUntilCancelledAsync(token);
        var backoffRequested = time.TimersCreated.ReadAsync().AsTask();

        await server.StartAsync();
        try
        {
            // Act
            await Task.WhenAny(client.Released, backoffRequested).WaitAsync(SignalTimeout);

            // Assert
            using (new AssertionScope())
            {
                accepted.IsSocketClosed.Should().BeTrue(
                    "a connection the loop accepted and could not configure is closed, not leaked; the " +
                    "server logged [{0}]",
                    Describe(logger));
                logger.Entries.Should().ContainSingle(
                    entry => entry.Level == LogLevel.Error
                        && entry.EventName == "ConnectionError"
                        && entry.ExceptionType == nameof(SocketException),
                    "the failure belongs to that one connection, so it is reported once as its error");
                logger.Entries.Should().NotContain(
                    entry => entry.EventName == "AcceptLoopFailed",
                    "no accept failed: the accept returned a connection, and configuring it is serving it");
                server.IsRunning.Should().BeTrue("one connection failing is not the server failing");
            }
        }
        finally
        {
            _server = null;
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcceptLoopAsync_ShouldKeepAccepting_WhenConfiguringAnAcceptedClientThrowsObjectDisposed()
    {
        // Arrange — the accept seam hands over a client whose socket is already disposed, so setting
        // TCP_NODELAY on it throws an ObjectDisposedException: the type the loop reads as its own
        // listener's stop. The second accept is the signal. A loop that ends on the first connection
        // never makes it, so in that case only the bound ends the wait.
        var logger = new CapturingLogger();
        var server = CreateServer(PortTheOsPicks, logger: logger, timeProvider: new FakeTimeProvider());
        using var accepted = AcceptedClients.DisposedSocket();
        using var client = new ReleaseSignallingClient(accepted.Socket);
        var attempts = 0;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AcceptOverride = token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                return ValueTask.FromResult<TcpClient>(client);

            secondAttempt.TrySetResult();
            return AcceptedClients.ParkUntilCancelledAsync(token);
        };

        await server.StartAsync();
        try
        {
            // Act
            var nextAccept = () => secondAttempt.Task;
            await nextAccept.Should().CompleteWithinAsync(
                SignalTimeout,
                "one connection's closed socket is not the server's stop, so the loop accepts again");
            await client.Released.WaitAsync(SignalTimeout);

            // Assert
            using (new AssertionScope())
            {
                server.IsRunning.Should().BeTrue("the server is still bound and still accepting");
                logger.Entries.Should().ContainSingle(
                    entry => entry.Level == LogLevel.Error
                        && entry.EventName == "ConnectionError"
                        && entry.ExceptionType == nameof(ObjectDisposedException),
                    "the failure belongs to that one connection, so it is reported once as its error; " +
                    "the server logged [{0}]",
                    Describe(logger));
                logger.Entries.Should().NotContain(
                    entry => entry.EventName == "AcceptLoopFailed",
                    "no accept failed");
            }
        }
        finally
        {
            _server = null;
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcceptLoopAsync_ShouldAcceptTheNextConnectionWithoutWaiting_WhenConfiguringOneFails()
    {
        // Arrange — the configure fails with a SocketException, the type the loop backs off for when an
        // accept fails. The clock is fake and never moves, so a backoff would park the loop for good,
        // and the timer it asks for is the signal the test ends on in that case. Otherwise the next
        // accept is.
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();
        var server = CreateServer(PortTheOsPicks, logger: logger, timeProvider: time);
        using var accepted = AcceptedClients.UdpBacked();
        using var client = new ReleaseSignallingClient(accepted.Socket);
        var attempts = 0;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AcceptOverride = token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                return ValueTask.FromResult<TcpClient>(client);

            secondAttempt.TrySetResult();
            return AcceptedClients.ParkUntilCancelledAsync(token);
        };
        var backoffRequested = time.TimersCreated.ReadAsync().AsTask();

        await server.StartAsync();
        try
        {
            // Act
            await Task.WhenAny(secondAttempt.Task, backoffRequested).WaitAsync(SignalTimeout);

            // Assert — the loop requests a backoff timer before its next accept, never after it, so
            // at the moment of the next accept, no timer means none was requested
            using (new AssertionScope())
            {
                secondAttempt.Task.IsCompleted.Should().BeTrue(
                    "no accept failed, so there is nothing to wait out before the next one; attempts = " +
                    "{0}, the server logged [{1}]",
                    Volatile.Read(ref attempts),
                    Describe(logger));
                backoffRequested.IsCompleted.Should().BeFalse("one connection's failure costs the next one no wait");
                logger.Entries.Should().NotContain(
                    entry => entry.EventName == "AcceptLoopFailed",
                    "no accept failed");
                server.IsRunning.Should().BeTrue();
            }
        }
        finally
        {
            _server = null;
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldReportTheFailure_WhenTheAcceptedClientIsNotConnected()
    {
        // Arrange — the accept seam hands over a TCP client that was never connected. Setting
        // TCP_NODELAY on it succeeds, so the failure lands one step later, when the handler asks the
        // client for its stream (GetStream() throws InvalidOperationException). The handler owns the
        // connection by then, so it has to close it and report the failure itself: nothing observes the
        // task the loop discards.
        //
        // Ordered by construction: the loop starts the handler inline, on its own continuation, and
        // this failure comes before the handler's first await, so the handler's task has completed
        // before the loop asks for its next accept. That next accept is the signal. The premise is
        // checked below, because the unobserved-exception witness reads nothing from a task that had
        // not completed when the collection ran.
        var logger = new CapturingLogger();
        var server = CreateServer(PortTheOsPicks, logger: logger, timeProvider: new FakeTimeProvider());
        using var accepted = AcceptedClients.NeverConnected();
        using var client = new ReleaseSignallingClient(accepted.Socket);
        using var unobserved = new UnobservedServerFaults();
        var attempts = 0;
        var releasedBeforeNextAccept = false;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AcceptOverride = token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                return ValueTask.FromResult<TcpClient>(client);

            releasedBeforeNextAccept = client.Released.IsCompleted;
            secondAttempt.TrySetResult();
            return AcceptedClients.ParkUntilCancelledAsync(token);
        };

        await server.StartAsync();
        try
        {
            // Act
            await secondAttempt.Task.WaitAsync(SignalTimeout);
            await client.Released.WaitAsync(SignalTimeout);
            UnobservedServerFaults.CollectDiscardedTasks();

            // Assert
            using (new AssertionScope())
            {
                releasedBeforeNextAccept.Should().BeTrue(
                    "the premise of this test is a handler that had finished with the connection before " +
                    "the loop accepted again; otherwise the collection may run before its task completes");
                accepted.IsSocketClosed.Should().BeTrue(
                    "the handler owns the connection, so it closes it when it cannot serve it");
                logger.Entries.Should().ContainSingle(
                    entry => entry.Level == LogLevel.Error
                        && entry.EventName == "ConnectionError"
                        && entry.ExceptionType == nameof(InvalidOperationException),
                    "a connection whose stream cannot be obtained is that connection's failure, reported " +
                    "once as its error; the server logged [{0}]",
                    Describe(logger));
                unobserved.Faults.Should().BeEmpty(
                    "the failure is reported where it happened, so it does not escape the task the loop " +
                    "discards, where nothing observes it; every unobserved exception seen: [{0}]",
                    string.Join("; ", unobserved.All));
                logger.Entries.Should().NotContain(
                    entry => entry.EventName == "AcceptLoopFailed",
                    "no accept failed");
                server.IsRunning.Should().BeTrue("one connection failing is not the server failing");
            }
        }
        finally
        {
            _server = null;
            await server.DisposeAsync();
        }
    }

    // -------------------------------------- a connection that fails after the server took it on

    [Fact]
    public async Task HandleConnection_ShouldReportIt_WhenAnObserverOfNewStreamsThrows()
    {
        // Arrange — OnStreamConnected is published from inside the connection handler, synchronously,
        // so a consumer's subscription throwing is an exception the handler is left holding. Until
        // this test that arm had no coverage of any kind, which left "a consumer's fault is not the
        // server's" as an intention rather than a fact.
        var port = GetFreePort();
        var logger = new CapturingLogger();
        var server = CreateServer(port, logger: logger);
        await server.StartAsync();

        using var sub = server.OnStreamConnected.Subscribe(
            _ => throw new InvalidOperationException("the consumer's handler failed"));

        using var client = await ConnectAsync(port);
        var stream = client.GetStream();

        // Act — a UUID frame is what takes the connection past the handler's wait and into the
        // publish that throws
        await stream.WriteAsync(BuildUuidFrame(Guid.NewGuid()));
        await stream.FlushAsync();

        // Assert — the read returns 0 only once the handler has released the connection, so the wait
        // is on the server winding it up and not on a clock. By then the catch has already logged.
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(SignalTimeout);

        read.Should().Be(
            0,
            "the handler releases the connection on the failing path too, rather than leaking it");
        logger.Entries.Should().ContainSingle(
            entry => entry.Level == LogLevel.Error
                && entry.EventName == "ConnectionError"
                && entry.ExceptionType == nameof(InvalidOperationException),
            "a failure that is neither the stop nor the idle deadline is the loss of that " +
            "connection, so it is logged at Error instead of vanishing into the unobserved task " +
            "the accept loop started the handler in");
        server.ActiveStreamCount.Should().Be(
            0,
            "the finally deregisters the session on the failing path as well as the clean one");
        server.IsRunning.Should().BeTrue("one connection failing is not the server failing");
    }

    [Fact]
    public async Task HandleConnection_ShouldWindUpAtOnce_WhenThePeerHangsUpAsSoonAsItIdentifies()
    {
        // Arrange — the idle deadline is set far beyond the wait below, deliberately: if the handler
        // ever needed the deadline to notice a session that was already gone, this test would sit out
        // the 10 s SignalTimeout and fail. What it pins is that the wind-up is driven by the
        // disconnect itself.
        //
        // The line that makes that true is not separable by this test, and saying so is the finding
        // rather than an omission. The handler checks `!session.IsConnected` after subscribing, and
        // the subscription is to a BehaviorSubject, which replays the session's current state to a
        // new subscriber — so on this ordering the replay has ALREADY completed the wait by the time
        // the check runs. The check still covers the case the replay cannot: a session disposed
        // while its last published state is still Connected. Removing it leaves this test green.
        var port = GetFreePort();
        var logger = new CapturingLogger();
        var server = CreateServer(port, idleTimeout: TimeSpan.FromSeconds(30), logger: logger);
        await server.StartAsync();

        using var client = await ConnectAsync(port);
        var stream = client.GetStream();

        // Act — identify, then hang up in the same breath. The half-close keeps this end readable,
        // so the server's own release is still observable from here.
        await stream.WriteAsync(BuildUuidFrame(Guid.NewGuid()));
        await stream.FlushAsync();
        client.Client.Shutdown(SocketShutdown.Send);

        // Assert
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(SignalTimeout);

        read.Should().Be(
            0,
            "a peer that hangs up the instant after it identifies itself is wound up when it hangs " +
            "up, not when a deadline nobody is waiting for expires");
        server.ActiveStreamCount.Should().Be(0, "the session is deregistered as it is wound up");
        logger.Entries.Should().NotContain(
            entry => entry.EventName == "ConnectionError",
            "a peer hanging up is an ending, not a failure, so it is not reported as one");
    }

    // ------------------------------- a registry releases the session it holds, and hands a shared id on

    // Asterisk does not keep the identification UUID unique: it kept four TCP connections open on one
    // UUID, and AudioSocket(), Dial(AudioSocket/) and an externalMedia create that reuses `data` all
    // produce a second one. Every test below connects in order and waits for each connection's own
    // announcement before the next one connects, so which connection is which is fixed by
    // construction. An ending is waited for on that connection's disposal, which its handler performs
    // strictly after its release, so every assertion reads the table after the release and after any
    // hand-over the release made.

    [Fact]
    public async Task HandleConnection_ShouldKeepTheLiveStreamRegistered_WhenASameIdConnectionEnds()
    {
        // Arrange — A holds X; B presents X while A is live, so B does not become its holder
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();
        using var announcements = new Announcements(server);
        var x = Guid.NewGuid();

        using var holder = await ConnectAndAwaitAnnouncementAsync(port, x, announcements);
        using var duplicate = await ConnectAndAwaitAnnouncementAsync(port, x, announcements);

        // Act
        await duplicate.HangUpAsync();

        // Assert
        server.GetStream(x.ToString()).Should().BeSameAs(
            holder.Stream,
            "an ending releases only the entry its own session holds, and the connection that ended " +
            "never held X — removing by key alone would unregister the other call's live stream");
        server.ActiveStreamCount.Should().Be(1, "the live holder is still counted");
    }

    [Fact]
    public async Task HandleConnection_ShouldHandTheIdToTheConnectionThatSharedIt_WhenTheHolderEnds()
    {
        // Arrange — the order a real call takes: A holds X, B presents X after it, and A ends first
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();
        using var announcements = new Announcements(server);
        var x = Guid.NewGuid();

        using var holder = await ConnectAndAwaitAnnouncementAsync(port, x, announcements);
        using var sharer = await ConnectAndAwaitAnnouncementAsync(port, x, announcements);

        // Act
        await holder.HangUpAsync();

        // Assert
        server.GetStream(x.ToString()).Should().BeSameAs(
            sharer.Stream,
            "the connection that presented X after the holder is still open, so the holder's ending " +
            "hands X to it instead of leaving a live call that nothing can find");
        server.ActiveStreamCount.Should().Be(1, "the connection that took X over is counted");

        await server.StopAsync().AsTask().WaitAsync(SignalTimeout);
        (await sharer.ReadAsync()).Should().Be(
            0,
            "the stop closes the connection that took X over, like any registered stream");
    }

    [Fact]
    public async Task HandleConnection_ShouldReleaseTheIdItRegisteredUnder_WhenTheConnectionIdentifiesItselfTwice()
    {
        // Arrange — C holds Y; A registers under X
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();
        using var announcements = new Announcements(server);
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();

        using var otherCall = await ConnectAndAwaitAnnouncementAsync(port, y, announcements);
        using var reidentifying = await ConnectAndAwaitAnnouncementAsync(port, x, announcements);

        // Act — a second identification frame naming Y, then a hangup. The session reassigns its
        // ChannelId to Y on that frame, before the hangup that ends it.
        await reidentifying.SendAsync(BuildUuidFrame(y));
        await reidentifying.HangUpAsync();

        // Assert
        server.GetStream(y.ToString()).Should().BeSameAs(
            otherCall.Stream,
            "the ending connection registered under X, so it has no claim on the other call's Y");
        server.GetStream(x.ToString()).Should().BeNull(
            "the entry the connection registered under X is the one its ending releases, whatever " +
            "its session reports as its id by then");
        server.ActiveStreamCount.Should().Be(1, "only the other call is still live");
    }

    [Fact]
    public async Task HandleConnection_ShouldHandTheIdOnInArrivalOrder_WhenAWaitingConnectionEndsFirst()
    {
        // Arrange — A holds X; B and then C present X
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();
        using var announcements = new Announcements(server);
        var x = Guid.NewGuid();

        using var first = await ConnectAndAwaitAnnouncementAsync(port, x, announcements);
        using var second = await ConnectAndAwaitAnnouncementAsync(port, x, announcements);
        using var third = await ConnectAndAwaitAnnouncementAsync(port, x, announcements);

        // Act and assert — B ends while it waits
        await second.HangUpAsync();
        server.GetStream(x.ToString()).Should().BeSameAs(
            first.Stream,
            "a waiting connection that ends holds nothing, so the live holder keeps X");

        // A ends: X passes to the earliest connection still open, which is C, never B's ended session
        await first.HangUpAsync();
        server.GetStream(x.ToString()).Should().BeSameAs(
            third.Stream,
            "the hand-over follows arrival order and skips a waiting connection that has ended");

        // C ends with nobody left waiting
        await third.HangUpAsync();
        server.GetStream(x.ToString()).Should().BeNull("the last connection that presented X has ended");
        server.ActiveStreamCount.Should().Be(0, "nothing is live");
    }

    [Fact]
    public async Task HandleConnection_ShouldSkipAWaitingConnectionThatHasDisconnected_WhenTheHolderEndsBeforeItsRelease()
    {
        // Arrange — A holds X; B and then C present X. The test above lets B's own release run before
        // A ends, and that release takes B out of the waiting list, so the hand-over never meets it.
        // This one holds B between its disconnect and its release. A session notifies its state
        // observers in subscription order, and the one below subscribes inside B's announcement, before
        // B's handler subscribes the observer that ends its wait. Parking inside B's Disconnected
        // notification therefore holds B's release back, by construction, while A ends.
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();
        using var announcements = new Announcements(server);
        var x = Guid.NewGuid();

        var disconnectedButNotReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var announced = 0;
        using var parking = server.OnStreamConnected.Subscribe(stream =>
        {
            if (Interlocked.Increment(ref announced) != 2)
                return;

            stream.StateChanges.Subscribe(state =>
            {
                if (state is AudioStreamState.Disconnected or AudioStreamState.Error
                    && disconnectedButNotReleased.TrySetResult())
                {
                    resume.Wait(SignalTimeout);
                }
            });
        });

        using var first = await ConnectAndAwaitAnnouncementAsync(port, x, announcements);
        using var second = await ConnectAndAwaitAnnouncementAsync(port, x, announcements);
        using var third = await ConnectAndAwaitAnnouncementAsync(port, x, announcements);

        IAudioStream? holderAfterFirstEnded;
        try
        {
            // Act — B hangs up and is parked before its release; then A ends
            await second.SendAsync(BuildHangupFrame());
            await disconnectedButNotReleased.Task.WaitAsync(SignalTimeout);
            await first.HangUpAsync();
            holderAfterFirstEnded = server.GetStream(x.ToString());
        }
        finally
        {
            resume.Set();
        }

        await second.Disposed.WaitAsync(SignalTimeout);

        // Assert
        holderAfterFirstEnded.Should().BeSameAs(
            third.Stream,
            "B had disconnected when A ended, so the hand-over passes it by for C rather than " +
            "registering a connection that is already over");
        server.GetStream(x.ToString()).Should().BeSameAs(third.Stream, "B's late release leaves C holding X");
        server.ActiveStreamCount.Should().Be(1, "only C is live");
    }

    [Fact]
    public async Task StopAsync_ShouldLeaveNothingRegistered_WhenSameIdConnectionsAreWaiting()
    {
        // A characterization pin, green before the hand-over existed: it holds that a hand-over never
        // outlives a stop. The stop's Clear() empties the table, and after that no entry maps to an
        // ending session any more, so no release can hand X to a connection that is still waiting.
        var port = GetFreePort();
        var server = CreateServer(port);
        await server.StartAsync();
        using var announcements = new Announcements(server);
        var x = Guid.NewGuid();

        using var holder = await ConnectAndAwaitAnnouncementAsync(port, x, announcements);
        using var firstWaiting = await ConnectAndAwaitAnnouncementAsync(port, x, announcements);
        using var secondWaiting = await ConnectAndAwaitAnnouncementAsync(port, x, announcements);

        // Act
        await server.StopAsync().AsTask().WaitAsync(SignalTimeout);
        await Task.WhenAll(holder.Disposed, firstWaiting.Disposed, secondWaiting.Disposed)
            .WaitAsync(SignalTimeout);

        // Assert
        server.GetStream(x.ToString()).Should().BeNull(
            "every ending was processed after the stop began, and none of them registered anything");
        server.ActiveStreamCount.Should().Be(0, "a stopped server counts nothing");
        (await holder.ReadAsync()).Should().Be(0, "the stop closes the holder's connection");
        (await firstWaiting.ReadAsync()).Should().Be(0, "and each waiting connection's");
        (await secondWaiting.ReadAsync()).Should().Be(0, "and each waiting connection's");
    }

    /// <summary>
    /// Connects, sends a UUID frame, and waits for the server to announce this connection. Unlike
    /// <see cref="ConnectAndSendUuidAsync"/>, which returns as soon as the UUID resolves, this works
    /// for a UUID another connection already holds: the lookup resolves before the duplicate is even
    /// read, but the announcement is this connection's own. The caller connects one at a time, so the
    /// next announcement is this one.
    /// </summary>
    private static async Task<AnnouncedConnection> ConnectAndAwaitAnnouncementAsync(
        int port, Guid uuid, Announcements announcements)
    {
        var client = await ConnectAsync(port);
        var stream = client.GetStream();
        await stream.WriteAsync(BuildUuidFrame(uuid));
        await stream.FlushAsync();

        var (announced, disposed) = await announcements.NextAsync();
        announced.ChannelId.Should().Be(uuid.ToString(), "the announcement is this connection's");
        return new AnnouncedConnection(client, announced, disposed);
    }

    /// <summary>
    /// Every stream the server announces, in announcement order, each paired with its disposal
    /// sentinel. The sentinel is subscribed inside the emission: the handler publishes synchronously
    /// and disposes the session only after the emission returns, so it cannot have been missed.
    /// </summary>
    private sealed class Announcements : IDisposable
    {
        private readonly Channel<(IAudioStream Stream, Task Disposed)> _announced =
            Channel.CreateUnbounded<(IAudioStream Stream, Task Disposed)>();

        private readonly IDisposable _subscription;

        public Announcements(AudioSocketServer server) =>
            _subscription = server.OnStreamConnected.Subscribe(
                stream => _announced.Writer.TryWrite((stream, WhenDisposed(stream))));

        public async Task<(IAudioStream Stream, Task Disposed)> NextAsync() =>
            await _announced.Reader.ReadAsync().AsTask().WaitAsync(SignalTimeout);

        public void Dispose() => _subscription.Dispose();
    }

    /// <summary>
    /// A loopback connection the server has announced. <see cref="Disposed"/> completes when the
    /// connection's handler disposes its session, which it does strictly after its own release.
    /// </summary>
    private sealed class AnnouncedConnection(TcpClient client, IAudioStream stream, Task disposed) : IDisposable
    {
        public IAudioStream Stream { get; } = stream;

        public Task Disposed { get; } = disposed;

        public async Task SendAsync(byte[] frame)
        {
            var network = client.GetStream();
            await network.WriteAsync(frame);
            await network.FlushAsync();
        }

        /// <summary>Sends a hangup frame and waits until the handler has released and disposed the session.</summary>
        public async Task HangUpAsync()
        {
            await SendAsync(BuildHangupFrame());
            await Disposed.WaitAsync(SignalTimeout);
        }

        /// <summary>One read from the far end; 0 is the server having closed the connection.</summary>
        public async Task<int> ReadAsync()
        {
            var buffer = new byte[1];
            return await client.GetStream().ReadAsync(buffer).AsTask().WaitAsync(SignalTimeout);
        }

        public void Dispose() => client.Dispose();
    }

    /// <summary>
    /// Completes when the session behind <paramref name="stream"/> is disposed: its state subject
    /// completes only from DisposeAsync. Call it from inside the OnStreamConnected emission, before
    /// the connection's handler can have disposed the session.
    /// </summary>
    private static Task WhenDisposed(IAudioStream stream)
    {
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.StateChanges.Subscribe(static _ => { }, () => disposed.TrySetResult());
        return disposed.Task;
    }

    // ---------------------------------------------------------------------------------- helpers

    /// <summary>
    /// An accept that never returns a connection and ends only when <paramref name="token"/> does, so
    /// a loop parked on it attempts nothing further and puts no wait on any clock, fake or real.
    /// </summary>
    private static async ValueTask<TcpClient> ParkUntilCancelledAsync(CancellationToken token)
    {
        var parked = new TaskCompletionSource<TcpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => parked.TrySetCanceled(token));
        return await parked.Task;
    }

    /// <summary>
    /// An accept that ends as <see cref="SocketError.OperationAborted"/> when <paramref name="token"/>
    /// is cancelled: the shape a Linux stop gives a pending accept, made deterministic.
    /// </summary>
    private static async ValueTask<TcpClient> AbortOnCancellationAsync(CancellationToken token)
    {
        var aborted = new TaskCompletionSource<TcpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(
            () => aborted.TrySetException(new SocketException((int)SocketError.OperationAborted)));
        return await aborted.Task;
    }

    /// <summary>The next timer created on <paramref name="time"/>, as soon as it exists.</summary>
    private static Task<FakeTimeProvider.FakeTimer> NextTimerAsync(FakeTimeProvider time) =>
        time.TimersCreated.ReadAsync().AsTask().WaitAsync(SignalTimeout);

    /// <summary>What the server logged so far, for a failure message.</summary>
    private static string Describe(CapturingLogger logger) =>
        string.Join(", ", logger.Entries.Select(entry => $"{entry.Level}:{entry.EventName}({entry.ExceptionType})"));

    /// <summary>
    /// A client around a socket an accept fixture built, that completes <see cref="Released"/> when its
    /// owner disposes it. For this server that is the handler's <c>using (client)</c>, the last thing
    /// it does, so it comes after anything the handler logs.
    /// </summary>
    private sealed class ReleaseSignallingClient : TcpClient
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ReleaseSignallingClient(Socket socket)
            : base(AddressFamily.InterNetwork)
        {
            var unused = Client;
            Client = socket;
            unused.Dispose();
        }

        public Task Released => _released.Task;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                _released.TrySetResult();
        }
    }

    /// <summary>
    /// Records every task exception that went unobserved while it is subscribed and whose stack names
    /// this server. The subscription is process-wide, so the filter keeps a fault from another test
    /// class running in parallel out of this one's assertion; <see cref="All"/> keeps everything, for
    /// the failure message.
    /// </summary>
    private sealed class UnobservedServerFaults : IDisposable
    {
        private readonly ConcurrentQueue<Exception> _seen = new();

        public UnobservedServerFaults() => TaskScheduler.UnobservedTaskException += OnUnobserved;

        /// <summary>The unobserved exceptions thrown through <see cref="AudioSocketServer"/>.</summary>
        public IReadOnlyList<string> Faults =>
        [
            .. _seen
                .Where(ex => ex.StackTrace?.Contains(nameof(AudioSocketServer), StringComparison.Ordinal) == true)
                .Select(ex => $"{ex.GetType().Name}: {ex.Message}")
        ];

        /// <summary>Every unobserved exception seen, whatever threw it.</summary>
        public IReadOnlyList<string> All => [.. _seen.Select(ex => $"{ex.GetType().Name}: {ex.Message}")];

        /// <summary>
        /// Collects, so a faulted task that nothing references any more is finalised and its
        /// exception, if nothing observed it, is published before this returns. Call it only once the
        /// task in question has completed.
        /// </summary>
        public static void CollectDiscardedTasks()
        {
            for (var pass = 0; pass < 3; pass++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        public void Dispose() => TaskScheduler.UnobservedTaskException -= OnUnobserved;

        private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.InnerExceptions)
                _seen.Enqueue(inner);
        }
    }

    /// <summary>A server log entry, reduced to what these tests assert on.</summary>
    private sealed record LogEntry(LogLevel Level, string? EventName, string? ExceptionType);

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
            _entries.Enqueue(new LogEntry(logLevel, eventId.Name, exception?.GetType().Name));
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
