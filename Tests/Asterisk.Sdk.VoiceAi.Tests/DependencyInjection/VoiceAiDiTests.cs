using Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Asterisk.Sdk.VoiceAi.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Pipeline;
using Asterisk.Sdk.VoiceAi.Testing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asterisk.Sdk.VoiceAi.Tests.DependencyInjection;

public class VoiceAiDiTests
{
    [Fact]
    public async Task AddVoiceAiPipeline_ShouldRegisterVoiceAiPipelineAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpeechRecognizer>(
            new FakeSpeechRecognizer().WithTranscript("test"));
        services.AddSingleton<SpeechSynthesizer>(
            new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20)));
        services.AddAudioSocketServer();
        services.AddVoiceAiPipeline<FakeConversationHandlerScoped>();

        await using var provider = services.BuildServiceProvider();

        var pipeline1 = provider.GetRequiredService<VoiceAiPipeline>();
        var pipeline2 = provider.GetRequiredService<VoiceAiPipeline>();
        pipeline1.Should().BeSameAs(pipeline2);
    }

    [Fact]
    public async Task AddVoiceAiPipeline_ShouldRegisterHandlerAsScoped()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpeechRecognizer>(
            new FakeSpeechRecognizer().WithTranscript("test"));
        services.AddSingleton<SpeechSynthesizer>(
            new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20)));
        services.AddAudioSocketServer();
        services.AddVoiceAiPipeline<FakeConversationHandlerScoped>();

        await using var provider = services.BuildServiceProvider();
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var h1 = scope1.ServiceProvider.GetRequiredService<IConversationHandler>();
        var h2 = scope2.ServiceProvider.GetRequiredService<IConversationHandler>();
        h1.Should().NotBeSameAs(h2);
    }

    [Fact]
    public async Task AddVoiceAiPipeline_ShouldRegisterSessionBrokerAsHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpeechRecognizer>(
            new FakeSpeechRecognizer().WithTranscript("test"));
        services.AddSingleton<SpeechSynthesizer>(
            new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20)));
        services.AddAudioSocketServer();
        services.AddVoiceAiPipeline<FakeConversationHandlerScoped>();

        await using var provider = services.BuildServiceProvider();

        var hostedServices = provider.GetServices<IHostedService>();
        hostedServices.Should().Contain(s => s is VoiceAiSessionBroker);
    }

    [Fact]
    public async Task AddVoiceAiPipeline_ShouldApplyOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpeechRecognizer>(
            new FakeSpeechRecognizer().WithTranscript("test"));
        services.AddSingleton<SpeechSynthesizer>(
            new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20)));
        services.AddAudioSocketServer();
        services.AddVoiceAiPipeline<FakeConversationHandlerScoped>(opts =>
            opts.EndOfUtteranceSilence = TimeSpan.FromMilliseconds(800));

        await using var provider = services.BuildServiceProvider();
        var opts = provider.GetRequiredService<IOptions<VoiceAiPipelineOptions>>().Value;
        opts.EndOfUtteranceSilence.Should().Be(TimeSpan.FromMilliseconds(800));
    }

    [Fact]
    public async Task AddVoiceAiPipeline_ShouldRegisterOptionsWithDefaults_WhenNoConfigureCallback()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpeechRecognizer>(
            new FakeSpeechRecognizer().WithTranscript("test"));
        services.AddSingleton<SpeechSynthesizer>(
            new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20)));
        services.AddAudioSocketServer();
        services.AddVoiceAiPipeline<FakeConversationHandlerScoped>();

        await using var provider = services.BuildServiceProvider();
        var opts = provider.GetRequiredService<IOptions<VoiceAiPipelineOptions>>().Value;
        opts.MaxHistoryTurns.Should().Be(20);
        opts.EndOfUtteranceSilence.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task AddVoiceAiPipeline_ShouldWireSessionBrokerToPipeline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpeechRecognizer>(
            new FakeSpeechRecognizer().WithTranscript("test"));
        services.AddSingleton<SpeechSynthesizer>(
            new FakeSpeechSynthesizer().WithSilence(TimeSpan.FromMilliseconds(20)));
        services.AddAudioSocketServer();
        services.AddVoiceAiPipeline<FakeConversationHandlerScoped>();

        await using var provider = services.BuildServiceProvider();
        var broker = provider.GetRequiredService<VoiceAiSessionBroker>();
        broker.Should().NotBeNull();
    }
}

file sealed class FakeConversationHandlerScoped : IConversationHandler
{
    public ValueTask<string> HandleAsync(string transcript, ConversationContext context, CancellationToken ct)
        => ValueTask.FromResult($"echo: {transcript}");
}
