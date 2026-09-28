using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.VoiceAi.AudioSocket.DependencyInjection;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Tests;

/// <summary>
/// The server's lifetime as its host and its container drive it.
/// <list type="bullet">
/// <item>The token the host hands <c>StartAsync</c> only means the start was aborted. The start binds
/// the listener whatever that token says, so the accept loop that serves the listener is handed off
/// unconditionally, and the server's own stop is what ends it.</item>
/// <item><c>AddAudioSocketServer</c> registers the server as a singleton and again as a hosted
/// service resolved from that singleton, so the container tracks the one instance under both
/// registrations and disposes it twice. Every disposal after the first has to be ignored, or the
/// second one throws and cuts the container's release short.</item>
/// </list>
/// </summary>
public sealed class AudioSocketServerLifetimeTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task StartAsync_ShouldStartASessionForAnIdentifiedPeer_WhenTheStartTokenIsAlreadyCancelled()
    {
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0 },
            NullLogger<AudioSocketServer>.Instance);

        // A host that aborts its start hands a token that is already cancelled. The start still
        // returns normally with the listener bound, so the listener must have a loop serving it.
        await server.StartAsync(new CancellationToken(canceled: true));
        server.BoundPort.Should().BePositive("the start binds its listener whatever its token says");
        var probe = SessionStartedProbe.SubscribeTo(server);
        var channelId = Guid.NewGuid();

        // The peer's connect and its UUID frame both land in the kernel's backlog whether or not the
        // server ever accepts, so only the server raising the session shows that the loop runs. A
        // timeout here is the failure: the accept loop was never started.
        await using var peer = await AudioSocketPeer.ConnectAsync(server, channelId);
        var session = await probe.Started(channelId).WaitAsync(SignalTimeout);

        session.ChannelId.Should().Be(channelId);
        server.ActiveSessionCount.Should().Be(
            1,
            "the accept loop took the connection, read its UUID frame and registered the session");
    }

    [Fact]
    public async Task StopAsync_ShouldEndTheAcceptLoopAndReleaseTheListener_WhenTheServerWasServing()
    {
        // The UUID timeout runs on a fake clock that never moves, so the connection below can be closed
        // only by the server's stop, never by a deadline.
        var connectionTimeout = TimeSpan.FromSeconds(30);
        var time = new FakeTimeProvider();
        await using var server = new AudioSocketServer(
            new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0, ConnectionTimeout = connectionTimeout },
            NullLogger<AudioSocketServer>.Instance,
            time);
        await server.StartAsync(CancellationToken.None);
        var port = server.BoundPort;

        // Serving: the accept loop takes a connection that has not identified itself and hands it on
        // with the loop's own token. The handler's first act is to arm the wait for the UUID frame, so
        // that timer is the signal that the handler is now waiting on the loop's token.
        using var waiting = new TcpClient();
        await waiting.ConnectAsync(IPAddress.Loopback, port);
        (await NextTimerAsync(time)).DueTime.Should().Be(
            connectionTimeout,
            "the first timer on this clock is the handler's wait for the UUID frame");

        await server.StopAsync(CancellationToken.None);

        (await ReadFromServerAsync(waiting)).Should().Be(
            0,
            "the stop cancels the accept loop's token, which ends the loop's pending accept and the " +
            "handler's wait alike, and the handler closes the connection it holds");
        var connectLate = async () =>
        {
            using var late = new TcpClient();
            await late.ConnectAsync(IPAddress.Loopback, port);
        };
        (await connectLate.Should().ThrowAsync<SocketException>(
                "the stop released the listener, so nothing is bound at its port to take a connection"))
            .Which.SocketErrorCode.Should().Be(SocketError.ConnectionRefused);
    }

    [Fact]
    public async Task DisposeAsync_ShouldCompleteWithoutThrowing_WhenCalledASecondTime()
    {
        var logger = new StopCountingLogger();
        var server = new AudioSocketServer(
            new AudioSocketOptions { ListenAddress = "127.0.0.1", Port = 0 },
            logger);
        await server.StartAsync(CancellationToken.None);
        await server.DisposeAsync();

        var second = async () => await server.DisposeAsync();

        await second.Should().NotThrowAsync(
            "IAsyncDisposable requires every call after the first to be ignored, and the SDK's own " +
            "registration makes the container dispose the server twice");
        logger.StopsLogged.Should().Be(
            1,
            "the second disposal releases nothing, so the server's stop ran once, from the first " +
            "disposal, rather than again over the source that disposal released");
    }

    [Fact]
    public async Task ServiceProviderDisposeAsync_ShouldCompleteAndReleaseEarlierSingletons_WhenAddAudioSocketServerRegisteredAStartedServer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<DisposalSentinel>();
        services.AddAudioSocketServer(o => { o.ListenAddress = "127.0.0.1"; o.Port = 0; });
        var provider = services.BuildServiceProvider();

        // Resolved first, so the container creates the sentinel before the server. It disposes in
        // reverse creation order, so both of the server's disposals come before the sentinel's.
        var sentinel = provider.GetRequiredService<DisposalSentinel>();
        var hosted = provider.GetServices<IHostedService>().ToList();
        hosted.Should().Contain(
            provider.GetRequiredService<AudioSocketServer>(),
            "the hosted-service registration forwards the singleton, which is why the container " +
            "tracks the one server under two registrations");
        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);
        foreach (var service in Enumerable.Reverse(hosted))
            await service.StopAsync(CancellationToken.None);

        var dispose = async () => await provider.DisposeAsync();

        await dispose.Should().NotThrowAsync(
            "the container disposes the server once per registration, and the second disposal is " +
            "ignored");
        sentinel.IsDisposed.Should().BeTrue(
            "a disposal that throws ends the container's release early, and the sentinel, created " +
            "before the server, is released after it");
    }

    [Fact]
    public async Task RunAsync_ShouldReturnNormally_WhenTheApplicationRequestsAStop()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAudioSocketServer(o => { o.ListenAddress = "127.0.0.1"; o.Port = 0; });
        var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        // The only trigger: once the host has started every service, the application asks it to stop.
        lifetime.ApplicationStarted.Register(lifetime.StopApplication);

        var run = () => host.RunAsync().WaitAsync(SignalTimeout);

        await run.Should().NotThrowAsync(
            "RunAsync stops the host and then disposes it, and the container's second disposal of " +
            "the server is ignored");
    }

    /// <summary>
    /// Reads one byte from the server's end of <paramref name="client"/>: 0 once the server has
    /// closed the connection.
    /// </summary>
    private static Task<int> ReadFromServerAsync(TcpClient client) =>
        client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(SignalTimeout);

    /// <summary>The next timer created on <paramref name="time"/>, whether it was created before or after this call.</summary>
    private static Task<FakeTimeProvider.FakeTimer> NextTimerAsync(FakeTimeProvider time) =>
        time.TimersCreated.ReadAsync().AsTask().WaitAsync(SignalTimeout);

    /// <summary>A singleton of the host's own that records whether the container released it.</summary>
    private sealed class DisposalSentinel : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    /// <summary>Counts the server's <c>ServerStopped</c> entries: one per stop that ran to its end.</summary>
    private sealed class StopCountingLogger : ILogger<AudioSocketServer>
    {
        private int _stopsLogged;

        public int StopsLogged => Volatile.Read(ref _stopsLogged);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == "ServerStopped")
                Interlocked.Increment(ref _stopsLogged);
        }
    }
}
