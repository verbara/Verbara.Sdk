# VoiceAiAssemblyAiExample

AssemblyAI-powered STT with Azure TTS. Demonstrates the **mix-and-match provider design** that [ADR-0014](../../docs/decisions/0014-raw-http-websocket-voiceai-providers.md) enables: the pipeline wires STT and TTS independently, so nothing forces both to come from the same vendor.

AssemblyAI ships STT only. Pairing it with Azure TTS gives you AssemblyAI's Universal Streaming transcription quality and Azure's broad voice catalog on the synthesis side — a common combination in English-first enterprise call centers.

## Prerequisites

- .NET 10 SDK
- Asterisk PBX with AudioSocket support
- AssemblyAI API key
- Azure Cognitive Services key + region + voice name

## Setup

Set keys in `appsettings.json` or environment variables:

```json
{
  "AssemblyAi": { "ApiKey": "..." },
  "Azure": {
    "ApiKey": "...",
    "Region": "eastus",
    "VoiceName": "en-US-JennyNeural",
    "Language": "en-US"
  }
}
```

## Run

```bash
dotnet run --project Examples/VoiceAiAssemblyAiExample/
```

Point Asterisk's `AudioSocket()` dialplan at `127.0.0.1:9092`.

## What It Shows

- `AddAudioSocketServer` — raw audio from Asterisk on port 9092.
- `AddAssemblyAi` — Universal Streaming v3 STT over WebSocket.
- `AddAzureTtsSpeechSynthesizer` — Azure Cognitive Services TTS (any `<voice>-Neural`).
- `AddVoiceAiPipeline<EchoConversationHandler>` — pipeline wiring STT from one vendor and TTS from another, glue-free.

## Why AssemblyAI Here

The **official AssemblyAI .NET SDK was discontinued in April 2025**. `Asterisk.Sdk.VoiceAi.Stt.AssemblyAi` fills that gap as an AOT-clean, hand-rolled wrapper over AssemblyAI's Universal Streaming protocol — zero reflection, no vendor-supplied binary dependency, no Kestrel/ASP.NET coupling.

## Key SDK Packages Used

- `Asterisk.Sdk.VoiceAi.AudioSocket` — AudioSocket server.
- `Asterisk.Sdk.VoiceAi.Stt` — AssemblyAI recognizer.
- `Asterisk.Sdk.VoiceAi.Tts` — Azure synthesizer.
- `Asterisk.Sdk.VoiceAi` — `IConversationHandler`, `AddVoiceAiPipeline`.
