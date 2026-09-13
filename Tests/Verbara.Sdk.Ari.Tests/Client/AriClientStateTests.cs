using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
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
