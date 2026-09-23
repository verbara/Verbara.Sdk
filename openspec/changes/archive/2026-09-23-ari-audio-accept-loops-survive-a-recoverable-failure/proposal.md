---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Operators running ARI audio servers in production, and anyone reading `IsRunning` on either of them
decision_ref: Sdk/ADR-0056
---

# Proposal: ari-audio-accept-loops-survive-a-recoverable-failure

## Why

### The last two catchless accept loops

`AudioSocketServer.AcceptLoopAsync` and `WebSocketAudioServer.AcceptLoopAsync` both put their `try`
**outside** their `while` and catch exactly two types, both commented as the stop path.
`TcpListener.AcceptTcpClientAsync` wraps `Socket.AcceptAsync`, whose contract also carries
`SocketException`. So an accept that fails while the server is still meant to be running — EMFILE/ENFILE,
ENOBUFS, a connection aborted in the backlog — faults the loop task. Nothing awaits it until `StopAsync`'s
`SuppressThrowing` at shutdown, no `UnobservedTaskException` handler exists anywhere in this repository,
and the runtime discards it. The server ends with **no log line**, `IsRunning` still reporting `true`,
and its socket still in LISTEN.

This is the same defect #291 fixed for `AriOutboundListener` and #298 fixed for `FastAgiServer`. These
are the last two. ADR-0056 R6 already decides what an accept loop does with its endings; this change
applies it rather than deciding anything new.

Native AOT makes the trigger reachable rather than theoretical: measured on this machine, a CoreCLR
process raises its descriptor limit to 524288 while a **Native AOT process leaves it at the systemd
default of 1024**. Each server caps at `MaxConcurrentStreams` = 1000, and the real ceiling is 2000
because each keeps its own `_streams` dictionary while both read the same options object — so a busy PBX
reaches descriptor exhaustion before it reaches the SDK's own limit.

### A′ cannot be transplanted here until the running flag is fixed first

This is what makes this change bigger than #298, and it was found by trying to copy that fix.

A′'s discriminator is a filtered `catch (SocketException) when (!IsRunning)`. It works in the two
already-fixed servers because their stop clears the flag **before** it aborts the pending accept, so the
flag cannot be late. Both of these servers do the opposite: `IsRunning = false` is the **penultimate**
statement of `StopAsync` (`AudioSocketServer.cs:196`, `WebSocketAudioServer.cs:300`), after `Stop()`,
after `CancelAsync()`, and after **awaiting the accept loop**. Copied literally, that filter would never
match on the stop path, and the `SocketException(OperationAborted)` a Linux stop produces — measured at
7 of 10 in a sibling change's probe — would fall to the unfiltered arm and log a **spurious Error on
roughly 70% of normal shutdowns**.

Two more defects sit in the same few lines, and fixing the ordering without them would be half a job:

- **`IsRunning` is a plain auto-property** (`{ get; private set; }`), written by the stopping thread and
  read by the accept loop's thread with no barrier. The two fixed servers read theirs through
  `Volatile.Read`. A filter built on an unsynchronised flag is a filter that can read stale.
- **`StartAsync` has no reentrancy guard.** A second call binds a second `TcpListener` and overwrites
  `_cts`, `_listener` and `_acceptLoop`, leaking all three and leaving the first loop running against a
  source nobody can cancel. The two fixed servers guard with `Interlocked.Exchange`.

## What Changes

1. **The running flag becomes `int _running` behind `Interlocked.Exchange`**, cleared as the **first**
   statement of `StopAsync` and set as the first of `StartAsync`, with
   `IsRunning => Volatile.Read(ref _running) == 1`. `StartAsync` returns early when it was already 1,
   and `StopAsync` when it was already 0.
2. **A′ in both accept loops**: `try` inside the `while`; stop clauses `break`; a filtered
   `catch (SocketException) when (!IsRunning)` breaks; an unfiltered `catch (SocketException ex)` logs
   `AcceptLoopFailed` at Error and the loop keeps accepting after a wait doubling 100 ms → 5 s on an
   injected `TimeProvider`, reset by the next successful accept.
3. **Neither loop writes a terminal state.** Neither server has one; `IsRunning` stays true because the
   listener is still bound and still accepting. The Error line is what an operator gains.
4. **Both servers move together.** They share `AudioServerOptions`, the hosted service and
   `CompositeAudioServer`; fixing one and not the other breaks the parity ADR-0058 claims.

## Impact

- `src/Verbara.Sdk.Ari/Audio/AudioSocketServer.cs` and `Audio/WebSocketAudioServer.cs`: the restructured
  loops, the running flag, backoff constants, a `TimeProvider` field with an `internal` constructor, and
  an `internal` accept seam. Each logger class gains one `[LoggerMessage]` at Error.
- `Tests/Verbara.Sdk.Ari.Tests/Audio/`: the new cases. `AudioSocketServerTests.cs` carries **4**
  grandfathered sync-fence barriers and `WebSocketAudioServerTests.cs` carries **none** — neither number
  may rise.
- **An observable change on a shipped property.** Both classes are in
  `src/Verbara.Sdk.Ari/PublicAPI.Shipped.txt`, so external SDK consumers are in scope even though neither
  Sdk.Pro nor Platform starts these servers. `IsRunning` now reads `false` from the moment a stop
  **begins** rather than from the moment it completes. The only reader inside `src/` is each server's own
  `DisposeAsync`, which is indifferent. A consumer polling `IsRunning` to decide when a stop finished was
  already racing — `StopAsync` returns a `ValueTask` they can await — but the window moves.
- **An observable change on the accept path**: a failure that used to end the server now logs at Error
  and the server keeps accepting. `### Changed`, not `### Fixed`.
- No new public member and no signature change, so no `PublicAPI.*.txt` entry is owed.

## Architectural Risk

- **Level:** MEDIUM — higher than its two predecessors, because it changes the semantics of a property on
  a shipped public type rather than only the behaviour of a private loop.
- **Affected:** the accept and stop paths of both ARI audio servers, `CompositeAudioServer`, the hosted
  service, and any external consumer reading `IsRunning`.
- **Mitigation:** the ordering change is pinned by the test that catches a filter transplanted without
  it — a clean stop must produce **no** `AcceptLoopFailed`. Without that case the fix passes green for
  the wrong reason, which is the failure ADR-0052 was written about. The backoff is driven by a fake
  clock. Both servers are changed in one PR so the parity is never briefly broken.
- **Residual, recorded and not closed here:** neither server has a health check, and `IAudioServer` does
  not expose `IsRunning`, so a consumer cannot write one without casting to the concrete type. A
  persistent accept failure is therefore visible only in the log. Whether these servers should carry a
  health check is a separate decision.
