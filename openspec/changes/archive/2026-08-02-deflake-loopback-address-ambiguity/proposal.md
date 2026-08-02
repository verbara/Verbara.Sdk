---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: CI reliability / every developer running the VoiceAi suites locally (all downstream repos gate on Sdk CI)
decision_ref: Sdk/ADR-0044
---

# Proposal: deflake-loopback-address-ambiguity

## Why

Two tests flaked under parallel load:

- `DeepgramSpeechSynthesizerTests.SynthesizeAsync_ShouldSendRequestToCorrectPath`, failing as
  `Expected _server.CapturedRequestUri to start with "/v1/speak", but found <null>`
- `LmntSpeechSynthesizerWsTests.SynthesizeAsync_ShouldSendTextMessage_WithCorrectText`

Both had been recorded as **test/fake-server synchronization** problems — an assertion racing the
fake server's capture of the inbound request. **That diagnosis was wrong.** There is no
synchronization race; there is an address-family ambiguity that lets two different servers own the
same port number at the same time. The chain, established by direct experiment:

1. `localhost` resolves to **`::1` first**, then `127.0.0.1`.
2. `WebSocketTestServer` (`Tests/Verbara.Sdk.TestInfrastructure/WebSocket/WebSocketTestServer.cs`)
   binds `new TcpListener(IPAddress.Loopback, 0)` — **IPv4 only**. It does not own `::1` on its port.
3. An `HttpListener` whose prefix is `http://localhost:{port}/` **successfully binds that same port
   number** while the `TcpListener` still holds it. Different address family, so no `EADDRINUSE` —
   both binds succeed and neither process learns of the other.
4. A client dialling `ws://localhost:{port}` resolves `::1` first and therefore reaches the
   **`HttpListener`**, not the WebSocket server. The `HttpListener` answers HTTP **200** where the
   WebSocket handshake requires **101**.

**What was reproduced verbatim:** under CPU saturation (32 spinner processes on 24 cores, 15
consecutive runs of the TTS suite), the failure surfaced as
`System.Net.WebSockets.WebSocketException : The server returned status code '200' when status code
'101' was expected` — the cross-wire in step 4, observed directly.

**What is attributed, not separately reproduced:** the historically recorded
`CapturedRequestUri == null` symptom. It is the same root cause seen from a different angle — the
client never reached the real fake server, so the handler that assigns `CapturedRequestUri` never
ran, leaving it null. This proposal claims that attribution, not a second verbatim reproduction.

**Compounding factor (context, not the fix).** The `HttpListener`-based fakes pick their port with a
TOCTOU probe: bind a `TcpListener` on port 0, read the assigned port, `Stop()` it, then hand the
now-free port to `HttpListener`. That probe cannot be removed — `HttpListener` cannot adopt an
existing socket, it only accepts a URL prefix. Each such fake already carries a retry loop whose own
comment reads *"Retry port allocation to avoid conflicts with parallel tests"*: the author knew
collisions happened and treated the symptom. The probe's window is not what makes this dangerous —
what makes it dangerous is that losing the race is **silent**. This change does not close the
window; it converts a lost race from a silent cross-wire into a loud `EADDRINUSE` that the existing
retry loop already handles.

**Verified fix direction.** With IPv4 **literals** on both the bind side and the dial side, the
competing bind is refused with `Address already in use` and each port has exactly one unambiguous
owner. The durable convention is recorded as `Sdk/ADR-0044`.

## What Changes

- **8 production test seams** change host `localhost` → `127.0.0.1` inside `_fakeServerPort` /
  `_fakeWsPort` branches that are reachable **only** from `internal` test-only constructors:
  `Stt/{Deepgram,AssemblyAi,Cartesia,Speechmatics}` recognizers and
  `Tts/{Deepgram,Cartesia,ElevenLabs,Lmnt}` synthesizers. No public-API change, no change to any
  production default URL, no runtime behavior change for consumers.
- **7 test fake-server sites** change to `127.0.0.1`: the `HttpListener` prefixes in
  `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Deepgram/DeepgramFakeServer.cs`, the
  `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/{ElevenLabs,Speechmatics,Lmnt}` fakes and
  `Tests/Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests/Internal/RealtimeFakeServer.cs`, plus the
  `BaseUri` properties of the Lmnt and Speechmatics fakes.
- **2 test-side dial sites** in the OpenAiRealtime suites set `OpenAiRealtimeBridge.BaseUri` to
  `ws://127.0.0.1:{port}/` instead of the name.
- **A deterministic source-scanning regression guard** is added to
  `Tests/Verbara.Sdk.Governance.Tests/` — the repo's existing idiom, alongside
  `SyncFenceRegressionGuardTests` — failing the build if a fake-server seam reintroduces
  `localhost`. It ships with a liveness self-test (the scan must actually walk a large file set, so
  "found zero files" cannot read as green) and detector unit tests pinning one true positive and
  false-positive immunity.
- **Real product defaults are deliberately NOT flagged**: `http://localhost:8088` (ARI base URL),
  `http://localhost:4317` (OTel OTLP endpoint) and the Toxiproxy API default are user-facing
  defaults for services the SDK does not host, not in-process test-server seams.

Not in scope: removing the TOCTOU port probe (impossible for `HttpListener`), and removing the
per-fake retry loops (they remain the correct handler for the now-loud collision).

## Capabilities

### New Capabilities

None. The contract lands in the existing `test-determinism` capability.

### Modified Capabilities

- `test-determinism`: two ADDED requirements. The capability already owns Sdk's deterministic-test
  contracts, and this is the same failure class it exists for — a test outcome that depends on
  scheduling rather than on the code under test. The prior requirements fence *time* (cancellation
  observed at a deterministic seam instead of raced against mock latency); these fence *address
  space* (a test server's port has exactly one owner instead of racing an address-family
  ambiguity). Same capability, new axis — no new capability is warranted.

## Impact

- `src/Verbara.Sdk.VoiceAi.Stt` and `src/Verbara.Sdk.VoiceAi.Tts`: string-literal host change inside
  test-only URI branches. Public API surface unchanged (`PublicAPI.*.txt` untouched), so nothing
  cascades to `Sdk.Pro` or `Platform`.
- `Tests/Verbara.Sdk.VoiceAi.{Stt,Tts,OpenAiRealtime}.Tests`: fake-server bind/dial sites.
- `Tests/Verbara.Sdk.Governance.Tests`: one new guard + its scanner and unit tests.
- CI: one more deterministic guard test; no new external dependency, no Docker requirement.

## Architectural Risk

**Level:** LOW.

**Affected:** the VoiceAi STT/TTS/OpenAiRealtime test suites and the two `internal` test-only
constructors' URI branches in `src/`. No public API, no production code path, no downstream repo.
An IPv4-literal dial is strictly narrower than a name lookup, so the only theoretical exposure is a
CI host with IPv4 loopback disabled — a configuration in which the existing
`TcpListener(IPAddress.Loopback, 0)` binds already could not work.

**Mitigation:** the change is a host-literal substitution with no logic change; the new governance
guard makes reintroduction a build failure rather than a future flake; the guard itself carries a
liveness self-test and detector unit tests so it cannot pass vacuously; `dotnet test` under the
repeat-run-under-load protocol is the acceptance evidence, and the zero-warnings
(`TreatWarningsAsErrors`) gate is unchanged.
