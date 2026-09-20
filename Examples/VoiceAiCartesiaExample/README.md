# VoiceAiCartesiaExample

Cartesia-powered voice agent: Ink-Whisper STT for conversational telephony + Sonic-3 TTS. The headline showcase for "ultra-low-latency voice agents" on Asterisk.

Shows the same pipeline shape as [`VoiceAiExample`](../VoiceAiExample/) (Deepgram + ElevenLabs) but with both STT and TTS from a single vendor — no split-bill, no latency stitching across two provider connections.

## Prerequisites

- .NET 10 SDK
- Asterisk PBX with AudioSocket support
- Cartesia API key and a voice id from your Cartesia account

## Setup

Set keys in `appsettings.json` or environment variables:

```json
{
  "Cartesia": { "ApiKey": "...", "VoiceId": "..." }
}
```

## Run

```bash
dotnet run --project Examples/VoiceAiCartesiaExample/
```

Point Asterisk's `AudioSocket()` dialplan at `127.0.0.1:9092` (or the host/IP where this example runs).

## What It Shows

- `AddAudioSocketServer` — raw audio from Asterisk on port 9092.
- `AddCartesiaSpeechRecognizer` — Ink-Whisper WebSocket STT, language `es`.
- `AddCartesiaSpeechSynthesizer` — Sonic-3 WebSocket TTS.
- `AddVoiceAiPipeline<EchoConversationHandler>` — turn-based pipeline wiring.
- `IConversationHandler` — `EchoConversationHandler` returns the user's transcript prefixed with `Dijiste:`.

## Key SDK Packages Used

- `Verbara.Sdk.VoiceAi.AudioSocket` — AudioSocket server.
- `Verbara.Sdk.VoiceAi.Stt` — Cartesia Ink-Whisper recognizer.
- `Verbara.Sdk.VoiceAi.Tts` — Cartesia Sonic-3 synthesizer.
- `Verbara.Sdk.VoiceAi` — `IConversationHandler`, `AddVoiceAiPipeline`.

## Why Cartesia

Cartesia publishes **sub-90 ms** time-to-first-audio for Sonic ([cartesia.ai/sonic](https://www.cartesia.ai/sonic), accessed 2026-09-20) — the lowest figure any vendor in this SDK's TTS catalog publishes. End-to-end turn latency on this example has not been measured under stated conditions, so no figure is published for it here. No official C# SDK exists for Cartesia, so the provider is hand-rolled over WebSocket + JSON (see ADR-0014).
