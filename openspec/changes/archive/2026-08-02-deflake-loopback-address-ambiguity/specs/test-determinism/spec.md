# test-determinism — Delta

## ADDED Requirements

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
