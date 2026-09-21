using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Diagnostics;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ari.Tests.Client;

public sealed class AriClientStateTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private static AriClient CreateClient()
    {
        var options = Options.Create(new AriClientOptions
        {
            BaseUrl = "http://localhost:8088",
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app"
        });
        return new AriClient(options, NullLogger<AriClient>.Instance);
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(10); // fence-allow: LOOP-DRIVER — AriClient exposes State but no state-change signal; bounded by the caller's timeout
        }
        return predicate();
    }

    [Fact]
    public async Task DisposeAsync_ShouldStopReconnecting_WhenDisposedWhileReconnecting()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();

        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var sut = new AriClient(Options.Create(new AriClientOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app",
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(500)
        }), NullLogger<AriClient>.Instance);

        // Accept the events socket, complete the upgrade, then drop it: the client falls into its backoff.
        var firstConnection = Task.Run(async () =>
        {
            using var accepted = await server.AcceptTcpClientAsync();
            var stream = accepted.GetStream();
            var (wsKey, _) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, CancellationToken.None);
            await WebSocketAudioServer.SendUpgradeResponseAsync(stream, wsKey!, CancellationToken.None);
        });
        await sut.ConnectAsync();
        await firstConnection;

        (await WaitForAsync(() => sut.State == AriConnectionState.Reconnecting, TimeSpan.FromSeconds(5)))
            .Should().BeTrue("dropping the events socket starts the reconnect backoff");

        await sut.DisposeAsync();

        // Several backoff periods later, a disposed client must not have dialled back in.
        using var window = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var redial = async () =>
        {
            using var late = await server.AcceptTcpClientAsync(window.Token);
        };
        await redial.Should().ThrowAsync<OperationCanceledException>("a disposed client must not reconnect");
    }

    [Fact]
    public async Task ConnectAsync_ShouldFaultAndStopReconnecting_WhenReconnectIsAnsweredUnauthorized()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        using var serverStop = new CancellationTokenSource();

        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var logger = new RecordingLogger();
        var sut = new AriClient(Options.Create(new AriClientOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app",
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(20),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(20),
            // Backstop one dial past the 401: a client that retried it still ends, through ReconnectGaveUp.
            MaxReconnectAttempts = 4
        }), logger);

        // Accept the events socket, complete the upgrade, then drop it: the client starts reconnecting.
        var firstConnection = Task.Run(async () =>
        {
            using var accepted = await server.AcceptTcpClientAsync();
            var stream = accepted.GetStream();
            var (wsKey, _) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, CancellationToken.None);
            await WebSocketAudioServer.SendUpgradeResponseAsync(stream, wsKey!, CancellationToken.None);
        });
        await sut.ConnectAsync();
        await firstConnection;

        // Reconnect dials are refused with 503, then 403, both worth retrying, and from then on with 401,
        // which is not. Each dial is counted before it is answered, so the count is final once the loop ends.
        string[] refusals = ["503 Service Unavailable", "403 Forbidden", "401 Unauthorized"];
        var dials = 0;
        var reconnects = Task.Run(async () =>
        {
            while (!serverStop.IsCancellationRequested)
            {
                using var accepted = await server.AcceptTcpClientAsync(serverStop.Token);
                var dial = Interlocked.Increment(ref dials);
                await RefuseUpgradeAsync(accepted.GetStream(), refusals[Math.Min(dial, refusals.Length) - 1], serverStop.Token);
            }
        });

        try
        {
            // The loop ends on its own, at the 401 or at the backstop; nothing here cancels it first.
            await sut.EventLoop!.WaitAsync(WaitLimit);

            using (new AssertionScope())
            {
                Volatile.Read(ref dials).Should().Be(3, "the 503 and the 403 are dialled again and the 401 is not");
                logger.Entries.Should().NotContain(
                    e => e.EventId.Name == "ReconnectGaveUp", "the loop stops at the 401, not at MaxReconnectAttempts");
                sut.State.Should().Be(AriConnectionState.Faulted, "credentials refused with 401 are not retried");
                logger.Entries
                    .Where(e => e.EventId.Name == "Reconnecting")
                    .Select(e => e.Properties.Single(p => p.Key == "Attempt").Value)
                    .Should().Equal([1, 2, 3], "the 503 and the 403 are retried and the 401 is not");
            }

            var rejection = logger.Entries.Should().ContainSingle(e => e.EventId.Name == "ReconnectRejected").Subject;
            rejection.Level.Should().Be(LogLevel.Error);
            rejection.Properties.Should().ContainSingle(p => p.Key == "StatusCode").Which.Value.Should().Be(401);
            rejection.Exception.Should().BeOfType<WebSocketException>();
        }
        finally
        {
            await sut.DisposeAsync();
            await serverStop.CancelAsync();
            await reconnects.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Theory]
    [InlineData("401 Unauthorized")]
    [InlineData("503 Service Unavailable")]
    public async Task ConnectAsync_ShouldLeaveStateFaulted_WhenTheUpgradeIsRefused(string status)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();

        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        await using var sut = new AriClient(Options.Create(new AriClientOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app"
        }), NullLogger<AriClient>.Instance);

        // The single dial the client makes is answered with a refusal instead of 101.
        var refusal = Task.Run(async () =>
        {
            using var accepted = await server.AcceptTcpClientAsync();
            await RefuseUpgradeAsync(accepted.GetStream(), status, CancellationToken.None);
        });

        var connect = async () => await sut.ConnectAsync();
        await connect.Should().ThrowAsync<WebSocketException>("a refused upgrade never becomes a connection");
        await refusal;

        var health = await new AriHealthCheck(sut).CheckHealthAsync(new HealthCheckContext());

        using (new AssertionScope())
        {
            sut.State.Should().Be(
                AriConnectionState.Faulted,
                "an attempt that ended without a connection is over, and nothing dials again");
            sut.IsConnected.Should().BeFalse();
            health.Status.Should().Be(HealthStatus.Unhealthy);
            health.Description.Should().Contain("Faulted", "the health message names the terminal state");
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldLeaveStateFaulted_WhenTheDialIsRefused()
    {
        // Bind on port 0 to take a port nothing else holds, then give it up: a dial there is
        // refused by the loopback stack at once, with no listener and no timing window.
        int port;
        using (var vacated = new TcpListener(IPAddress.Loopback, 0))
        {
            vacated.Start();
            port = ((IPEndPoint)vacated.LocalEndpoint).Port;
        }

        await using var sut = new AriClient(Options.Create(new AriClientOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app"
        }), NullLogger<AriClient>.Instance);

        var connect = async () => await sut.ConnectAsync();
        await connect.Should().ThrowAsync<WebSocketException>("a refused dial never becomes a connection");

        using (new AssertionScope())
        {
            sut.State.Should().Be(
                AriConnectionState.Faulted, "a connection the stack refused is an ending, not an attempt in progress");
            sut.IsConnected.Should().BeFalse();
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldLeaveStateDisconnected_WhenTheCallerCancelsTheAttempt()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();

        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var logger = new RecordingLogger();
        await using var sut = new AriClient(Options.Create(new AriClientOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app"
        }), logger);

        // The dial is accepted and then left unanswered, so the only thing that can end the attempt
        // is the token the caller handed in. The accept is the signal the test waits on, not a clock.
        var accepted = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var silence = Task.Run(async () =>
        {
            using var held = await server.AcceptTcpClientAsync();
            accepted.SetResult();
            await release.Task;
        });

        try
        {
            using var caller = new CancellationTokenSource();
            var connect = sut.ConnectAsync(caller.Token).AsTask();
            await accepted.Task.WaitAsync(WaitLimit);
            await caller.CancelAsync();

            var withdrawn = async () => await connect;
            await withdrawn.Should().ThrowAsync<OperationCanceledException>("the caller withdrew the attempt");

            using (new AssertionScope())
            {
                sut.State.Should().Be(
                    AriConnectionState.Disconnected, "an ending the caller asked for is not a fault");
                sut.IsConnected.Should().BeFalse();
                logger.Entries.Should().NotContain(
                    e => e.Level == LogLevel.Error, "a withdrawal the caller asked for is not an error");
            }
        }
        finally
        {
            release.TrySetResult();
            await silence.WaitAsync(WaitLimit).ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldLeaveStateConnected_WhenTheUpgradeSucceeds()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();

        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var sut = new AriClient(Options.Create(new AriClientOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app"
        }), NullLogger<AriClient>.Instance);

        // The upgrade is answered and the socket is then held open, so the events loop stays in its
        // receive for the whole of the assertions instead of racing them into the reconnect backoff.
        var release = new TaskCompletionSource();
        var upgrade = Task.Run(async () =>
        {
            using var accepted = await server.AcceptTcpClientAsync();
            var stream = accepted.GetStream();
            var (wsKey, _) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, CancellationToken.None);
            await WebSocketAudioServer.SendUpgradeResponseAsync(stream, wsKey!, CancellationToken.None);
            await release.Task;
        });

        try
        {
            await sut.ConnectAsync();

            using (new AssertionScope())
            {
                sut.State.Should().Be(AriConnectionState.Connected, "the dial succeeded");
                sut.IsConnected.Should().BeTrue();
                sut.EventLoop.Should().NotBeNull("a successful connect starts the events loop");
                sut.EventLoop!.IsCompleted.Should().BeFalse("the events loop runs while the socket is open");
            }
        }
        finally
        {
            release.TrySetResult();
            await upgrade.WaitAsync(WaitLimit).ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            await sut.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldNotDialAgain_WhenTheFirstAttemptFailed()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();

        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        // AutoReconnect stays at its default true and the backoff is short, so a client that did
        // start a reconnect loop after a failed first dial would be back well inside the window below.
        await using var sut = new AriClient(Options.Create(new AriClientOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app",
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(20),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(20)
        }), NullLogger<AriClient>.Instance);

        // 503 is a refusal the reconnect loop retries, and the listener stays up: a second dial
        // would be accepted here, which is what makes its absence evidence rather than a dead port.
        var refusal = Task.Run(async () =>
        {
            using var accepted = await server.AcceptTcpClientAsync();
            await RefuseUpgradeAsync(accepted.GetStream(), "503 Service Unavailable", CancellationToken.None);
        });

        var connect = async () => await sut.ConnectAsync();
        await connect.Should().ThrowAsync<WebSocketException>("a refused upgrade never becomes a connection");
        await refusal;

        using var window = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var redial = async () =>
        {
            using var late = await server.AcceptTcpClientAsync(window.Token);
        };
        await redial.Should().ThrowAsync<OperationCanceledException>("a failed first connect starts no reconnect loop");
        sut.EventLoop.Should().BeNull("the events loop is started only after a dial that succeeded");
    }

    [Fact]
    public async Task ConnectAsync_ShouldConnect_WhenRetriedAfterAFailedAttempt()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();

        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var sut = new AriClient(Options.Create(new AriClientOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}",
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app"
        }), NullLogger<AriClient>.Instance);

        // One listener, two dials: the first refused, the second answered 101 and then held open.
        var release = new TaskCompletionSource();
        var dials = Task.Run(async () =>
        {
            using (var refused = await server.AcceptTcpClientAsync())
            {
                await RefuseUpgradeAsync(refused.GetStream(), "503 Service Unavailable", CancellationToken.None);
            }

            using var answered = await server.AcceptTcpClientAsync();
            var stream = answered.GetStream();
            var (wsKey, _) = await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, CancellationToken.None);
            await WebSocketAudioServer.SendUpgradeResponseAsync(stream, wsKey!, CancellationToken.None);
            await release.Task;
        });

        try
        {
            var first = async () => await sut.ConnectAsync();
            await first.Should().ThrowAsync<WebSocketException>("the first dial is refused");

            await sut.ConnectAsync();

            using (new AssertionScope())
            {
                sut.State.Should().Be(
                    AriConnectionState.Connected,
                    "the state a failed attempt leaves is a statement about that attempt, not a gate on the next one");
                sut.IsConnected.Should().BeTrue();
            }
        }
        finally
        {
            release.TrySetResult();
            await dials.WaitAsync(WaitLimit).ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            await sut.DisposeAsync();
        }
    }

    [Fact]
    public async Task State_ShouldBeInitial_WhenNewClientCreated()
    {
        await using var sut = CreateClient();

        sut.State.Should().Be(AriConnectionState.Initial);
    }

    [Fact]
    public async Task IsConnected_ShouldBeFalse_WhenNotConnected()
    {
        await using var sut = CreateClient();

        sut.IsConnected.Should().BeFalse();
    }

    /// <summary>
    /// Reads the upgrade request of an accepted dial and answers it with <paramref name="status"/>
    /// instead of 101 Switching Protocols.
    /// </summary>
    private static async Task RefuseUpgradeAsync(NetworkStream stream, string status, CancellationToken ct)
    {
        await WebSocketAudioServer.ReadUpgradeRequestAsync(stream, ct);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct);
    }

    /// <summary>
    /// Keeps what the client logged. <see cref="Entries"/> hands out a copy taken under the write lock.
    /// </summary>
    private sealed class RecordingLogger : ILogger<AriClient>
    {
        private readonly List<LogEntry> _entries = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_gate) return [.. _entries]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            IReadOnlyList<KeyValuePair<string, object?>> properties =
                state is IReadOnlyList<KeyValuePair<string, object?>> values ? [.. values] : [];
            lock (_gate) _entries.Add(new LogEntry(logLevel, eventId, properties, exception));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        IReadOnlyList<KeyValuePair<string, object?>> Properties,
        Exception? Exception);
}
