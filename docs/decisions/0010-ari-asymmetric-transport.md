# ADR-0010: ARI asymmetric transport — WebSocket for events, HTTP REST for commands

- **Status:** Accepted
- **Date:** 2026-04-19 (retrospective)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0001 (AOT-first), ADR-0003 (source generators)

## Context

Asterisk REST Interface (ARI) is Asterisk's modern control plane: channels, bridges, playback, stasis apps, and live events. Unlike AMI (single TCP, text protocol, events + commands interleaved), ARI splits transport:

- **Events** flow over a long-lived WebSocket — push-oriented, server-initiated.
- **Commands** go over plain HTTP REST — pull-oriented, client-initiated, request/response with JSON.

This is Asterisk's design; clients don't choose. But we do choose how to **model** it in `AriClient`.

Options for the SDK:

1. **Mirror Asterisk's split** — one `ClientWebSocket` for events + one `HttpClient` for REST commands in a single `AriClient` facade.
2. **Unify behind a single API surface** — hide the transport, expose only "send command / subscribe to events" on an abstract bus.
3. **Split into two separate clients** — `AriEventStream` + `AriRestClient` as independent types consumers compose manually.

## Decision

`AriClient` **mirrors Asterisk's split**: it owns both a `ClientWebSocket` (events only, fire-and-forget to subscribers) and an `HttpClient` (REST commands with per-call `CancellationToken`). The two transports share the same `AriClientOptions` (host, port, auth, SSL) but are independently lifecycle-managed.

The facade exposes:

- `StartAsync(ct)` / `StopAsync(ct)` — manages the WebSocket loop.
- Typed REST resource properties: `client.Channels`, `client.Bridges`, `client.Playbacks`, `client.Endpoints`, etc. Each returns a source-generated client backed by `HttpClient`.
- `IObservable<AriEvent>` and specific typed observables — the WebSocket loop dispatches.

No unified "bus" abstraction. No split into two clients.

## Consequences

- **Positive:** Consumers see one object, one config, one lifetime. Command → immediate HTTP response (no event-correlation dance). Event subscribers get strongly-typed `StasisStart` / `ChannelDtmfReceived` etc. directly. WebSocket reconnection is internal; REST retries are per-call (consumers decide per command).
- **Negative:** Two sockets per `AriClient` instance — `NgHttpClient` + `ClientWebSocket`. For consumers running many AriClients (multi-server deployments), that's 2× the FDs of a unified design. The WebSocket loop and HTTP client must be disposed in the right order.
- **Trade-off:** We accept the two-transport complexity inside `AriClient` to keep the consumer API natural (commands are synchronous, events are streams).

## Alternatives considered

- **Unified abstract bus** — rejected because it forces command/response flows into a pub-sub mental model, losing the HTTP status code + response body semantics that REST gives for free. Also would add a correlation-ID layer that isn't in the Asterisk ARI protocol.
- **Split into `AriEventStream` + `AriRestClient`** — rejected because consumers always need both (an ARI app subscribes to events and then calls commands on the channels it observed). Forcing composition on the consumer adds ceremony without benefit.
- **HTTP polling instead of WebSocket for events** — rejected because ARI event timing (DTMF, Stasis entry) is latency-sensitive and Asterisk only publishes via WS.

## Notes

- Code: `src/Asterisk.Sdk.Ari/Client/AriClient.cs` lines 56–62 (two transports), 147–264 (event loop).
- 8 REST resource clients are source-generated from the Asterisk 22 OpenAPI JSON via `Asterisk.Sdk.Ari.SourceGenerators` (see ADR-0003).
- Per-instance `AriClient` is the unit we pool in `IAriClientFactory` for multi-server deployments.
- Benchmarks: `ARI parse StasisStart` 1.68 µs / `ARI Channel deserialize` 283 ns / `ARI Channel serialize` 148 ns. The event loop in `EventLoopAsync` is zero-alloc on the hot path (per ADR-0003's source-gen).
