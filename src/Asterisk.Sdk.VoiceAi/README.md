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

## Custom STT / TTS Providers

When writing your own `SpeechRecognizer` or `SpeechSynthesizer` subclass, override `ProviderName` with a stable literal to avoid the per-utterance `GetType().Name` allocation on the pipeline hot path (used as a tag on STT/TTS activities):

```csharp
public sealed class MyCustomRecognizer : SpeechRecognizer
{
    public override string ProviderName => "MyCustom";

    public override IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        CancellationToken ct = default)
    {
        // ...
    }
}
```

If you don't override `ProviderName` the default falls back to `GetType().Name` — functional, but incurs one reflection call per utterance.

## Observability

- **Metrics:** `VoiceAiMetrics` (sessions started/completed/failed, session duration), `SpeechRecognitionMetrics` (transcriptions started/completed/failed, latency), `SpeechSynthesisMetrics` (syntheses started/completed/failed, latency, characters).
- **Tracing:** `VoiceAiActivitySource` — session / recognition / synthesis spans.
- **Health:** `VoiceAiHealthCheck`, `SttHealthCheck`, `TtsHealthCheck` auto-registered by `AddVoiceAiPipeline<THandler>()`.
- Discover names via `AsteriskTelemetry.ActivitySourceNames` / `MeterNames` from `Asterisk.Sdk.Hosting`.

## Documentation

See the [main README](../../README.md) for full documentation.
