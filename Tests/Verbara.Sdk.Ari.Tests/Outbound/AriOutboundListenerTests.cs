using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Channels;
using Verbara.Sdk;
using Verbara.Sdk.Ari.Outbound;
using Verbara.Sdk.Ari.Tests.TestSupport;
using Verbara.Sdk.Tests.Shared.Sockets;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ari.Tests.Outbound;

public sealed class AriOutboundListenerTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private static (AriOutboundListener listener, AriOutboundListenerOptions options) CreateListener(
        Action<AriOutboundListenerOptions>? configure = null,
        ILogger<AriOutboundListener>? logger = null,
        TimeProvider? timeProvider = null)
    {
        var options = new AriOutboundListenerOptions
        {
            ListenAddress = "127.0.0.1",
            Port = 0, // ephemeral
            Path = "/ari/events",
            ConnectionIdleTimeout = TimeSpan.FromSeconds(30)
        };
        configure?.Invoke(options);
        var listener = new AriOutboundListener(
            Options.Create(options),
            logger ?? NullLogger<AriOutboundListener>.Instance,
            timeProvider ?? TimeProvider.System);
        return (listener, options);
    }

    private static async Task<ClientWebSocket> ConnectClientAsync(
        int port,
        string path = "/ari/events",
        string? app = "myapp",
        string? basicAuthHeader = null)
    {
        var client = new ClientWebSocket();
        if (basicAuthHeader is not null)
            client.Options.SetRequestHeader("Authorization", basicAuthHeader);

        var appQuery = app is not null ? $"?app={Uri.EscapeDataString(app)}" : string.Empty;
        var uri = new Uri($"ws://127.0.0.1:{port}{path}{appQuery}");
        await client.ConnectAsync(uri, CancellationToken.None);
        return client;
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    [Fact]
    public async Task StartAsync_ShouldAcceptHandshake_WhenAllHeadersValid()
    {
        var (listener, _) = CreateListener();
        await listener.StartAsync();
        try
        {
            using var client = await ConnectClientAsync(listener.BoundPort);
            client.State.Should().Be(WebSocketState.Open);

            (await WaitForAsync(() => listener.ActiveConnectionCount == 1, TimeSpan.FromSeconds(2)))
                .Should().BeTrue();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_ShouldRejectHandshake_WhenPathMismatch()
    {
        var (listener, _) = CreateListener(o => o.Path = "/ari/events");
        await listener.StartAsync();
        try
        {
            // Wrong path — client upgrade should fail.
            using var client = new ClientWebSocket();
            var uri = new Uri($"ws://127.0.0.1:{listener.BoundPort}/wrong/path?app=myapp");
            var act = async () => await client.ConnectAsync(uri, CancellationToken.None);

            await act.Should().ThrowAsync<WebSocketException>();
            listener.ActiveConnectionCount.Should().Be(0);
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_ShouldRejectHandshake_WhenAuthMismatch()
    {
        var (listener, _) = CreateListener(o =>
        {
            o.ExpectedUsername = "alice";
            o.ExpectedPassword = "secret";
        });
        await listener.StartAsync();
        try
        {
            var badAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:wrong"));
            using var client = new ClientWebSocket();
            client.Options.SetRequestHeader("Authorization", badAuth);
            var uri = new Uri($"ws://127.0.0.1:{listener.BoundPort}/ari/events?app=myapp");

            var act = async () => await client.ConnectAsync(uri, CancellationToken.None);
            await act.Should().ThrowAsync<WebSocketException>();

            listener.ActiveConnectionCount.Should().Be(0);
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_ShouldRejectHandshake_WhenAppNotAllowed()
    {
        var (listener, _) = CreateListener(o => o.AllowedApplications.Add("only-this-one"));
        await listener.StartAsync();
        try
        {
            using var client = new ClientWebSocket();
            var uri = new Uri($"ws://127.0.0.1:{listener.BoundPort}/ari/events?app=other");

            var act = async () => await client.ConnectAsync(uri, CancellationToken.None);
            await act.Should().ThrowAsync<WebSocketException>();

            listener.ActiveConnectionCount.Should().Be(0);
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_ShouldAcceptHandshake_WhenAllowedListEmpty()
    {
        var (listener, _) = CreateListener(); // empty AllowedApplications
        await listener.StartAsync();
        try
        {
            using var client = await ConnectClientAsync(listener.BoundPort, app: "anything");
            client.State.Should().Be(WebSocketState.Open);
            (await WaitForAsync(() => listener.ActiveConnectionCount == 1, TimeSpan.FromSeconds(2)))
                .Should().BeTrue();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task EventDispatch_ShouldEmitTypedEvent_WhenAsteriskSendsJson()
    {
        var (listener, _) = CreateListener();
        await listener.StartAsync();
        try
        {
            AriOutboundConnection? accepted = null;
            using var sub = listener.OnConnectionAccepted.Subscribe(c => accepted = c);

            using var client = await ConnectClientAsync(listener.BoundPort);
            (await WaitForAsync(() => accepted is not null, TimeSpan.FromSeconds(2))).Should().BeTrue();

            AriEvent? received = null;
            using var evtSub = accepted!.Events.Subscribe(e => received = e);

            const string json = """{"type":"StasisStart","application":"myapp","timestamp":"2026-04-19T10:00:00Z","args":["arg1"],"channel":{"id":"ch-1","name":"PJSIP/test","state":"Up"}}""";
            await client.SendAsync(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);

            (await WaitForAsync(() => received is not null, TimeSpan.FromSeconds(2))).Should().BeTrue();
            received.Should().BeOfType<Verbara.Sdk.Ari.Events.StasisStartEvent>();
            received!.Type.Should().Be("StasisStart");
            received.Application.Should().Be("myapp");
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task MultipleConnections_ShouldCoexist_ForSameApplication()
    {
        var (listener, _) = CreateListener();
        await listener.StartAsync();
        try
        {
            using var a = await ConnectClientAsync(listener.BoundPort, app: "app1");
            using var b = await ConnectClientAsync(listener.BoundPort, app: "app1");
            using var c = await ConnectClientAsync(listener.BoundPort, app: "app1");

            (await WaitForAsync(() => listener.ActiveConnectionCount == 3, TimeSpan.FromSeconds(2)))
                .Should().BeTrue();
            listener.GetByApplication("app1").Should().HaveCount(3);
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task MultipleApps_ShouldBeIndexedSeparately()
    {
        var (listener, _) = CreateListener();
        await listener.StartAsync();
        try
        {
            using var a = await ConnectClientAsync(listener.BoundPort, app: "app1");
            using var b = await ConnectClientAsync(listener.BoundPort, app: "app2");
            using var c = await ConnectClientAsync(listener.BoundPort, app: "app2");

            (await WaitForAsync(() => listener.ActiveConnectionCount == 3, TimeSpan.FromSeconds(2)))
                .Should().BeTrue();
            listener.GetByApplication("app1").Should().HaveCount(1);
            listener.GetByApplication("app2").Should().HaveCount(2);
            listener.GetByApplication("nonexistent").Should().BeEmpty();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task IdleTimeout_ShouldCloseConnection_WhenNoFramesSent()
    {
        var (listener, _) = CreateListener(o => o.ConnectionIdleTimeout = TimeSpan.FromMilliseconds(250));
        await listener.StartAsync();
        try
        {
            using var client = await ConnectClientAsync(listener.BoundPort);
            (await WaitForAsync(() => listener.ActiveConnectionCount == 1, TimeSpan.FromSeconds(2)))
                .Should().BeTrue();

            (await WaitForAsync(() => listener.ActiveConnectionCount == 0, TimeSpan.FromSeconds(3)))
                .Should().BeTrue();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task ActiveConnectionCount_ShouldReflectReality()
    {
        var (listener, _) = CreateListener();
        await listener.StartAsync();
        try
        {
            listener.ActiveConnectionCount.Should().Be(0);

            using (var a = await ConnectClientAsync(listener.BoundPort))
            {
                (await WaitForAsync(() => listener.ActiveConnectionCount == 1, TimeSpan.FromSeconds(2)))
                    .Should().BeTrue();

                using (var b = await ConnectClientAsync(listener.BoundPort))
                {
                    (await WaitForAsync(() => listener.ActiveConnectionCount == 2, TimeSpan.FromSeconds(2)))
                        .Should().BeTrue();

                    await b.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    (await WaitForAsync(() => listener.ActiveConnectionCount == 1, TimeSpan.FromSeconds(2)))
                        .Should().BeTrue();
                }

                await a.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                (await WaitForAsync(() => listener.ActiveConnectionCount == 0, TimeSpan.FromSeconds(2)))
                    .Should().BeTrue();
            }
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisconnectAsync_ShouldRemoveConnectionFromActiveSet()
    {
        var (listener, _) = CreateListener();
        await listener.StartAsync();
        try
        {
            AriOutboundConnection? accepted = null;
            using var sub = listener.OnConnectionAccepted.Subscribe(c => accepted = c);

            using var client = await ConnectClientAsync(listener.BoundPort);
            (await WaitForAsync(() => accepted is not null, TimeSpan.FromSeconds(2))).Should().BeTrue();

            await accepted!.DisconnectAsync();

            (await WaitForAsync(() => listener.ActiveConnectionCount == 0, TimeSpan.FromSeconds(2)))
                .Should().BeTrue();
            accepted.IsConnected.Should().BeFalse();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldCloseAllActiveSessions()
    {
        var (listener, _) = CreateListener();
        await listener.StartAsync();

        using var a = await ConnectClientAsync(listener.BoundPort, app: "app1");
        using var b = await ConnectClientAsync(listener.BoundPort, app: "app2");
        (await WaitForAsync(() => listener.ActiveConnectionCount == 2, TimeSpan.FromSeconds(2)))
            .Should().BeTrue();

        await listener.DisposeAsync();

        listener.IsRunning.Should().BeFalse();
        listener.ActiveConnectionCount.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldBeIdempotent()
    {
        var (listener, _) = CreateListener();
        await listener.StartAsync();
        try
        {
            // Second Start should be a no-op and not throw.
            await listener.StartAsync();
            listener.IsRunning.Should().BeTrue();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_ShouldAcceptAuth_WhenBasicAuthHeaderMatchesExpected()
    {
        var (listener, _) = CreateListener(o =>
        {
            o.ExpectedUsername = "alice";
            o.ExpectedPassword = "secret";
        });
        await listener.StartAsync();
        try
        {
            var auth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:secret"));
            using var client = await ConnectClientAsync(listener.BoundPort, basicAuthHeader: auth);
            client.State.Should().Be(WebSocketState.Open);
            (await WaitForAsync(() => listener.ActiveConnectionCount == 1, TimeSpan.FromSeconds(2)))
                .Should().BeTrue();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_ShouldCloseTheConnectionUnanswered_WhenTheUpgradeRequestIsMalformed()
    {
        // Arrange — a raw socket, because this ending is unreachable through ClientWebSocket: it is
        // the one rejection where the listener writes no response at all, so there is nothing for a
        // WebSocket client to fail on. Until this test the path had no coverage of any kind, which
        // left task 5.1's edit to it — dropping the hand-written `client.Dispose()` in favour of the
        // enclosing `using (client)` — unverified by the suite.
        var logger = new CapturingLogger();
        var (listener, _) = CreateListener(logger: logger);
        await listener.StartAsync();
        try
        {
            using var raw = new TcpClient();
            await raw.ConnectAsync(IPAddress.Parse("127.0.0.1"), listener.BoundPort);
            var stream = raw.GetStream();

            // Act — a request line of one token, so ReadUpgradeRequestAsync returns null. The
            // trailing CRLFCRLF is what ends its read, so the listener never waits for more.
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GARBAGE\r\n\r\n"));
            await stream.FlushAsync();

            // Assert — the read returns 0 only once the listener has closed its end, so the wait is
            // on that close and not on a clock. The close is the `using (client)` block itself.
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(SignalTimeout);

            read.Should().Be(
                0,
                "a malformed upgrade is refused by closing the connection unanswered, and the " +
                "handler releases it before returning");
            logger.Entries.Should().Contain(
                entry => entry.Level == LogLevel.Warning && entry.EventName == "UpgradeRejected",
                "the refusal is reported rather than swallowed");
            listener.ActiveConnectionCount.Should().Be(
                0,
                "a connection refused at the handshake is never tracked");
        }
        finally
        {
            await listener.DisposeAsync();
        }
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
        var (listener, _) = CreateListener(logger: logger, timeProvider: time);
        var attempts = 0;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.AcceptOverride = token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new SocketException((int)SocketError.TooManyOpenSockets);

            secondAttempt.TrySetResult();
            return ParkUntilCancelledAsync(token);
        };

        await listener.StartAsync();
        try
        {
            // Act — the loop reaches its backoff, which is what creates the only timer on this clock
            var backoff = await NextTimerAsync(time);

            // Assert — the failure was reported, and the listener still says it is accepting
            backoff.DueTime.Should().Be(
                AriOutboundListener.InitialAcceptBackoff,
                "the first of a run of failed accepts waits the initial backoff");
            logger.Entries.Should().ContainSingle(
                entry => entry.Level == LogLevel.Error
                    && entry.EventName == "AcceptLoopFailed"
                    && entry.ExceptionType == nameof(SocketException),
                "an accept that failed while the listener is still running is the loss of every " +
                "connection, so it is logged at Error rather than swallowed");
            listener.IsRunning.Should().BeTrue(
                "IsRunning means bound and accepting, and after a backed-off failure the listener " +
                "is still both");
            secondAttempt.Task.IsCompleted.Should().BeFalse(
                "the loop waits the backoff out before it accepts again, instead of spinning");

            // Act — the backoff is the only thing holding the loop, so moving the clock releases it
            time.Advance(AriOutboundListener.InitialAcceptBackoff);
            await secondAttempt.Task.WaitAsync(SignalTimeout);

            // Assert
            Volatile.Read(ref attempts).Should().Be(
                2,
                "the loop keeps accepting after a failure rather than ending with the listener bound");
            listener.IsRunning.Should().BeTrue();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcceptLoopAsync_ShouldDoubleTheBackoffToTheCap_WhenAcceptsKeepFailing()
    {
        // Arrange — every accept fails, so the loop is a pure backoff generator and each wait it asks
        // for is read off the fake clock without any of it being spent.
        var time = new FakeTimeProvider();
        var (listener, _) = CreateListener(logger: new CapturingLogger(), timeProvider: time);
        listener.AcceptOverride = _ => throw new SocketException((int)SocketError.TooManyOpenSockets);

        await listener.StartAsync();
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
            waits[0].Should().Be(AriOutboundListener.InitialAcceptBackoff);
            waits[^1].Should().Be(AriOutboundListener.MaxAcceptBackoff);
            listener.IsRunning.Should().BeTrue("the listener is still bound and still accepting");
        }
        finally
        {
            await listener.DisposeAsync();
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
        var (listener, _) = CreateListener(logger: new CapturingLogger(), timeProvider: time);
        var attempts = 0;
        listener.AcceptOverride = token => Interlocked.Increment(ref attempts) switch
        {
            2 => ValueTask.FromResult(accepted),
            >= 4 => ParkUntilCancelledAsync(token),
            _ => throw new SocketException((int)SocketError.TooManyOpenSockets)
        };

        await listener.StartAsync();
        try
        {
            // Act — the first failure's wait, then the success, then the second failure's wait
            var afterFirstFailure = await NextTimerAsync(time);
            time.Advance(afterFirstFailure.DueTime);
            var afterSuccess = await NextTimerAsync(time);

            // Assert
            afterFirstFailure.DueTime.Should().Be(AriOutboundListener.InitialAcceptBackoff);
            afterSuccess.DueTime.Should().Be(
                AriOutboundListener.InitialAcceptBackoff,
                "a successful accept starts the run over, so the next failure waits the initial " +
                "backoff again rather than the doubled one");
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopAsync_ShouldEndTheAcceptLoopWithoutBackingOff_WhenTheListenerIsStopped()
    {
        // Arrange — the real listener and a real accepted connection, so the loop is known to be parked
        // on its next accept when the stop lands. That is the ending StopAsync produces on Linux: it
        // clears _running, then Stop() aborts the pending accept as SocketException(OperationAborted).
        // The clock is fake and never moves, so any backoff the loop took would be visible as a timer
        // it created and would never be waited out by accident.
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger();
        var (listener, _) = CreateListener(logger: logger, timeProvider: time);
        await listener.StartAsync();

        using var client = await ConnectClientAsync(listener.BoundPort);
        (await WaitForAsync(() => listener.ActiveConnectionCount == 1, TimeSpan.FromSeconds(2)))
            .Should().BeTrue("the loop has accepted once and is parked on the next accept");

        // Act — StopAsync awaits the accept loop, so it returns only once the loop has ended
        await listener.StopAsync().AsTask().WaitAsync(SignalTimeout);

        // Assert
        listener.IsRunning.Should().BeFalse();

        // There is deliberately no assertion on `time.TimersCreated` here, and the omission is the
        // finding rather than an oversight. It looks like the natural way to pin "the stop breaks
        // rather than backs off", but it cannot fail: StopAsync cancels _cts before the loop could
        // reach `Task.Delay(backoff, _timeProvider, ct)`, and a Task.Delay handed an already-cancelled
        // token returns without ever calling CreateTimer. Measured — removing the `break;` from the
        // `catch (SocketException) when (!IsRunning)` arm, which is literally "a stopping listener
        // that backed off", leaves this test green. The `break` in that arm is not separable by any
        // test reachable from outside the type; it is correct by construction, and recorded as
        // unseparable in tasks.md 4.3 alongside the IsRunning discriminator itself. What IS pinned
        // below is real: an accept aborted by the stop must not be reported as a failure.
        logger.Entries.Should().NotContain(
            entry => entry.EventName == "AcceptLoopFailed",
            "an accept aborted by the stop is the stop, not a failure, so it is not reported as one");
        logger.Entries.Should().Contain(
            entry => entry.EventName == "ListenerStopped",
            "the stop ran to the end rather than being left behind by a loop still waiting");
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
        var (listener, _) = CreateListener(logger: logger, timeProvider: time);
        using var accepted = AcceptedClients.UdpBacked();
        using var client = new ReleaseSignallingClient(accepted.Socket);
        var attempts = 0;
        listener.AcceptOverride = token => Interlocked.Increment(ref attempts) == 1
            ? ValueTask.FromResult<TcpClient>(client)
            : AcceptedClients.ParkUntilCancelledAsync(token);
        var backoffRequested = time.TimersCreated.ReadAsync().AsTask();

        await listener.StartAsync();
        try
        {
            // Act
            await Task.WhenAny(client.Released, backoffRequested).WaitAsync(SignalTimeout);

            // Assert
            using (new AssertionScope())
            {
                accepted.IsSocketClosed.Should().BeTrue(
                    "a connection the loop accepted and could not configure is closed, not leaked; the " +
                    "listener logged [{0}]",
                    Describe(logger));
                logger.Entries.Should().ContainSingle(
                    entry => entry.Level == LogLevel.Error
                        && entry.EventName == "ConnectionError"
                        && entry.ExceptionType == nameof(SocketException),
                    "the failure belongs to that one connection, so it is reported once as its error");
                logger.Entries.Should().NotContain(
                    entry => entry.EventName == "AcceptLoopFailed",
                    "no accept failed: the accept returned a connection, and configuring it is serving it");
                listener.IsRunning.Should().BeTrue("one connection failing is not the listener failing");
            }
        }
        finally
        {
            await listener.DisposeAsync();
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
        var (listener, _) = CreateListener(logger: logger, timeProvider: new FakeTimeProvider());
        using var accepted = AcceptedClients.DisposedSocket();
        using var client = new ReleaseSignallingClient(accepted.Socket);
        var attempts = 0;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.AcceptOverride = token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                return ValueTask.FromResult<TcpClient>(client);

            secondAttempt.TrySetResult();
            return AcceptedClients.ParkUntilCancelledAsync(token);
        };

        await listener.StartAsync();
        try
        {
            // Act
            var nextAccept = () => secondAttempt.Task;
            await nextAccept.Should().CompleteWithinAsync(
                SignalTimeout,
                "one connection's closed socket is not the listener's stop, so the loop accepts again");
            await client.Released.WaitAsync(SignalTimeout);

            // Assert
            using (new AssertionScope())
            {
                listener.IsRunning.Should().BeTrue("the listener is still bound and still accepting");
                logger.Entries.Should().ContainSingle(
                    entry => entry.Level == LogLevel.Error
                        && entry.EventName == "ConnectionError"
                        && entry.ExceptionType == nameof(ObjectDisposedException),
                    "the failure belongs to that one connection, so it is reported once as its error; " +
                    "the listener logged [{0}]",
                    Describe(logger));
                logger.Entries.Should().NotContain(
                    entry => entry.EventName == "AcceptLoopFailed",
                    "no accept failed");
            }
        }
        finally
        {
            await listener.DisposeAsync();
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
        var (listener, _) = CreateListener(logger: logger, timeProvider: time);
        using var accepted = AcceptedClients.UdpBacked();
        using var client = new ReleaseSignallingClient(accepted.Socket);
        var attempts = 0;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.AcceptOverride = token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                return ValueTask.FromResult<TcpClient>(client);

            secondAttempt.TrySetResult();
            return AcceptedClients.ParkUntilCancelledAsync(token);
        };
        var backoffRequested = time.TimersCreated.ReadAsync().AsTask();

        await listener.StartAsync();
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
                    "{0}, the listener logged [{1}]",
                    Volatile.Read(ref attempts),
                    Describe(logger));
                backoffRequested.IsCompleted.Should().BeFalse("one connection's failure costs the next one no wait");
                logger.Entries.Should().NotContain(
                    entry => entry.EventName == "AcceptLoopFailed",
                    "no accept failed");
                listener.IsRunning.Should().BeTrue();
            }
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    // ------------------------------------------------ a connection that fails after it was accepted

    [Fact]
    public async Task HandleConnectionAsync_ShouldReportItOnce_WhenTheConnectionDiesUnderTheHandshake()
    {
        // Arrange — a real loopback connection whose peer aborts it with a linger-zero close, which
        // puts an RST on the wire instead of a FIN. The FIN ending already has a test of its own: a
        // read of zero bytes is a malformed upgrade, refused with a warning and no response. An RST is
        // the other ending, and the one this test exists for — the connection did not end, it died,
        // and the listener's own read of the upgrade request is what discovers it. The accept seam
        // hands the dead connection over so the failure is the handshake read's and never the
        // accept's; everything past the seam is a real socket failing for real.
        using var pair = new TcpListener(IPAddress.Loopback, 0);
        pair.Start();
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)pair.LocalEndpoint).Port);
        using var accepted = await pair.AcceptTcpClientAsync();

        // A raw Socket rather than a TcpClient, and the reason is the whole point of the test:
        // TcpClient.Dispose shuts the socket down before it closes it, which puts a FIN on the wire
        // and produces the graceful ending this test is NOT about. Socket.Dispose closes without
        // that shutdown, so the linger-zero option is honoured and the peer aborts.
        //
        // The explicit Dispose below is the ACT — it is what puts the RST on the wire, once
        // LingerState is set. The `using` above is only the throw guard: it covers the paths where
        // ConnectAsync or AcceptTcpClientAsync raise before that line is reached, which otherwise
        // leak the socket (cs/dispose-not-called-on-throw). Socket.Dispose is idempotent, so the
        // second release at scope exit is a no-op and the abort semantics are unchanged.
        peer.LingerState = new LingerOption(enable: true, seconds: 0);
        peer.Dispose();

        var logger = new CapturingLogger();
        var (listener, _) = CreateListener(logger: logger);
        var attempts = 0;
        listener.AcceptOverride = token => Interlocked.Increment(ref attempts) == 1
            ? ValueTask.FromResult(accepted)
            : ParkUntilCancelledAsync(token);

        await listener.StartAsync();
        try
        {
            // Act — the handler reads the upgrade request that is never coming and meets the reset
            (await WaitForAsync(
                    () => logger.Entries.Any(entry => entry.EventName == "ConnectionError"),
                    SignalTimeout))
                .Should().BeTrue(
                    "a connection that dies mid-handshake is reported rather than lost — all the " +
                    "listener logged was [{0}]",
                    string.Join(", ", logger.Entries.Select(e => e.EventName)));

            // Assert
            logger.Entries.Should().ContainSingle(
                entry => entry.Level == LogLevel.Error
                    && entry.EventName == "ConnectionError"
                    && entry.ExceptionType == nameof(IOException),
                "the transport failing under a connection is that connection's ending, so it is " +
                "logged exactly once, at Error, as a connection error");
            logger.Entries.Should().NotContain(
                entry => entry.EventName == "AcceptLoopFailed",
                "the connection failed, not the accept — reporting it as an accept failure would " +
                "point at the listener when the fault is one peer's");
            listener.IsRunning.Should().BeTrue(
                "one connection dying is not the listener dying");
            listener.ActiveConnectionCount.Should().Be(
                0,
                "a connection that never reached the upgrade response is never tracked");
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldReportItAndKeepListening_WhenAnObserverOfAcceptedConnectionsThrows()
    {
        // Arrange — OnConnectionAccepted is published from inside the handler, synchronously, so a
        // consumer's subscription throwing is an exception the handler is left holding. It throws on
        // the first connection only, which makes what happens to the SECOND one the evidence: a
        // listener that survived a consumer's fault still accepts, and one that did not, does not.
        var logger = new CapturingLogger();
        var (listener, _) = CreateListener(logger: logger);
        var observed = 0;
        using var sub = listener.OnConnectionAccepted.Subscribe(_ =>
        {
            if (Interlocked.Increment(ref observed) == 1)
                throw new InvalidOperationException("the consumer's handler failed");
        });

        await listener.StartAsync();
        try
        {
            // Act
            using var first = await ConnectClientAsync(listener.BoundPort, app: "first");

            (await WaitForAsync(
                    () => logger.Entries.Any(entry => entry.EventName == "ConnectionError"),
                    SignalTimeout))
                .Should().BeTrue("the observer's failure reached the handler and was reported");

            // Assert
            logger.Entries.Should().ContainSingle(
                entry => entry.Level == LogLevel.Error
                    && entry.EventName == "ConnectionError"
                    && entry.ExceptionType == nameof(InvalidOperationException),
                "a failure that is neither the stop nor the transport is still the loss of that " +
                "connection, so it is logged at Error instead of escaping into the unobserved " +
                "Task.Run the accept loop started the handler in");

            using var second = await ConnectClientAsync(listener.BoundPort, app: "second");

            (await WaitForAsync(() => listener.GetByApplication("second").Any(), SignalTimeout))
                .Should().BeTrue(
                    "one observer throwing costs the connection it threw on and nothing else — the " +
                    "listener is still accepting");
            listener.IsRunning.Should().BeTrue();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldCloseItSilently_WhenTheListenerStopsMidHandshake()
    {
        // Arrange — a real loopback connection, handed over by the accept seam, whose peer sends
        // nothing. The handler is therefore parked inside the upgrade-request read, on the stop
        // token, when the stop lands; and if the stop lands before the handler even gets there, the
        // first read under an already-cancelled token ends the same way. Both orderings reach the
        // same arm, so this test has no race to lose — which is the point, because the arm was
        // reached only incidentally by other tests before it, and not on every run.
        using var pair = new TcpListener(IPAddress.Loopback, 0);
        pair.Start();
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)pair.LocalEndpoint).Port);
        using var accepted = await pair.AcceptTcpClientAsync();

        var logger = new CapturingLogger();
        var (listener, _) = CreateListener(logger: logger);
        var attempts = 0;
        var handedOver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.AcceptOverride = token =>
        {
            if (Interlocked.Increment(ref attempts) > 1)
                return ParkUntilCancelledAsync(token);

            handedOver.TrySetResult();
            return ValueTask.FromResult(accepted);
        };

        await listener.StartAsync();
        try
        {
            // The hand-off is awaited rather than assumed: StartAsync only queues the accept loop, so
            // a stop issued before that loop's first iteration would cancel it while it was still a
            // queued work item, and nothing would ever be handed to a handler. Measured — without
            // this wait the test fails under a loaded run roughly half the time, on the connection
            // never being served at all rather than on anything it asserts.
            await handedOver.Task.WaitAsync(SignalTimeout);

            // Act
            await listener.StopAsync().AsTask().WaitAsync(SignalTimeout);

            // Assert — the peer's read returns 0 only once the handler has released its end, so the
            // wait is on that release and not on a clock. The release is the `using (client)` block.
            var buffer = new byte[1];
            var read = await peer.GetStream().ReadAsync(buffer).AsTask().WaitAsync(SignalTimeout);

            read.Should().Be(
                0,
                "a connection the listener was still handshaking is closed by the stop rather than " +
                "left open on a listener that no longer accepts");
            logger.Entries.Should().NotContain(
                entry => entry.EventName == "ConnectionError",
                "a handshake cut short by the listener stopping is the stop, not a connection " +
                "error, so it is swallowed rather than reported");

            // StopAsync returned only after the loop ended, so anything the loop logs is already here
            logger.Entries.Should().NotContain(
                entry => entry.EventName == "AcceptLoopFailed",
                "the accept the loop was parked on ended with the stop, which is not an accept failure");
            logger.Entries.Should().Contain(
                entry => entry.EventName == "ListenerStopped",
                "the stop ran to the end");
            listener.IsRunning.Should().BeFalse();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    // ------------------------------------------------------------ a connection whose announcement fails

    [Fact]
    public async Task HandleConnectionAsync_ShouldReleaseTheConnection_WhenAnObserverOfAcceptedConnectionsThrows()
    {
        // Arrange — the listener tracks a connection before it announces it, and only the read pump's
        // `finally` untracks it. An observer that throws during the announcement means the read pump
        // never runs, so this test follows the connection that observer was told about. The observer
        // subscribes to that connection's events while it is being told, the one moment it can: a
        // released connection's event stream takes no more subscribers. The accept seam hands the
        // listener a loopback connection this test accepted itself, and the wait ends on the handler
        // releasing it, which is the handler's last step.
        var logger = new CapturingLogger();
        var (listener, _) = CreateListener(logger: logger);
        using var seam = new LoopbackAcceptSeam();
        listener.AcceptOverride = seam.AcceptAsync;
        AriOutboundConnection? announced = null;
        IDisposable? eventsSubscription = null;
        var eventsCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = listener.OnConnectionAccepted.Subscribe(connection =>
        {
            announced = connection;
            eventsSubscription = connection.Events.Subscribe(_ => { }, () => eventsCompleted.TrySetResult());
            throw new InvalidOperationException("the consumer's handler failed");
        });

        await listener.StartAsync();
        try
        {
            // The observer takes the subscription, so it cannot be the resource of a using itself;
            // this declaration disposes it before the finally disposes the listener, as before.
            using var eventsRelease = Disposable.Create(() => eventsSubscription?.Dispose());

            // Act
            using var peer = await ConnectClientAsync(seam.Port, app: "first").WaitAsync(SignalTimeout);
            var served = await seam.NextHandedOverAsync();
            await served.Released.WaitAsync(SignalTimeout);
            var peerSawItClosed = await SeesItsConnectionClosedAsync(peer);

            // Assert
            announced.Should().NotBeNull("the handler announced the connection before the observer threw");
            using (new AssertionScope())
            {
                listener.ActiveConnectionCount.Should().Be(
                    0,
                    "a connection whose handler already ended and closed its socket is not active");
                listener.GetByApplication("first").Should().BeEmpty(
                    "a lookup returns the connections the listener serves, and it serves this one no more");
                announced!.IsConnected.Should().BeFalse(
                    "the connection the observer was told about has been closed");
                eventsCompleted.Task.IsCompleted.Should().BeTrue(
                    "a released connection completes its event stream, so no subscriber waits on events " +
                    "that will never come");
                peerSawItClosed.Should().BeTrue("the far end sees its connection closed");
                logger.Entries.Should().ContainSingle(
                    entry => entry.Level == LogLevel.Error
                        && entry.EventName == "ConnectionError"
                        && entry.ExceptionType == nameof(InvalidOperationException),
                    "the observer's failure is reported once, as that connection's error; the listener " +
                    "logged [{0}]",
                    Describe(logger));
            }
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldReportTheFailureWithinABound_WhenTheFarEndDoesNotRead()
    {
        // Arrange — a raw peer that sends its upgrade request and never reads again, so it never answers
        // a close frame. A graceful close sends one and then waits for the answer with no bound, so a
        // release that closed gracefully would hold the handler for as long as this peer stays
        // connected. The bound starts once the observer is being told, after the handshake, so it
        // measures the release and the report alone.
        var logger = new CapturingLogger();
        var (listener, _) = CreateListener(logger: logger);
        using var seam = new LoopbackAcceptSeam();
        listener.AcceptOverride = seam.AcceptAsync;
        var announcing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = listener.OnConnectionAccepted.Subscribe(_ =>
        {
            announcing.TrySetResult();
            throw new InvalidOperationException("the consumer's handler failed");
        });

        await listener.StartAsync();
        try
        {
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, seam.Port);
            await peer.GetStream().WriteAsync(UpgradeRequest(app: "first"));
            var served = await seam.NextHandedOverAsync();
            await announcing.Task.WaitAsync(SignalTimeout);

            // Act
            var release = () => served.Released;

            // Assert — the handler's last step is the release, so once it has returned, what it
            // reports is already logged
            await release.Should().CompleteWithinAsync(
                ReleaseBound,
                "a failed announcement is released without waiting for the far end to answer a close, " +
                "and this far end never answers one");
            logger.Entries.Should().ContainSingle(
                entry => entry.Level == LogLevel.Error
                    && entry.EventName == "ConnectionError"
                    && entry.ExceptionType == nameof(InvalidOperationException),
                "the observer's failure is reported once, as that connection's error");
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldAnnounceTheNextConnection_WhenTheObserverThrewForThePreviousOne()
    {
        // Arrange — the observer throws for the first connection only. The second connection is made
        // once the handler has released the first, so by then the observer has already thrown.
        var logger = new CapturingLogger();
        var (listener, _) = CreateListener(logger: logger);
        using var seam = new LoopbackAcceptSeam();
        listener.AcceptOverride = seam.AcceptAsync;
        var told = 0;
        var secondAnnounced = new TaskCompletionSource<AriOutboundConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = listener.OnConnectionAccepted.Subscribe(connection =>
        {
            if (Interlocked.Increment(ref told) == 1)
                throw new InvalidOperationException("the consumer's handler failed");

            secondAnnounced.TrySetResult(connection);
        });

        await listener.StartAsync();
        try
        {
            using var first = await ConnectClientAsync(seam.Port, app: "first").WaitAsync(SignalTimeout);
            var firstServed = await seam.NextHandedOverAsync();
            await firstServed.Released.WaitAsync(SignalTimeout);

            // Act — a second connection, which sends one event once it has been announced
            var connecting = ConnectClientAsync(seam.Port, app: "second");
            var announcement = () => secondAnnounced.Task;
            var connection = (await announcement.Should().CompleteWithinAsync(
                SignalTimeout,
                "one observer throwing costs only the connection it threw on, so the listener goes on " +
                "accepting and announcing")).Which;
            using var second = await connecting.WaitAsync(SignalTimeout);
            var received = new TaskCompletionSource<AriEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var events = connection.Events.Subscribe(evt => received.TrySetResult(evt));
            const string json = """{"type":"StasisStart","application":"second","timestamp":"2026-04-19T10:00:00Z","args":[],"channel":{"id":"ch-2","name":"PJSIP/test","state":"Up"}}""";
            await second.SendAsync(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);
            var delivered = await received.Task.WaitAsync(SignalTimeout);

            // Assert
            using (new AssertionScope())
            {
                connection.ApplicationName.Should().Be("second", "the observer is told about the next connection");
                listener.GetByApplication("second").Should().ContainSingle(
                    tracked => ReferenceEquals(tracked, connection),
                    "the next connection is counted");
                connection.IsConnected.Should().BeTrue("and it is being served");
                delivered.Should().BeOfType<Verbara.Sdk.Ari.Events.StasisStartEvent>(
                    "its events reach its subscribers as they did before");
                logger.Entries.Should().ContainSingle(
                    entry => entry.EventName == "ConnectionError",
                    "only the announcement the observer threw on failed");
            }
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task HandleConnectionAsync_ShouldLetAnObserverFindTheConnectionItIsToldAbout()
    {
        // Arrange — an observer that looks the connection up while it is being told about it. Releasing
        // a failed announcement must not be bought by announcing before tracking.
        var (listener, _) = CreateListener();
        var lookedUp = new TaskCompletionSource<(AriOutboundConnection Told, AriOutboundConnection[] Found)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = listener.OnConnectionAccepted.Subscribe(connection =>
            lookedUp.TrySetResult((connection, listener.GetByApplication(connection.ApplicationName).ToArray())));

        await listener.StartAsync();
        try
        {
            // Act
            using var peer = await ConnectClientAsync(listener.BoundPort, app: "myapp").WaitAsync(SignalTimeout);
            var (told, found) = await lookedUp.Task.WaitAsync(SignalTimeout);

            // Assert
            found.Should().ContainSingle(
                tracked => ReferenceEquals(tracked, told),
                "the listener tracks a connection before it announces it, so an observer can find the " +
                "connection it is being told about");
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    // ------------------------------------------------------- the constructor consumers actually use

    [Fact]
    public async Task PublicConstructor_ShouldBindAndAccept_WhenGivenOnlyOptionsAndALogger()
    {
        // The two-argument constructor is the one a consumer resolves from DI; every other test in
        // this file reaches past it for the internal three-argument overload that takes a clock. So
        // until this test nothing exercised the delegation, and nothing said that a listener built
        // the way consumers build it binds and accepts at all. A real handshake is the assertion
        // because completing one is the whole of what the delegated construction has to produce.
        var options = new AriOutboundListenerOptions
        {
            ListenAddress = "127.0.0.1",
            Port = 0, // ephemeral
            Path = "/ari/events",
            ConnectionIdleTimeout = TimeSpan.FromSeconds(30)
        };

        await using var listener = new AriOutboundListener(
            Options.Create(options),
            NullLogger<AriOutboundListener>.Instance);

        await listener.StartAsync();

        using var client = await ConnectClientAsync(listener.BoundPort);

        client.State.Should().Be(
            WebSocketState.Open,
            "the delegated construction produced a listener that completes the upgrade");
        (await WaitForAsync(() => listener.ActiveConnectionCount == 1, SignalTimeout))
            .Should().BeTrue("and one that tracks what it accepted");
        listener.IsRunning.Should().BeTrue();
    }

    // ------------------------------------------------------------------------------------ helpers

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
    /// How long releasing a failed announcement may take. Far shorter than a graceful close's wait, which
    /// against a far end that never answers has no end at all.
    /// </summary>
    private static readonly TimeSpan ReleaseBound = TimeSpan.FromSeconds(2);

    /// <summary>A WebSocket upgrade request for this listener's path, as a raw peer sends it.</summary>
    private static byte[] UpgradeRequest(string app) => Encoding.ASCII.GetBytes(
        $"GET /ari/events?app={app} HTTP/1.1\r\n" +
        "Host: 127.0.0.1\r\n" +
        "Upgrade: websocket\r\n" +
        "Connection: Upgrade\r\n" +
        "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
        "Sec-WebSocket-Version: 13\r\n\r\n");

    /// <summary>
    /// Whether <paramref name="peer"/>'s next receive finds its connection closed, by a close frame or by
    /// the transport ending without one. The receive ends on that close, not on a clock.
    /// </summary>
    private static async Task<bool> SeesItsConnectionClosedAsync(ClientWebSocket peer)
    {
        try
        {
            var result = await peer.ReceiveAsync(new ArraySegment<byte>(new byte[16]), CancellationToken.None)
                .WaitAsync(SignalTimeout);
            return result.MessageType == WebSocketMessageType.Close;
        }
        catch (WebSocketException)
        {
            // The transport ended without a close handshake, which is how an aborted connection ends
            return true;
        }
    }

    /// <summary>The next timer created on <paramref name="time"/>, as soon as it exists.</summary>
    private static Task<FakeTimeProvider.FakeTimer> NextTimerAsync(FakeTimeProvider time) =>
        time.TimersCreated.ReadAsync().AsTask().WaitAsync(SignalTimeout);

    /// <summary>What the listener logged so far, for a failure message.</summary>
    private static string Describe(CapturingLogger logger) =>
        string.Join(", ", logger.Entries.Select(entry => $"{entry.Level}:{entry.EventName}({entry.ExceptionType})"));

    /// <summary>
    /// The listener's accept, served from a loopback listener of the test's own. Each connection a peer
    /// makes to <see cref="Port"/> is handed to the listener in a <see cref="ReleaseSignallingClient"/>,
    /// and to the test as well, so the test can wait for the handler to release that connection.
    /// </summary>
    private sealed class LoopbackAcceptSeam : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Channel<ReleaseSignallingClient> _handedOver = Channel.CreateUnbounded<ReleaseSignallingClient>();
        private readonly ConcurrentQueue<ReleaseSignallingClient> _all = new();

        public LoopbackAcceptSeam() => _listener.Start();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async ValueTask<TcpClient> AcceptAsync(CancellationToken token)
        {
            var client = new ReleaseSignallingClient(await _listener.AcceptSocketAsync(token));
            _all.Enqueue(client);
            _handedOver.Writer.TryWrite(client);
            return client;
        }

        /// <summary>The next connection handed to the listener, as soon as it has been.</summary>
        public Task<ReleaseSignallingClient> NextHandedOverAsync() =>
            _handedOver.Reader.ReadAsync().AsTask().WaitAsync(SignalTimeout);

        public void Dispose()
        {
            _listener.Dispose();
            while (_all.TryDequeue(out var client))
                client.Dispose();
        }
    }

    /// <summary>A listener log entry, reduced to what these tests assert on.</summary>
    private sealed record LogEntry(LogLevel Level, string? EventName, string? ExceptionType);

    /// <summary>Records every entry the listener logs.</summary>
    private sealed class CapturingLogger : ILogger<AriOutboundListener>
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

    // ------------------------------------------------------ second disposal and failed start

    [Fact]
    public async Task DisposeAsync_ShouldNotThrowAndCompleteAnnouncementsOnce_WhenAStartedListenerIsDisposedTwice()
    {
        // Arrange
        var (listener, _) = CreateListener();
        await listener.StartAsync();
        var completions = 0;
        using var subscription = listener.OnConnectionAccepted.Subscribe(static _ => { }, () => Interlocked.Increment(ref completions));
        await listener.DisposeAsync();

        // Act
        var second = await Record.ExceptionAsync(async () => await listener.DisposeAsync());

        // Assert
        using (new AssertionScope())
        {
            second.Should().BeNull("every disposal after the first is a no-op");
            Volatile.Read(ref completions).Should().Be(1, "the announcements complete once");
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldNotThrow_WhenANeverStartedListenerIsDisposedTwice()
    {
        // Arrange
        var (listener, _) = CreateListener();
        await listener.DisposeAsync();

        // Act
        var second = await Record.ExceptionAsync(async () => await listener.DisposeAsync());

        // Assert
        second.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_ShouldLeaveTheListenerStoppedAndStartable_WhenItsPortIsBusy()
    {
        // Arrange — another listener holds the port
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        var port = ((IPEndPoint)busy.LocalEndpoint).Port;
        var (listener, _) = CreateListener(o => o.Port = port);
        Exception? first;
        Exception? second;
        bool runningAfterFailure;
        bool accepts;
        try
        {
            // Act
            first = await Record.ExceptionAsync(async () => await listener.StartAsync());
            runningAfterFailure = listener.IsRunning;
            busy.Stop();
            second = await Record.ExceptionAsync(async () => await listener.StartAsync());
            accepts = await AcceptsAsync(port);
        }
        finally
        {
            busy.Dispose();
            await listener.DisposeAsync();
        }

        // Assert
        using (new AssertionScope())
        {
            first.Should().BeOfType<SocketException>("the bind failed");
            runningAfterFailure.Should().BeFalse("a start whose bind failed leaves the listener stopped");
            second.Should().BeNull();
            accepts.Should().BeTrue("the next start, once the port is free, binds and accepts");
        }
    }

    [Fact]
    public async Task StartAsync_ShouldLeaveTheListenerStoppedAndStartable_WhenItsListenAddressIsInvalid()
    {
        // Arrange
        var (listener, options) = CreateListener(o => o.ListenAddress = "not-an-address");
        Exception? first;
        Exception? second;
        bool runningAfterFailure;
        bool accepts;
        try
        {
            // Act
            first = await Record.ExceptionAsync(async () => await listener.StartAsync());
            runningAfterFailure = listener.IsRunning;
            options.ListenAddress = "127.0.0.1";
            second = await Record.ExceptionAsync(async () => await listener.StartAsync());
            accepts = listener.IsRunning && await AcceptsAsync(listener.BoundPort);
        }
        finally
        {
            await listener.DisposeAsync();
        }

        // Assert
        using (new AssertionScope())
        {
            first.Should().BeOfType<FormatException>();
            runningAfterFailure.Should().BeFalse("a start that threw leaves the listener stopped");
            second.Should().BeNull();
            accepts.Should().BeTrue("a start with a valid address binds and accepts");
        }
    }

    private static async Task<bool> AcceptsAsync(int port)
    {
        if (port == 0)
            return false;

        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SignalTimeout);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

public sealed class AriOutboundListenerOptionsValidatorTests
{
    [Fact]
    public void Validate_ShouldSucceed_ForDefaults()
    {
        var validator = new AriOutboundListenerOptionsValidator();
        var result = validator.Validate(null, new AriOutboundListenerOptions());
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Validate_ShouldFail_WhenPortOutOfRange(int port)
    {
        var validator = new AriOutboundListenerOptionsValidator();
        var opts = new AriOutboundListenerOptions { Port = port };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenPathDoesNotStartWithSlash()
    {
        var validator = new AriOutboundListenerOptionsValidator();
        var opts = new AriOutboundListenerOptions { Path = "ari/events" };
        var result = validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
    }
}
