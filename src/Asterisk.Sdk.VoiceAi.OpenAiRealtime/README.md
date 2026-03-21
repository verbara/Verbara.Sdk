# Asterisk.Sdk.VoiceAi.OpenAiRealtime

OpenAI Realtime API bridge for Asterisk.Sdk.VoiceAi — persistent WebSocket session with function calling, transcript events, and dual-loop audio streaming.

## Installation

```bash
dotnet add package Asterisk.Sdk.VoiceAi.OpenAiRealtime
```

## Quick Start

```csharp
// Prerequisite: register AudioSocket transport
services.AddAudioSocketServer(opts => opts.Port = 9092);

// Register the bridge (replaces the STT→LLM→TTS chain)
services.AddOpenAiRealtimeBridge(opts =>
{
    opts.ApiKey = configuration["OpenAI:ApiKey"]!;
    opts.Model = "gpt-4o-realtime-preview";
    opts.Voice = "alloy";
    opts.Instructions = "You are a helpful call center assistant.";
    opts.VadMode = VadMode.ServerSide;
})
.AddFunction<GetAccountBalanceFunction>();   // optional tool functions

// Subscribe to bridge events
var bridge = app.Services.GetRequiredService<OpenAiRealtimeBridge>();
bridge.Events.Subscribe(evt =>
{
    if (evt is RealtimeTranscriptEvent t && t.IsFinal)
        Console.WriteLine($"[{t.ChannelId}] {t.Text}");
});
```

## Features

- `OpenAiRealtimeBridge` — `ISessionHandler` that opens a WebSocket to OpenAI Realtime API per call
- Dual audio loops: inbound PCM16 → base64 → OpenAI, OpenAI audio delta → PCM16 → Asterisk
- Automatic resampling between Asterisk 8 kHz and OpenAI 24 kHz via `ResamplerFactory`
- Function calling: register `IRealtimeFunctionHandler` implementations with `AddFunction<T>()`
- Observable `Events` stream (`RealtimeTranscriptEvent`, `RealtimeFunctionCalledEvent`, `RealtimeResponseStartedEvent`, etc.)
- `AddOpenAiRealtimeBridge()` and `AddFunction<T>()` DI extension methods
- Native AOT compatible (AOT-safe JSON via `System.Text.Json` source generation)

## Documentation

See the [main README](../../README.md) for full documentation.
