# NatsBridgeExample

Demonstrates the **`Asterisk.Sdk.Push.Nats`** bridge added in v1.12.0 — each event published on the in-process `RxPushEventBus` is republished to a NATS subject derived from the topic path, unlocking multi-node deployments where one SDK process fans out to an arbitrary number of remote subscribers.

This example runs a producer (the SDK `IPushEventBus` → `Asterisk.Sdk.Push.Nats` bridge) **and** an inline NATS subscriber in the same process, so you can see the end-to-end flow without spinning up a separate consumer app.

## Prerequisites

- .NET 10 SDK
- A reachable NATS server (default `nats://127.0.0.1:4222`)

Quick local NATS server via Docker:

```bash
docker run --rm -p 4222:4222 nats:2.10-alpine
```

## Run

```bash
dotnet run --project Examples/NatsBridgeExample/
```

You should see:

```
Push.Nats bridge started → nats://127.0.0.1:4222 (prefix: asterisk.sdk)
[push] published topic=calls.inbound.started
[nats] subject=asterisk.sdk.calls.inbound.started body={ ... }
[push] published topic=agents.42.state
[nats] subject=asterisk.sdk.agents.42.state body={ ... }
[push] published topic=queues.sales.wait
[nats] subject=asterisk.sdk.queues.sales.wait body={ ... }
```

## What It Shows

- `AddAsteriskPush` + `AddPushNats` — two DI calls wire the in-process bus and the NATS bridge.
- Subject translation — the SDK's `TopicPath` (`calls.inbound.started`) becomes the NATS subject `asterisk.sdk.calls.inbound.started`. `NatsSubjectTranslator` handles both `/` and `.` separators and sanitizes NATS-invalid characters.
- Wildcard consumption — the inline subscriber listens on `asterisk.sdk.>` and receives every event.
- Envelope shape — the payload is the same JSON envelope produced by `Asterisk.Sdk.Push.Webhooks`, so consumers can treat both transports interchangeably.

## Production Deployment Pattern

In a real multi-node deployment:

- Each SDK instance publishes to a single shared NATS cluster.
- A separate service subscribes to `asterisk.sdk.calls.>` / `asterisk.sdk.agents.>` / etc. from any host.
- NATS handles fan-out, back-pressure, and horizontal scaling.

This is the pattern the Go-based `CyCoreSystems/ari-proxy` popularized; `Asterisk.Sdk.Push.Nats` is the .NET counterpart.

## Key SDK Packages Used

- `Asterisk.Sdk.Push` — `IPushEventBus`, `PushEventMetadata`.
- `Asterisk.Sdk.Push.Nats` — `AddPushNats`, `NatsSubjectTranslator`.
