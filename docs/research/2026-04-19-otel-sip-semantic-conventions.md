# Proposal: OpenTelemetry semantic conventions for SIP / Asterisk telephony

OpenTelemetry's semantic conventions catalog — the set of standard attribute names that make traces and metrics interoperable across instrumented systems — covers HTTP, database, messaging, RPC, FaaS, and many other domains, but **has no SIP / VoIP / telephony profile**. The gap has been tracked in `open-telemetry/opentelemetry-specification#2517` since 2022 without resolution. This document proposes a draft convention set tailored for Asterisk and SIP-adjacent systems, grounded in the 9 `ActivitySource`s and 13 `Meter`s that Asterisk.Sdk already ships in v1.11.1, and offered as a reference point for future upstream standardization.

- **Date:** 2026-04-19
- **Status:** Draft — proposal, not yet adopted in code
- **Related:** [../decisions/](../decisions/) (telemetry-related ADRs) · `src/Asterisk.Sdk.Hosting/AsteriskTelemetry.cs` (discovery surface) · [OTel spec issue #2517](https://github.com/open-telemetry/opentelemetry-specification/issues/2517)

---

## §1 Motivation

OpenTelemetry's value proposition depends on standardized attribute names. When an SDK emits `http.request.method = "POST"`, every OTel backend — Grafana Tempo, Jaeger, Honeycomb, Datadog, AWS X-Ray, Azure Monitor — knows how to index, query, and aggregate that field. When it emits a domain attribute without convention, each backend treats it as an opaque tag and cross-system correlation breaks.

For telephony, no convention exists. Asterisk.Sdk today emits Activity tags like `asterisk.channel.id` and `call.direction` because the engineer writing the instrumentation picked a name that felt obvious — but a second consumer building a Grafana dashboard is guessing at the shape, a third consumer writing a correlation join is guessing at the type, and a fourth consumer integrating with a VoIP-analytics platform (CallMiner, Invoca, Observe.AI) is either translating or living without correlation.

This draft does three things:

1. Proposes attribute names, types, and example values for the information Asterisk.Sdk is already producing.
2. Maps each attribute to the existing `ActivitySource` / `Meter` where it should be emitted.
3. Sketches a spec-issue contribution plan so the convention can move upstream once validated by field use.

No code behavior change is implied by this draft. The companion work — aligning the 9 ActivitySources to emit these attributes — is scoped separately (see §6).

---

## §2 Asterisk.Sdk telemetry surface — today

The SDK exposes a discovery helper at [`src/Asterisk.Sdk.Hosting/AsteriskTelemetry.cs`](../../src/Asterisk.Sdk.Hosting/AsteriskTelemetry.cs):

**9 ActivitySources** (traces):
`Asterisk.Sdk.Ami`, `Asterisk.Sdk.Ari`, `Asterisk.Sdk.Agi`, `Asterisk.Sdk.Live`, `Asterisk.Sdk.Sessions`, `Asterisk.Sdk.Push`, `Asterisk.Sdk.VoiceAi`, `Asterisk.Sdk.VoiceAi.AudioSocket`, `Asterisk.Sdk.VoiceAi.OpenAiRealtime`.

**14 Meters** (metrics): the 9 above plus `Asterisk.Sdk.Ari.Audio`, `Asterisk.Sdk.Push.Webhooks`, `Asterisk.Sdk.Push.Nats`, `Asterisk.Sdk.VoiceAi.Stt`, `Asterisk.Sdk.VoiceAi.Tts`.

Each source produces spans keyed by different phases of a call: AMI events, ARI REST operations, dialplan execution, session lifecycle, push dispatch, STT/TTS turns, realtime streaming. Their tags today are ad-hoc. This draft proposes a unified set.

---

## §3 Proposed attributes — resource-level

Attributes that identify the Asterisk instance producing the telemetry. These are set once on the `Resource`, not per-span.

| Attribute | Type | Example | Rationale |
|-----------|------|---------|-----------|
| `asterisk.server.name` | string | `"asterisk-pbx-01"` | Matches the `AsteriskServer.Name` in the SDK's Live API. Cardinality is the deployment's node count. |
| `asterisk.server.version` | string | `"22.5.0"` | Asterisk version reported by `GetInfoAsync`. Stable across a process lifetime. |
| `asterisk.server.hostname` | string | `"pbx01.example.com"` | Kernel hostname. Redundant with `host.name` from OTel's HOST convention but kept for backends that read only the asterisk.* namespace. |
| `asterisk.sdk.version` | string | `"1.12.0"` | The `Asterisk.Sdk` package version. Useful for correlating bug reports to SDK releases. |

These four resource-level attributes answer "which box produced this trace?". They are cheap to join against and they belong on the resource rather than on every span.

---

## §4 Proposed attributes — span-level

Attributes that describe a specific operation. Cardinality must stay bounded (see §5).

### §4.1 Channel

Emitted on spans in `Asterisk.Sdk.Ari`, `Asterisk.Sdk.Live`, `Asterisk.Sdk.VoiceAi`, `Asterisk.Sdk.VoiceAi.AudioSocket` whenever a channel is the subject.

| Attribute | Type | Example | Notes |
|-----------|------|---------|-------|
| `asterisk.channel.id` | string | `"PJSIP/alice-00000042"` | Asterisk's `Channel.id` — unique per call, globally unique across the deployment when combined with server. |
| `asterisk.channel.name` | string | `"PJSIP/alice-00000042"` | Same as id on modern Asterisk but spec'd separately so that legacy `chan_*` drivers with divergent id/name remain legible. |
| `asterisk.channel.state` | string | `"Up"` / `"Ring"` / `"Busy"` / `"Hangup"` | Restricted to the state enum Asterisk publishes. |
| `asterisk.channel.driver` | string | `"PJSIP"` / `"AudioSocket"` / `"WebSocket"` | Lets consumers separate PJSIP signalling from ExternalMedia transports. |

### §4.2 Bridge

Emitted whenever a span's subject is a bridge (conference, two-party mix, holding bridge).

| Attribute | Type | Example | Notes |
|-----------|------|---------|-------|
| `asterisk.bridge.id` | string | `"4d3a6f12-..."` | Bridge UUID. |
| `asterisk.bridge.name` | string? | `"sales-conf"` | Optional human name. |
| `asterisk.bridge.type` | string | `"mixing"` / `"holding"` / `"meetme"` | Bridge technology. |
| `asterisk.bridge.channel_count` | int | `3` | Snapshot at span emission; not a time-series. For live counts use a Meter. |

### §4.3 Call and dialplan

Call-level correlation across multiple legs and transport spans. Emit on every span that can be tied to a call.

| Attribute | Type | Example | Notes |
|-----------|------|---------|-------|
| `call.id` | string | `"1728043200.42"` | Asterisk's `LinkedID` — stable across bridge/transfer. Primary call-correlation key. Analogous to OTel's `trace.id` but at the call domain, not the network-trace domain. |
| `call.direction` | string | `"inbound"` / `"outbound"` / `"internal"` | Relative to the deployed system. Inbound = external caller into PBX; Outbound = PBX places call; Internal = extension-to-extension. |
| `call.state` | string | `"dialing"` / `"ringing"` / `"talking"` / `"held"` / `"transferring"` / `"ended"` | High-level call lifecycle. Complements `asterisk.channel.state` which is channel-level. |
| `call.duration_ms` | int | `42000` | Populated only on call-completion spans. Avoid putting on mid-call spans (breaks the convention of span duration ≠ call duration). |
| `dialplan.context` | string | `"from-trunk"` | Dialplan context the channel is in. |
| `dialplan.extension` | string | `"1001"` | Extension being processed. |
| `dialplan.priority` | int | `1` | Priority step. Usually low cardinality (1, 2, 3, rarely >10). |
| `dialplan.application` | string | `"Dial"` / `"Queue"` / `"Stasis"` | The current dialplan app. |

### §4.4 SIP signalling

Emit on spans where the SIP protocol is directly relevant (PJSIP register/invite/re-invite/bye, response to a SIP transaction).

| Attribute | Type | Example | Notes |
|-----------|------|---------|-------|
| `sip.call_id` | string | `"abc123@example.com"` | SIP Call-ID header. Distinct from `call.id` — this is the SIP signalling dialog identifier; there can be one-to-many between `call.id` (LinkedID) and `sip.call_id`. |
| `sip.method` | string | `"INVITE"` / `"BYE"` / `"REGISTER"` / `"OPTIONS"` / `"REFER"` | Uppercase per RFC 3261. Bounded enum. |
| `sip.response_code` | int | `200` / `404` / `486` | SIP response code. Only on response-direction spans; absent on request-direction. |
| `sip.response_phrase` | string | `"OK"` / `"Not Found"` / `"Busy Here"` | Optional readable description. Do not enforce — some stacks send custom phrases. |
| `sip.from_uri` | string | `"sip:alice@example.com"` | Caller URI. High cardinality — see §5. |
| `sip.to_uri` | string | `"sip:bob@example.com"` | Callee URI. High cardinality. |
| `sip.user_agent` | string | `"Grandstream GXP1628"` / `"Polycom VVX 450"` | Caller's device string from User-Agent header. Moderate cardinality (device models are bounded). |
| `sip.transport` | string | `"udp"` / `"tcp"` / `"tls"` / `"ws"` / `"wss"` | Transport used by the signalling leg. |

### §4.5 Media (RTP, codec, jitter)

Emit on spans where the audio path is the subject. Typically in `Asterisk.Sdk.VoiceAi.AudioSocket` and `Asterisk.Sdk.VoiceAi` pipeline spans.

| Attribute | Type | Example | Notes |
|-----------|------|---------|-------|
| `media.codec` | string | `"opus"` / `"ulaw"` / `"alaw"` / `"g722"` / `"slin16"` | The negotiated codec. |
| `media.sample_rate` | int | `8000` / `16000` / `48000` | In Hz. |
| `media.direction` | string | `"in"` / `"out"` / `"both"` | Matches the `chan_websocket` `SET_MEDIA_DIRECTION` values so direct mapping works. |
| `media.bitrate_bps` | int | `64000` | Optional. Useful for Opus/VBR streams. |
| `media.frames_received` | int | `1234` | Count up to this span boundary. Does not replace the Meter counter; useful on span close for dashboards that read spans only. |
| `media.frames_lost` | int | `2` | Packets/frames dropped detected during the span. |

### §4.6 Queue / agent

Emit on spans within `Asterisk.Sdk.Live.Queues` and `Asterisk.Sdk.Sessions.Queues`.

| Attribute | Type | Example | Notes |
|-----------|------|---------|-------|
| `asterisk.queue.name` | string | `"sales"` | Queue name. |
| `asterisk.queue.strategy` | string | `"leastrecent"` / `"fewestcalls"` / `"ringall"` | Configured strategy. |
| `asterisk.queue.wait_ms` | int | `12000` | Caller wait time at span end. |
| `asterisk.agent.id` | string | `"1001"` / `"agent-alice"` | Agent identifier. |
| `asterisk.agent.state` | string | `"ready"` / `"busy"` / `"unavailable"` / `"paused"` | Agent lifecycle state. |

### §4.7 VoiceAI

Emit on `Asterisk.Sdk.VoiceAi`, `.Stt`, `.Tts`, `.OpenAiRealtime` spans.

| Attribute | Type | Example | Notes |
|-----------|------|---------|-------|
| `voiceai.provider` | string | `"OpenAI"` / `"Deepgram"` / `"Azure"` / `"Cartesia"` / `"AssemblyAI"` / `"Speechmatics"` / `"ElevenLabs"` / `"Google"` / `"Whisper"` | Mirrors the `ProviderName` property already emitted (ADR-0005). |
| `voiceai.operation` | string | `"stt"` / `"tts"` / `"llm"` / `"vad"` / `"realtime"` | What kind of AI operation this span represents. |
| `voiceai.model` | string | `"sonic-3"` / `"ink-whisper"` / `"nova-2"` / `"gpt-4o-realtime"` | The specific model in use. |
| `voiceai.language` | string | `"en"` / `"es"` / `"pt-BR"` | BCP-47 language tag. |
| `voiceai.latency.ttft_ms` | int | `220` | Time to first token / first audio. |
| `voiceai.latency.ttfb_ms` | int | `350` | Time to first byte of response. |
| `voiceai.tokens.input` / `.output` | int | `42` / `128` | LLM token counts when the span wraps an LLM call. |
| `voiceai.audio.duration_ms` | int | `3200` | Audio length produced/consumed. |
| `voiceai.interrupted` | bool | `false` / `true` | Whether the turn was cut short by barge-in. |

---

## §5 Cardinality guidance

Attribute cardinality is the enemy of OpenTelemetry backends at scale. The proposal separates attributes into three tiers:

**Low cardinality (safe for metrics labels):** `call.direction`, `call.state`, `asterisk.channel.state`, `asterisk.channel.driver`, `asterisk.bridge.type`, `sip.method`, `sip.response_code` (bounded to HTTP-style response codes — ~50), `media.codec`, `media.direction`, `media.sample_rate`, `asterisk.queue.strategy`, `asterisk.agent.state`, `voiceai.operation`, `voiceai.provider`, `voiceai.interrupted`. Use these freely as metric labels.

**Medium cardinality (safe for traces, costly as metric labels):** `asterisk.server.name`, `asterisk.queue.name`, `dialplan.context`, `dialplan.application`, `voiceai.model`, `voiceai.language`, `sip.user_agent`. Use on every span, but in metrics reserve for low-dimensional counters (per-queue throughput, not per-queue + per-context cross-product).

**High cardinality (traces only):** `asterisk.channel.id`, `asterisk.bridge.id`, `call.id`, `sip.call_id`, `sip.from_uri`, `sip.to_uri`, `asterisk.agent.id`. These are per-call identifiers. They are essential for trace correlation but must never become metric dimensions — a one-day window of metric samples aggregated by `call.id` would produce millions of unique time-series per day in a production PBX.

Each metric emitted by the SDK should pick at most 3-4 low-cardinality dimensions. Medium-cardinality metrics are acceptable only when the dimension has a closed set (e.g. 5 queue names, 3 providers).

---

## §6 Alignment work

Turning this draft into reality requires three follow-up items. None is blocking for v1.12.0.

1. **Audit every `Activity.SetTag` and `Meter.Create*` call-site in `src/`**. Today the spans set tags like `ChannelId` (PascalCase property shadow), `provider.name`, `audio.codec` — a mix of conventions. Align each call to the snake-case proposal above. Scope: ~40 call-sites across 9 ActivitySources.
2. **Add `AsteriskSemanticConventions` static class** in `Asterisk.Sdk.Hosting` that exposes each attribute name as a const string (`public const string ChannelId = "asterisk.channel.id";` etc.). This is the same pattern OTel itself uses. Gives consumers IntelliSense completion and prevents typos.
3. **Submit the draft upstream** as a comment on OTel spec issue #2517 once field-validated on at least one production deployment. If the community accepts it, move the attributes into OTel's own namespaces; if rejected, keep in the `asterisk.*` / domain-specific namespaces as shipped.

Estimated effort: item 1 is 2-3 days (mostly mechanical), item 2 is 1 hour, item 3 is forum time not dev time. Put on the v1.13 or v1.14 roadmap.

---

## §7 Reference backend mapping

How the proposal reads on the three most common OTel backends today:

- **Grafana Tempo / Loki / Mimir:** `call.id` becomes a Loki label for log joins; all span attributes become Tempo attributes queryable via TraceQL. `voiceai.provider` is a low-cardinality Mimir metric label that can be a top-of-dashboard filter.
- **Honeycomb:** Every attribute indexed automatically. `sip.call_id` and `call.id` are perfect BubbleUp dimensions for slicing anomaly detection. `voiceai.latency.ttft_ms` becomes a histogram dimension for SLO tracking.
- **Datadog APM:** Attributes show up as Tags. `call.direction` is a natural grouping dimension. `voiceai.model` enables per-model performance comparison.

None of these backends requires a non-standard setup to consume the proposal — the attributes are plain string/int/bool, nested under predictable prefixes.

---

## §8 Relationship to W3C Trace Context

This proposal does not replace W3C traceparent propagation. The SDK already captures traceparent across the Push bus (v1.10.1/v1.10.2, ADR-0019 draft) and will continue to do so. The SIP/telephony attributes here are the **payload**; traceparent is the **envelope**. Both coexist.

Specifically: a call that spans multiple SDK processes (e.g. AMI node → Push bus → ARI node → VoiceAi worker) carries `call.id` end-to-end as an attribute and traceparent end-to-end as a header. The two identifiers are independent — `call.id` is a domain identifier that outlives any single trace; `trace.id` is a technical identifier that lives only for a bounded trace duration. Operators slice with `call.id` for call-level analysis, and drill into `trace.id` for request-level analysis within a call.

---

## §9 Open questions

These are honest gaps in the draft worth calling out:

- **Multi-tenant attribution.** Asterisk.Sdk.Pro (private repo) supports tenant isolation. A convention like `tenant.id` would be cleanly on top of this proposal, but whether it belongs in a SIP/VoIP spec (vs in OTel's general multi-tenancy conventions, which also do not exist) is a bigger question. Deferred.
- **Transfer and conference semantics.** When a call is attended-transferred, `call.id` changes mid-call (from the original LinkedID to a new one). The proposal does not prescribe how to represent this. A follow-up could add `call.previous_id` and `call.transfer.kind` (attended / blind / transfer-target) to handle the lineage.
- **Encrypted signalling.** `sip.from_uri` and `sip.to_uri` are PII in many jurisdictions. The proposal does not prescribe redaction policy; SDK consumers should apply their own via SpanProcessor.
- **Recording.** A `asterisk.recording.id` / `asterisk.recording.name` attribute set makes sense but is outside the v1.11 SDK's Live API scope. Propose separately once recordings are first-class citizens.

---

## §10 Next steps

- Park this document for 1-2 months of field use before formalizing.
- After field validation: add `AsteriskSemanticConventions` static class + tag audit (item 2 and 1 in §6).
- After adoption in code: open a comment on [OTel spec issue #2517](https://github.com/open-telemetry/opentelemetry-specification/issues/2517) with this proposal as a concrete contribution.

The goal is not to win the upstream spec race — the goal is to make Asterisk.Sdk's telemetry coherent, predictable, and useful on every OTel backend today, and to position the SDK as the reference implementation for SIP/VoIP semantic conventions when the wider community eventually standardizes them.
