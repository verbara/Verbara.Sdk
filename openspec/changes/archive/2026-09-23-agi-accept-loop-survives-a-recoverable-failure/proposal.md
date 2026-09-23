---
tier: PEQUEÑO
owner: Harol
approver: Harol
stakeholder: Operators who page on AGI health, and anyone whose dialplan reaches a FastAGI script
decision_ref: Sdk/ADR-0056
---

# Proposal: agi-accept-loop-survives-a-recoverable-failure

## Why

`FastAgiServer.AcceptLoopAsync` (`src/Verbara.Sdk.Agi/Server/FastAgiServer.cs:90`) puts its `try`
**outside** its `while` and catches exactly two types — `OperationCanceledException` and
`ObjectDisposedException` — both commented as the stop path. `TcpListener.AcceptTcpClientAsync(ct)`
wraps `Socket.AcceptAsync`, whose contract also carries `SocketException` and
`InvalidOperationException`. Neither is taken.

So an accept that fails while the server is still meant to be running — EMFILE/ENFILE, ENOBUFS,
ECONNABORTED in the backlog — **faults the loop task**. Nothing awaits that task until `StopAsync`,
whose `SuppressThrowing` swallows it at shutdown. There is no `UnobservedTaskException` handler
anywhere in this repository and `ThrowUnobservedTaskExceptions` defaults false, so the runtime discards
it. The loop ends with **no log line at all**.

### The health check then lies, and that is what makes this worse than its siblings

`AgiServerState.Faulted` exists and its own documentation names this case:
*"Listener faulted (address in use, **fd exhaustion**)"* (`src/Verbara.Sdk/Enums/AgiServerState.cs:14`).
But the only `SetState(AgiServerState.Faulted)` in the type is inside `StartAsync` (`:81`). The accept
loop never writes it. `AgiHealthCheck` maps `Listening => Healthy("AGI server listening")`
(`src/Verbara.Sdk.Agi/Diagnostics/AgiHealthCheck.cs:17`).

The result: the loop is dead, the socket is still in LISTEN, `IsRunning` still reports `true`, and the
health check an operator is paging on reports **Healthy**. Asterisk connects and gets nothing. This is
the only one of the repository's catchless accept loops that has a health check to mislead.

### Native AOT makes the trigger reachable, not theoretical

Measured on this machine (Debian 13, .NET 10.0.12): `systemctl show -p DefaultLimitNOFILESoft` is
**1024**, and a process started under that limit sees

| runtime | `Max open files` |
|---|---|
| CoreCLR/JIT | **524288** — raised to the hard limit |
| **Native AOT** | **1024** — untouched |

This ecosystem ships Native AOT. A FastAGI server on a busy PBX holds one accepted socket per
concurrent script, and nothing in this repository documents a `LimitNOFILE` for deployment (`grep` for
`ulimit|nofile|LimitNOFILE` across `docs/`, `README.md` and `src/`: zero hits). Descriptor exhaustion is
the realistic trigger, and it is exactly what the enum's own comment anticipated.

### The decision that governs this already exists

ADR-0056 R6 records that an accept loop classifies its endings, and that a failure the listener can
survive is logged and survived rather than ending the loop. It was applied to
`AriOutboundListener.AcceptLoopAsync` in #291 as option A′ and the owner explicitly rejected the
smaller "log and let the loop exit" alternative. This change **applies that decision** to the third
loop; it does not make a new one, so it adds no ADR.

## What Changes

1. **The `try` moves inside the `while`**, so the loop can continue. The two stop clauses `break`.
2. **A filtered `catch (SocketException) when (!IsRunning)` breaks**, and an unfiltered
   `catch (SocketException ex)` logs at `LogLevel.Error` through a new `AcceptLoopFailed` event and
   **keeps accepting** after a backoff. `IsRunning` is the discriminator rather than the token, and here
   it needs no other change: `StopAsync` writes `Stopping` as its **first** statement (`:184`), before
   `_listener?.Stop()` (`:186`), and `IsRunning => State == AgiServerState.Listening` is already read
   through `Volatile.Read` (`:48`). *(The two ARI audio servers invert that ordering and would need it
   corrected first — they are not in this change.)*
3. **A backoff doubling 100 ms → 5 s on an injected `TimeProvider`**, so a persistent failure cannot
   spin the loop and flood the log. Same constants and shape as the sibling.
4. **`Faulted` is NOT written from the loop.** Under this change the listener is still bound and still
   retrying, and `Faulted` would block `StartAsync` (`:66`) over a loop that is still accepting. What
   an operator gains is the Error line, not a state transition — and that limit is recorded rather than
   left to be discovered.

## Impact

- `src/Verbara.Sdk.Agi/Server/FastAgiServer.cs`: the restructured loop, two backoff constants, a
  `TimeProvider` field with an `internal` constructor, and an `internal` accept seam for tests.
  `FastAgiServerLog` gains one `[LoggerMessage]` at Error.
- `src/Verbara.Sdk.Agi/Verbara.Sdk.Agi.csproj`: an `InternalsVisibleTo` for the test assembly, which
  this project does not have today.
- `Tests/Verbara.Sdk.Agi.Tests/`: a `FakeTimeProvider` (the project has none; this is the repository's
  fourth hand-written copy, which is recorded as debt rather than solved here) and the new cases.
- **No public API change.** Every addition is `internal`, so no `PublicAPI.*.txt` entry is owed and
  nothing cascades to Sdk.Pro or Platform — neither of which starts this server.
- **An observable change on a normal path**: an accept failure that used to end the server now logs at
  Error and the server keeps accepting. That is a `### Changed`, not a `### Fixed`.

## Architectural Risk

- **Level:** LOW.
- **Affected:** `FastAgiServer`'s accept path, and any consumer reading `AgiHealthCheck`. The type is
  public and shipped (`src/Verbara.Sdk.Agi/PublicAPI.Shipped.txt`), so external SDK consumers are in
  scope even though Pro and Platform are not.
- **Mitigation:** the stop path keeps its own clauses and is pinned by a test that asserts a clean stop
  produces **no** `AcceptLoopFailed` entry — the case that catches a filter copied without checking the
  `StopAsync` ordering. The backoff is driven by a fake clock, never a real wait.
- **Residual, recorded and not closed here:** under a *persistent* failure the health check still
  reports `Healthy`, because the loop is alive and retrying. This change removes the silent death, not
  the optimistic health check. Turning a retrying loop into `Degraded` is a separate decision about
  what `AgiServerState` means.
