# test-determinism Specification

## Purpose
Determinism fences for Sdk's async streaming tests so suites never depend on wall-clock or
scheduling races: cooperative-cancellation contracts observed at a deterministic seam (iteration
entry) instead of raced against a mock's completion timing, so a pre-cancelled token always
throws before any provider call regardless of mock latency or CI scheduling pressure. This is
Sdk's first capability instance of the ecosystem-wide deterministic-test-fences convergence
(verbara-meta ADR-0004, adopt-on-touch), mirroring Platform's `test-determinism` living spec
(C1→C3) at the seam Sdk actually owns: STT streaming recognizers. All 7 providers (Deepgram,
Whisper, AzureWhisper, Google, Speechmatics, AssemblyAI, Cartesia) now assert this contract via a
`StreamAsync_ShouldAbort_WhenCancelled` test — the coverage gap closed by
`stt-provider-cancellation-tests` (archived 2026-07-18).
## Requirements
### Requirement: STT streaming observes cancellation deterministically
STT streaming recognizers SHALL observe a cancelled token deterministically: a token cancelled
before or during `StreamAsync` enumeration SHALL surface `OperationCanceledException` at the next
iteration boundary, independent of provider/mock latency. A pre-cancelled token SHALL throw before
the first provider request is issued. This contract MUST be asserted by a
`StreamAsync_ShouldAbort_WhenCancelled` test for every STT provider (Deepgram, Whisper,
AzureWhisper, Google, Speechmatics, AssemblyAI, Cartesia), not a subset. The frame generator these
per-provider tests use to keep the stream open until the cancellation seam is observed
(`EndlessFrames`) SHOULD live in a single shared test helper rather than being duplicated per
suite, so the seven-provider assertion rests on one non-duplicated generator.

#### Scenario: Pre-cancelled token throws before any provider call
- **GIVEN** a `CancellationTokenSource` cancelled before `StreamAsync` is enumerated
- **WHEN** the stream is enumerated (e.g. `ToListAsync(ct)`)
- **THEN** `OperationCanceledException` is thrown deterministically and no provider HTTP request is issued

#### Scenario: Cancellation tests do not race the mock
- **GIVEN** the per-provider `StreamAsync_ShouldAbort_WhenCancelled` tests (all 7 providers: Deepgram/Whisper/AzureWhisper/Google/Speechmatics/AssemblyAI/Cartesia)
- **WHEN** the suite runs repeatedly under load or coverage instrumentation
- **THEN** the tests pass deterministically — the assertion targets the iteration-boundary contract, not a scheduling race

#### Scenario: Cancellation-test frame generator is not duplicated per suite
- **GIVEN** the shared `EndlessFrames` frame generator used by the per-provider cancellation tests
- **WHEN** a new STT provider suite adds a `StreamAsync_ShouldAbort_WhenCancelled` test
- **THEN** it references the single shared helper instead of copying the generator, and the `Task.Delay` pacer carries exactly one `fence-allow: LOOP-DRIVER` annotation at the shared site

### Requirement: TTS synthesis observes cancellation deterministically

TTS speech synthesizers SHALL observe a cancelled token deterministically: a token cancelled
before or during `SynthesizeAsync` enumeration SHALL surface `OperationCanceledException` at the
next iteration boundary, independent of provider/mock latency. A pre-cancelled token SHALL throw
before the first provider request is issued. Per-provider cancellation tests MUST NOT race a
wall-clock timer (`CancellationTokenSource(delay)`) against fake-server behaviour.

#### Scenario: Pre-cancelled token throws before any provider call

- **GIVEN** a `CancellationTokenSource` cancelled before `SynthesizeAsync` is enumerated
- **WHEN** the stream is enumerated (e.g. `ToListAsync(ct)`)
- **THEN** `OperationCanceledException` is thrown deterministically and no provider request is issued

#### Scenario: Cancellation tests do not race the fake server

- **GIVEN** the per-provider `SynthesizeAsync_ShouldAbort_WhenCancelled` tests (Deepgram, ElevenLabs, Lmnt)
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

