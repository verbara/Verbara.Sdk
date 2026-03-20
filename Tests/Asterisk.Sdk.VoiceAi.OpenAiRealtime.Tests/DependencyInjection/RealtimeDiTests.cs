using Asterisk.Sdk.VoiceAi;
using Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.DependencyInjection;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Asterisk.Sdk.VoiceAi.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.DependencyInjection;

public sealed class RealtimeDiTests : IAsyncDisposable
{
    private ServiceProvider? _provider;

    private ServiceProvider BuildProvider(Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // AudioSocketServer is a prerequisite for AddOpenAiRealtimeBridge.
        // Port = 0 means OS assigns a port; we never call StartAsync in DI tests.
        services.AddAudioSocketServer(o => o.Port = 0);

        services.AddOpenAiRealtimeBridge(o =>
        {
            o.ApiKey = "test-key";
            o.Model = "gpt-4o-realtime-preview";
        });

        extra?.Invoke(services);
        _provider = services.BuildServiceProvider();
        return _provider;
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            try { await _provider.DisposeAsync(); }
            catch (ObjectDisposedException) { /* Bridge subjects already disposed — idempotency gap */ }
        }
    }

    [Fact]
    public void ISessionHandler_ResolvesAs_OpenAiRealtimeBridge()
    {
        var sp = BuildProvider();
        var handler = sp.GetRequiredService<ISessionHandler>();
        handler.Should().BeOfType<OpenAiRealtimeBridge>();
    }

    [Fact]
    public void OpenAiRealtimeBridge_IsSingleton()
    {
        var sp = BuildProvider();
        var a = sp.GetRequiredService<OpenAiRealtimeBridge>();
        var b = sp.GetRequiredService<OpenAiRealtimeBridge>();
        a.Should().BeSameAs(b);
    }

    [Fact]
    public void VoiceAiSessionBroker_IsRegisteredAsHostedService()
    {
        var sp = BuildProvider();
        var hostedServices = sp.GetServices<IHostedService>();
        hostedServices.Should().ContainSingle(s => s is VoiceAiSessionBroker);
    }

    [Fact]
    public void AddFunction_RegistersMultipleHandlers()
    {
        var sp = BuildProvider(s =>
            s.AddFunction<TestFunctionA>()
             .AddFunction<TestFunctionB>());

        var handlers = sp.GetServices<IRealtimeFunctionHandler>().ToList();
        handlers.Should().HaveCount(2);
        handlers.Select(h => h.Name).Should().BeEquivalentTo(["func-a", "func-b"]);
    }

    private sealed class TestFunctionA : IRealtimeFunctionHandler
    {
        public string Name => "func-a";
        public string Description => "A";
        public string ParametersSchema => """{"type":"object","properties":{}}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => ValueTask.FromResult("{}");
    }

    private sealed class TestFunctionB : IRealtimeFunctionHandler
    {
        public string Name => "func-b";
        public string Description => "B";
        public string ParametersSchema => """{"type":"object","properties":{}}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => ValueTask.FromResult("{}");
    }
}
