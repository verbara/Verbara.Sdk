using Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Asterisk.Sdk.VoiceAi.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Stt.DependencyInjection;
using Asterisk.Sdk.VoiceAi.Tts.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VoiceAiSpeechmaticsExample;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddAudioSocketServer(opt => opt.Port = 9092);

        services.AddSpeechmaticsStt(opt =>
        {
            opt.ApiKey = ctx.Configuration["Speechmatics:ApiKey"]
                ?? throw new InvalidOperationException("Missing Speechmatics:ApiKey");
            opt.Language = "es";
            opt.OperatingPoint = "enhanced";
        });

        services.AddSpeechmaticsTts(opt =>
        {
            opt.ApiKey = ctx.Configuration["Speechmatics:ApiKey"]
                ?? throw new InvalidOperationException("Missing Speechmatics:ApiKey");
            opt.Voice = "eleanor";
            opt.Language = "es";
        });

        services.AddVoiceAiPipeline<EchoConversationHandler>(opt =>
        {
            opt.EndOfUtteranceSilence = TimeSpan.FromMilliseconds(600);
        });
    })
    .Build();

await host.RunAsync();
