# OpenAiRealtimeExample

Demonstrates a GPT-4o Realtime voice bridge: Asterisk sends audio over AudioSocket, the OpenAI Realtime API handles speech recognition and synthesis end-to-end, and tool calls are supported via registered functions.

## Prerequisites

- .NET 10 SDK
- Asterisk PBX with AudioSocket support
- OpenAI API key with access to `gpt-4o-realtime-preview`

## Setup

```bash
docker compose -f docker/functional/docker-compose.functional.yml up -d
```

Set your API key in `appsettings.json` or an environment variable:

```json
{
  "OpenAI": { "ApiKey": "..." }
}
```

## Run

```bash
dotnet run --project Examples/OpenAiRealtimeExample/
```

## What It Shows

- `AddAudioSocketServer` to receive Asterisk audio on port 9092
- `AddOpenAiRealtimeBridge` to connect each call to the OpenAI Realtime API
- Configuring model, voice, and system instructions
- Registering a tool function (`GetCurrentTimeFunction`) with `.AddFunction<T>()`
- Subscribing to `RealtimeTranscriptEvent` to print final user transcripts
- Handling `RealtimeResponseStartedEvent`, `RealtimeFunctionCalledEvent`, and `RealtimeErrorEvent`

## Key SDK Packages Used

- `Asterisk.Sdk.VoiceAi.AudioSocket` — AudioSocket server
- `Asterisk.Sdk.VoiceAi.OpenAiRealtime` — `OpenAiRealtimeBridge`, realtime events
