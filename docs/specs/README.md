# Technical Specifications

Design documents describing what a feature **is** and how it works. Paired with plans (how we'll ship it) and ADRs (why we chose the design).

## When to add a spec

- A non-trivial feature that multiple contributors will work on.
- A protocol or wire-format definition.
- An integration contract between two SDK packages.

For one-off implementations, the code + XML doc comments are enough.

## File convention

`{YYYY-MM-DD}-{feature-kebab}.md` — date-prefixed for chronological sort.

## Lifecycle

```
specs/              ← current designs
  └ archived/       ← superseded specs kept for history
```

## Catalog

- [2026-03-17-session-engine-design.md](2026-03-17-session-engine-design.md) — Session Engine: call session correlation, state machine lifecycle, domain events, pluggable extension points (`ISessionEnricher` / `ISessionPolicy` / `ISessionEventHandler`).
- [2026-03-19-sprint23-voiceai-audiosocket-design.md](2026-03-19-sprint23-voiceai-audiosocket-design.md) — VoiceAi AudioSocket transport: TCP server, codec negotiation, bidirectional streaming via `System.IO.Pipelines`.
- [2026-03-19-sprint23-voiceai-stt-tts-design.md](2026-03-19-sprint23-voiceai-stt-tts-design.md) — VoiceAi STT/TTS provider abstraction layer + initial provider set.
- [2026-03-19-sprint24-openai-realtime-design.md](2026-03-19-sprint24-openai-realtime-design.md) — OpenAI Realtime API bridge: dual-loop WebSocket, function calling, observability events.
- [2026-03-30-ari-audio-lifecycle-design.md](2026-03-30-ari-audio-lifecycle-design.md) — ARI audio lifecycle: playback / recording / channel media-control state machines.
- [2026-04-25-r1.5-voiceai-refresh-design.md](2026-04-25-r1.5-voiceai-refresh-design.md) — R1.5 VoiceAi refresh: Deepgram Aura 2 WS + LMNT TTS + ElevenLabs Flash 2.5 polish + TTFA observability.

Older specs that have been superseded live under [archived/](archived/).
