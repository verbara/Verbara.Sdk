using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.VoiceAi.AudioSocket.DependencyInjection;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Tests;

/// <summary>
/// The server's lifetime as its container and its host drive it. <c>AddAudioSocketServer</c>
/// registers the server as a singleton and again as a hosted service resolved from that singleton,
/// so the container tracks the one instance under both registrations and disposes it twice. Every
/// disposal after the first has to be ignored, or the second one throws and cuts the container's
/// release short.
/// </summary>
public sealed class AudioSocketServerLifetimeTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

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
