# ADR-0007: Hierarchical topic tree for the push event bus

- **Status:** Accepted
- **Date:** 2026-04-13 (retrospective — ships in v1.8.0+)
- **Deciders:** Harol A. Reina H.
- **Related:** `Asterisk.Sdk.Push`, `Asterisk.Sdk.Push.AspNetCore`, `Asterisk.Sdk.Push.Webhooks`

## Context

The SDK dispatches a lot of real-time events: call state changes, agent presence, queue depth, voice-AI transcript snippets. Consumers (dashboards, webhooks, downstream services) care about **slices** of that firehose:

- A dashboard for queue "support" wants `calls.queue.support.*`.
- An AI coach wants `calls.agent.{self}.voiceai.*` for exactly the agent session it's attached to.
- An auditing webhook wants `calls.**` (everything), with HMAC signing.

The first version of the bus (v1.6) used flat topic strings with regex subscriptions. That worked but was hard to reason about: "does `calls.queue.*` match `calls.queue.support.joined`?" depended on whether you wrote `.*` or `.*\..*`.

## Decision

Topics are a **hierarchical dot-separated tree**. Two first-class types:

- `TopicName` = a concrete path, e.g. `calls.queue.support.joined`. Publishers emit these.
- `TopicPattern` = a subscription pattern with two wildcards: `*` (single segment), `**` (zero or more segments), and `{self}` (scoped substitution at subscribe time, replaced with the subscriber's session/agent ID).

Matching is segment-by-segment; `calls.**` matches every `calls.*` path at any depth. Subscriptions go through `ISubscriptionAuthorizer` so a consumer can enforce "agent X can only subscribe to `calls.agent.X.**`" server-side.

Delivery has three transports:

- In-process `IObservable<PushEvent>` (v1.6, core).
- Server-Sent Events via `Asterisk.Sdk.Push.AspNetCore` (v1.8).
- Outbound webhooks with HMAC-SHA256 signing via `Asterisk.Sdk.Push.Webhooks` (v1.11).

## Consequences

- **Positive:** Matching is deterministic and explainable — users stop asking "why didn't my subscription fire?" Authorization is a hook, not a rewrite. The three transports share one topic model, so an event published once reaches every interested consumer. Webhook signing is standard HMAC, so clients can verify with any library.
- **Negative:** Two types (`TopicName` vs `TopicPattern`) add a small learning curve. `{self}` substitution is scoped at subscribe time, not match time — consumers who want dynamic IDs have to re-subscribe when the ID changes.
- **Trade-off:** We accept the complexity of the pattern mini-language in exchange for a bus that scales from 1 subscriber to 1000+ without code changes.

## Alternatives considered

- **Raw regex subscriptions** — rejected because regexes are hard to debug and wildly variable in match cost; `calls\.queue\..*\.joined` and `.*joined.*` look similar and behave wildly differently.
- **MQTT-style topic tree without `{self}`** — rejected because contact-center UIs need per-agent scoping as a core primitive; bolting it on later would fork the API.
- **Message-bus libraries (MassTransit, NServiceBus)** — rejected because the push bus is a lightweight real-time fan-out, not a durable command bus; those libraries bring dependencies (RabbitMQ, Azure Service Bus) that conflict with ADR-0001's AOT-first stance.

## Notes

- Full guide: `src/Asterisk.Sdk.Push/README.md`.
- Webhook subscriber example: `Examples/WebhookSubscriberExample/`.
- Metrics: 4 counters per subscription on `AsteriskTelemetry.PushWebhooksMeter` — `deliveries.succeeded`, `deliveries.failed`, `deliveries.retried`, `deliveries.dead_letter`.
