# server-accept-lifecycle — Delta

## ADDED Requirements

### Requirement: The flag that classifies an accept failure is cleared before the stop that causes one
A server whose accept loop tells a stop from a failure by reading its own published running state SHALL
clear that state **before** it does anything that can abort a pending accept, and SHALL publish it
through a read that cannot observe a stale value. A stop that aborts the accept first and lowers the
flag afterwards leaves the loop classifying its own shutdown as a failure, which turns every ordinary
stop into a reported error and makes the report worthless precisely when a real failure needs to stand
out.

The same state SHALL make a second start a no-op rather than a second listener: a server that binds
again while already bound leaks the listener, the cancellation source and the loop task it replaces, and
leaves the first loop running against a source nothing can cancel.

#### Scenario: A stop is not reported as an accept failure
- **GIVEN** a running server with a connection accepted and its loop parked on the next accept
- **WHEN** the server is stopped and the pending accept is aborted
- **THEN** the loop ends, the stop completes, and nothing is reported as an accept failure — because the
  running state was already false when the abort reached the loop

#### Scenario: The running state is readable from the accept loop's thread
- **GIVEN** a stop running on one thread and an accept loop reading the running state on another
- **WHEN** the stop clears that state
- **THEN** the loop's next read observes the clear rather than a cached value, because the state is
  published and read through a memory barrier rather than a plain field

#### Scenario: Starting an already-started server changes nothing
- **GIVEN** a server that is already bound and accepting
- **WHEN** it is started again
- **THEN** the call returns without binding a second listener, without replacing the cancellation source
  or the loop task, and the server goes on accepting on the one it already had

#### Scenario: A stop that begins is a stop, for anyone reading the state
- **GIVEN** a consumer reading the server's published running state
- **WHEN** a stop begins but has not yet finished releasing connections
- **THEN** the state already reads as not running, because it describes the server's intent rather than
  the completion of its teardown

## Architectural Risk

- **Level:** MEDIUM. This changes the semantics of a property on two shipped public types
  (`Verbara.Sdk.Ari.Audio.AudioSocketServer`, `WebSocketAudioServer`), not only the behaviour of a
  private loop. `IsRunning` moves from "the stop has finished" to "a stop has begun".
- **Affected:** both ARI audio servers, `CompositeAudioServer`, the ARI audio hosted service, and any
  external SDK consumer reading `IsRunning`. Neither Sdk.Pro nor Platform starts these servers. No new
  public member and no signature change, so no `PublicAPI.*.txt` entry is owed.
- **Mitigation:** the reordering is pinned by the case that fails when a classification filter is
  transplanted without it — a clean stop must report no accept failure. Without that case the change
  passes green for the wrong reason. Both servers move in one change so their parity is never briefly
  broken.
- **Residual:** neither server has a health check and `IAudioServer` does not expose the running state,
  so a consumer cannot build one without casting to the concrete type. A persistent accept failure stays
  visible only in the log. Whether these servers should carry a health check is a separate decision.
