using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Verbara.Sdk.Ari.Audio;
using FluentAssertions;
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

    private static byte[] BuildFrame(AudioFrameType type, byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        frame[0] = (byte)type;
        frame[1] = (byte)(payload.Length >> 16);
        frame[2] = (byte)(payload.Length >> 8);
        frame[3] = (byte)(payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    private static byte[] BuildUuidFrame(Guid uuid) =>
        BuildFrame(AudioFrameType.Uuid, uuid.ToByteArray());

    private static byte[] BuildHangupFrame() =>
        BuildFrame(AudioFrameType.Hangup, []);

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
