# Verbara.Sdk.Push.Nats

NATS bridge for `Verbara.Sdk.Push`. The bridge subscribes to the in-process Push bus and republishes every event to a NATS subject derived from the event's `TopicPath`. This unlocks multi-node deployments: one NATS cluster, N SDK instances, each fans out local events to the cluster for topic-based filtering by remote subscribers.

This is the .NET answer to the Go-based `ari-proxy` pattern: keep the SDK's local Rx bus as the source of truth, let NATS be the transport when you need horizontal scale.

## Usage

```csharp
using Verbara.Sdk.Push.Hosting;
using Verbara.Sdk.Push.Nats;

builder.Services.AddVerbaraPush()
                .AddPushNats(opts =>
                {
                    opts.Url = "nats://nats.internal:4222";
                    opts.SubjectPrefix = "asterisk.sdk";
                    opts.Username = "sdk-bridge";
                    opts.Password = builder.Configuration["NATS_PASSWORD"];
                    opts.ConnectTimeoutSeconds = 10;
                });
```

## Subject translation

The bridge maps Push topic paths to NATS subjects by replacing separators with `.`, skipping empty segments, and sanitizing characters that NATS forbids. Example:

| Push `TopicPath` | NATS subject (prefix `asterisk.sdk`) |
|------------------|--------------------------------------|
| `push.channels.uniqueid-42` | `asterisk.sdk.push.channels.uniqueid-42` |
| `push/channels/uniqueid-42` | `asterisk.sdk.push.channels.uniqueid-42` |
| `queues/42/agent state` | `asterisk.sdk.queues.42.agent_state` |
| `calls.*.ended` | `asterisk.sdk.calls._.ended` (wildcards are disallowed in subjects — they become `_`) |

Both `.` and `/` are accepted as input separators so callers are not locked into one convention.

## Extension points

- **Custom payload shape:** implement `INatsPayloadSerializer` and register as singleton before `AddPushNats`. The default serializer emits the same envelope as `Verbara.Sdk.Push.Webhooks`, so downstream consumers can treat both transports interchangeably.

## Observability

Counters on the `Verbara.Sdk.Push.Nats` meter:

- `asterisk.push.nats.events.published`
- `asterisk.push.nats.events.failed`
- `asterisk.push.nats.events.received`
- `asterisk.push.nats.events.skipped`
- `asterisk.push.nats.events.decode_failed`

Enroll via `Verbara.Sdk.OpenTelemetry.WithAllSources()` — the meter name is already registered in `VerbaraTelemetry.MeterNames`.

## Subscribe side

- Enabled by setting `Subscribe = new NatsSubscribeOptions { ... }`: the bridge consumes from NATS if and only if `Subscribe` is non-null. `PublishOnly` is kept only for compatibility with v1.12 configurations.
- `SkipSelfOriginated` then requires `NodeId`, and startup validation refuses the configuration without it (loop prevention).
- Remote events arrive on the local bus as `RemotePushEvent`.
- JetStream and durable replay are out of scope for this MIT package.
