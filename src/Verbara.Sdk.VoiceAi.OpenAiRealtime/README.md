# Verbara.Sdk.VoiceAi.OpenAiRealtime

OpenAI Realtime API bridge for Verbara.Sdk.VoiceAi — persistent WebSocket session with function calling, transcript events, and dual-loop audio streaming.

## Installation

```bash
dotnet add package Verbara.Sdk.VoiceAi.OpenAiRealtime
```

## Quick Start

```csharp
// Prerequisite: register AudioSocket transport
services.AddAudioSocketServer(opts => opts.Port = 9092);

// Register the bridge (replaces the STT→LLM→TTS chain)
services.AddOpenAiRealtimeBridge(opts =>
{
    opts.ApiKey = configuration["OpenAI:ApiKey"]!;
    opts.Model = "gpt-realtime";
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
- Function calling: register `IRealtimeFunctionHandler` implementations with `AddFunction<T>()`, each call bounded by `FunctionCallTimeout` (default 30 s; written `hh:mm:ss` in configuration): a function that outlasts it is answered `{"error":"timeout"}`, logged at Warning, counted in `openai_realtime.function_calls.timed_out` and no longer awaited ([migration guide](../../docs/guides/voice-bounds-migration.md))
- Observable `Events` stream (`RealtimeTranscriptEvent`, `RealtimeFunctionCalledEvent`, `RealtimeResponseStartedEvent`, etc.)
- `AddOpenAiRealtimeBridge()` and `AddFunction<T>()` DI extension methods
- Native AOT compatible (AOT-safe JSON via `System.Text.Json` source generation)

## When the conversation ends

The bridge runs one Realtime conversation per AudioSocket session, and the session broker that
`AddOpenAiRealtimeBridge` registers ends the line as soon as the bridge returns:

- **The vendor closes the conversation.** The bridge returns and the broker hangs the line up with
  one hangup frame. On the `AudioSocket()` route of Asterisk 20 and later the caller goes on in the
  dialplan at once (measured 2026-10-02 on 20.20.1, 22.9.0 and 23.4.1: 60 of 60 calls, within
  2 ms of the bridge returning); on an ARI `externalMedia` route the channel leaves Stasis with the
  caller still in your bridge (60 of 60). Put what the caller should hear next in the dialplan or
  your ARI application. On Asterisk 18 the application fails and the call is hung up.
- **The caller hangs up.** The bridge closes the vendor session and returns; nothing more is sent
  to the caller.
- **The host stops.** A graceful stop hangs up every live call with a frame and waits for each
  conversation to return, within the host's shutdown budget. A conversation the host cancels
  returns once both of its audio loops have ended, without waiting for a function still running, and an event a session raises after the
  bridge was disposed is dropped and logged at Debug rather than thrown inside the session.

Register `AddAudioSocketServer` before `AddOpenAiRealtimeBridge` (the prerequisite above), so the
broker stops before the server and every call ends with a hangup frame.

## Documentation

See the [main README](../../README.md) for full documentation. Upgrading from 2.6.1: [voice-session-ending-migration.md](../../docs/guides/voice-session-ending-migration.md) and [voice-bounds-migration.md](../../docs/guides/voice-bounds-migration.md).
