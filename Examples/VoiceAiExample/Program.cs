using Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Asterisk.Sdk.VoiceAi.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Stt.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Tts.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VoiceAiExample;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddAudioSocketServer(opt => opt.Port = 9092);

        services.AddDeepgramSpeechRecognizer(opt =>
        {
            opt.ApiKey = ctx.Configuration["Deepgram:ApiKey"]
                ?? throw new InvalidOperationException("Missing Deepgram:ApiKey");
            opt.Language = "es";
        });

        services.AddElevenLabsSpeechSynthesizer(opt =>
        {
            opt.ApiKey = ctx.Configuration["ElevenLabs:ApiKey"]
                ?? throw new InvalidOperationException("Missing ElevenLabs:ApiKey");
            opt.VoiceId = ctx.Configuration["ElevenLabs:VoiceId"]
                ?? throw new InvalidOperationException("Missing ElevenLabs:VoiceId");
        });

        services.AddVoiceAiPipeline<EchoConversationHandler>(opt =>
        {
            opt.EndOfUtteranceSilence = TimeSpan.FromMilliseconds(600);
        });
    })
    .Build();

await host.RunAsync();
