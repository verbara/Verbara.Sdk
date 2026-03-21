# Asterisk.Sdk.VoiceAi

Voice AI pipeline for Asterisk.Sdk — orchestration layer for STT, TTS, and conversation with turn-taking and barge-in detection.

## Installation

```bash
dotnet add package Asterisk.Sdk.VoiceAi
```

## Quick Start

```csharp
// Implement your conversation handler (called once per user utterance)
public class MyHandler : IConversationHandler
{
    public ValueTask<string> HandleAsync(
        string transcript, ConversationContext context, CancellationToken ct)
    {
        return ValueTask.FromResult($"You said: {transcript}");
    }
}

// Register in DI
services.AddAudioSocketServer(opts => opts.Port = 9092);
services.AddVoiceAiPipeline<MyHandler>(opts =>
{
    opts.InputFormat = AudioFormat.Slin16Mono8kHz;
    opts.OutputFormat = AudioFormat.Slin16Mono8kHz;
    opts.EndOfUtteranceSilence = TimeSpan.FromMilliseconds(600);
});

// Subscribe to pipeline events
var pipeline = app.Services.GetRequiredService<VoiceAiPipeline>();
pipeline.Events.Subscribe(evt => Console.WriteLine(evt));
```

## Features

- `VoiceAiPipeline` — full VAD → STT → `IConversationHandler` → TTS loop per AudioSocket session
- Barge-in detection: cancels TTS playback when the caller speaks
- `IConversationHandler` — scoped per session; implement to plug in any LLM or business logic
- `ISessionHandler` — low-level interface; implement for fully custom session handling
- `VoiceAiSessionBroker` — hosted service that routes `AudioSocketSession` instances to the active handler
- Observable `Events` stream (`SpeechStartedEvent`, `TranscriptReceivedEvent`, `BargInDetectedEvent`, etc.)
- Native AOT compatible

## Documentation

See the [main README](../../README.md) for full documentation.
