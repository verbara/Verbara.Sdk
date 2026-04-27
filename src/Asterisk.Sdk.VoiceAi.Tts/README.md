# Asterisk.Sdk.VoiceAi.Tts

Text-to-speech providers for [Asterisk.Sdk.VoiceAi](https://www.nuget.org/packages/Asterisk.Sdk.VoiceAi) turn-based pipelines. **4 providers**, each implementing `ISpeechSynthesizer` from `Asterisk.Sdk.VoiceAi`. Native AOT, zero reflection, hand-rolled HTTP/WebSocket clients (no vendor SDK dependencies). MIT licensed.

## Providers

| Provider | Mode | TTFA target | Notes |
|----------|------|------------|-------|
| **ElevenLabs** | Streaming WebSocket | ~150 ms (Flash 2.5) | Production default. Premium quality. Multi-language. |
| **Cartesia (Sonic-3)** | Streaming WebSocket | **40-90 ms** | Lowest TTFA in the catalog. Production-grade quality. |
| **Speechmatics** | Streaming WebSocket | ~200 ms | Enterprise-grade with multi-locale. |
| **Azure** | REST batch | ~500 ms | Microsoft Cognitive Services TTS. Mature, broad locale support. Batch mode (no streaming). |

TTFA = Time-To-First-Audio. Streaming providers begin returning PCM bytes mid-synthesis; batch providers return the full clip in one response. All providers report metrics via the `Asterisk.Sdk.VoiceAi.Tts` `Meter` (latency histogram, request counters, byte throughput tagged by provider name). Health checks (`TtsHealthCheck`) auto-registered when the synthesizer is added through DI.

## Install

```sh
dotnet add package Asterisk.Sdk.VoiceAi.Tts
```

You almost always want `Asterisk.Sdk.VoiceAi` (the orchestration package) too:

```sh
dotnet add package Asterisk.Sdk.VoiceAi
```

## Quick start (ElevenLabs)

```csharp
using Asterisk.Sdk.VoiceAi.Tts.DependencyInjection;

services.AddElevenLabsSpeechSynthesizer(o =>
{
    o.ApiKey = configuration["ElevenLabs:ApiKey"]!;
    o.VoiceId = "EXAVITQu4vr4xnSDxMaL";   // "Bella" — pick any from your ElevenLabs library
    o.Model = "eleven_flash_v2_5";        // Flash 2.5 for lowest TTFA
});
```

The synthesizer is now resolvable as `ISpeechSynthesizer` and registered with the VoiceAi pipeline.

## Per-provider DI extensions

Each provider has its own `Add*SpeechSynthesizer` extension (in `Asterisk.Sdk.VoiceAi.Tts.DependencyInjection`):

```csharp
services.AddElevenLabsSpeechSynthesizer(o => { ... });
services.AddCartesiaSpeechSynthesizer(o => { ... });
services.AddSpeechmaticsSpeechSynthesizer(o => { ... });
services.AddAzureTtsSpeechSynthesizer(o => { ... });
```

## Choosing a provider

- **Best TTFA** → Cartesia Sonic-3 (40-90 ms). Lowest perceived latency for interactive AI agents.
- **Best quality / familiarity** → ElevenLabs (Flash 2.5 is fast; Multilingual v2 is premium).
- **Best language coverage** → Azure TTS (broad locale support; batch-only).
- **Mid-market enterprise** → Speechmatics (good balance).

## Examples

- `Examples/VoiceAiExample/` — ElevenLabs + Deepgram + echo handler (default demo).
- `Examples/VoiceAiCartesiaExample/` — Cartesia Sonic-3 with sub-100 ms TTFA.
- `Examples/VoiceAiSpeechmaticsExample/` — Speechmatics TTS.

## Native AOT

All HTTP/WebSocket clients hand-rolled with `HttpClient` / `ClientWebSocket`. JSON serialization via source-generated `JsonSerializerContext` (`VoiceAiTtsJsonContext`). 0 trim warnings. See [ADR-0014](https://github.com/Harol-Reina/Asterisk.Sdk/blob/main/docs/decisions/0014-raw-http-websocket-voiceai-providers.md) for the no-vendor-SDK rationale.

## License

MIT. Part of the [Asterisk.Sdk](https://github.com/Harol-Reina/Asterisk.Sdk) project.
