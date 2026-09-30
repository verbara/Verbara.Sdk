using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.VoiceAi.AudioSocket.Internal;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Tests;

[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class AudioSocketServerEdgeCaseTests : IAsyncLifetime
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The event ids the logging source generator gives <c>NoUuidFrame</c> and
    /// <c>HandleConnectionError</c>. A consumer's log filter can key on them, so they are asserted
    /// along with the names.
    /// </summary>
    private const int NoUuidFrameEventId = 1608362970;

    private const int HandleConnectionErrorEventId = 663784587;

    private readonly AudioSocketServer _server;

    public AudioSocketServerEdgeCaseTests()
    {
        var options = new AudioSocketOptions
        {
            Port = 0,
            ConnectionTimeout = TimeSpan.FromSeconds(1),
            MaxConcurrentSessions = 2
        };
        _server = new AudioSocketServer(options, NullLogger<AudioSocketServer>.Instance);
    }

    [Fact]
    public async Task Server_ShouldRejectConnection_WhenNoUuidSentWithinTimeout()
    {
        await _server.StartAsync(CancellationToken.None);

        // Connect a raw TCP client that does NOT send a UUID frame
        using var rawClient = new TcpClient();
        await rawClient.ConnectAsync("127.0.0.1", _server.BoundPort);

        // Wait longer than the timeout
        await Task.Delay(1500);

        // The server should have closed the connection
        _server.ActiveSessionCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldLogOnlyNoUuidFrameWarning_WhenNoUuidArrivesWithinTimeout()
    {
        // Arrange — a real loopback connection whose server end goes straight to the handler, as the
        // accept loop hands it over. The UUID timeout runs on a fake clock, so it fires when the test
        // moves the clock and at no other moment; nothing here waits on a real one.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var rawClient = new TcpClient();
        await rawClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();

        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();
        var connectionTimeout = TimeSpan.FromSeconds(30);
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0, ConnectionTimeout = connectionTimeout },
            logger,
            time);

        var handling = server.HandleConnectionAsync(accepted, CancellationToken.None);
        var uuidTimeout = await NextTimerAsync(time);
        uuidTimeout.DueTime.Should().Be(connectionTimeout, "the wait for the UUID frame is bounded by ConnectionTimeout");
        handling.IsCompleted.Should().BeFalse("with nothing sent and the clock not moved, the handler is still waiting for the UUID frame");

        // Act — the client has sent nothing; move the clock to the deadline
        time.Advance(connectionTimeout);
        await handling.WaitAsync(SignalTimeout);

        // Assert — the handler has returned, so these are all the entries it logged for the connection
        logger.Entries.Should().Equal(
            [new LogEntry(LogLevel.Warning, NoUuidFrameEventId, "NoUuidFrame", null)],
            "a client that misses the UUID deadline gets the designed warning and nothing else, no connection error");
        (await ReadFromServerAsync(rawClient)).Should().Be(0, "the server closes the connection it gave up on");
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldCloseWithoutWarningOrError_WhenServerStopsBeforeUuidArrives()
    {
        // Arrange — a real loopback connection whose server end goes straight to the handler, with the
        // test's own token standing in for the one StartAsync hands every connection. Called directly,
        // the handler returns at its first await, the UUID read, so it is known to be waiting there
        // before the token is cancelled; through the accept loop that would be a race. The UUID timeout
        // runs on a fake clock that never moves, so only the token can end the wait.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var rawClient = new TcpClient();
        await rawClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();

        var logger = new CapturingLogger();
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0, ConnectionTimeout = TimeSpan.FromSeconds(30) },
            logger,
            new FakeTimeProvider());
        using var serverStopping = new CancellationTokenSource();

        var handling = server.HandleConnectionAsync(accepted, serverStopping.Token);
        handling.IsCompleted.Should().BeFalse("with nothing sent, the handler is still waiting for the UUID frame");

        // Act
        await serverStopping.CancelAsync();
        await handling.WaitAsync(SignalTimeout);

        // Assert
        logger.Entries.Should().NotContain(
            entry => entry.Level >= LogLevel.Warning,
            "a stopping server that closes a connection still waiting for its UUID has hit neither the timeout nor an error");
        (await ReadFromServerAsync(rawClient)).Should().Be(0, "the server still closes the connection");
    }

    [Fact]
    public async Task AcceptLoopAsync_ShouldCloseTheAcceptedConnection_WhenTheServerStopsBeforeTheHandoffRuns()
    {
        // Arrange — a real loopback pair whose server end is what the accept returns. The override
        // cancels the loop's own token and then returns that connection in the same call, so the
        // hand-off that follows always sees a cancelled token: the ordering is built, not timed. The
        // UUID timeout runs on a fake clock the test never advances, and is set far above any wait the
        // loop could ask for, so nothing here can end on a clock — the only thing that can close the
        // connection is the server closing it.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();

        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0, ConnectionTimeout = TimeSpan.FromHours(1) },
            logger,
            time);
        using var serverStopping = new CancellationTokenSource();
        var attempts = 0;
        server.AcceptOverride = _ =>
        {
            Interlocked.Increment(ref attempts);
            serverStopping.Cancel();
            return ValueTask.FromResult(accepted);
        };

        // Act — the loop runs with the test's own token, which is the one StartAsync hands it
        var loop = Task.Run(() => server.AcceptLoopAsync(serverStopping.Token));
        var loopFault = await Record.ExceptionAsync(() => loop.WaitAsync(SignalTimeout));

        // Assert
        (await ReadFromServerAsync(peer)).Should().Be(
            0,
            "a connection the server accepted is closed even when the stop landed before the hand-off ran");
        Volatile.Read(ref attempts).Should().Be(1, "the loop ends on the cancelled token rather than accepting again");
        loopFault.Should().BeNull("a cancelled token ends the loop; it neither hangs nor escapes as an exception");
        loop.Status.Should().Be(TaskStatus.RanToCompletion);
        logger.Entries.Should().NotContain(
            entry => entry.Level >= LogLevel.Warning,
            "a connection closed because the server was already stopping missed no deadline and failed in no way");
    }

    [Fact]
    public async Task AcceptLoopAsync_ShouldLeaveTheConnectionOpen_WhileTheHandlerIsServingIt()
    {
        // Arrange — the same harness with the loop's token live. The override returns the connection
        // on its first call and afterwards parks on an accept that ends only when that token does, so
        // no second connection and no backoff wait can appear: the handler's UUID timeout is the only
        // timer the fake clock ever sees, and its due time is far above every backoff wait anyway.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();

        var time = new FakeTimeProvider();
        var connectionTimeout = TimeSpan.FromHours(1);
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0, ConnectionTimeout = connectionTimeout },
            new CapturingLogger(),
            time);
        using var serverStopping = new CancellationTokenSource();
        var attempts = 0;
        server.AcceptOverride = token => Interlocked.Increment(ref attempts) == 1
            ? ValueTask.FromResult(accepted)
            : ParkUntilCancelledAsync(token);

        // The read is outstanding from before the loop starts, so the check below covers the whole
        // hand-off rather than an instant of it. The server never writes, so only its close ends it.
        var peerRead = ReadFromServerAsync(peer);

        // Act — the handler taking the connection over is what creates the UUID timeout
        var loop = Task.Run(() => server.AcceptLoopAsync(serverStopping.Token));
        var uuidTimeout = await NextTimerAsync(time);

        // Assert — the connection is being served, so it is still open
        uuidTimeout.DueTime.Should().Be(connectionTimeout, "the handler bounds its wait for the UUID frame by ConnectionTimeout");
        peerRead.IsCompleted.Should().BeFalse("a connection the handler is serving is not closed under it");

        // Act — the stop is the only thing that ends the wait
        await serverStopping.CancelAsync();

        // Assert
        (await peerRead).Should().Be(0, "the handler closes the connection once the server stops");
        var loopFault = await Record.ExceptionAsync(() => loop.WaitAsync(SignalTimeout));
        loopFault.Should().BeNull("a cancelled accept ends the loop; it neither hangs nor escapes as an exception");
    }

    [Fact]
    public async Task Server_ShouldLogHandleConnectionError_WhenOnSessionStartedThrowsOperationCanceledException()
    {
        // Arrange — a subscriber runs only once the UUID frame has arrived, so an
        // OperationCanceledException it raises is the consumer's own failure, not the end of the wait
        // for the UUID. The UUID timeout runs on a fake clock that never moves, so it cannot fire.
        var logger = new CapturingLogger();
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0 },
            logger,
            new FakeTimeProvider());
        server.OnSessionStarted += _ => ValueTask.FromException(new OperationCanceledException());
        await server.StartAsync(CancellationToken.None);

        // Act
        await using var client = new AudioSocketClient("127.0.0.1", server.BoundPort, Guid.NewGuid());
        await client.ConnectAsync();
        var firstWarningOrAbove = await logger.FirstWarningOrAbove.WaitAsync(SignalTimeout);
        var audioBytes = await AudioBytesUntilServerClosesAsync(client).WaitAsync(SignalTimeout);

        // Assert — the handler closes the connection after it logs, so once the client has seen the close,
        // every entry the handler logs for the connection is in. The session's read loop can still add a
        // warning of its own as the connection goes, so the full list is not pinned.
        firstWarningOrAbove.Should().Be(
            new LogEntry(LogLevel.Error, HandleConnectionErrorEventId, "HandleConnectionError", nameof(OperationCanceledException)),
            "a cancellation raised by a subscriber after the UUID arrived is a connection error, as it always was");
        logger.Entries.Should().NotContain(
            entry => entry.EventName == "NoUuidFrame",
            "the client sent its UUID frame, so it missed no deadline");
        logger.Entries.Where(entry => entry.Level >= LogLevel.Error).Should().ContainSingle(
            "the subscriber's failure is logged once");
        audioBytes.Should().Be(0, "the server sends no audio and closes the connection");
    }

    [Fact]
    public async Task ReleaseSession_ShouldKeepTheHolderRegistered_WhenTheReleasedSessionDoesNotHoldItsId()
    {
        // Arrange — the holder registers under X through the handler, the way the accept loop hands a
        // connection over, and is announced only after its registration.
        var x = Guid.NewGuid();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var server = NewServerOnStoppedClock();
        using var holderPeer = await ConnectAndIdentifyAsync(listener, x);
        var holder = await HandOverAndAwaitAnnouncementAsync(server, listener);

        // A second session that presented X but holds no entry for it: the shape of a same-id
        // connection that did not win the registration, or of a holder whose entry a stop cleared
        // before its hangup ran. It is built through the internal constructor on a loopback pair of its
        // own, so it never went near the registry.
        using var otherPeer = new TcpClient();
        await otherPeer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var otherEnd = await listener.AcceptTcpClientAsync();
        await using var other = new AudioSocketSession(
            x,
            otherEnd,
            PipeReader.Create(otherEnd.GetStream()),
            new AudioSocketOptions().DefaultFormat,
            NullLogger.Instance);

        // Act — the release that session's hangup performs
        server.ReleaseSession(other);

        // Assert
        server.ActiveSessionCount.Should().Be(
            1,
            "a release removes only the entry its own session holds, and the released session never held X — removing by key alone would unregister the other call's live session");
        holder.IsConnected.Should().BeTrue("nothing has ended the holder's connection");
        await server.StopAsync(CancellationToken.None);
        (await ReadFromServerAsync(holderPeer)).Should().Be(
            0,
            "the holder is still registered, so the stop finds it and closes its connection");
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldKeepTheHolderRegistered_WhenASameIdConnectionIsRefused()
    {
        // A characterization pin since the registry change. It pins what a refusal of a same-id
        // connection leaves behind: the connection waits for the holder, and is refused with a hangup
        // frame only once the grace has run out with the holder still live. Its session is disposed
        // before the read loop starts, so its hangup never fires and it releases nothing, which is the
        // invariant the by-value release relies on: the live holder keeps its entry.
        var x = Guid.NewGuid();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var time = new FakeTimeProvider();
        await using var server = NewServer(time, new CapturingLogger());
        var announcements = 0;
        server.OnSessionStarted += _ =>
        {
            Interlocked.Increment(ref announcements);
            return ValueTask.CompletedTask;
        };
        using var holderPeer = await ConnectAndIdentifyAsync(listener, x);
        var holder = await HandOverAndAwaitAnnouncementAsync(server, listener);
        await NextTimerAsync(time); // the holder's UUID deadline

        // Act — a second connection presents X through the same handler, which parks on the holder;
        // the grace then runs out with the holder still live
        using var refusedPeer = await ConnectAndIdentifyAsync(listener, x);
        using var refusedEnd = await listener.AcceptTcpClientAsync();
        var refusing = server.HandleConnectionAsync(refusedEnd, CancellationToken.None);
        await NextTimerAsync(time); // the second connection's UUID deadline
        var grace = await NextTimerAsync(time);
        time.Advance(grace.DueTime);

        // Assert — the server closes the refused connection only after it gave up on the registration,
        // so that read ending orders every check below after the refusal
        (await ReadUntilServerClosesAsync(refusedPeer)).Should().Equal(
            HangupFrame,
            "the server refuses a connection whose id stays held, with a hangup frame so the call goes on in the dialplan");
        await refusing.WaitAsync(SignalTimeout);
        server.ActiveSessionCount.Should().Be(
            1,
            "the refused connection never held X, so the live holder keeps its entry");
        Volatile.Read(ref announcements).Should().Be(1, "a refused connection is never announced");
        holder.IsConnected.Should().BeTrue("the refusal leaves the holder's connection alone");
        await server.StopAsync(CancellationToken.None);
        (await ReadFromServerAsync(holderPeer)).Should().Be(
            0,
            "the holder is still registered, so the stop finds it and closes its connection");
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldServeAConnectionThatPresentsAHeldId_WhenTheHolderHangsUpWithinTheGrace()
    {
        // Arrange — the order a re-entered AudioSocket(), a redirect or a transfer produces: Asterisk
        // connects again with the id it saved 0.1–7 ms after the previous connection ends, before this
        // server has released it. Refusing it at once lost 1–2.3 % of re-entries at rest, 12.5–21 % of
        // redirect-and-dial transfers with 300 other calls on the server, and 25–50 % of the first 20
        // calls of a new process. The clock never moves, so only the holder's hangup can end the wait.
        var x = Guid.NewGuid();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();
        await using var server = NewServer(time, logger);
        using var holderPeer = await ConnectAndIdentifyAsync(listener, x);
        var holder = await HandOverAndAwaitAnnouncementAsync(server, listener);
        await NextTimerAsync(time); // the holder's UUID deadline

        var order = new ConcurrentQueue<string>();
        holder.OnHangup += () => order.Enqueue("holder hung up");
        var announced = new TaskCompletionSource<AudioSocketSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnSessionStarted += session =>
        {
            order.Enqueue("second announced");
            announced.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        using var secondPeer = await ConnectAndIdentifyAsync(listener, x);
        using var secondEnd = await listener.AcceptTcpClientAsync();
        var handling = server.HandleConnectionAsync(secondEnd, CancellationToken.None);
        await NextTimerAsync(time); // the second connection's UUID deadline
        (await NextTimerAsync(time)).DueTime.Should().Be(
            TimeSpan.FromSeconds(1),
            "a connection that presents a held id waits for the holder, bounded by the grace");

        // Act — the holder's call leaves the bot
        await SendFrameAsync(holderPeer, AudioSocketFrameType.Hangup, []);
        var second = await announced.Task.WaitAsync(SignalTimeout);
        await handling.WaitAsync(SignalTimeout);

        // Assert
        second.ChannelId.Should().Be(x);
        second.IsConnected.Should().BeTrue("the connection that came back is served, not refused");
        order.Should().Equal(
            ["holder hung up", "second announced"],
            "the second session is announced after every hangup handler of the first, so a consumer keyed by channel id never holds two");
        server.ActiveSessionCount.Should().Be(1, "the holder released X and the second connection holds it");
        logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning, "a connection that comes back is not a refusal");
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldAnnounceTheConnectionThatCameBack_OnlyAfterEveryHangupHandlerOfThePrevious()
    {
        // Arrange — the connection that comes back arrives while a consumer's OnHangup handler for the
        // holder is still running. A release that runs in the server's own handler, before the
        // consumer's, lets it in early: measured live, 8 of 1500 re-entries on one harness were announced
        // before the consumer's hangup handler of the previous session had returned. Here the consumer's
        // handler blocks on a gate, so the order is decided by signals alone and never by a clock.
        var x = Guid.NewGuid();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var time = new FakeTimeProvider();
        await using var server = NewServer(time, new CapturingLogger());
        using var holderPeer = await ConnectAndIdentifyAsync(listener, x);
        var holder = await HandOverAndAwaitAnnouncementAsync(server, listener);
        await NextTimerAsync(time); // the holder's UUID deadline

        using var gate = new ManualResetEventSlim(false);
        var consumerHandlerRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        holder.OnHangup += () =>
        {
            consumerHandlerRunning.TrySetResult();
            gate.Wait(SignalTimeout);
        };
        var announced = new TaskCompletionSource<AudioSocketSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnSessionStarted += session =>
        {
            announced.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        await SendFrameAsync(holderPeer, AudioSocketFrameType.Hangup, []);
        await consumerHandlerRunning.Task.WaitAsync(SignalTimeout);

        try
        {
            // Act — the call comes back while the consumer is still handling the previous session's hangup
            using var secondPeer = await ConnectAndIdentifyAsync(listener, x);
            using var secondEnd = await listener.AcceptTcpClientAsync();
            var handling = server.HandleConnectionAsync(secondEnd, CancellationToken.None);
            await NextTimerAsync(time); // the second connection's UUID deadline
            var nextTimer = NextTimerAsync(time);
            var first = await Task.WhenAny(announced.Task, nextTimer);

            // Assert — a connection that parks on the holder creates the grace timer and is not announced;
            // one that finds the entry already gone is announced with no further timer
            first.Should().BeSameAs(
                nextTimer,
                "the call that came back must not be announced before the consumer's handler returned: a consumer keyed by channel id would hold two sessions under one id");
            (await nextTimer).DueTime.Should().Be(
                TimeSpan.FromSeconds(1),
                "the connection waits for the holder, bounded by the grace");
            announced.Task.IsCompleted.Should().BeFalse("the consumer's hangup handler for the holder has not returned");

            gate.Set();
            (await announced.Task.WaitAsync(SignalTimeout)).ChannelId.Should().Be(
                x,
                "once every hangup handler of the holder has returned, the call that came back is served");
            await handling.WaitAsync(SignalTimeout);
        }
        finally
        {
            // Never leave the holder's read loop blocked in the consumer's handler past the test.
            gate.Set();
        }
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldServeAConnectionThatPresentsAHeldId_WhenTheServerIsAtItsLimit()
    {
        // Arrange — the same re-entry, on a server at MaxConcurrentSessions = 1 whose only session's call
        // comes back. That call holds its place through its previous connection, which is ending;
        // admitting it replaces that session and never raises the count past the limit.
        var x = Guid.NewGuid();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0, ConnectionTimeout = TimeSpan.FromSeconds(30), MaxConcurrentSessions = 1 },
            logger,
            time);
        using var holderPeer = await ConnectAndIdentifyAsync(listener, x);
        await HandOverAndAwaitAnnouncementAsync(server, listener);
        await NextTimerAsync(time); // the holder's UUID deadline
        var announced = new TaskCompletionSource<AudioSocketSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnSessionStarted += session =>
        {
            announced.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        using var secondPeer = await ConnectAndIdentifyAsync(listener, x);
        using var secondEnd = await listener.AcceptTcpClientAsync();
        var handling = server.HandleConnectionAsync(secondEnd, CancellationToken.None);
        await NextTimerAsync(time); // the second connection's UUID deadline

        // Act — the holder's call leaves the bot
        await SendFrameAsync(holderPeer, AudioSocketFrameType.Hangup, []);
        await handling.WaitAsync(SignalTimeout);

        // Assert
        using (new AssertionScope())
        {
            announced.Task.IsCompletedSuccessfully.Should().BeTrue(
                "the connection that came back is the call that already held the only place, not a new call over the limit");
            logger.Entries.Select(entry => entry.EventName).Should().NotContain(
                "SessionLimitReached", "the limit was not exceeded: one call, one live session");
        }
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldLogTheChannelIdNotTheLimit_WhenItRefusesASameIdConnection()
    {
        // Arrange
        var x = Guid.NewGuid();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();
        await using var server = NewServer(time, logger);
        using var holderPeer = await ConnectAndIdentifyAsync(listener, x);
        await HandOverAndAwaitAnnouncementAsync(server, listener);
        await NextTimerAsync(time); // the holder's UUID deadline
        using var refusedPeer = await ConnectAndIdentifyAsync(listener, x);
        using var refusedEnd = await listener.AcceptTcpClientAsync();
        var refusing = server.HandleConnectionAsync(refusedEnd, CancellationToken.None);
        await NextTimerAsync(time); // the second connection's UUID deadline
        var grace = await NextTimerAsync(time);

        // Act
        time.Advance(grace.DueTime);
        await refusing.WaitAsync(SignalTimeout);

        // Assert
        logger.Entries.Where(entry => entry.Level >= LogLevel.Warning).Select(entry => entry.EventName).Should().Equal(
            ["ChannelIdInUse"],
            "the refusal is logged as what it is: the session limit was not reached");
        logger.Messages.Should().ContainSingle(
            message => message.Contains(x.ToString(), StringComparison.Ordinal) && message.Contains("1000 ms", StringComparison.Ordinal),
            "the warning names the channel id and how long the connection waited for it");
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldRefuseWithAHangupFrameAndLogTheLimit_WhenMaxConcurrentSessionsIsReached()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var logger = new CapturingLogger();
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { Port = 0, ConnectionTimeout = TimeSpan.FromSeconds(30), MaxConcurrentSessions = 1 },
            logger,
            new FakeTimeProvider());
        using var holderPeer = await ConnectAndIdentifyAsync(listener, Guid.NewGuid());
        await HandOverAndAwaitAnnouncementAsync(server, listener);

        // Act — a connection with another id, over the limit
        using var refusedPeer = await ConnectAndIdentifyAsync(listener, Guid.NewGuid());
        using var refusedEnd = await listener.AcceptTcpClientAsync();
        await server.HandleConnectionAsync(refusedEnd, CancellationToken.None).WaitAsync(SignalTimeout);

        // Assert
        (await ReadUntilServerClosesAsync(refusedPeer)).Should().Equal(
            HangupFrame,
            "a refusal writes a hangup frame, so the call goes on in the dialplan whatever the refusal's timing");
        logger.Entries.Where(entry => entry.Level >= LogLevel.Warning).Select(entry => entry.EventName).Should().Equal(
            ["SessionLimitReached"],
            "only a refusal for the limit is logged as the limit");
        server.ActiveSessionCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldLogNothing_WhenTheServerStopsWhileASameIdConnectionWaits()
    {
        // Arrange
        var x = Guid.NewGuid();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();
        await using var server = NewServer(time, logger);
        using var holderPeer = await ConnectAndIdentifyAsync(listener, x);
        await HandOverAndAwaitAnnouncementAsync(server, listener);
        await NextTimerAsync(time); // the holder's UUID deadline
        using var serverStopping = new CancellationTokenSource();
        using var waitingPeer = await ConnectAndIdentifyAsync(listener, x);
        using var waitingEnd = await listener.AcceptTcpClientAsync();
        var waiting = server.HandleConnectionAsync(waitingEnd, serverStopping.Token);
        await NextTimerAsync(time); // the second connection's UUID deadline
        await NextTimerAsync(time); // parked on the holder

        // Act
        await serverStopping.CancelAsync();
        await waiting.WaitAsync(SignalTimeout);

        // Assert
        (await ReadUntilServerClosesAsync(waitingPeer)).Should().Equal(HangupFrame, "a stopping server still lets the call go on");
        logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning, "a stop is not a refusal the client caused");
        server.ActiveSessionCount.Should().Be(1, "the holder is untouched until the stop reaches it");
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldReleaseTheHoldersEntry_WhenItsClientSendsAHangupFrame()
    {
        // Arrange
        var x = Guid.NewGuid();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var server = NewServerOnStoppedClock();
        using var holderPeer = await ConnectAndIdentifyAsync(listener, x);
        var holder = await HandOverAndAwaitAnnouncementAsync(server, listener);
        server.ActiveSessionCount.Should().Be(1, "the handler announces a session only after registering it");

        // HungUp completes only after every OnHangup handler has returned and the release has run, so it
        // (not a handler of this test's own) is the edge that orders the check below after the release.

        // Act
        await SendFrameAsync(holderPeer, AudioSocketFrameType.Hangup, []);
        await holder.HungUp.WaitAsync(SignalTimeout);

        // Assert
        server.ActiveSessionCount.Should().Be(0, "a session that hangs up releases the entry it holds");
        (await ReadFromServerAsync(holderPeer)).Should().Be(0, "the session closes its connection on the hangup frame");
    }

    [Fact]
    public async Task Server_ShouldRejectConnection_WhenMaxSessionsReached()
    {
        await _server.StartAsync(CancellationToken.None);

        // Fill up to max (2 sessions)
        await using var client1 = new AudioSocketClient("127.0.0.1", _server.BoundPort, Guid.NewGuid());
        await using var client2 = new AudioSocketClient("127.0.0.1", _server.BoundPort, Guid.NewGuid());
        await client1.ConnectAsync();
        await client2.ConnectAsync();
        await Task.Delay(300);

        _server.ActiveSessionCount.Should().Be(2);

        // Third connection should be rejected
        await using var client3 = new AudioSocketClient("127.0.0.1", _server.BoundPort, Guid.NewGuid());
        await client3.ConnectAsync();
        await Task.Delay(300);

        // Session count should not exceed max
        _server.ActiveSessionCount.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public void Server_BoundPort_ShouldBeZero_BeforeStart()
    {
        var options = new AudioSocketOptions { Port = 0 };
        var server = new AudioSocketServer(options, NullLogger<AudioSocketServer>.Instance);

        server.BoundPort.Should().Be(0);
    }

    [Fact]
    public async Task Server_StopAsync_ShouldCleanUpAllSessions()
    {
        await _server.StartAsync(CancellationToken.None);

        await using var client1 = new AudioSocketClient("127.0.0.1", _server.BoundPort, Guid.NewGuid());
        await client1.ConnectAsync();
        await Task.Delay(200);

        _server.ActiveSessionCount.Should().Be(1);

        await _server.StopAsync(CancellationToken.None);

        _server.ActiveSessionCount.Should().Be(0);
    }

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        await _server.DisposeAsync();
    }

    /// <summary>
    /// Reads from a connection the server never writes to, so the only way the read completes is the
    /// server closing its end, which reads as 0.
    /// </summary>
    private static Task<int> ReadFromServerAsync(TcpClient client) =>
        client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(SignalTimeout);

    /// <summary>The frame a server writes before it closes a connection it will not serve.</summary>
    private static readonly byte[] HangupFrame = [0x00, 0x00, 0x00];

    /// <summary>
    /// A server on <paramref name="time"/> that logs to <paramref name="logger"/>. It is never started: a
    /// test hands it connections through the handler, and moves its clock only with <c>Advance</c>.
    /// </summary>
    private static AudioSocketServer NewServer(FakeTimeProvider time, CapturingLogger logger) =>
        new(new AudioSocketOptions { Port = 0, ConnectionTimeout = TimeSpan.FromSeconds(30) }, logger, time);

    /// <summary>Everything the server writes to <paramref name="client"/> until it closes the connection.</summary>
    private static async Task<byte[]> ReadUntilServerClosesAsync(TcpClient client)
    {
        using var all = new MemoryStream();
        var buffer = new byte[256];
        int read;
        while ((read = await client.GetStream().ReadAsync(buffer).AsTask().WaitAsync(SignalTimeout)) > 0)
            all.Write(buffer, 0, read);

        return all.ToArray();
    }

    /// <summary>
    /// A server whose UUID timeout runs on a fake clock that never moves, so nothing a test does with
    /// it can end on a clock. It is never started: a test hands it connections through the handler.
    /// </summary>
    private static AudioSocketServer NewServerOnStoppedClock() =>
        new(
            new AudioSocketOptions { Port = 0, ConnectionTimeout = TimeSpan.FromSeconds(30) },
            NullLogger<AudioSocketServer>.Instance,
            new FakeTimeProvider());

    /// <summary>Connects a peer to <paramref name="listener"/> and sends the UUID frame naming <paramref name="channelId"/>.</summary>
    private static async Task<TcpClient> ConnectAndIdentifyAsync(TcpListener listener, Guid channelId)
    {
        var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        await SendFrameAsync(peer, AudioSocketFrameType.Uuid, channelId.ToByteArray(bigEndian: true));
        return peer;
    }

    /// <summary>Writes one AudioSocket frame from a peer, in the codec's wire format.</summary>
    private static async Task SendFrameAsync(TcpClient peer, AudioSocketFrameType type, byte[] payload)
    {
        var frame = new ArrayBufferWriter<byte>();
        AudioSocketFrameCodec.WriteFrame(frame, type, payload);
        await peer.GetStream().WriteAsync(frame.WrittenMemory);
    }

    /// <summary>
    /// Accepts the next connection on <paramref name="listener"/> and hands it to the handler, as the
    /// accept loop does, then returns the session the handler announces. The handler registers a
    /// session before it announces it, so the announcement is the sentinel for the registration.
    /// </summary>
    private static async Task<AudioSocketSession> HandOverAndAwaitAnnouncementAsync(
        AudioSocketServer server,
        TcpListener listener)
    {
        var announced = new TaskCompletionSource<AudioSocketSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask OnStarted(AudioSocketSession session)
        {
            announced.TrySetResult(session);
            return ValueTask.CompletedTask;
        }

        server.OnSessionStarted += OnStarted;
        try
        {
            var accepted = await listener.AcceptTcpClientAsync();
            await server.HandleConnectionAsync(accepted, CancellationToken.None).WaitAsync(SignalTimeout);
            return await announced.Task.WaitAsync(SignalTimeout);
        }
        finally
        {
            server.OnSessionStarted -= OnStarted;
        }
    }

    /// <summary>
    /// Totals the audio the server sends until it closes the connection. The enumeration ends only on
    /// that close, so completing at all is the signal.
    /// </summary>
    private static async Task<int> AudioBytesUntilServerClosesAsync(AudioSocketClient client)
    {
        var bytes = 0;
        await foreach (var frame in client.ReadAudioAsync())
            bytes += frame.Length;

        return bytes;
    }

    /// <summary>
    /// An accept that never returns a connection and ends only when <paramref name="token"/> does, so
    /// a loop parked on it attempts nothing further and puts no wait on any clock, fake or real. The
    /// park is the token's own registration rather than a delay, so nothing here is timed.
    /// </summary>
    private static async ValueTask<TcpClient> ParkUntilCancelledAsync(CancellationToken token)
    {
        var parked = new TaskCompletionSource<TcpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => parked.TrySetCanceled(token));
        return await parked.Task.ConfigureAwait(false);
    }

    /// <summary>The next timer created on <paramref name="time"/>, as soon as it exists.</summary>
    private static Task<FakeTimeProvider.FakeTimer> NextTimerAsync(FakeTimeProvider time) =>
        time.TimersCreated.ReadAsync().AsTask().WaitAsync(SignalTimeout);

    /// <summary>A server log entry, reduced to what these tests assert on.</summary>
    private sealed record LogEntry(LogLevel Level, int EventId, string? EventName, string? ExceptionType);

    /// <summary>
    /// Records every entry the server logs, and completes <see cref="FirstWarningOrAbove"/> with the
    /// first one at <see cref="LogLevel.Warning"/> or above.
    /// </summary>
    private sealed class CapturingLogger : ILogger<AudioSocketServer>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        private readonly TaskCompletionSource<LogEntry> _firstWarningOrAbove =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        private readonly ConcurrentQueue<string> _messages = new();

        /// <summary>Every entry's formatted message, in logging order.</summary>
        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public Task<LogEntry> FirstWarningOrAbove => _firstWarningOrAbove.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = new LogEntry(logLevel, eventId.Id, eventId.Name, exception?.GetType().Name);
            _entries.Enqueue(entry);
            _messages.Enqueue(formatter(state, exception));
            if (logLevel >= LogLevel.Warning)
                _firstWarningOrAbove.TrySetResult(entry);
        }
    }
}
