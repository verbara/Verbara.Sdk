using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.Ari.Audio;
using FluentAssertions;
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

        // Same channel id: this session loses TryAdd and is never registered.
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
        logger.Entries.Should().Contain(
            entry => entry.EventName == "ServerStopped",
            "the stop ran to the end");
        server.IsRunning.Should().BeFalse();
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

    private static WebSocketAudioServer CreateServer(
        int port,
        ILogger<WebSocketAudioServer>? logger = null,
        TimeProvider? timeProvider = null) =>
        new(
            new AudioServerOptions { ListenAddress = "127.0.0.1", WebSocketPort = port },
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
