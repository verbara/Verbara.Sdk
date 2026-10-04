using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Tests;

/// <summary>
/// Which <see cref="AudioSocketOptions.ConnectionTimeout"/> the server accepts: it refuses at
/// construction exactly the values that fail every connection, and serves with every other value as
/// it always has.
/// </summary>
public sealed class AudioSocketServerConnectionTimeoutTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private const long Ms = TimeSpan.TicksPerMillisecond;

    /// <summary>The longest timeout a <see cref="CancellationTokenSource"/> on a <see cref="TimeProvider"/> accepts, in milliseconds.</summary>
    private const long LongestServedMs = 4_294_967_294L;

    [Theory]
    [InlineData(0L)]
    [InlineData(-2L * Ms)]
    [InlineData(-5_000L * Ms)]
    [InlineData((LongestServedMs + 1) * Ms)]
    [InlineData(long.MaxValue)]
    public void Constructor_ShouldThrowNamingConnectionTimeout_WhenTheTimeoutFailsEveryConnection(long ticks)
    {
        // Each of these refused every identified peer before the check: zero with a Warning per
        // connection, the others with an ArgumentOutOfRangeException per connection, logged as an Error
        var options = new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0, ConnectionTimeout = TimeSpan.FromTicks(ticks) };

        var construct = () => new AudioSocketServer(options, NullLogger<AudioSocketServer>.Instance);

        construct.Should().Throw<ArgumentOutOfRangeException>(
            $"a ConnectionTimeout of {TimeSpan.FromTicks(ticks)} fails every connection")
            .Which.ParamName.Should().Be(nameof(AudioSocketOptions.ConnectionTimeout));
    }

    [Theory]
    [InlineData(30_000L * Ms)]
    [InlineData(200L * Ms)]
    [InlineData(30L * 24 * 60 * 60 * 1000 * Ms)]
    [InlineData(LongestServedMs * Ms)]
    [InlineData(-1L * Ms)]
    public async Task Constructor_ShouldAcceptAndServe_WhenTheTimeoutWorksToday(long ticks)
    {
        // A pin, green before and after: the range rejects nothing that works. -1 ms is
        // Timeout.InfiniteTimeSpan, which means no deadline
        var timeout = TimeSpan.FromTicks(ticks);
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0, ConnectionTimeout = timeout },
            NullLogger<AudioSocketServer>.Instance);
        await server.StartAsync(CancellationToken.None);
        var probe = SessionStartedProbe.SubscribeTo(server);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();

        var peers = new List<AudioSocketClient>();
        try
        {
            foreach (var id in ids)
                peers.Add(await AudioSocketPeer.ConnectAsync(server, id));
            var served = await Task.WhenAll(ids.Select(id => probe.Started(id))).WaitAsync(SignalTimeout);

            served.Should().HaveCount(5, $"a ConnectionTimeout of {timeout} serves every identified peer");
        }
        finally
        {
            foreach (var peer in peers)
                await peer.DisposeAsync();
        }
    }

    [Fact]
    public async Task InfiniteTimeout_ShouldKeepASilentPeerOpenWithoutAWarning_UntilTheServerStops()
    {
        // A pin, green before and after
        var logger = new LevelLogger();
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0, ConnectionTimeout = Timeout.InfiniteTimeSpan },
            logger);
        await server.StartAsync(CancellationToken.None);
        using var silent = new TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        var read = silent.GetStream().ReadAsync(new byte[1]).AsTask();

        var closedBeforeStop = await Signalled(read, TimeSpan.FromSeconds(1));
        await server.StopAsync(CancellationToken.None);
        var closedByStop = await Signalled(read, SignalTimeout);

        using (new AssertionScope())
        {
            closedBeforeStop.Should().BeFalse("no deadline closes a silent peer");
            closedByStop.Should().BeTrue("the stop closes it");
            logger.Levels.Should().NotContain(level => level >= LogLevel.Warning);
        }
    }

    /// <summary>Whether the server closed the peer within <paramref name="window"/>.</summary>
    private static async Task<bool> Signalled(Task task, TimeSpan window)
    {
        try
        {
            await task.WaitAsync(window);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Records the level of every entry the server writes.</summary>
    private sealed class LevelLogger : ILogger<AudioSocketServer>
    {
        private readonly ConcurrentQueue<LogLevel> _levels = new();

        public IReadOnlyCollection<LogLevel> Levels => _levels.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _levels.Enqueue(logLevel);
    }
}
