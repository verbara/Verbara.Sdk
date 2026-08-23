# test-determinism Specification

## Purpose
Determinism fences for Sdk's async streaming tests, so a suite's outcome never depends on which of
two unrelated clocks wins. The capability covers four seams the repo actually owns:

1. **Cooperative cancellation observed at the iteration boundary** rather than raced against a
   mock's completion timing — and asserted so that the *subject* is what throws, never the
   enumerator standing in for it (ADR-0052 F3).
2. **The address and substrate in-process test servers bind** — an IPv4 loopback literal, because
   `localhost` resolves `::1` first (ADR-0044), on the shared `WebSocketTestServer`.
3. **What an in-process WebSocket fake is allowed to do**: answer on a protocol sentinel rather
   than a wall-clock delay, hold the socket open on its own token rather than on its receive loop,
   and hand tests a snapshot rather than the live collection its receive loop writes (ADR-0045) —
   plus the corollary on the test side, that a test ends on the signal it asserts and a token
   expiry means failure, never completion.
4. **How a harness drives a pipeline it does not own**: an in-process AudioSocket harness
   sequences every phase on a signal the pipeline itself emits — the detector call the monitor loop
   makes once per frame, a synthesizer that parks between chunks, and the event stream that
   announces each stage of a response cycle — never on a fixed delay standing in for "it has
   finished reacting" (ADR-0045). The three sentinels are not interchangeable: neither the detector
   nor the park can speak for the recognition, handler and synthesis work that runs on after an
   end-of-utterance decision, and a harness waiting on the end of a cycle must count the error event
   as an ending or the one test exercising a failing stage is the one test left hanging.

Coverage is enumerated **by selectable code path, not by provider name**: a closed provider list
under an open contract hides the surfaces nobody looked at, which is how multi-transport
synthesizers went uncovered while every named provider was green. Where a rule can be checked by
reading source, it is enforced by a Governance source-scanning guard that fails the build rather
than by review; where it cannot, it is evidenced by negative-testing the fence.

This is Sdk's first capability instance of the ecosystem-wide deterministic-test-fences convergence
(`verbara-meta/ADR-0004`, adopt-on-touch), mirroring Platform's `test-determinism` living spec
(C1→C3).
## Requirements
### Requirement: STT streaming observes cancellation deterministically
STT streaming recognizers SHALL observe a cancelled token deterministically: a token cancelled
before or during `StreamAsync` enumeration SHALL surface `OperationCanceledException` at the next
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
Every route through `StreamAsync` a caller can reach through options — each transport of a
multi-transport recognizer included — SHALL carry its own `StreamAsync_ShouldAbort_WhenCancelled`
test. The frame generator these tests use to keep the stream open until the cancellation seam is
observed (`EndlessFrames`) SHOULD live in a single shared test helper rather than being duplicated
per suite, so the assertion rests on one non-duplicated generator.

#### Scenario: Pre-cancelled token throws before any provider call
- **GIVEN** a `CancellationTokenSource` cancelled before `StreamAsync` is enumerated
- **WHEN** the stream is enumerated plainly, the token having been passed to `StreamAsync` and not to the enumerator
- **THEN** `OperationCanceledException` is thrown deterministically and no provider HTTP request is issued

#### Scenario: The enumerator cannot supply the throw
- **GIVEN** the subject altered to swallow the token and end its sequence with `yield break`
- **WHEN** the cancellation test runs
- **THEN** it fails, because the assertion has no source of `OperationCanceledException` other than the subject

#### Scenario: Every selectable code path carries its own test
- **GIVEN** a recognizer whose options select between transports or request modes
- **WHEN** its cancellation coverage is enumerated
- **THEN** each selectable path has a test, because a contract declared on `StreamAsync` cannot hold on one path and not on another — and a closed list of provider names hides the paths nobody looked at

#### Scenario: Cancellation tests do not race the mock
- **GIVEN** the per-path `StreamAsync_ShouldAbort_WhenCancelled` tests
- **WHEN** the suite runs repeatedly under load or coverage instrumentation
- **THEN** the tests pass deterministically — the assertion targets the iteration-boundary contract, not a scheduling race

#### Scenario: Cancellation-test frame generator is not duplicated per suite
- **GIVEN** the shared `EndlessFrames` frame generator used by the per-provider cancellation tests
- **WHEN** a new STT provider suite adds a `StreamAsync_ShouldAbort_WhenCancelled` test
- **THEN** it references the single shared helper instead of copying the generator, and the `Task.Delay` pacer carries exactly one `fence-allow: LOOP-DRIVER` annotation at the shared site

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

### Requirement: In-process test servers and the seams that dial them use an IPv4 loopback literal
Every in-process test server SHALL bind an explicit IPv4 loopback address, and every seam that
dials one SHALL address it by the matching IPv4 loopback **literal** (`127.0.0.1`); the
address-family-ambiguous name `localhost` MUST NOT be used on either side. This covers the
`HttpListener` prefixes and `BaseUri` properties of the VoiceAi fake servers, the shared
`WebSocketTestServer`, and the `_fakeServerPort` / `_fakeWsPort` URI branches in `src/` that exist
solely to let a test dial those fakes. `localhost` in a **product-facing default** for a service the
SDK does not host (the ARI base URL, the OTel OTLP endpoint, the Toxiproxy API default) is NOT
covered by this requirement and MUST NOT be rewritten.

