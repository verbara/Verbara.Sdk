using Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Asterisk.Sdk.VoiceAi.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Stt.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Tts.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VoiceAiAssemblyAiExample;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddAudioSocketServer(opt => opt.Port = 9092);

        services.AddAssemblyAi(opt =>
        {
            opt.ApiKey = ctx.Configuration["AssemblyAi:ApiKey"]
                ?? throw new InvalidOperationException("Missing AssemblyAi:ApiKey");
            opt.FormatTurns = 1;
        });

        services.AddAzureTtsSpeechSynthesizer(opt =>
        {
            opt.ApiKey = ctx.Configuration["Azure:ApiKey"]
                ?? throw new InvalidOperationException("Missing Azure:ApiKey");
            opt.Region = ctx.Configuration["Azure:Region"]
                ?? throw new InvalidOperationException("Missing Azure:Region");
            opt.VoiceName = ctx.Configuration["Azure:VoiceName"]
                ?? throw new InvalidOperationException("Missing Azure:VoiceName");
            opt.Language = ctx.Configuration["Azure:Language"] ?? "en-US";
        });

        services.AddVoiceAiPipeline<EchoConversationHandler>(opt =>
        {
            opt.EndOfUtteranceSilence = TimeSpan.FromMilliseconds(600);
        });
    })
    .Build();

await host.RunAsync();
