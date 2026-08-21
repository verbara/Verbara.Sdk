# test-determinism — Delta

## ADDED Requirements

### Requirement: An in-process WebSocket fake answers on a protocol sentinel, never on a wall-clock delay
A WebSocket fake server SHALL gate every response it sends on an observable protocol event from the
client — the arrival of a specific frame — and MUST NOT use a fixed `Task.Delay` as a stand-in for
"the client has finished its request." Where the client sends an unconditional first or last frame
(`session.update` for the Realtime bridge, `Flush` for Deepgram TTS), that frame is the sentinel.
The wait MUST be bounded by a timeout so a client that never sends cannot stall the suite, and the
timeout MUST be long enough that reaching it means the protocol assumption is wrong, not that the
machine was busy.

#### Scenario: The fake waits for the client's request frame before answering
- **GIVEN** a fake server whose test asserts on a frame the client sends
- **WHEN** the client is slowed by load and its frame arrives later than any fixed delay would have allowed
- **THEN** the fake still answers after that frame, and the assertion holds — the outcome does not depend on which of the two won a race

#### Scenario: A silent client fails loudly instead of hanging
- **GIVEN** a fake server waiting on its protocol sentinel
- **WHEN** the client never sends the expected frame
- **THEN** the bounded timeout elapses and the test fails on its own assertion, rather than the suite hanging until the runner kills it

#### Scenario: Removing the fence fails the test
- **GIVEN** the sentinel wait replaced by the fixed delay it superseded
- **WHEN** the suite runs under load
- **THEN** the affected test is observed failing, so the fence is proven load-bearing rather than accepted on a green run

### Requirement: A hold-open flag keeps the socket alive until the fake is disposed
A WebSocket fake server that offers a hold-open capability SHALL keep the connection alive until its
own lifetime token fires, and MUST NOT implement that hold as `await receiveTask`. The client's
receive loop ends at the client's half-close (`CloseOutputAsync`), which is not the end of the
session: returning there tears down a socket the test still needs open. The correct implementation
waits on the server's cancellation token and only then drains the receive loop.

#### Scenario: A cancellation test observes cancellation on a live socket
- **GIVEN** a test asserting that a receive loop terminates on cancellation
- **WHEN** the fake is put in hold-open mode and the test's token fires
- **THEN** the socket is still open at that moment, so the loop's exit is attributable to cancellation and not to the server having already closed

#### Scenario: A half-closing client does not end the session
- **GIVEN** a fake in hold-open mode and a client that half-closes after sending its last frame
- **WHEN** the client's receive loop ends
- **THEN** the fake keeps the socket open, and the test's completion source is not signalled early

#### Scenario: Disposing the fake releases the hold
- **GIVEN** a fake held open with no client traffic
- **WHEN** the test disposes it
- **THEN** the session ends promptly and the test does not depend on a runner-level timeout to finish

### Requirement: A fake hands tests a snapshot of captured frames, never the live collection
A WebSocket fake server SHALL expose captured client frames as an immutable snapshot taken under the
same lock its receive loop writes under — `IReadOnlyList<T>` returning a copy, never the backing
`List<T>`. Collections that carry test→server configuration written before the server starts (the
events or frames a test queues for delivery) are NOT captures and are exempt.

#### Scenario: An assertion cannot tear a concurrently mutated list
- **GIVEN** a fake whose receive loop is still appending frames
- **WHEN** a test enumerates the captured frames
- **THEN** it sees a stable snapshot, and no enumeration can fail with a concurrent-modification error or observe a partially written entry

#### Scenario: Configuration collections stay writable
- **GIVEN** a test queueing events on the fake before calling `Start()`
- **WHEN** this requirement is applied
- **THEN** that collection stays a plain writable list — it is written by the test before any receive loop exists, so no synchronisation is warranted and none is added

### Requirement: In-process WebSocket fakes run on the shared WebSocketTestServer substrate
Every in-process WebSocket fake in this repo SHALL be built on
`Tests/Verbara.Sdk.TestInfrastructure/WebSocket/WebSocketTestServer.cs`, and MUST NOT be built on
`HttpListener` + `AcceptWebSocketAsync` — the path whose `Abort()` dispose hangs on Linux test
plumbing, and the reason the shared substrate exists. A fake on the shared substrate MUST NOT carry
its own port-allocation probe or retry loop: `TcpListener(IPAddress.Loopback, 0)` binds a free port
directly, so the check-then-bind window that `HttpListener` forces has no equivalent here.

#### Scenario: A new WebSocket fake reuses the substrate
- **GIVEN** a developer adding a fake for a new WebSocket provider
- **WHEN** they follow the fakes already in the repo
- **THEN** every one of them supplies a per-connection handler to `WebSocketTestServer`, so the accept path, the RFC 6455 handshake and the dispose path are shared and validated once

