# VoiceAiCartesiaExample

Cartesia-powered voice agent: Ink-Whisper STT for conversational telephony + Sonic-3 TTS at 40-90ms TTFA (the lowest in the 2026 market). The headline showcase for "ultra-low-latency voice agents" on Asterisk.

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
- `AddCartesiaSpeechSynthesizer` — Sonic-3 WebSocket TTS (40-90ms TTFA).
- `AddVoiceAiPipeline<EchoConversationHandler>` — turn-based pipeline wiring.
- `IConversationHandler` — `EchoConversationHandler` returns the user's transcript prefixed with `Dijiste:`.

## Key SDK Packages Used

- `Asterisk.Sdk.VoiceAi.AudioSocket` — AudioSocket server.
- `Asterisk.Sdk.VoiceAi.Stt` — Cartesia Ink-Whisper recognizer.
- `Asterisk.Sdk.VoiceAi.Tts` — Cartesia Sonic-3 synthesizer.
- `Asterisk.Sdk.VoiceAi` — `IConversationHandler`, `AddVoiceAiPipeline`.

## Why Cartesia

Sonic-3's **40-90ms TTFA** (time-to-first-audio) is the lowest measured in the 2026 provider landscape; end-to-end turn latency on this example lands in the 200-400ms range on a decent connection — inside the window where a caller perceives the exchange as conversational rather than transactional. No official C# SDK exists for Cartesia, so the provider is hand-rolled over WebSocket + JSON (see ADR-0014).
