# ADR-0021: AMI heartbeat detection strategy (enabled by default, 30 s interval, 10 s timeout)

- **Status:** Accepted
- **Date:** 2026-03-26 (retrospective — decision made during the v1.5.1 stability hardening)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0008 (AMI exponential backoff reconnect)

## Context

AMI runs over a long-lived TCP connection between the SDK and the Asterisk `manager` interface. TCP on its own is not a liveness signal: a half-open connection — where the peer has crashed, rebooted, or been partitioned away — can remain in an "ESTABLISHED" state on the local side for minutes. From the SDK's point of view, the socket is still writable and reads block waiting for bytes that will never come. Every millisecond spent in that state is a window in which the application thinks it has a live Asterisk and silently drops actions into a dead socket.

The socket's own read timeout is the obvious first line of defence. Set `ReceiveTimeout` to a short window and the socket errors out when no data arrives. But AMI is an asynchronous protocol: events arrive when Asterisk has something to report, and during a quiet interval (say, a small PBX at night) the stream can be silent for many minutes. A receive timeout short enough to detect a network partition quickly would also tear down an idle but healthy connection, producing the opposite failure: churn.

The heartbeat is the solution to that dilemma. The SDK sends a `Ping` action on a regular interval; Asterisk responds with `Pong`. If the response does not arrive within a bounded window, the connection is declared dead and the reconnect loop (ADR-0008) takes over. The heartbeat separates "we have seen traffic recently" from "nothing is happening", and only the former counts as liveness.

The default values — 30 s interval, 10 s timeout — are the product of the v1.5.1 stability sprint. An interval shorter than 30 s adds observable action load on the manager interface during steady state (every AMI action the SDK sends competes for the same socket, and heartbeat actions are not free). A timeout shorter than 10 s false-positives under ordinary GC pauses and stop-the-world moments. A timeout longer than 10 s delays detection past the point that consumers report as "feeling unresponsive". The 30 s / 10 s pair has held up across v1.5.1 → v1.11.1 without a single tuning complaint.

Heartbeat is enabled by default because the cost of false-negatives (silent half-open connection) is much higher than the cost of false-positives (a detected-alive-but-slow PBX reconnects once unnecessarily).

## Decision

`AmiConnectionOptions` exposes three knobs: `EnableHeartbeat` (default `true`), `HeartbeatInterval` (default `30 s`), `HeartbeatTimeout` (default `10 s`). `AmiConnection` runs a heartbeat loop alongside the read loop when enabled: it sends a `Ping` action every `HeartbeatInterval`, and if a `Pong` does not arrive within `HeartbeatTimeout` it triggers the reconnect path defined in ADR-0008. The heartbeat is distinct from — and does not replace — the socket's own `ReceiveTimeout`; the two work together.

## Consequences

- **Positive:**
  - Half-open AMI connections are detected within 40 s in the worst case (30 s wait for next heartbeat + 10 s for the `Pong` timeout), regardless of traffic volume.
  - The defaults hold across deployment profiles from small (one PBX, low traffic) to large (fleet of nodes, high traffic) without tuning.
  - Heartbeat-enabled-by-default matches the expectations of operators who assume "a managed client detects disconnection". No flag to discover, no runbook entry required for baseline resilience.
  - Works in concert with ADR-0008: heartbeat detects the dead connection, backoff handles the reconnect dance.
- **Negative:**
  - On a PBX running at many thousands of actions per second, the additional `Ping` every 30 s is statistically negligible but does cross the manager wire and get logged in verbose AMI traces. Operators reading full AMI dumps will see the heartbeat noise.
  - A misconfigured `manager.conf` that denies the `ping` permission would break the heartbeat mechanism without breaking other AMI actions. The SDK would reconnect on every heartbeat timeout, producing a reconnect-loop symptom. Troubleshooting requires reading the heartbeat error log and correlating with the ACL.
- **Trade-off:** We trade some trivial ping traffic for half-open-connection detection within a bounded window. The alternative — no heartbeat, rely on socket timeouts — means that a healthy but idle connection is indistinguishable from a dead one, which is the exact failure the heartbeat exists to disambiguate. The audit in [`docs/research/2026-04-19-product-alignment-audit.md`](../research/2026-04-19-product-alignment-audit.md) §4 item #2 flagged this as a decision that could be conflated with socket-level timeouts by someone unfamiliar with the AMI protocol shape.

## Alternatives considered

- **Socket-level read timeout alone** — rejected because AMI idles are legitimate on small deployments; a timeout short enough to detect partitions would kill healthy idle connections. The heartbeat exists exactly to separate "idle" from "dead".
- **Heartbeat disabled by default (opt-in)** — rejected because the cost of the feature being off is silent half-open connections in the field, and the cost of the feature being on is negligible ping traffic. Defaults should match the safe choice.
- **TCP `SO_KEEPALIVE` instead of application-level ping** — rejected because TCP keepalive's defaults are OS-tunable at minutes-to-hours granularity and not reliably applied to connections behind NAT devices or load balancers. Application-level heartbeat is bounded, deterministic, and crosses every intermediary that passes AMI traffic.
- **Piggyback on existing AMI events (treat any received event as a heartbeat)** — rejected because it only works on a PBX with active call volume; a quiet PBX would look dead. A dedicated ping is the protocol's intended mechanism.
- **Tie heartbeat interval to idle detection** (ping only if no events received for N seconds) — considered but rejected as over-engineering. The steady-state ping cost is so low that conditional pinging buys nothing but a more complex state machine. Simpler wins.