#### Scenario: The port probe disappears with the substrate
- **GIVEN** a fake migrated off `HttpListener`
- **WHEN** the migration lands
- **THEN** its TOCTOU port probe and retry loop are deleted rather than carried over, because the listener now owns the port from the moment it is bound

### Requirement: A test ends on the signal it asserts, not on a cancellation timeout
A test SHALL reach its assertions via a deterministic signal — a completion source released by the
protocol event under test, or the completion of the operation itself — and MUST NOT depend on a
`CancellationTokenSource` timeout or a fixed `Task.Delay` as its normal path to the assertion. A
cancellation token that exists only to bound a hang is a safety net: reaching it MUST mean the test
failed, never that it finished. Per-file wall-clock barrier counts in `sync-fence-baseline.json` are
lowered as barriers are removed and MUST NOT be raised.

#### Scenario: A passing test does not wait out its own timeout
- **GIVEN** a test whose only exit today is a multi-second cancellation token expiring
- **WHEN** it is rewritten against the signal it asserts on
- **THEN** it completes as soon as that signal arrives, and its remaining token bounds a hang rather than pacing the test

#### Scenario: The barrier ratchet only goes down
- **GIVEN** the grandfathered per-file barrier counts in `sync-fence-baseline.json`
- **WHEN** a change removes wall-clock barriers from a file
- **THEN** that file's count is lowered in the same commit, and no count anywhere is raised to accommodate a new barrier

## MODIFIED Requirements

### Requirement: TTS synthesis observes cancellation deterministically

TTS speech synthesizers SHALL observe a cancelled token deterministically: a token cancelled before
or during `SynthesizeAsync` enumeration SHALL surface `OperationCanceledException` at the next
iteration boundary, independent of provider/mock latency. A pre-cancelled token SHALL throw before
the first provider request is issued. Per-provider cancellation tests MUST NOT race a wall-clock
timer (`CancellationTokenSource(delay)`) against fake-server behaviour.

A cancellation test SHALL hand the cancelled token to the **subject only**. The consumer that
enumerates the result MUST NOT receive it — no `ToListAsync(ct)`, no `ToArrayAsync(ct)`, no
`WithCancellation(ct)` — because each of those checks the token itself at every iteration boundary
and throws whether or not the subject does. The assertion then passes over a silent `yield break`
identically to a propagated throw, so the test measures the enumerator rather than the code under
test (ADR-0052 F3).

Coverage of this requirement SHALL be enumerated by **selectable code path**, not by provider name.
Every route through `SynthesizeAsync` a caller can reach through options — each transport of a
multi-transport synthesizer included — SHALL carry its own mid-enumeration cancellation test.

#### Scenario: Pre-cancelled token throws before any provider call

- **GIVEN** a `CancellationTokenSource` cancelled before `SynthesizeAsync` is enumerated
- **WHEN** the stream is enumerated plainly, the token having been passed to `SynthesizeAsync` and not to the enumerator
- **THEN** `OperationCanceledException` is thrown deterministically and no provider request is issued

#### Scenario: Cancellation landing mid-enumeration reaches the caller

- **GIVEN** a synthesis whose response leaves further reads outstanding after the first chunk is yielded
- **WHEN** the token is cancelled after that first chunk
- **THEN** `OperationCanceledException` propagates out of the caller's own `await foreach`, rather than the sequence simply ending

#### Scenario: The enumerator cannot supply the throw

- **GIVEN** the subject altered to swallow the token and end its sequence with `yield break`
- **WHEN** the cancellation test runs
- **THEN** it fails, because the assertion has no source of `OperationCanceledException` other than the subject

#### Scenario: Every selectable transport carries its own test

- **GIVEN** a synthesizer whose options select between transports
- **WHEN** its cancellation coverage is enumerated
- **THEN** each selectable transport has a test, because a contract declared on `SynthesizeAsync` cannot hold on one transport and not on another

#### Scenario: Cancellation tests do not race the fake server

- **GIVEN** the per-path mid-enumeration cancellation tests
- **WHEN** the suite runs repeatedly under load or coverage instrumentation
- **THEN** the tests pass deterministically — the assertion targets the iteration-boundary contract, not a timer-vs-connect scheduling race

## Architectural Risk

**Level:** LOW.

**Affected:** the in-process WebSocket fakes and the suites that drive them — one test project in
this change, with the contract stated so the remaining eight surfaces can be swept against it later.
No production code, no public API surface, nothing that cascades to `Sdk.Pro` or `Platform`.

**Mitigation:** each requirement is enforced by something that fails rather than by review alone —
the `sync-fence-baseline.json` ratchet for wall-clock barriers, a Governance detector for the
snapshot rule, and a negative test per fence (remove it, watch the test fail; restore it, watch it
pass). The hold-open rule is the only new runtime behaviour and is bounded by the fake's own
cancellation token, so a mis-wired hold surfaces as a failing disposal test rather than a hung
suite.
