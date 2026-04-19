# VoiceAiSpeechmaticsExample

Speechmatics full stack: Realtime v2 WebSocket STT + REST TTS, from the same vendor. Pitched for **enterprise multilingual deployments where cost per character matters** — Speechmatics publishes list prices roughly 27× below ElevenLabs at sub-150ms latency on their Enhanced tier, with 55+ languages on the STT side.

This example uses Spanish with the `enhanced` operating point (best accuracy at ~150ms latency) and the `eleanor` voice for TTS.

## Prerequisites

- .NET 10 SDK
- Asterisk PBX with AudioSocket support
- Speechmatics API key (same key works for both STT and TTS)

## Setup

Set the key in `appsettings.json` or environment variables:

```json
{
  "Speechmatics": { "ApiKey": "..." }
}
```

## Run

```bash
dotnet run --project Examples/VoiceAiSpeechmaticsExample/
```

Point Asterisk's `AudioSocket()` dialplan at `127.0.0.1:9092`.

## What It Shows

- `AddAudioSocketServer` — raw audio from Asterisk on port 9092.
- `AddSpeechmaticsStt` — Realtime v2 WebSocket STT (Spanish, `enhanced` operating point).
- `AddSpeechmaticsTts` — REST synthesis, `eleanor` voice, Spanish.
- `AddVoiceAiPipeline<EchoConversationHandler>` — turn-based wiring.

## Why Speechmatics

- **Price floor.** At ~$0.011 per 1K characters of TTS, roughly 27× cheaper than ElevenLabs at the same quality tier. STT pricing is similarly below the premium providers.
- **Language coverage.** 55+ STT languages, including colloquial Latin-American Spanish variants that most providers collapse into a single Castilian model.
- **Latency.** Sub-150ms first-result on Enhanced, comparable to Deepgram Nova-2 / Cartesia Ink.
- **No official C# SDK.** This provider is a hand-rolled, AOT-clean wrapper per ADR-0014.

## Key SDK Packages Used

- `Asterisk.Sdk.VoiceAi.AudioSocket` — AudioSocket server.
- `Asterisk.Sdk.VoiceAi.Stt` — Speechmatics recognizer.
- `Asterisk.Sdk.VoiceAi.Tts` — Speechmatics synthesizer.
- `Asterisk.Sdk.VoiceAi` — `IConversationHandler`, `AddVoiceAiPipeline`.
