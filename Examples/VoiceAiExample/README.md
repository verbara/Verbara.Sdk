# VoiceAiExample

Demonstrates a complete voice AI pipeline: Asterisk sends audio over AudioSocket, Deepgram transcribes speech to text, a custom handler generates a reply, and ElevenLabs synthesizes the response back to the caller.

## Prerequisites

- .NET 10 SDK
- Asterisk PBX with AudioSocket support
- Deepgram API key
- ElevenLabs API key and Voice ID

## Setup

```bash
docker compose -f docker/functional/docker-compose.functional.yml up -d
```

Set API keys in `appsettings.json` or environment variables:

```json
{
  "Deepgram": { "ApiKey": "..." },
  "ElevenLabs": { "ApiKey": "...", "VoiceId": "..." }
}
```

## Run

```bash
dotnet run --project Examples/VoiceAiExample/
```

## What It Shows

- `AddAudioSocketServer` to receive raw audio from Asterisk on port 9092
- `AddDeepgramSpeechRecognizer` for real-time Spanish speech-to-text
- `AddElevenLabsSpeechSynthesizer` for text-to-speech synthesis
- `AddVoiceAiPipeline<THandler>` wiring all stages together
- Implementing `IConversationHandler` (`EchoConversationHandler`) to process transcripts and return responses
- Configurable end-of-utterance silence detection (`EndOfUtteranceSilence`)

## Key SDK Packages Used

- `Asterisk.Sdk.VoiceAi.AudioSocket` — AudioSocket server
- `Asterisk.Sdk.VoiceAi.Stt` — Deepgram speech recognizer
- `Asterisk.Sdk.VoiceAi.Tts` — ElevenLabs speech synthesizer
- `Asterisk.Sdk.VoiceAi` — `IConversationHandler`, `AddVoiceAiPipeline`
