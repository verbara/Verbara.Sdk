using Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Asterisk.Sdk.VoiceAi.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Stt.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Tts.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VoiceAiCartesiaExample;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddAudioSocketServer(opt => opt.Port = 9092);

        services.AddCartesiaSpeechRecognizer(opt =>
        {
            opt.ApiKey = ctx.Configuration["Cartesia:ApiKey"]
                ?? throw new InvalidOperationException("Missing Cartesia:ApiKey");
            opt.Language = "es";
        });

        services.AddCartesiaSpeechSynthesizer(opt =>
        {
            opt.ApiKey = ctx.Configuration["Cartesia:ApiKey"]
                ?? throw new InvalidOperationException("Missing Cartesia:ApiKey");
            opt.VoiceId = ctx.Configuration["Cartesia:VoiceId"]
                ?? throw new InvalidOperationException("Missing Cartesia:VoiceId");
            opt.Language = "es";
        });

        services.AddVoiceAiPipeline<EchoConversationHandler>(opt =>
        {
            opt.EndOfUtteranceSilence = TimeSpan.FromMilliseconds(600);
        });
    })
    .Build();

await host.RunAsync();