#### Scenario: A competing bind on a held port is refused instead of silently succeeding
- **GIVEN** an in-process test server holding a port on `127.0.0.1`
- **WHEN** another in-process test server in the same test run attempts to bind the same port number
- **THEN** the second bind fails loudly with `Address already in use`, which the fake's existing port-allocation retry loop handles by picking another port — rather than succeeding on a different address family and leaving two owners for one port number

#### Scenario: A WebSocket client reaches the WebSocket server, not a co-resident HTTP listener
- **GIVEN** a `WebSocketTestServer` bound to `127.0.0.1` and a seam dialling `ws://127.0.0.1:{port}`
- **WHEN** the suite runs under CPU saturation with the fake servers of other suites allocating ports concurrently
- **THEN** the handshake completes with HTTP `101` and the fake server's request-capture handler runs — the client can never be resolved onto an `HttpListener` that answers `200`, because the dialled address is a literal and only one server owns it

#### Scenario: Product-facing localhost defaults are unaffected
- **GIVEN** the shipped defaults `http://localhost:8088` (ARI) and `http://localhost:4317` (OTel OTLP), and the Toxiproxy API default
- **WHEN** this requirement is applied
- **THEN** those values are left exactly as they are — they address services the SDK does not host, where name resolution is the caller's choice and no in-process port ownership is at stake

### Requirement: A source-scanning guard fails the build when a fake-server seam reintroduces localhost
The repo SHALL carry a deterministic source-scanning regression guard, in
`Tests/Verbara.Sdk.Governance.Tests/` alongside the existing guards, that fails the build when a
fake-server bind site or a test-only dialling seam reintroduces the `localhost` name. The guard MUST
be deterministic (source scanning only — no network, no timing, no Docker), MUST ship detector unit
tests pinning both a true positive and immunity to the product-facing-default false positives, and
MUST ship a liveness self-test so that scanning zero files cannot be reported as a pass.

#### Scenario: Reintroducing localhost at a fake-server seam fails the build
- **GIVEN** the guard in the Governance test project
- **WHEN** a change sets an `HttpListener` prefix, a fake server's `BaseUri`, or a `_fakeServerPort` / `_fakeWsPort` URI branch back to `localhost`
- **THEN** the guard test fails with a message naming the offending file, and CI blocks the merge

#### Scenario: Product defaults do not trip the detector
- **GIVEN** source containing `http://localhost:8088`, `http://localhost:4317`, or the Toxiproxy API default
- **WHEN** the guard's detector scans it
- **THEN** no violation is reported, and a detector unit test pins that immunity so a future broadening of the pattern is caught by its own suite

#### Scenario: The guard cannot pass vacuously
- **GIVEN** the guard's liveness self-test
- **WHEN** the scan walks the source tree
- **THEN** the number of scanned files must exceed a conservative floor, so a broken path or an empty enumeration fails instead of reporting a false green

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

### Requirement: An in-process AudioSocket harness orders its phases on a pipeline signal, never on a wall-clock delay
A test harness driving a `VoiceAiPipeline` over an in-process AudioSocket pair SHALL sequence every
phase — frames sent, an utterance ended, a synthesis started, a barge-in delivered — on an
observable signal emitted by the pipeline itself, and MUST NOT use a fixed `Task.Delay` as a stand-in
for "the pipeline has finished reacting." Three seams carry that meaning and are the sentinels:
`ITurnDetector.Analyze`, which the pipeline calls synchronously once per frame on the monitor loop's
own thread; a speech synthesizer that parks between chunks; and the pipeline's own event stream,
which announces each stage of a response cycle. A detector signal orders frames and a park orders
the synthesis window, but neither can speak for the recognition, handler and synthesis work that
runs on after an end-of-utterance decision — that is what the event stream is for. A harness waiting
on the end of a response cycle MUST treat the error event as an ending too, or a test written to
exercise a failing stage waits for a success event that will never be published. Each wait MUST be
bounded by a timeout set far above any plausible scheduling delay, so reaching it fails the test's
own assertion rather than pacing it.

#### Scenario: The harness waits for the frame's decision before sending the next
- **GIVEN** a harness that must know a frame has been acted on before it sends another
- **WHEN** it sends one frame and waits on that frame's own detector signal
- **THEN** it proceeds the instant the pipeline has decided on that frame, and no elapsed time is involved in the ordering

#### Scenario: A synthesis in flight is a fact, not a probability
- **GIVEN** a test that must act while the assistant is mid-sentence
- **WHEN** the synthesizer parks after its first chunk and announces it
- **THEN** anything the test does before releasing the park provably happens inside the synthesis window

#### Scenario: A response cycle that fails still ends the wait
- **GIVEN** a harness waiting for the pipeline to finish reacting to an utterance
- **WHEN** the stage under test fails and the pipeline publishes an error instead of a synthesis ending
- **THEN** the wait ends on that error, so a test written to exercise the failure is not the one test the sweep leaves hanging

#### Scenario: Removing the signal fails the test
- **GIVEN** a harness phase newly ordered by a signal instead of a delay
- **WHEN** that signal is removed
- **THEN** the dependent test fails, demonstrating the ordering is real rather than incidental

#### Scenario: A hang bound is kept, not swept
- **GIVEN** a multi-second cancellation token in the same file whose only role is to bound a deadlock
- **WHEN** the file's wall-clock barriers are retired
- **THEN** that token is examined and kept, and the decision to keep it is recorded so a later sweep does not remove the safety net

