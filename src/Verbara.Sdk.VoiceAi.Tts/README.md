# Asterisk.Sdk.VoiceAi.Tts

Text-to-speech providers for [Asterisk.Sdk.VoiceAi](https://www.nuget.org/packages/Asterisk.Sdk.VoiceAi) turn-based pipelines. **6 providers**, each implementing `ISpeechSynthesizer` from `Asterisk.Sdk.VoiceAi`. Native AOT, zero reflection, hand-rolled HTTP/WebSocket clients (no vendor SDK dependencies). MIT licensed.

## Providers

| Provider | Mode | TTFA target | Notes |
|----------|------|------------|-------|
| **ElevenLabs** | Streaming WebSocket | ~150 ms (Flash 2.5) | Production default. Premium quality. Multi-language. Flash 2.5 default since v1.15.3. |
| **Cartesia (Sonic-3)** | Streaming WebSocket | **40-90 ms** | Lowest TTFA in the catalog. Production-grade quality. |
| **Speechmatics** | Streaming WebSocket | ~200 ms | Enterprise-grade with multi-locale. |
| **Azure** | REST batch | ~500 ms | Microsoft Cognitive Services TTS. Mature, broad locale support. Batch mode (no streaming). |
| **Deepgram Aura 2** | Streaming WebSocket | ~150-200 ms | New in v1.15.3. Aura 2 voices via `wss://api.deepgram.com/v1/speak`. Token-by-token input streaming. |
| **LMNT** | Streaming WebSocket (HTTP fallback) | **sub-200 ms** | New in v1.15.3. Sub-200 ms TTFA target for conversational AI agents. |

TTFA = Time-To-First-Audio. Streaming providers begin returning PCM bytes mid-synthesis; batch providers return the full clip in one response. All providers report metrics via the `Asterisk.Sdk.VoiceAi.Tts` `Meter` (latency histogram, TTFA histogram, request counters, byte throughput tagged by provider name). Health checks (`TtsHealthCheck`) auto-registered when the synthesizer is added through DI.

## Observability — metric catalog

All metrics are emitted on Meter name `Asterisk.Sdk.VoiceAi.Tts`.

| Metric | Type | Unit | Description |
|--------|------|------|-------------|
| `tts.syntheses.started` | Counter | syntheses | Synthesis attempts started |
| `tts.syntheses.completed` | Counter | syntheses | Syntheses completed successfully |
| `tts.syntheses.failed` | Counter | syntheses | Syntheses failed with error |
| `tts.synthesis.characters` | Counter | {characters} | Total characters synthesized |
| `tts.synthesis.latency_ms` | Histogram | ms | Total synthesis latency (start → last frame). Buckets: 5/10/25/50/100/250/500/1000/2500/5000 ms |
| `tts.synthesis.ttfa_ms` | Histogram | ms | **Time-to-first-audio**: elapsed from synthesis start until first audio frame yielded to caller. Tags: `voiceai.provider`. Buckets: 5/10/25/50/100/250/500/1000/2500/5000 ms |

The `tts.synthesis.ttfa_ms` histogram is the key metric for evaluating provider responsiveness in interactive voice agents. Compare across providers using the `voiceai.provider` tag.

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
