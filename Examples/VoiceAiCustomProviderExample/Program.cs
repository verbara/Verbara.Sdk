using Asterisk.Sdk.VoiceAi;
using Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Asterisk.Sdk.VoiceAi.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VoiceAiCustomProviderExample;

// This example shows how to plug a custom STT and a custom TTS implementation
// into the Voice AI pipeline by registering them directly as the
// SpeechRecognizer / SpeechSynthesizer DI services — no provider-specific
// package (VoiceAi.Stt / VoiceAi.Tts) required.
//
// The two custom types both override ProviderName with a stable literal so
// that the pipeline's per-utterance activity tags avoid the GetType().Name
// reflection fallback. See docs/guides/high-load-tuning.md for the perf
// rationale.

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        // AudioSocket transport (accept Asterisk connections on port 9092).
        services.AddAudioSocketServer(opt => opt.Port = 9092);

        // Custom providers — overriding ProviderName keeps the STT/TTS hot
        // path free of GetType().Name reflection (v1.10.0+).
        services.AddSingleton<SpeechRecognizer, MyEchoRecognizer>();
        services.AddSingleton<SpeechSynthesizer, MySilenceSynthesizer>();

        // Pipeline + a simple echo conversation handler.
        services.AddVoiceAiPipeline<EchoConversationHandler>(opt =>
        {
            opt.EndOfUtteranceSilence = TimeSpan.FromMilliseconds(600);
        });
    })
    .Build();

await host.RunAsync();
