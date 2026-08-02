# ADR-0044: In-process test servers and their dialling seams use an IPv4 loopback literal, never `localhost`

- **Status:** Accepted
- **Date:** 2026-08-02
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0009 (three-tier test strategy — the unit tier runs in-process and in parallel,
  with no Docker), ADR-0005 (Testcontainers is the *integration* substrate; unit-tier fakes are
  in-process by design), ADR-0014 (VoiceAi providers are hand-rolled `HttpClient` /
  `ClientWebSocket` code, which is why each provider carries its own fake server),
  ADR-0038 (CI pipeline slimming — suites run concurrently, so cross-suite port pressure is the
  normal condition), `verbara-meta/ADR-0004` (ecosystem-wide deterministic-test-fences convergence,
  adopt-on-touch)

## Context

The unit tier of this repo runs entirely in-process: each VoiceAi provider suite stands up its own
fake server — an `HttpListener` for the HTTP providers, the shared `WebSocketTestServer` for the
WebSocket ones — on an ephemeral loopback port, and the code under test dials it through a
test-only URI branch guarded by an `internal` constructor parameter (`_fakeServerPort` /
`_fakeWsPort`). xunit runs those suites in parallel, and CI runs them under load.

Two tests flaked in that environment:
`DeepgramSpeechSynthesizerTests.SynthesizeAsync_ShouldSendRequestToCorrectPath` (as
`Expected _server.CapturedRequestUri to start with "/v1/speak", but found <null>`) and
`LmntSpeechSynthesizerWsTests.SynthesizeAsync_ShouldSendTextMessage_WithCorrectText`. Both were
recorded as fake-server *synchronization* problems — an assertion racing the server's capture of
the inbound request. That reading was wrong, and it was wrong in a way that kept producing
plausible-but-ineffective fixes (add a wait, add a signal, add a retry).

The actual mechanism is address-family ambiguity:

1. `localhost` is a **name**, and on this platform it resolves to **`::1` first**, then `127.0.0.1`.
2. `WebSocketTestServer` binds `new TcpListener(IPAddress.Loopback, 0)` — **IPv4 only**. It holds
   `127.0.0.1:{port}`. It does **not** own `::1:{port}`.
3. An `HttpListener` registered with the prefix `http://localhost:{port}/` therefore **binds
   successfully on the same port number** while the `TcpListener` still holds it. Different address
   family, no `EADDRINUSE`, no error, no signal to either side. One port number, two owners.
4. A client that dials `ws://localhost:{port}` resolves `::1` first and lands on the
   **`HttpListener`**, which answers HTTP `200` where a WebSocket handshake requires `101`.

Reproduced directly under CPU saturation (32 spinner processes on 24 cores, 15 consecutive runs of
the TTS suite) as
`System.Net.WebSockets.WebSocketException : The server returned status code '200' when status code
'101' was expected`. The older `CapturedRequestUri == null` symptom is the same fault seen from the
other side: the client never reached the real fake server, so the handler that assigns
`CapturedRequestUri` never ran. That attribution is inference from the same mechanism, not a second
verbatim reproduction.

A compounding factor is worth stating precisely, because it is the part that *cannot* be fixed. The
`HttpListener` fakes choose their port with a TOCTOU probe: bind a `TcpListener` on port `0`, read
the assigned port, `Stop()` it, then hand the now-free port to `HttpListener`. `HttpListener` cannot
adopt an existing socket — it takes a URL prefix and nothing else — so the probe cannot be removed.
Every such fake already carries a retry loop whose own comment reads *"Retry port allocation to
avoid conflicts with parallel tests"*: collisions were known and the symptom was treated. The window
is not the defect. The defect is that **losing the race was silent** — with a name on both sides,
the second bind succeeds and the mis-routed client gets a wrong-but-well-formed HTTP response
instead of a failure.

## Decision

**In-process test servers, and every seam that dials them, address the loopback interface by IPv4
literal (`127.0.0.1`). The name `localhost` is not used on either side.** Concretely:

- **D1 — Port exclusivity is the property that matters, not "does it connect".** A test server is
  only a valid test double if the port it holds has exactly one owner. `localhost` on both sides
  makes ownership a function of resolver order, and resolver order is not a property the test
  controls. An IPv4 literal makes a competing bind fail with `Address already in use`.
- **D2 — Bind side: an explicit IPv4 address, never a name.** `TcpListener` takes
  `IPAddress.Loopback`; `HttpListener` prefixes are `http://127.0.0.1:{port}/`. A fake server's
  advertised `BaseUri` uses the same literal, so the address it hands out is the address it holds.
- **D3 — Dial side: the same literal, including inside `src/`.** The test-only URI branches behind
  `internal` constructors (`_fakeServerPort`, `_fakeWsPort`) dial `127.0.0.1`. A literal bind with a
  named dial is still ambiguous — both sides must agree, or the client can still be resolved onto
  someone else's listener.
