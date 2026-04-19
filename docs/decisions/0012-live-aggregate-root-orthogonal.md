# ADR-0012: Live domain state as aggregate root orthogonal to AMI/ARI transports

- **Status:** Accepted
- **Date:** 2026-04-19 (retrospective)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0006 (pluggable ISessionStore)

## Context

Asterisk exposes its state through two transports — AMI (events + actions) and ARI (WebSocket events + REST resources). Both carry the same underlying state: channels, bridges, queues, agents, meet-me conferences. A consumer who wants "tell me about channel X" needs to aggregate events from whichever transport is active, apply per-entity state machines (Dialing → Up → Ringing → Busy), and expose a domain view.

We had three ways to organize that aggregation:

1. **Fold into AMI package.** Live lives in `Asterisk.Sdk.Ami`, populated from AMI events, ignores ARI.
2. **Fold into ARI package.** Same, backwards.
3. **Separate package.** Live is its own aggregate root consuming both AMI and ARI as data sources.

Option 1 was the asterisk-java ancestry (everything AMI). Option 3 is where we landed.

## Decision

`Asterisk.Sdk.Live` is a **separate package** owning the live domain state. `AsteriskServer` is the aggregate root:

- `Channels` — per-channel state machine (Dialing/Ringing/Up/Busy/Hangup transitions).
- `Bridges` — conference + 2-party bridges.
- `Queues` + `Agents` — ACD model (enqueue, deliver, answer, abandon).
- `MeetMes` — ConfBridge rooms.

It consumes AMI events via `IAmiConnection` and/or ARI events via `IAriClient` — transport-agnostic. The `Asterisk.Sdk.Sessions` package (session engine from ADR-0006) is layered on top; sessions are cross-channel trackers that reference Live entities.

## Consequences

- **Positive:** Consumers who use only ARI (modern Stasis apps) don't pull the full AMI event-mapping code into their AOT binary. Consumers who use only AMI don't pull ARI REST clients. Live can add new event sources later (ARI Events API updates, future protocols) without breaking AMI or ARI packages. The state machines are the contract; how they got populated is an implementation detail.
- **Negative:** Two extra package boundaries to maintain (MIT consumers typically pull `Live` + one of AMI/ARI). Internal types that Live needs from AMI must be `public` (can't stay `internal`) so that cross-assembly reflection-free access works.
- **Trade-off:** We accept the extra package count for the trim-friendliness and the "add a new source later without breaking consumers" optionality.

## Alternatives considered

- **Fold into `Asterisk.Sdk.Ami`** — rejected because ARI-only consumers would be forced to install AMI. Also, the asterisk-java precedent is a JIT-runtime — trim/AOT was not a concern there and everything-in-one-jar was fine.
- **Fold into `Asterisk.Sdk.Ari`** — rejected because the SDK's primary current audience uses AMI more heavily (legacy Asterisk deployments), and AMI-only consumers shouldn't pull ARI REST clients.
- **Merge into a hypothetical `Asterisk.Sdk.Core`** — rejected because such a core package becomes an everything-sink over time (we saw this pattern in asterisk-java's `Manager` class). Separate packages enforce cohesion.

## Notes

- Code: `src/Asterisk.Sdk.Live/Server/AsteriskServer.cs` lines 36–45 (aggregate root composition).
- Per-entity locks: `Asterisk.Sdk.Live/Agents/AgentManager.cs` uses one lock per agent (not a global lock) to allow concurrent state mutations across agents under load. Benchmarks: 6.1 ns `ChannelManager.GetById`, 135 M lookups/sec.
- Pro (`Asterisk.Sdk.Pro.Cluster`) extends this by federating `AsteriskServer` state across SDK instances — Live stays MIT, federation is Pro.
- The package also ships `LiveMetrics` (`AsteriskTelemetry.LiveMeter`) for per-entity counters: channels created/destroyed, queue depth, agent state transitions.
