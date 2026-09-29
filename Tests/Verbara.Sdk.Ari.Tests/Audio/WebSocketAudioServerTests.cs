using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Tests.TestSupport;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.Ari.Tests.Audio;

public class WebSocketAudioServerTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ReadUpgradeRequestAsync_ShouldParseWsKeyAndChannelId()
    {
        const string request = "GET /ws/ch-12345 HTTP/1.1\r\n" +
                               "Host: localhost:9093\r\n" +
                               "Upgrade: websocket\r\n" +
                               "Connection: Upgrade\r\n" +
                               "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                               "Sec-WebSocket-Version: 13\r\n\r\n";

        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(request));
        var (wsKey, channelId) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, CancellationToken.None);

        wsKey.Should().Be("dGhlIHNhbXBsZSBub25jZQ==");
        channelId.Should().Be("ch-12345");
    }

    [Fact]
    public async Task ReadUpgradeRequestAsync_ShouldHandleSimplePath()
    {
        const string request = "GET /my-channel-id HTTP/1.1\r\n" +
                               "Sec-WebSocket-Key: abc123==\r\n\r\n";

        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(request));
        var (wsKey, channelId) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, CancellationToken.None);

        wsKey.Should().Be("abc123==");
        channelId.Should().Be("my-channel-id");
    }

    [Fact]
    public async Task ReadUpgradeRequestAsync_ShouldReturnNull_ForEmptyStream()
    {
        using var stream = new MemoryStream([]);
        var (wsKey, channelId) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, CancellationToken.None);

        wsKey.Should().BeNull();
        channelId.Should().BeNull();
    }

    [Fact]
    public async Task SendUpgradeResponseAsync_ShouldWriteHttp101()
    {
        using var stream = new MemoryStream();

        await WebSocketAudioServer.SendUpgradeResponseAsync(stream, "dGhlIHNhbXBsZSBub25jZQ==", CancellationToken.None);

        stream.Position = 0;
        var response = Encoding.ASCII.GetString(stream.ToArray());
        response.Should().StartWith("HTTP/1.1 101 Switching Protocols");
        response.Should().Contain("Upgrade: websocket");
        response.Should().Contain("Connection: Upgrade");
        response.Should().Contain("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=");
    }

    [Fact]
    public void AudioServerOptions_ShouldHaveCorrectDefaults()
    {
        var options = new AudioServerOptions();

        options.AudioSocketPort.Should().Be(9092);
        options.WebSocketPort.Should().Be(9093);
        options.ListenAddress.Should().Be("0.0.0.0");
        options.MaxConcurrentStreams.Should().Be(1000);
        options.DefaultFormat.Should().Be("slin16");
        options.IdleTimeout.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldRemoveAndDisposeSession_WhenClientCloses()
    {
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
        var disposedSignal = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tracking = server.OnStreamConnected.Subscribe(stream => disposedSignal.TrySetResult(WhenDisposed(stream)));

        using var client = new ClientWebSocket();
        await client.ConnectAsync(ChannelUri(port, "ch-close"), CancellationToken.None);
        var disposed = await disposedSignal.Task.WaitAsync(WaitLimit);
        server.ActiveStreamCount.Should().Be(1);

        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await disposed.WaitAsync(WaitLimit);

        server.ActiveStreamCount.Should().Be(0);
        server.GetStream("ch-close").Should().BeNull();
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldRemoveAndDisposeSession_WhenStreamConnectedSubscriberThrows()
    {
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
        var disposedSignal = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Observers run in subscription order: this one sees the session before the next one throws.
        using var tracking = server.OnStreamConnected.Subscribe(stream => disposedSignal.TrySetResult(WhenDisposed(stream)));
        using var throwing = server.OnStreamConnected.Subscribe(static _ => throw new InvalidOperationException("subscriber failure"));

        using var client = new ClientWebSocket();
        await client.ConnectAsync(ChannelUri(port, "ch-throw"), CancellationToken.None);
        var disposed = await disposedSignal.Task.WaitAsync(WaitLimit);

        // The client stays open, so only the failed connection's own cleanup can dispose the session.
        await disposed.WaitAsync(WaitLimit);

        server.ActiveStreamCount.Should().Be(0);
        server.GetStream("ch-throw").Should().BeNull();
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldKeepFirstSession_WhenDuplicateChannelConnectionCloses()
    {
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
        var firstSignal = new TaskCompletionSource<IAudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDisposedSignal = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tracking = server.OnStreamConnected.Subscribe(stream =>
        {
            if (!firstSignal.TrySetResult(stream))
                secondDisposedSignal.TrySetResult(WhenDisposed(stream));
        });

        using var firstClient = new ClientWebSocket();
        await firstClient.ConnectAsync(ChannelUri(port, "ch-shared"), CancellationToken.None);
        var first = await firstSignal.Task.WaitAsync(WaitLimit);

        // Same channel id: this session waits, unregistered, while the first session is live.
        using var secondClient = new ClientWebSocket();
        await secondClient.ConnectAsync(ChannelUri(port, "ch-shared"), CancellationToken.None);
        var secondDisposed = await secondDisposedSignal.Task.WaitAsync(WaitLimit);
        await secondClient.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await secondDisposed.WaitAsync(WaitLimit);

        server.ActiveStreamCount.Should().Be(1);
        server.GetStream("ch-shared").Should().BeSameAs(first);
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldCloseClient_WhenUpgradeRequestHasNoKey()
    {
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /ws/ch-nokey HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));

        // No Sec-WebSocket-Key: the server answers nothing and closes the connection.
        var read = await stream.ReadAsync(new byte[64]).AsTask().WaitAsync(WaitLimit);

        read.Should().Be(0);
        server.ActiveStreamCount.Should().Be(0);
    }

    [Fact]
    public async Task StopAsync_ShouldDisposeSession_WhenClientStillConnected()
    {
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
        var disposedSignal = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tracking = server.OnStreamConnected.Subscribe(stream => disposedSignal.TrySetResult(WhenDisposed(stream)));

        using var client = new ClientWebSocket();
        await client.ConnectAsync(ChannelUri(port, "ch-stop"), CancellationToken.None);
        var disposed = await disposedSignal.Task.WaitAsync(WaitLimit);

        // The client stays open, so the session's own connection handler disposes it once
        // cancellation reaches the handler. StopAsync has to wait for that handler, so the
        // session is already disposed when StopAsync returns, not merely on its way there.
        await server.StopAsync();

        disposed.IsCompleted.Should().BeTrue("StopAsync returns only after the connected session is disposed");
        server.ActiveStreamCount.Should().Be(0);
        server.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task StopAsync_ShouldReturnWithSessionDisposed_WhenTokenIsCancelledWhileSubscriberBlocks()
    {
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
        var disposedSignal = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriberEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriberLeft = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        // Observers run in subscription order: the first sees the session, the second parks the
        // connection's handler inside OnNext until the test releases it.
        using var tracking = server.OnStreamConnected.Subscribe(stream => disposedSignal.TrySetResult(WhenDisposed(stream)));
        using var blocking = server.OnStreamConnected.Subscribe(_ =>
        {
            try
            {
                subscriberEntered.TrySetResult();
                release.Wait();
            }
            finally
            {
                subscriberLeft.TrySetResult();
            }
        });

        try
        {
            using var client = new ClientWebSocket();
            await client.ConnectAsync(ChannelUri(port, "ch-blocked"), CancellationToken.None);
            await subscriberEntered.Task.WaitAsync(WaitLimit);
            var disposed = await disposedSignal.Task.WaitAsync(WaitLimit);

            using var stopToken = new CancellationTokenSource();
            var stop = server.StopAsync(stopToken.Token).AsTask();
            await stopToken.CancelAsync();
            // Bounded, so a StopAsync that ignores its token fails here instead of hanging the run.
            await stop.WaitAsync(WaitLimit);

            subscriberLeft.Task.IsCompleted.Should().BeFalse("the subscriber is still blocked when StopAsync returns");
            disposed.IsCompleted.Should().BeTrue("StopAsync disposes the registered session once its wait is cancelled");
            server.ActiveStreamCount.Should().Be(0);
            server.IsRunning.Should().BeFalse();
        }
        finally
        {
            release.Set();
            if (subscriberEntered.Task.IsCompleted)
                await subscriberLeft.Task.WaitAsync(WaitLimit);
        }
    }

    // ------------------------------------------------- the running flag: one listener, one teardown

    [Fact]
    public async Task StartAsync_ShouldBindNoSecondListener_WhenTheServerIsAlreadyRunning()
    {
        // Arrange — a server already bound and accepting.
        var port = GetFreePort();
        var logger = new CapturingLogger();
        var server = await StartServerAsync(port, logger);
        var connected = new TaskCompletionSource<IAudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tracking = server.OnStreamConnected.Subscribe(stream => connected.TrySetResult(stream));

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

            using var client = new ClientWebSocket();
            await client.ConnectAsync(ChannelUri(port, "ch-restart"), CancellationToken.None);
            var stream = await connected.Task.WaitAsync(WaitLimit);

            stream.ChannelId.Should().Be(
                "ch-restart",
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
            try
            {
                await server.DisposeAsync().AsTask().WaitAsync(WaitLimit);
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
        await using var server = await StartServerAsync(port, logger);

        // Act — the second stop finds the server already stopped. Without the guard it walks the
        // whole teardown again: Stop() on a listener that is already down, a second CancelAsync,
        // and a second pass over the connection and session tables.
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
        // Arrange — a connection handler parked inside the OnStreamConnected emission. StopAsync
        // waits for every tracked handler, so while this one is parked the stop cannot finish, and
        // the flag can be read from the middle of it. The 101 response has already gone out by the
        // time the emission happens, so the client's connect completes before the park.
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
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
            using var client = new ClientWebSocket();
            await client.ConnectAsync(ChannelUri(port, "ch-stopping"), CancellationToken.None);
            await handlerParked.Task.WaitAsync(WaitLimit);

            // Act — StopAsync runs synchronously as far as its first await, so by the time it
            // hands back its ValueTask the listener is already down: the stop has unambiguously
            // begun. Both reads are taken before anything is released, so neither waits on a clock.
            var stop = server.StopAsync().AsTask();
            stopWasStillRunningWhenRead = !stop.IsCompleted;
            runningWhileStopping = server.IsRunning;

            release.Set();
            await stop.WaitAsync(WaitLimit);
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
        await using var server = CreateServer(GetFreePort(), logger, time);
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

        // Act — the loop reaches its backoff, which is what creates the only timer on this clock
        var backoff = await NextTimerAsync(time);

        // Assert — the failure was reported, and the server still says it is accepting
        backoff.DueTime.Should().Be(
            WebSocketAudioServer.InitialAcceptBackoff,
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
        time.Advance(WebSocketAudioServer.InitialAcceptBackoff);
        await secondAttempt.Task.WaitAsync(WaitLimit);

        // Assert
        Volatile.Read(ref attempts).Should().Be(
            2,
            "the loop keeps accepting after a failure rather than ending with the port bound");
        server.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task AcceptLoopAsync_ShouldDoubleTheBackoffToTheCap_WhenAcceptsKeepFailing()
    {
        // Arrange — every accept fails, so the loop is a pure backoff generator and each wait it asks
        // for is read off the fake clock without any of it being spent.
        var time = new FakeTimeProvider();
        await using var server = CreateServer(GetFreePort(), new CapturingLogger(), time);
        server.AcceptOverride = _ => throw new SocketException((int)SocketError.TooManyOpenSockets);

        await server.StartAsync();

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
        waits[0].Should().Be(WebSocketAudioServer.InitialAcceptBackoff);
        waits[^1].Should().Be(WebSocketAudioServer.MaxAcceptBackoff);
        server.IsRunning.Should().BeTrue("the server is still bound and still accepting");
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
        await using var server = CreateServer(GetFreePort(), new CapturingLogger(), time);
        var attempts = 0;
        server.AcceptOverride = token => Interlocked.Increment(ref attempts) switch
        {
            2 => ValueTask.FromResult(accepted),
            >= 4 => ParkUntilCancelledAsync(token),
            _ => throw new SocketException((int)SocketError.TooManyOpenSockets)
        };

        await server.StartAsync();

        // Act — the first failure's wait, then the success, then the second failure's wait
        var afterFirstFailure = await NextTimerAsync(time);
        time.Advance(afterFirstFailure.DueTime);
        var afterSuccess = await NextTimerAsync(time);

        // Assert
        afterFirstFailure.DueTime.Should().Be(WebSocketAudioServer.InitialAcceptBackoff);
        afterSuccess.DueTime.Should().Be(
            WebSocketAudioServer.InitialAcceptBackoff,
            "a successful accept starts the run over, so the next failure waits the initial " +
            "backoff again rather than the doubled one");
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
        await using var server = CreateServer(GetFreePort(), logger, time);
        var accepting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AcceptOverride = token =>
        {
            accepting.TrySetResult();
            return AbortOnCancellationAsync(token);
        };

        await server.StartAsync();
        await accepting.Task.WaitAsync(WaitLimit);

        // Act — StopAsync awaits the accept loop, so it returns only once the loop has ended
        await server.StopAsync().AsTask().WaitAsync(WaitLimit);

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
        var port = GetFreePort();
        var logger = new CapturingLogger();
        await using var server = await StartServerAsync(port, logger);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tracking = server.OnStreamConnected.Subscribe(_ => connected.TrySetResult());

        using var client = new ClientWebSocket();
        await client.ConnectAsync(ChannelUri(port, "ch-quiet-stop"), CancellationToken.None);
        await connected.Task.WaitAsync(WaitLimit);

        // Act
        await server.StopAsync().AsTask().WaitAsync(WaitLimit);

        // Assert
        logger.Entries.Should().NotContain(
            entry => entry.EventName == "AcceptLoopFailed",
            "a real stop aborting a real pending accept is the stop, not a failure");
        logger.Entries.Should().NotContain(
            entry => entry.EventName == "ConnectionError",
            "the connection the stop cut short ended with the stop, which is not a connection error; " +
            "StopAsync waits for its handler, so anything it logged is already here");
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
        await using var server = CreateServer(PortTheOsPicks, logger, time);
        using var accepted = AcceptedClients.UdpBacked();
        using var client = new ReleaseSignallingClient(accepted.Socket);
        var attempts = 0;
        server.AcceptOverride = token => Interlocked.Increment(ref attempts) == 1
            ? ValueTask.FromResult<TcpClient>(client)
            : AcceptedClients.ParkUntilCancelledAsync(token);
        var backoffRequested = time.TimersCreated.ReadAsync().AsTask();

        await server.StartAsync();

        // Act
        await Task.WhenAny(client.Released, backoffRequested).WaitAsync(WaitLimit);

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

    [Fact]
    public async Task AcceptLoopAsync_ShouldKeepAccepting_WhenConfiguringAnAcceptedClientThrowsObjectDisposed()
    {
        // Arrange — the accept seam hands over a client whose socket is already disposed, so setting
        // TCP_NODELAY on it throws an ObjectDisposedException: the type the loop reads as its own
        // listener's stop. The second accept is the signal. A loop that ends on the first connection
        // never makes it, so in that case only the bound ends the wait.
        var logger = new CapturingLogger();
        await using var server = CreateServer(PortTheOsPicks, logger, new FakeTimeProvider());
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

        // Act
        var nextAccept = () => secondAttempt.Task;
        await nextAccept.Should().CompleteWithinAsync(
            WaitLimit,
            "one connection's closed socket is not the server's stop, so the loop accepts again");
        await client.Released.WaitAsync(WaitLimit);

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

    [Fact]
    public async Task AcceptLoopAsync_ShouldAcceptTheNextConnectionWithoutWaiting_WhenConfiguringOneFails()
    {
        // Arrange — the configure fails with a SocketException, the type the loop backs off for when an
        // accept fails. The clock is fake and never moves, so a backoff would park the loop for good,
        // and the timer it asks for is the signal the test ends on in that case. Otherwise the next
        // accept is.
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();
        await using var server = CreateServer(PortTheOsPicks, logger, time);
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

        // Act
        await Task.WhenAny(secondAttempt.Task, backoffRequested).WaitAsync(WaitLimit);

        // Assert — the loop requests a backoff timer before its next accept, never after it, so at the
        // moment of the next accept, no timer means none was requested
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

    [Fact]
    public async Task HandleConnectionAsync_ShouldReportTheFailure_WhenTheAcceptedClientIsNotConnected()
    {
        // Arrange — the accept seam hands over a TCP client that was never connected. Setting
        // TCP_NODELAY on it succeeds, so the failure lands one step later, when the handler asks the
        // client for its stream (GetStream() throws InvalidOperationException). On this server that
        // call already sits inside the handler's try, so this is a control, green before the configure
        // moved and after: it pins the stream staying inside the region that reports and closes.
        //
        // Ordered by construction: the loop starts the handler inline, on its own continuation, and
        // this failure comes before the handler's first await, so the handler has released the client
        // before the loop asks for its next accept. That next accept is the signal.
        var logger = new CapturingLogger();
        await using var server = CreateServer(PortTheOsPicks, logger, new FakeTimeProvider());
        using var accepted = AcceptedClients.NeverConnected();
        using var client = new ReleaseSignallingClient(accepted.Socket);
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

        // Act
        await secondAttempt.Task.WaitAsync(WaitLimit);

        // Assert
        using (new AssertionScope())
        {
            releasedBeforeNextAccept.Should().BeTrue(
                "the handler owns the connection and closes it when it cannot serve it, before the loop " +
                "accepts again");
            accepted.IsSocketClosed.Should().BeTrue(
                "the handler owns the connection, so it closes it when it cannot serve it");
            logger.Entries.Should().ContainSingle(
                entry => entry.Level == LogLevel.Error
                    && entry.EventName == "ConnectionError"
                    && entry.ExceptionType == nameof(InvalidOperationException),
                "a connection whose stream cannot be obtained is that connection's failure, reported " +
                "once as its error; the server logged [{0}]",
                Describe(logger));
            logger.Entries.Should().NotContain(
                entry => entry.EventName == "AcceptLoopFailed",
                "no accept failed");
            server.IsRunning.Should().BeTrue("one connection failing is not the server failing");
        }
    }

    // ------------------------------- a shared channel id passes to the connection that shared it

    // chan_websocket upgrades every call through one websocket_client connection on the configured
    // URI, so every concurrent call on it presents the same last path segment here: a shared key is
    // the normal case on this server. Every test below connects in order and waits for each
    // connection's own announcement before the next one connects, so which connection is which is
    // fixed by construction. An ending is waited for on that connection's disposal, which its handler
    // performs strictly after its release, so every assertion reads the table after the release and
    // after any hand-over the release made.

    [Fact]
    public async Task HandleConnectionAsync_ShouldHandTheChannelToTheConnectionThatSharedIt_WhenTheFirstCloses()
    {
        // Arrange — the first client holds ch-shared; a second connects to the same path after it
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
        using var announcements = new Announcements(server);

        using var first = await ConnectAndAwaitAnnouncementAsync(port, "ch-shared", announcements);
        using var second = await ConnectAndAwaitAnnouncementAsync(port, "ch-shared", announcements);

        // Act — the order a real call takes: the connection that arrived first ends first
        await first.CloseAsync();

        // Assert
        server.GetStream("ch-shared").Should().BeSameAs(
            second.Stream,
            "the connection that presented ch-shared after the first is still open, so the first " +
            "one's ending hands the id to it instead of leaving a live call that nothing can find");
        server.ActiveStreamCount.Should().Be(1, "the connection that took the id over is counted");
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldKeepALiveSessionFindable_WhenSeveralConnectionsShareOnePath()
    {
        // Arrange — the chan_websocket shape: three calls through one websocket_client connection
        // upgrade on the same URI, in the order first, second, third
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
        using var announcements = new Announcements(server);

        using var first = await ConnectAndAwaitAnnouncementAsync(port, "ch-client", announcements);
        using var second = await ConnectAndAwaitAnnouncementAsync(port, "ch-client", announcements);
        using var third = await ConnectAndAwaitAnnouncementAsync(port, "ch-client", announcements);

        // Act and assert — the holder closes: the earliest connection still open takes the id over
        await first.CloseAsync();
        server.GetStream("ch-client").Should().BeSameAs(
            second.Stream,
            "the hand-over follows arrival order, so the second connection takes the id over");

        // A waiting connection closes: it holds nothing, so the new holder keeps the id
        await third.CloseAsync();
        server.GetStream("ch-client").Should().BeSameAs(
            second.Stream,
            "a waiting connection that ends holds nothing, so the live holder keeps the id");

        // The last one closes, with nobody left waiting
        await second.CloseAsync();
        server.GetStream("ch-client").Should().BeNull(
            "the last connection that presented the id has ended, and an ended waiter is never " +
            "handed the id");
        server.ActiveStreamCount.Should().Be(0, "nothing is live");
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldSkipAWaitingConnectionThatHasClosed_WhenTheFirstClosesBeforeItsRelease()
    {
        // Arrange — the first client holds ch-skip; a second and then a third connect to the same
        // path. The test above lets the second one's own release run before the first ends, and that
        // release takes it out of the waiting list, so the hand-over never meets it. This one holds
        // the second connection between its close and its release. A session notifies its state
        // observers in subscription order, and the one below subscribes inside the second
        // connection's announcement, before its handler subscribes the observer that ends its wait.
        // Parking inside the second session's Disconnected notification therefore holds its release
        // back, by construction, while the first one ends.
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
        using var announcements = new Announcements(server);

        var closedButNotReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var announced = 0;
        using var parking = server.OnStreamConnected.Subscribe(stream =>
        {
            if (Interlocked.Increment(ref announced) != 2)
                return;

            stream.StateChanges.Subscribe(state =>
            {
                if (state is AudioStreamState.Disconnected or AudioStreamState.Error
                    && closedButNotReleased.TrySetResult())
                {
                    resume.Wait(WaitLimit);
                }
            });
        });

        using var first = await ConnectAndAwaitAnnouncementAsync(port, "ch-skip", announcements);
        using var second = await ConnectAndAwaitAnnouncementAsync(port, "ch-skip", announcements);
        using var third = await ConnectAndAwaitAnnouncementAsync(port, "ch-skip", announcements);

        IAudioStream? holderAfterFirstClosed;
        try
        {
            // Act — the second client closes and is parked before its release; then the first closes
            await second.SendCloseAsync();
            await closedButNotReleased.Task.WaitAsync(WaitLimit);
            await first.CloseAsync();
            holderAfterFirstClosed = server.GetStream("ch-skip");
        }
        finally
        {
            resume.Set();
        }

        await second.Disposed.WaitAsync(WaitLimit);

        // Assert
        holderAfterFirstClosed.Should().BeSameAs(
            third.Stream,
            "the second connection had closed when the first ended, so the hand-over passes it by " +
            "for the third rather than registering a connection that is already over");
        server.GetStream("ch-skip").Should().BeSameAs(
            third.Stream,
            "the second connection's late release leaves the third holding the id");
        server.ActiveStreamCount.Should().Be(1, "only the third connection is live");
    }

    // ------------------------------------ every live connection is counted and admitted, whatever key it shares

    [Fact]
    public async Task ActiveStreamCount_ShouldCountEveryLiveConnection_WhenConnectionsShareOnePath()
    {
        // Arrange — the chan_websocket shape: three live calls upgrade on one URI
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
        using var announcements = new Announcements(server);

        using var first = await ConnectAndAwaitAnnouncementAsync(port, "ch-count", announcements);
        using var second = await ConnectAndAwaitAnnouncementAsync(port, "ch-count", announcements);
        using var third = await ConnectAndAwaitAnnouncementAsync(port, "ch-count", announcements);

        // Assert — three live audio streams, whichever one the key resolves to
        using (new AssertionScope())
        {
            server.ActiveStreamCount.Should().Be(
                3,
                "each live connection is an active audio stream, and Asterisk sends every call of one " +
                "websocket_client connection on the same URI");
            server.ActiveStreams.Should().HaveCount(3)
                .And.Contain(first.Stream)
                .And.Contain(second.Stream)
                .And.Contain(third.Stream);
            server.GetStream("ch-count").Should().BeSameAs(
                first.Stream, "the key still resolves to the earliest live connection that presented it");
        }

        // Act — a connection that waits for the key ends
        await third.CloseAsync();

        // Assert
        server.ActiveStreamCount.Should().Be(2, "the two connections still open are counted");
    }

    [Fact]
    public async Task AcceptLoop_ShouldCloseTheConnectionOverTheLimit_WhenLiveConnectionsSharingOnePathFillIt()
    {
        // Arrange — two live calls on one URI, under a limit of two
        var port = GetFreePort();
        await using var server = CreateServer(port, maxStreams: 2);
        await server.StartAsync();
        using var announcements = new Announcements(server);

        using var first = await ConnectAndAwaitAnnouncementAsync(port, "ch-limit", announcements);
        using var second = await ConnectAndAwaitAnnouncementAsync(port, "ch-limit", announcements);

        // Act — a third call on the same URI
        using var third = new ClientWebSocket();
        var connect = async () =>
            await third.ConnectAsync(ChannelUri(port, "ch-limit"), CancellationToken.None).WaitAsync(WaitLimit);

        // Assert
        await connect.Should().ThrowAsync<WebSocketException>(
            "two live connections fill a limit of two whatever key they share, so the third is closed " +
            "before its upgrade is read");
        server.ActiveStreamCount.Should().Be(2);
    }

    [Fact]
    public async Task AcceptLoop_ShouldCloseTheConnectionOverTheLimit_WhenAnEarlierConnectionHasNotSentItsUpgrade()
    {
        // Arrange — a connection the server has accepted and that has not sent its upgrade request.
        // Its handshake completed before the next connection's began, and the loop accepts in that
        // order, so it has taken its place by the time the next one is admitted or refused.
        var port = GetFreePort();
        await using var server = CreateServer(port, maxStreams: 1);
        await server.StartAsync();
        using var silent = new TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, port);

        // Act
        using var late = new ClientWebSocket();
        var connect = async () =>
            await late.ConnectAsync(ChannelUri(port, "ch-late"), CancellationToken.None).WaitAsync(WaitLimit);

        // Assert
        await connect.Should().ThrowAsync<WebSocketException>(
            "the silent connection holds the only place from its accept; a limit that counts only " +
            "registered sessions lets every accept that lands before a registration past it");
    }

    [Fact]
    public async Task AcceptLoop_ShouldGiveThePlaceBack_WhenAConnectionSendsNoUpgradeWithinTheIdleTimeout()
    {
        // Arrange — a limit of one, held from its accept by a connection that never sends its upgrade.
        // The server runs on a fake clock with the default IdleTimeout, and the wait for the upgrade is
        // bounded on that clock: its timer is read as it is created, and the clock is moved past it.
        var time = new FakeTimeProvider();
        var port = GetFreePort();
        await using var server = CreateServer(port, timeProvider: time, maxStreams: 1);
        await server.StartAsync();
        using var announcements = new Announcements(server);
        using var silent = new TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, port);

        var upgradeBound = await time.TimersCreated.ReadAsync().AsTask().WaitAsync(WaitLimit);
        upgradeBound.DueTime.Should().Be(
            new AudioServerOptions().IdleTimeout, "the wait for the upgrade is bounded by IdleTimeout");

        // Act — the server's clock passes the idle timeout
        time.Advance(upgradeBound.DueTime);

        // Assert — the silent connection is closed, and its place serves the next call
        (await silent.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(WaitLimit)).Should().Be(
            0, "a connection that sent no upgrade within IdleTimeout is closed");
        using var call = await ConnectUntilAdmittedAsync(port, "ch-after-idle", announcements);
        server.GetStream("ch-after-idle").Should().BeSameAs(
            call.Stream, "a place held from the accept is given back when that connection is closed");
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldKeepDeliveringAudio_WhenAsteriskSendsItsJsonMediaStart()
    {
        // Arrange — chan_websocket's JSON control format (Dial option f(json)): the first frame is
        // MEDIA_START, named by `event`, as captured from Asterisk 22.9.0 and 23.4.1. The connection keeps
        // its path key; nothing here depends on the key.
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
        using var announcements = new Announcements(server);
        using var client = new ClientWebSocket();
        await client.ConnectAsync(ChannelUri(port, "ch-json"), CancellationToken.None);
        var (stream, _) = await announcements.NextAsync();

        // The SDK's control-message model does not read Asterisk's `event` field yet, so this frame
        // must be dropped: nothing is published for it. That the audio survives is what is claimed
        // here, not that control messages are understood.
        var published = 0;
        using var controlMessages = ((IChanWebSocketSession)stream).ControlMessages.Subscribe(
            _ => Interlocked.Increment(ref published));

        await client.SendAsync(
            Encoding.UTF8.GetBytes(
                "{\"event\":\"MEDIA_START\",\"connection_id\":\"c1\",\"channel\":\"WebSocket/c1/0x7f0638004770\"," +
                "\"channel_id\":\"1790617675.20\",\"format\":\"slin16\",\"optimal_frame_size\":640,\"ptime\":20}"),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
        await client.SendAsync(new byte[640], WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

        // Act
        var frame = await stream.ReadFrameAsync().AsTask().WaitAsync(WaitLimit);

        // Assert
        using (new AssertionScope())
        {
            frame.Length.Should().Be(
                640, "a control message the SDK does not model must not end the call's audio stream");
            Volatile.Read(ref published).Should().Be(
                0, "a text frame the SDK cannot read is dropped, not published as a control message");
        }
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldKeepDeliveringAudio_WhenAsteriskSendsItsPlainTextMediaStart()
    {
        // Arrange — chan_websocket's default control format: the first frame is a plain-text
        // MEDIA_START, which is not JSON at all
        var port = GetFreePort();
        await using var server = await StartServerAsync(port);
        using var announcements = new Announcements(server);
        using var client = new ClientWebSocket();
        await client.ConnectAsync(ChannelUri(port, "ch-text"), CancellationToken.None);
        var (stream, _) = await announcements.NextAsync();
        var published = 0;
        using var controlMessages = ((IChanWebSocketSession)stream).ControlMessages.Subscribe(
            _ => Interlocked.Increment(ref published));

        await client.SendAsync(
            Encoding.UTF8.GetBytes(
                "MEDIA_START connection_id:c1 channel:WebSocket/c1/0x7f0638004770 channel_id:1790617675.20 " +
                "format:slin16 optimal_frame_size:640 ptime:20"),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
        await client.SendAsync(new byte[640], WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

        // Act
        var frame = await stream.ReadFrameAsync().AsTask().WaitAsync(WaitLimit);

        // Assert
        using (new AssertionScope())
        {
            frame.Length.Should().Be(640, "the plain-text MEDIA_START is dropped and the audio keeps flowing");
            Volatile.Read(ref published).Should().Be(0, "no control message is published for it");
        }
    }

    // ------------------------------------ every place taken at the accept comes back when its connection ends

    [Fact]
    public async Task AcceptLoop_ShouldGiveThePlaceBack_WhenItsUpgradeIsInvalid()
    {
        // Arrange — a limit of one; the first connection's upgrade has no Sec-WebSocket-Key
        var port = GetFreePort();
        await using var server = CreateServer(port, maxStreams: 1);
        await server.StartAsync();
        using var announcements = new Announcements(server);
        using (var invalid = new TcpClient())
        {
            await invalid.ConnectAsync(IPAddress.Loopback, port);
            var network = invalid.GetStream();
            await network.WriteAsync(Encoding.ASCII.GetBytes("GET /ws/ch-invalid HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
            (await network.ReadAsync(new byte[64]).AsTask().WaitAsync(WaitLimit)).Should().Be(
                0, "an invalid upgrade is closed unanswered");
        }

        // Act — the next call, admitted once the invalid connection's place is back
        using var call = await ConnectUntilAdmittedAsync(port, "ch-after-invalid", announcements);

        // Assert
        server.GetStream("ch-after-invalid").Should().BeSameAs(
            call.Stream, "a connection closed for an invalid upgrade gives back the place it took at its accept");
    }

    [Fact]
    public async Task AcceptLoop_ShouldAdmitAFullSecondWave_WhenEveryConnectionOfTheFirstHasEnded()
    {
        // Arrange — a limit of two, filled by two calls on distinct paths that then close
        var port = GetFreePort();
        await using var server = CreateServer(port, maxStreams: 2);
        await server.StartAsync();
        using var announcements = new Announcements(server);

        using (var first = await ConnectAndAwaitAnnouncementAsync(port, "ch-wave-1", announcements))
        using (var second = await ConnectAndAwaitAnnouncementAsync(port, "ch-wave-2", announcements))
        {
            await first.CloseAsync();
            await second.CloseAsync();
        }

        // Act — a second wave of two, then one more
        using var third = await ConnectUntilAdmittedAsync(port, "ch-wave-3", announcements);
        using var fourth = await ConnectUntilAdmittedAsync(port, "ch-wave-4", announcements);
        using var over = new ClientWebSocket();
        var connect = async () =>
            await over.ConnectAsync(ChannelUri(port, "ch-wave-5"), CancellationToken.None).WaitAsync(WaitLimit);

        // Assert
        await connect.Should().ThrowAsync<WebSocketException>(
            "every place of the first wave came back, and the second wave fills the limit again");
        using (new AssertionScope())
        {
            server.GetStream("ch-wave-3").Should().BeSameAs(third.Stream);
            server.GetStream("ch-wave-4").Should().BeSameAs(fourth.Stream);
            server.ActiveStreamCount.Should().Be(2);
        }
    }

    /// <summary>
    /// Connects to <paramref name="channelId"/>'s path until the server admits and announces the
    /// connection. A connection handler closes its socket before it gives its place back, so a
    /// connection made right after another one's end of stream can still find the limit full and be
    /// closed before its upgrade is read; this connects again at once when that happens. One bound
    /// covers every attempt.
    /// </summary>
    private static async Task<AnnouncedClient> ConnectUntilAdmittedAsync(
        int port, string channelId, Announcements announcements)
    {
        using var giveUp = new CancellationTokenSource();
        try
        {
            return await AttemptAsync(giveUp.Token).WaitAsync(WaitLimit);
        }
        finally
        {
            await giveUp.CancelAsync();
        }

        async Task<AnnouncedClient> AttemptAsync(CancellationToken token)
        {
            var announced = announcements.ReadAsync(token);
            while (true)
            {
                var client = new ClientWebSocket();
                try
                {
                    await client.ConnectAsync(ChannelUri(port, channelId), token);
                }
                catch (WebSocketException)
                {
                    // Closed before the upgrade was answered: refused at the accept. Connect again.
                    client.Dispose();
                    continue;
                }

                var (stream, disposed) = await announced;
                stream.ChannelId.Should().Be(channelId, "the announcement is this connection's");
                return new AnnouncedClient(client, stream, disposed);
            }
        }
    }

    /// <summary>
    /// Connects to <paramref name="channelId"/>'s path and waits for the server to announce this
    /// connection. The client's connect completes on the 101 response, which the server sends before
    /// it builds and announces the session, so the announcement is the sentinel that the connection
    /// has arrived. The caller connects one at a time, so the next announcement is this one.
    /// </summary>
    private static async Task<AnnouncedClient> ConnectAndAwaitAnnouncementAsync(
        int port, string channelId, Announcements announcements)
    {
        var client = new ClientWebSocket();
        await client.ConnectAsync(ChannelUri(port, channelId), CancellationToken.None);

        var (announced, disposed) = await announcements.NextAsync();
        announced.ChannelId.Should().Be(channelId, "the announcement is this connection's");
        return new AnnouncedClient(client, announced, disposed);
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

        public Announcements(WebSocketAudioServer server) =>
            _subscription = server.OnStreamConnected.Subscribe(
                stream => _announced.Writer.TryWrite((stream, WhenDisposed(stream))));

        public async Task<(IAudioStream Stream, Task Disposed)> NextAsync() =>
            await _announced.Reader.ReadAsync().AsTask().WaitAsync(WaitLimit);

        /// <summary>The next announcement, bounded only by <paramref name="token"/>; for a caller that holds its own bound.</summary>
        public Task<(IAudioStream Stream, Task Disposed)> ReadAsync(CancellationToken token) =>
            _announced.Reader.ReadAsync(token).AsTask();

        public void Dispose() => _subscription.Dispose();
    }

    /// <summary>
    /// A client the server has announced. <see cref="Disposed"/> completes when the connection's
    /// handler disposes its session, which it does strictly after its own release.
    /// </summary>
    private sealed class AnnouncedClient(ClientWebSocket client, IAudioStream stream, Task disposed) : IDisposable
    {
        public IAudioStream Stream { get; } = stream;

        public Task Disposed { get; } = disposed;

        /// <summary>Sends the close frame, without waiting for anything the server does next.</summary>
        public Task SendCloseAsync() =>
            client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

        /// <summary>Closes and waits until the handler has released and disposed the session.</summary>
        public async Task CloseAsync()
        {
            await SendCloseAsync();
            await Disposed.WaitAsync(WaitLimit);
        }

        public void Dispose() => client.Dispose();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task PublicConstructor_ShouldBindAndAccept_WhenGivenOnlyOptionsAndALogger()
    {
        // The two-argument constructor is the one a consumer resolves from DI; every other test here
        // reaches past it for the internal overload that takes a clock. A TCP connection the server
        // accepts is the assertion: it proves the delegated construction bound a listener and put a
        // loop on it, which is what the delegation has to produce.
        var port = GetFreePort();
        await using var server = new WebSocketAudioServer(
            new AudioServerOptions { ListenAddress = "127.0.0.1", WebSocketPort = port },
            NullLogger<WebSocketAudioServer>.Instance);

        await server.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);

        client.Connected.Should().BeTrue("the delegated construction produced a bound listener");
        server.IsRunning.Should().BeTrue("and a running server");
    }

    /// <summary>
    /// Port 0, so the OS picks the port when the server binds. It is for a server the test never
    /// dials, because its accepts come from <c>AcceptOverride</c>. A port probed with
    /// <see cref="GetFreePort"/> is released before the server binds it, and any process on the
    /// machine can take it in between; the start then fails with "Address already in use".
    /// </summary>
    private const int PortTheOsPicks = 0;

    private static WebSocketAudioServer CreateServer(
        int port,
        ILogger<WebSocketAudioServer>? logger = null,
        TimeProvider? timeProvider = null,
        int maxStreams = 1000,
        TimeSpan? idleTimeout = null) =>
        new(
            new AudioServerOptions
            {
                ListenAddress = "127.0.0.1",
                WebSocketPort = port,
                MaxConcurrentStreams = maxStreams,
                IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(60),
            },
            logger ?? NullLogger<WebSocketAudioServer>.Instance,
            timeProvider ?? TimeProvider.System);

    private static async Task<WebSocketAudioServer> StartServerAsync(
        int port, ILogger<WebSocketAudioServer>? logger = null)
    {
        var server = CreateServer(port, logger);
        await server.StartAsync();
        return server;
    }

    private static Uri ChannelUri(int port, string channelId) => new($"ws://127.0.0.1:{port}/ws/{channelId}");

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
        time.TimersCreated.ReadAsync().AsTask().WaitAsync(WaitLimit);

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

    /// <summary>A server log entry, reduced to what these tests assert on.</summary>
    private sealed record LogEntry(LogLevel Level, string? EventName, string? ExceptionType);

    /// <summary>Records every entry the server logs.</summary>
    private sealed class CapturingLogger : ILogger<WebSocketAudioServer>
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
}