- **D4 — The TOCTOU port probe stays, and is tolerated.** `HttpListener` cannot adopt a socket, so
  the probe is not removable. Once D1–D3 hold, a lost race is loud: the `HttpListener.Start()` call
  throws and the fake's existing retry loop picks another port. Tolerating a race whose failure mode
  is a caught exception is sound; tolerating one whose failure mode is a silent cross-wire is not.
- **D5 — This does not apply to product-facing defaults.** `http://localhost:8088` (ARI base URL),
  `http://localhost:4317` (OTel OTLP endpoint) and the Toxiproxy API default address services this
  SDK does not host. There is no in-process port ownership at stake and the name is the friendlier
  default for an operator. They stay as they are, and the guard must not flag them.
- **D6 — A deterministic guard enforces it, not a review habit.** A source-scanning guard in
  `Tests/Verbara.Sdk.Governance.Tests/` — the same idiom as `SyncFenceRegressionGuardTests` and
  `ReflectionBanGuardTests` — fails the build when a fake-server seam reintroduces the name. It
  ships with detector unit tests pinning a true positive and the D5 false-positive immunity, plus a
  liveness self-test so that scanning zero files cannot report a pass.
- **D7 — A future provider suite inherits the obligation.** Any new in-process fake server binds an
  explicit IPv4 literal, publishes that literal in whatever `BaseUri`/`Port` accessor it exposes,
  and adds its test-only dial seam using the literal. If a suite ever needs IPv6 coverage, it binds
  `IPAddress.IPv6Loopback` and dials `[::1]` **explicitly** — a second literal, still never a name.

## Consequences

- Positive: the failure mode is eliminated at its source rather than papered over. There is no
  arrangement of waits, signals or retries that fixes a client talking to the wrong server; there is
  no way for a client to talk to the wrong server once each port has one owner.
- Positive: the existing retry loops become correct instead of superstitious. They were written for
  a collision they could never actually observe; now they observe it.
- Positive: a whole class of future flakes is closed by construction, and the guard makes
  reintroduction a build failure with a named file rather than a bug report weeks later, filed under
  the wrong cause.
- Positive: the mis-diagnosis is on the record. "Fake-server synchronization" is now a documented
  wrong answer for this symptom, which is worth more than the one-line fix.
- Negative: `127.0.0.1` reads as less friendly than `localhost` in test source, and the reason is
  non-obvious. Mitigated by an inline comment at the bind sites and by D6 — the guard explains
  itself when it fires.
- Negative: the convention spans the `src`/`Tests` boundary (D3), so a purely test-motivated rule
  now constrains string literals inside shipped packages. Those literals are unreachable outside the
  `internal` test-only constructors, but the coupling is real and must be respected by anyone
  touching a provider's URI builder.
- Neutral: this says nothing about the *integration* tier. Testcontainers-backed suites (ADR-0005)
  address containers by whatever the container runtime publishes and are out of scope.
- Neutral: no IPv6 coverage is lost, because there was none — every in-process fake already bound
  IPv4 only. The change makes the existing IPv4-only reality explicit instead of accidental.

## Alternatives considered

- **Option B: bind the test servers dual-stack (`IPAddress.IPv6Any` with dual-mode) and keep
  dialling `localhost`** — rejected. It removes the cross-wire only if *every* in-process listener
  in the repo is converted and stays converted; one IPv4-only listener anywhere reopens the exact
  hole. It also enlarges what each fake owns (a dual-stack bind claims the port on both families),
  making unrelated suites collide more often, and `HttpListener` prefix semantics for dual-stack are
  materially harder to reason about than a literal. Strictly more machinery for strictly less
  certainty.
- **Option C: keep `localhost` and make every listener dual-stack by policy, enforced by a guard** —
  rejected, and it is Option B's cost with an extra guard on top. The guard would have to prove a
  *binding mode* rather than match a string, which means either reflection over listener state or a
  runtime probe — both worse than a source scan, and the runtime probe reintroduces the timing
  dependence this whole ADR exists to remove.
- **Option D: serialize the affected suites with an xunit collection** — rejected. It trades wall
  time for correctness in the wrong direction: the suites would run sequentially forever (ADR-0038
  slimmed CI precisely to avoid that), and it does not actually fix anything — a developer running
  two suites in two terminals, or CI running two *projects* concurrently, still cross-wires. It
  suppresses the symptom under one specific scheduler.
- **Option E: keep the retry loop and accept the flake as environmental** — rejected. This was the
  status quo, and it is what produced two tests with a wrong recorded cause. The retry loop cannot
  help: it only runs when a bind *fails*, and the entire defect is that the bind *succeeds*. A flake
  attributed to the environment is a flake nobody can fix, and it erodes trust in every other red
  build.
- **Option F: give each fake server a fixed, hand-assigned port per suite** — rejected. It removes
  the TOCTOU probe but replaces a rare, now-loud collision with a permanent one: two developers, two
  branches, or one CI agent running two jobs would deadlock on the same fixed port with no retry
  path. Ephemeral ports plus a loud failure plus retry is strictly better than a static allocation
  table that has to be maintained by hand.
