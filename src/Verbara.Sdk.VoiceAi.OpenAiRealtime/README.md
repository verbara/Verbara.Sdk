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

- **The vendor closes the conversation.** The bridge answers the vendor's close with its own, also
  while an input audio send is in flight, and its close code decides the session (a normal close
  completes it, a failure close fails it). The bridge then returns and the broker hangs the line up with
  one hangup frame. On the `AudioSocket()` route of Asterisk 20 and later the caller goes on in the
  dialplan at once (measured 2026-10-02 on 20.20.1, 22.9.0 and 23.4.1: 60 of 60 calls, within
  2 ms of the bridge returning); on an ARI `externalMedia` route the channel leaves Stasis with the
  caller still in your bridge (60 of 60). Put what the caller should hear next in the dialplan or
  your ARI application. On Asterisk 18 the application fails and the call is hung up.
- **The caller hangs up.** The bridge stops forwarding vendor audio and sends its close. It keeps
  reading until the vendor answers or the existing 10-second bound expires, including when an audio
  write failed because the caller's transport ended. A normal vendor close completes the session;
  a failure close fails it, even after the caller left.
- **The host stops.** A graceful stop hangs up every live call with a frame and waits for each
  conversation to return, within the host's shutdown budget. A conversation the host cancels
  returns once both of its audio loops have ended, without waiting for a function still running, and an event a session raises after the
  bridge was disposed is dropped and logged at Debug rather than thrown inside the session.

If the vendor loop ends while an input audio send is stalled, the bridge attempts its terminal close
under the existing write discipline and 10-second close bound. A send still blocking that attempt at
the bound is ended by aborting the transport; both loops finish before their resources are released.
This cleanup does not replace the vendor's close code or the receive failure that ended the session.

An event the bridge cannot read does not end the conversation. An unreadable `error` event is published as a
`RealtimeErrorEvent` carrying the raw frame, as a readable one is published with its message; any other unreadable
event is skipped, logged at Warning and counted on `openai_realtime.frames.unparseable` (tagged `voiceai.provider`),
and the call goes on.

Every session the bridge starts is counted in exactly one of `openai_realtime.sessions.completed`, `.failed` or
`.cancelled`. A session is `cancelled` only when the token handed to the bridge was cancelled (the host, a stop that
is no longer graceful, the broker's disposal), and its `openai_realtime.session.duration_ms` sample carries
`voiceai.ending` = `session-cancelled`. A normal vendor close, or an unanswered close after the caller's
hangup, is `completed`; a vendor failure close is `failed`, including after a hangup. A host cancellation that
aborts an input audio send is still `cancelled`, even when the socket reports a state error.

Register `AddAudioSocketServer` before `AddOpenAiRealtimeBridge` (the prerequisite above), so the
broker stops before the server and every call ends with a hangup frame.

## Health

`AddOpenAiRealtimeBridge` registers the `openai-realtime` health entry (`RealtimeHealthCheck`). It reports
`Unhealthy` with no API key configured, `Degraded` once the vendor rejected the bridge's last 3 consecutive sessions
(an upgrade refused with a 4xx, or a failure close before any vendor audio reached the caller), and `Healthy`
otherwise, again from the first session the vendor serves. A session the host cancelled, or one that ended on a
transport failure, counts neither way. Its data carries `consecutiveRejections` and, once a rejection happened,
`lastRejection` and `lastRejectionAt`. Rejections never report `Unhealthy`, so a default health endpoint keeps
answering 200.

## Documentation

See the [main README](../../README.md) for full documentation. Upgrading from 2.6.1: [voice-session-ending-migration.md](../../docs/guides/voice-session-ending-migration.md) and [voice-bounds-migration.md](../../docs/guides/voice-bounds-migration.md).
