# Verbara.Sdk.VoiceAi.Tts

Text-to-speech providers for [Verbara.Sdk.VoiceAi](https://www.nuget.org/packages/Verbara.Sdk.VoiceAi) turn-based pipelines. **6 providers**, each implementing `ISpeechSynthesizer` from `Verbara.Sdk.VoiceAi`. Native AOT, zero reflection, hand-rolled HTTP/WebSocket clients (no vendor SDK dependencies). MIT licensed.

## Providers

| Provider | Mode | Latency, as the vendor publishes it | Notes |
|----------|------|------------|-------|
| **ElevenLabs** | Streaming WebSocket | ~75 ms *model inference* (Flash v2.5) [^el] | Production default. Premium quality. Multi-language. Flash 2.5 default since v1.15.3. The vendor states this figure excludes network round-trip, which it puts at 20-200 ms. |
| **Cartesia (Sonic-3)** | Streaming WebSocket | sub-90 ms [^ca] | Production-grade quality. The fastest figure any vendor in this table publishes. |
| **Speechmatics** | Streaming WebSocket | — | Enterprise-grade with multi-locale. The vendor publishes no TTS latency figure we could cite. |
| **Azure** | REST, non-streaming | — | Microsoft Cognitive Services TTS. Mature, broad locale support. Returns the full clip in one response. Microsoft publishes latency for its *batch synthesis* API, which this provider does not use — it calls the real-time endpoint `/cognitiveservices/v1`. |
| **Deepgram Aura 2** | Streaming WebSocket | sub-200 ms TTFB [^dg] | New in v1.15.3. Aura 2 voices via `wss://api.deepgram.com/v1/speak`. Token-by-token input streaming. Deepgram's own latency *documentation* walks through an example measuring 277 ms first-byte, so treat sub-200 ms as its target, not a guarantee. |
| **LMNT** | Streaming WebSocket (HTTP fallback) | — | **The vendor has shut down** ("Our speech generation journey has come to an end", [docs.lmnt.com](https://docs.lmnt.com/intro), accessed 2026-09-20). The provider code still ships; do not start new work against it. |

[^el]: [ElevenLabs — Models](https://elevenlabs.io/docs/overview/models), accessed 2026-09-20.
[^ca]: [Cartesia — Sonic](https://www.cartesia.ai/sonic), accessed 2026-09-20.
[^dg]: [Deepgram — Introducing Aura-2](https://deepgram.com/learn/introducing-aura-2-enterprise-text-to-speech) and [Text to Speech Latency](https://developers.deepgram.com/docs/text-to-speech-latency), both accessed 2026-09-20.

Every figure above is the **vendor's own published number**, cited and dated, never a measurement of this
SDK (ADR-0042 D8 as amended). A dash means the vendor publishes nothing we could cite — not that the
provider is slow. None of these are comparable to each other: they measure different things (model
inference, time-to-first-audio, time-to-first-byte) under conditions each vendor chose.

TTFA = Time-To-First-Audio. Streaming providers begin returning PCM bytes mid-synthesis; batch providers return the full clip in one response. All providers report metrics via the `Verbara.Sdk.VoiceAi.Tts` `Meter` (latency histogram, TTFA histogram, request counters, byte throughput tagged by provider name). Health checks (`TtsHealthCheck`) auto-registered when the synthesizer is added through DI.

## Observability — metric catalog

All metrics are emitted on Meter name `Verbara.Sdk.VoiceAi.Tts`.

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
dotnet add package Verbara.Sdk.VoiceAi.Tts
```

You almost always want `Verbara.Sdk.VoiceAi` (the orchestration package) too:

```sh
dotnet add package Verbara.Sdk.VoiceAi
```

## Quick start (ElevenLabs)

```csharp
using Verbara.Sdk.VoiceAi.Tts.DependencyInjection;

services.AddElevenLabsSpeechSynthesizer(o =>
{
    o.ApiKey = configuration["ElevenLabs:ApiKey"]!;
    o.VoiceId = "EXAVITQu4vr4xnSDxMaL";   // "Bella" — pick any from your ElevenLabs library
    o.Model = "eleven_flash_v2_5";        // Flash 2.5 for lowest TTFA
});
```

The synthesizer is now resolvable as `ISpeechSynthesizer` and registered with the VoiceAi pipeline.

## Per-provider DI extensions

Each provider has its own `Add*SpeechSynthesizer` extension (in `Verbara.Sdk.VoiceAi.Tts.DependencyInjection`):

```csharp
services.AddElevenLabsSpeechSynthesizer(o => { ... });
services.AddCartesiaSpeechSynthesizer(o => { ... });
services.AddSpeechmaticsSpeechSynthesizer(o => { ... });
services.AddAzureTtsSpeechSynthesizer(o => { ... });
```

## Choosing a provider

- **Lowest published latency** → Cartesia Sonic-3 (sub-90 ms, the vendor's figure). Note that the
  vendors do not measure the same quantity, so this ranks their claims, not their performance.
- **Best quality / familiarity** → ElevenLabs (Flash 2.5 is fast; Multilingual v2 is premium).
- **Best language coverage** → Azure TTS (broad locale support; non-streaming).
- **Mid-market enterprise** → Speechmatics (good balance).

## Examples

- `Examples/VoiceAiExample/` — ElevenLabs + Deepgram + echo handler (default demo).
- `Examples/VoiceAiCartesiaExample/` — Cartesia Sonic-3 (vendor-published sub-90 ms).
- `Examples/VoiceAiSpeechmaticsExample/` — Speechmatics TTS.

## Native AOT

All HTTP/WebSocket clients hand-rolled with `HttpClient` / `ClientWebSocket`. JSON serialization via source-generated `JsonSerializerContext` (`VoiceAiTtsJsonContext`). 0 trim warnings. See [ADR-0014](https://github.com/verbara/Verbara.Sdk/blob/main/docs/decisions/0014-raw-http-websocket-voiceai-providers.md) for the no-vendor-SDK rationale.

## License

MIT. Part of the [Verbara.Sdk](https://github.com/verbara/Verbara.Sdk) project.
