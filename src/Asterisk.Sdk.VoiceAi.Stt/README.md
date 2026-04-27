# Asterisk.Sdk.VoiceAi.Stt

Speech-to-text providers for [Asterisk.Sdk.VoiceAi](https://www.nuget.org/packages/Asterisk.Sdk.VoiceAi) turn-based pipelines. **7 providers**, each implementing `ISpeechRecognizer` from `Asterisk.Sdk.VoiceAi`. Native AOT, zero reflection, hand-rolled HTTP/WebSocket clients (no vendor SDK dependencies). MIT licensed.

## Providers

| Provider | Mode | Notes |
|----------|------|-------|
| **Deepgram** | Streaming WebSocket | Nova-2 model. Production default. Lowest latency in the catalog (~150ms). |
| **Whisper** (local) | Batch | Self-hosted whisper.cpp / OpenAI Whisper API endpoint. Air-gapped option. |
| **Azure Whisper** | Batch | Azure OpenAI Whisper deployments. Same format as `Whisper` with Azure auth. |
| **Google Speech** | Streaming gRPC over HTTP/2 | Standard model. Multi-language support. |
| **Cartesia (Ink-Whisper)** | Streaming WebSocket | Newer entrant; competitive latency at lower cost. |
| **AssemblyAI (Universal)** | Streaming WebSocket | Universal-2 model with strong technical/code recognition. |
| **Speechmatics** | Streaming WebSocket | Enterprise-grade with fine-grained punctuation/casing. |

All providers report metrics via the `Asterisk.Sdk.VoiceAi.Stt` `Meter` (latency histogram, request counters, error counters tagged by provider name). Health checks (`SttHealthCheck`) auto-registered when the recognizer is added through DI.

## Install

```sh
dotnet add package Asterisk.Sdk.VoiceAi.Stt
```

You almost always want `Asterisk.Sdk.VoiceAi` (the orchestration package) too:

```sh
dotnet add package Asterisk.Sdk.VoiceAi
```

## Quick start (Deepgram)

```csharp
using Asterisk.Sdk.VoiceAi.Stt.DependencyInjection;

services.AddDeepgramSpeechRecognizer(o =>
{
    o.ApiKey = configuration["Deepgram:ApiKey"]!;
    o.Model = "nova-2";
    o.Language = "en-US";
});
```

The recognizer is now resolvable as `ISpeechRecognizer` and registered with the VoiceAi pipeline. Connect AudioSocket on the Asterisk side and you have a streaming STT bridge.

## Per-provider DI extensions

Each provider has its own `Add*SpeechRecognizer` extension (in `Asterisk.Sdk.VoiceAi.Stt.DependencyInjection`):

```csharp
services.AddDeepgramSpeechRecognizer(o => { ... });
services.AddWhisperSpeechRecognizer(o => { ... });
services.AddAzureWhisperSpeechRecognizer(o => { ... });
services.AddGoogleSpeechRecognizer(o => { ... });
services.AddCartesiaSpeechRecognizer(o => { ... });
services.AddAssemblyAiSpeechRecognizer(o => { ... });
services.AddSpeechmaticsSpeechRecognizer(o => { ... });
```

## Examples

- `Examples/VoiceAiExample/` — Deepgram + ElevenLabs + echo handler (default end-to-end demo).
- `Examples/VoiceAiAssemblyAiExample/` — AssemblyAI Universal-2.
- `Examples/VoiceAiSpeechmaticsExample/` — Speechmatics enterprise STT.
- `Examples/VoiceAiCartesiaExample/` — Cartesia Ink-Whisper STT + Sonic-3 TTS.

## Native AOT

All HTTP/WebSocket clients hand-rolled with `HttpClient` / `ClientWebSocket`. JSON serialization via source-generated `JsonSerializerContext` (`VoiceAiSttJsonContext`). 0 trim warnings. See [ADR-0014](https://github.com/Harol-Reina/Asterisk.Sdk/blob/main/docs/decisions/0014-raw-http-websocket-voiceai-providers.md) for the no-vendor-SDK rationale.

## License

MIT. Part of the [Asterisk.Sdk](https://github.com/Harol-Reina/Asterisk.Sdk) project.
