using Verbara.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.DependencyInjection;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Bridge;

/// <summary>
/// The bridge's disposal as its container drives it. <c>AddOpenAiRealtimeBridge</c> registers the
/// bridge as a singleton and again as the <see cref="ISessionHandler"/> resolved from that singleton,
/// so the container tracks the one instance under both registrations and disposes it twice. Every
/// disposal after the first has to be ignored, or the second one completes the event stream over the
/// subject the first one released, throws, and cuts the container's release short.
/// </summary>
/// <remarks>
/// No test here starts a session, so the bridge never opens a connection: it connects only inside
/// <see cref="OpenAiRealtimeBridge.HandleSessionAsync"/>, and a dummy API key satisfies the options
/// validator.
/// </remarks>
public sealed class OpenAiRealtimeBridgeLifetimeTests
{
    /// <summary>Upper bound on any single wait. Reaching it is a failure, never a pace.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DisposeAsync_ShouldCompleteWithoutThrowing_WhenCalledASecondTime()
    {
        var bridge = new OpenAiRealtimeBridge(
            Options.Create(new OpenAiRealtimeOptions { ApiKey = "test-key" }),
            new RealtimeFunctionRegistry([]),
            NullLogger<OpenAiRealtimeBridge>.Instance);
        var observer = new CompletionCounter();
        using var subscription = bridge.Events.Subscribe(observer);
        await bridge.DisposeAsync();

        var second = async () => await bridge.DisposeAsync();

        await second.Should().NotThrowAsync(
            "IAsyncDisposable requires every call after the first to be ignored, and the SDK's own " +
            "registration makes the container dispose the bridge twice");
        observer.Completions.Should().Be(
            1,
            "the first disposal completes the event stream, and the second releases nothing, so a " +
            "subscriber sees the stream end exactly once");
    }

    [Fact]
    public async Task ServiceProviderDisposeAsync_ShouldCompleteWithoutThrowing_WhenAddOpenAiRealtimeBridgeRegisteredTheBridge()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAudioSocketServer(o => { o.ListenAddress = "127.0.0.1"; o.Port = 0; });
        services.AddOpenAiRealtimeBridge(o => o.ApiKey = "test-key");
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISessionHandler>().Should().BeSameAs(
            provider.GetRequiredService<OpenAiRealtimeBridge>(),
            "the session-handler registration forwards the singleton, which is why the container " +
            "tracks the one bridge under two registrations");

        var dispose = async () => await provider.DisposeAsync();

        await dispose.Should().NotThrowAsync(
            "the container disposes the bridge once per registration, and the second disposal is " +
            "ignored");
    }

    [Fact]
    public async Task RunAsync_ShouldReturnNormally_WhenTheApplicationRequestsAStopOnARealtimeHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAudioSocketServer(o => { o.ListenAddress = "127.0.0.1"; o.Port = 0; });
        builder.Services.AddOpenAiRealtimeBridge(o => o.ApiKey = "test-key");
        var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        // The only trigger: once the host has started every service, the application asks it to stop.
        lifetime.ApplicationStarted.Register(lifetime.StopApplication);

        var run = () => host.RunAsync().WaitAsync(SignalTimeout);

        await run.Should().NotThrowAsync(
            "RunAsync stops the host and then disposes it, and the container's second disposal of " +
            "the bridge is ignored");
    }

    /// <summary>Counts the completions of the event stream it is subscribed to.</summary>
    private sealed class CompletionCounter : IObserver<RealtimeEvent>
    {
        private int _completions;

        public int Completions => Volatile.Read(ref _completions);

        public void OnCompleted() => Interlocked.Increment(ref _completions);

        public void OnError(Exception error)
        {
        }

        public void OnNext(RealtimeEvent value)
        {
        }
    }
}
