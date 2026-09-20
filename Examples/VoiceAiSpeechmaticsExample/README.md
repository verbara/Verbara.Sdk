# VoiceAiSpeechmaticsExample

Speechmatics full stack: Realtime v2 WebSocket STT + REST TTS, from the same vendor. Pitched for **enterprise multilingual deployments**, with 55+ languages on the STT side.

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

- **Cost.** Speechmatics positions itself below the premium providers on list price. No figure is published here: a price is a third party's, it changes without notice, and a ratio between two vendors' price lists is our arithmetic on their data rather than anyone's published measurement (ADR-0042 D8 as amended).
- **Language coverage.** 55+ STT languages, including colloquial Latin-American Spanish variants that most providers collapse into a single Castilian model.
- **Latency.** Speechmatics documents partial transcripts returned in **under 500 ms** and final transcripts **as fast as 0.7 s**, with `max_delay` configurable between 0.7 s and 4 s ([Output – Realtime](https://docs.speechmatics.com/features/realtime-latency), accessed 2026-09-20). For voice agents the vendor recommends a `max_delay` of 0.7-1.5 s.
- **No official C# SDK.** This provider is a hand-rolled, AOT-clean wrapper per ADR-0014.

## Key SDK Packages Used

- `Verbara.Sdk.VoiceAi.AudioSocket` — AudioSocket server.
- `Verbara.Sdk.VoiceAi.Stt` — Speechmatics recognizer.
- `Verbara.Sdk.VoiceAi.Tts` — Speechmatics synthesizer.
- `Verbara.Sdk.VoiceAi` — `IConversationHandler`, `AddVoiceAiPipeline`.
