using Asterisk.Sdk.VoiceAi.OpenAiRealtime;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.DependencyInjection;
using Asterisk.Sdk.VoiceAi.AudioSocket.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAiRealtimeExample;
using System.Reactive.Linq;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        // Step 1: Start the AudioSocket server (Asterisk dials in here)
        services.AddAudioSocketServer(o => o.Port = 9092);

        // Step 2: Connect to OpenAI Realtime API
        services.AddOpenAiRealtimeBridge(o =>
        {
            o.ApiKey       = ctx.Configuration["OpenAI:ApiKey"]
                ?? throw new InvalidOperationException("OpenAI:ApiKey is required. Set it in appsettings.json or environment variables.");
            o.Model        = "gpt-4o-realtime-preview";
            o.Voice        = "alloy";
            o.Instructions = "You are a friendly contact center assistant. Always respond in English. Be concise.";
        })
        .AddFunction<GetCurrentTimeFunction>();
    })
    .Build();

// Subscribe to events to see what's happening in real time
var bridge = host.Services.GetRequiredService<OpenAiRealtimeBridge>();

bridge.Events
    .OfType<RealtimeTranscriptEvent>()
    .Where(e => e.IsFinal)
    .Subscribe(e => Console.WriteLine($"[{e.ChannelId:D}] User said: {e.Transcript}"));

bridge.Events
    .OfType<RealtimeResponseStartedEvent>()
    .Subscribe(e => Console.WriteLine($"[{e.ChannelId:D}] AI responding..."));

bridge.Events
    .OfType<RealtimeFunctionCalledEvent>()
    .Subscribe(e => Console.WriteLine($"[{e.ChannelId:D}] Tool '{e.FunctionName}' called → {e.ResultJson}"));

bridge.Events
    .OfType<RealtimeErrorEvent>()
    .Subscribe(e => Console.Error.WriteLine($"[{e.ChannelId:D}] ERROR: {e.Message}"));

Console.WriteLine("OpenAI Realtime bridge listening on AudioSocket port 9092.");
Console.WriteLine("Dial your Asterisk number to start a conversation with GPT-4o.");
Console.WriteLine("Press Ctrl+C to stop.");

await host.RunAsync();
