# ADR-0017: AudioSocket codec negotiation (slin16 / ulaw / alaw / gsm, per-connection)

- **Status:** Accepted
- **Date:** 2026-03-19 (retrospective — decision made during VoiceAi AudioSocket introduction)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0013 (`ISessionHandler` abstraction), ADR-0014 (raw HTTP/WebSocket providers)

## Context

AudioSocket is Asterisk's bidirectional TCP bridge between dialplan and an external audio application. The protocol carries an 18-byte header per frame with a type byte and a payload-length field; the payload is raw PCM (slin16), G.711 μ-law or A-law, or GSM. There is no handshake in the classic sense — the first audio frame establishes the format for the session. In practice, deployments have heterogeneous needs: some tenants force slin16 because their STT provider wants wideband audio, some force G.711 to match a trunk's codec and avoid transcoding, some use GSM for bandwidth-sensitive mobile scenarios.

A first-pass design could simply hard-code one codec — slin16 is the obvious candidate given its fidelity and the direction AI voice is moving — and let operators configure Asterisk to transcode into it before sending. That works, but it makes AudioSocket brittle: a dialplan change upstream starts sending ulaw, the session silently corrupts, and nothing in the SDK surfaces the mismatch until the STT provider returns garbled transcripts. A second-pass design could read a single codec choice from `AudioSocketOptions` at startup, but that loses per-tenant flexibility in a multi-tenant deployment sharing one AudioSocket server.

The real forces are: the SDK cannot predict which codec a given dialplan will send; codec must be discoverable from the first frame; transcoding must be optional (the VoiceAi pipeline's `AudioProcessor` already handles resampling and format conversion when needed); and the negotiation must not add a round-trip because AudioSocket sessions are fragile — Asterisk drops the call if the TCP side stalls.

## Decision

Every `AudioSocketSession` negotiates its codec per-connection from the first inbound audio frame. The frame type byte (`AudioSocketFrameType.Slin16`, `.Ulaw`, `.Alaw`, `.Gsm`) determines the session's audio format, and the `AudioSocketFrameTypeExtensions` helpers project that type onto the SDK's `AudioFormat` domain model. Downstream consumers (VoiceAi pipeline, OpenAI Realtime bridge, custom `ISessionHandler` implementations) receive a typed `AudioFormat` and can request transcoding from the `AudioProcessor` if they need a different target format.

## Consequences

- **Positive:**
  - A single AudioSocket server transparently handles heterogeneous tenants — no per-tenant configuration, no registry of codec-per-dialplan.
  - Upstream dialplan changes that alter codec surface as a detected frame type, not as silent corruption.
  - Transcoding is opt-in: consumers that already have the right codec pay zero CPU; consumers that need slin16 for STT pay one transcode pass at the session boundary, not per frame.
  - The negotiation is free in wall-clock terms because the type byte is the header's first byte; no round-trip, no extra packet.
- **Negative:**
  - The first-frame-wins contract means a misconfigured dialplan that sends mixed codecs in one session will break silently after the first frame. No consumer has reported this — AudioSocket is session-scoped and Asterisk drives the codec consistently per session — but the failure mode exists.
  - Four codec types means four conversion paths in `AudioProcessor` that must stay correct under AOT. Each codec adds a small test surface.
- **Trade-off:** We trade the simplicity of a single hard-coded codec for per-connection adaptability. The complexity is bounded — four codec types, one negotiation point, one test suite per codec path — and the alternative loses the single-server multi-tenant story that AudioSocket is the natural fit for. The audit in [`docs/research/2026-04-19-product-alignment-audit.md`](../research/2026-04-19-product-alignment-audit.md) §4 item #6 flagged this as a load-bearing decision whose rationale is invisible from the code alone.

## Alternatives considered

- **Single hard-coded codec (slin16)** — rejected because it forces every deployment that currently uses G.711 trunks into a transcode-at-Asterisk configuration, which some operators cannot change. A dialplan change upstream would then silently corrupt the audio stream.
- **Configuration-driven single codec per server (`AudioSocketOptions.Codec`)** — rejected because it loses per-tenant flexibility. In a contact-center-as-a-service deployment, one AudioSocket server fronts many dialplans; one global codec setting makes the server a per-tenant resource instead of a shared one.
- **Explicit handshake frame before audio** — rejected because it would add a round-trip to every session and breaks wire compatibility with the AudioSocket protocol that Asterisk already speaks. The SDK does not own the protocol; Asterisk does.
- **Runtime codec discovery via sample-rate heuristics on the first frames** — rejected because the type byte is already in the header. Heuristic detection would be slower, more brittle, and would introduce false-positive misdetection failures in telephony conditions (silence, DTMF, comfort noise) that the explicit type byte avoids.
