# Asterisk.Sdk.Push.Nats

NATS bridge for `Asterisk.Sdk.Push`. The bridge subscribes to the in-process Push bus and republishes every event to a NATS subject derived from the event's `TopicPath`. This unlocks multi-node deployments: one NATS cluster, N SDK instances, each fans out local events to the cluster for topic-based filtering by remote subscribers.

This is the .NET answer to the Go-based `ari-proxy` pattern: keep the SDK's local Rx bus as the source of truth, let NATS be the transport when you need horizontal scale.

## Usage

```csharp
using Asterisk.Sdk.Push.Hosting;
using Asterisk.Sdk.Push.Nats;

builder.Services.AddAsteriskPush()
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

- **Custom payload shape:** implement `INatsPayloadSerializer` and register as singleton before `AddPushNats`. The default serializer emits the same envelope as `Asterisk.Sdk.Push.Webhooks`, so downstream consumers can treat both transports interchangeably.

## Observability

Counters on the `Asterisk.Sdk.Push.Nats` meter:

- `asterisk.push.nats.events.published`
- `asterisk.push.nats.events.failed`

Enroll via `Asterisk.Sdk.OpenTelemetry.WithAllSources()` once the meter name is added to `AsteriskTelemetry.MeterNames`.

## Roadmap

- `PublishOnly = false` — subscribe to remote NATS subjects and republish inbound events onto the local Push bus (planned for a later v1.12.x release, tracked in `docs/research/2026-04-19-v1.12.0-product-opportunities.md`).
- JetStream durable subscriptions + ordered consumer support.
