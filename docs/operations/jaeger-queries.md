# Jaeger / Tempo query patterns — Asterisk SDK ActivitySources

Example searches for the 9 SDK `ActivitySource`s. All tag keys use `AsteriskSemanticConventions` constants so queries stay stable across SDK versions. Replace `{VAR}` placeholders with concrete values.

## Source names

The SDK publishes these source names (`Asterisk.Sdk.Hosting.AsteriskTelemetry.SourceNames` is the canonical catalog):

| Source | Emitted by |
|---|---|
| `Asterisk.Sdk.Ami` | AMI connection handshake, action send/response |
| `Asterisk.Sdk.Ari` | ARI REST calls, WebSocket subscribe / receive |
| `Asterisk.Sdk.Agi` | FastAGI session execution |
| `Asterisk.Sdk.Sessions` | Session manager lifecycle (Created / Attached / Completed) |
| `Asterisk.Sdk.Activities` | Activity state machines (Call, Hold, Transfer, Play) |
| `Asterisk.Sdk.VoiceAi` | STT/TTS pipeline spans (per-turn) |
| `Asterisk.Sdk.VoiceAi.AudioSocket` | AudioSocket session accept / stream lifecycle |
| `Asterisk.Sdk.Push` | Local push event dispatch + NATS bridge publish/receive |
| `Asterisk.Sdk.Push.Webhooks` | Webhook HTTP delivery (retry + circuit) |

## Queries

### End-to-end call trace for a tenant

```
service.name="your-app" asterisk.tenant.id="{TENANT_ID}" call.id="{LINKED_ID}"
```

Filters all spans scoped to a single call regardless of which SDK `ActivitySource` emitted them — the `call.id` (Asterisk `LinkedID`) is stable across bridges and transfers.

### Slow AMI actions (> 1 s)

```
service.name="your-app" span.name=~"ami.action.*" duration > 1s
```

Narrow by action type with `ami.action.type="Originate"`.

### ARI WebSocket drops

```
service.name="your-app" span.name="ari.ws.disconnected"
```

Group by `ari.reason` tag to quantify which disconnect causes dominate.

### Push event hot path

```
service.name="your-app" span.name=~"push.(publish|subscribe|dispatch)" event.type="{EVENT_TYPE}"
```

Replace `{EVENT_TYPE}` with e.g. `conversation.state.changed` to trace a specific event class through the bus + webhook pipeline.

### Webhook deliveries that opened a circuit

```
service.name="your-app" span.name=~"webhook.delivery.*" event="webhook.circuit.opened"
```

Correlate with the `asterisk.push.webhooks.circuit.opened` counter to see which URL(s) are tripping the breaker.

### VoiceAi latency outliers

```
service.name="your-app" span.name=~"voiceai.(stt|tts).turn" voiceai.latency.ttfb_ms > 500
```

Uses the `voiceai.latency.ttfb_ms` tag (see `AsteriskSemanticConventions.VoiceAi.LatencyTtfbMs`) to surface turns where Time-To-First-Byte exceeded 500 ms. Group by `voiceai.provider` + `voiceai.model` to isolate provider regressions.

### Cross-node push trace continuity

```
service.name="your-app" span.name="push.nats.receive" trace.id="{TRACE_ID}"
```

Given the publisher's `trace.id`, verify the receive-side span inherited the W3C `traceparent` carried in the push envelope — parent span id should match the publisher's active span.

### Sessions reconciliation

```
service.name="your-app" span.name="sessions.reconcile" asterisk.tenant.id="{TENANT_ID}"
```

Reconciliation spans carry `session.reconcile.attached` / `session.reconcile.detached` / `session.reconcile.orphaned` counters as tags — group by these to quantify reconciler effectiveness.

## Notes

- The `traceparent` flowing through the push backplane is captured by `RxPushEventBus.PublishAsync` automatically (SDK ≥ 1.10.2). Consumers don't need to wire it manually.
- All queries assume OTel collector default attribute preservation; if you strip `asterisk.*` prefixes at the collector, adjust filters accordingly.
- Emitted `Activity` names, kinds, and tag sets are documented in the per-module `Diagnostics/*ActivitySource.cs` files — grep for `StartActivity` to find emission sites.
