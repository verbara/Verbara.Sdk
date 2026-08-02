---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: VoiceAi provider maintainers — whoever has to prove an STT/TTS client still speaks a vendor's wire format after the vendor changes it, and downstream Pro/Platform consumers who inherit a silently-wrong parser
decision_ref: Sdk/ADR-0041
---

# Proposal: wiremock-http-provider-substrate

## Why

The 7 STT and 6 TTS provider clients are exercised today against **hand-rolled, per-provider
fakes**. Three different substrates coexist:

| Substrate | Where | Used by |
|-----------|-------|---------|
| `MockHttpMessageHandler` (canned single response, no socket) | `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Helpers/`, `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Helpers/` — two independent copies with different constructors (`string jsonBody` vs `byte[] responseBytes`) | Whisper, AzureWhisper, Google, Azure TTS |
| `HttpListener` fake servers | `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Speechmatics/SpeechmaticsFakeServer.cs`, `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Lmnt/LmntFakeServer.cs` (`LmntHttpFakeServer`) | Speechmatics TTS, LMNT HTTP path |
| `WebSocketTestServer` (`Tests/Verbara.Sdk.TestInfrastructure/WebSocket/`) + a per-provider protocol fake | `Deepgram/`, `AssemblyAi/`, `Cartesia/`, `Speechmatics/` (STT); `Cartesia/`, `Deepgram/`, `ElevenLabs/`, `Lmnt/` (TTS) | the 8 WebSocket streaming surfaces |

**What the existing fakes already prove — and prove well.** This is not an uncovered area. The
suites already assert request shape (query strings, `Authorization` scheme, provider-specific API-key
headers, `application/ssml+xml` content type, multipart form data, XML escaping of synthesized text),
response mapping (interim vs final transcripts, confidence values, binary frame yielding, filtering
of `alignment`/`metadata`/`warning` control messages), connection lifecycle (server close, server
abort, abort mid-send, HTTP error status), and the deterministic cancellation contract the
`test-determinism` capability mandates. `WebSocketTestServer` in particular is a deliberate,
load-bearing piece of infrastructure — it exists because the earlier `HttpListener`-based fakes hung
on Linux dispose paths. **None of that is being replaced because it is weak.**

**What they structurally cannot prove.** Every fixture in the repo is hand-authored by the same
person who wrote the parser it feeds. A shared misreading of a vendor's schema is invisible:
`DeepgramFakeServer.BuildResultJson` emits a five-field minimal object, while a real Deepgram
`Results` message carries `speech_final`, `channel_index`, `duration`, `start`, `metadata` and
word-level arrays. A parser that depends on field ordering, that mishandles null-vs-absent, or that
would throw on an unmodelled sibling field passes today and fails in production. Two more
consequences follow:

- **`MockHttpMessageHandler` returns the same response for every request** regardless of method, URL
  or headers. Status-code sequences, chunked transfer, content-encoding and per-request matching are
  not expressible, so retry/failure-path behaviour cannot be driven from the transport.
- **TTS "audio" is `new byte[320]` — zeros.** No real codec bytes ever traverse the frame-chunking
  path, so a frame-boundary or byte-order defect is undetectable.

There is no drift detector either: when a vendor changes a response shape, nothing in this repo
turns red. The gap is **wire-format fidelity against recorded real provider responses**, not
coverage breadth.

## What Changes

Adopt **WireMock.NET** as the single test substrate for the provider surfaces whose production
transport is HTTP request/response, and drive each such suite from **checked-in recordings** of real
provider responses (JSON for STT, binary captures for TTS). Recorded in **Sdk/ADR-0041**.

WireMock.NET is not in `Directory.Packages.props`; under ADR-0004 every NuGet version is pinned
there, so this is a new test-dependency convention and needs the ADR rather than a bare pin.

**The substitution is partial by design.** WireMock.NET is an HTTP/1.1 request-matching mock server;
bidirectional WebSocket framing is not its contract. Verified per provider against
`src/Verbara.Sdk.VoiceAi.Stt/` and `src/Verbara.Sdk.VoiceAi.Tts/`:

**STT — 7 providers** (`Tests/Verbara.Sdk.VoiceAi.Stt.Tests/`)

| Provider | Suite directory | Transport | Substrate after this change |
|----------|-----------------|-----------|-----------------------------|
| OpenAI Whisper | `Whisper/WhisperSpeechRecognizerTests.cs` | HTTP (multipart POST) | **WireMock** |
| Azure OpenAI Whisper | `Whisper/AzureWhisperSpeechRecognizerTests.cs` | HTTP (deployment endpoint) | **WireMock** |
| Google Speech-to-Text | `Google/` | HTTP (`speech:recognize`, JSON) | **WireMock** |
| Deepgram | `Deepgram/` | WebSocket (`ClientWebSocket`) | unchanged — `WebSocketTestServer` |
| AssemblyAI | `AssemblyAi/` | WebSocket | unchanged — `WebSocketTestServer` |
| Cartesia | `Cartesia/` | WebSocket | unchanged — `WebSocketTestServer` |
| Speechmatics | `Speechmatics/` | WebSocket | unchanged — `WebSocketTestServer` |

**TTS — 6 providers** (`Tests/Verbara.Sdk.VoiceAi.Tts.Tests/`)

| Provider | Suite directory | Transport | Substrate after this change |
|----------|-----------------|-----------|-----------------------------|
| Azure TTS | `Azure/` | HTTP (SSML POST → audio stream) | **WireMock** |
| Speechmatics | `Speechmatics/` | HTTP (JSON POST → audio bytes) | **WireMock** |
| LMNT | `Lmnt/` | **both** — `LmntTransport.WebSocket` (default) and `LmntTransport.Http` | **split**: HTTP path → WireMock; WS path unchanged |
| Cartesia | `Cartesia/` | WebSocket | unchanged — `WebSocketTestServer` |
| Deepgram | `Deepgram/` | WebSocket | unchanged — `WebSocketTestServer` |
| ElevenLabs | `ElevenLabs/` | WebSocket | unchanged — `WebSocketTestServer` |

That is **6 of 14 transport surfaces on WireMock** (5 HTTP-only providers + LMNT's HTTP path) and
**8 staying on `WebSocketTestServer`**. Any claim of a blanket substitution would be false.

The WebSocket suites still get the half of this change that matters most to them: their fakes are
re-seeded from **recorded** provider frames instead of hand-authored minimal JSON. The substrate is
untouched; the payloads become real.

Recordings are a licensing and PII surface — they land in a **public MIT repo on github.com**. The
change therefore fixes a redaction rule (no API keys, no request/account identifiers, synthetic or
public-domain source audio only, capture permitted by the provider's terms) and decides Git-LFS
handling for binary TTS captures. `.gitattributes` currently tracks only `*.onnx` under LFS.

## Capabilities

### New Capabilities

- `provider-contract-fidelity`: the durable statement that a VoiceAi provider client's conformance
  to a vendor wire format is asserted against a **recorded real response**, that HTTP-transport
  providers share one substrate while WebSocket providers keep the in-process one, and that anything
  recorded into this public repo is redaction-safe.

### Modified Capabilities

- None. `test-determinism` is deliberately untouched: every
  `StreamAsync_ShouldAbort_WhenCancelled` / `SynthesizeAsync_ShouldAbort_WhenCancelled` assertion
  keeps its current shape and its iteration-boundary semantics. A substrate swap must not become a
  cancellation-contract rewrite.

## Impact

- `Directory.Packages.props`: one new `PackageVersion` (test-only). Its license must clear
  `dependency-review`'s deny-list (AGPL/GPL/SSPL) before the pin lands.
- `Tests/Verbara.Sdk.TestInfrastructure/`: a shared WireMock fixture + recording loader.
- `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/`, `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/`: 6 suites migrated,
  8 suites re-seeded with recorded payloads, the two duplicate `MockHttpMessageHandler` copies
  retired once no suite references them.
- New `Recordings/` fixture trees plus a `.gitattributes` LFS rule if binary captures exceed the
  size cap.
- **No production code changes.** No `src/**` file is touched; no public API moves; nothing cascades
  to Sdk.Pro or Platform.
- CI: a real loopback HTTP server per fixture is slower than the socket-less
  `MockHttpMessageHandler`. Against ADR-0038's wall-clock budget this must be measured, not assumed
  — the `HttpListener` fakes already pay this cost for two suites.
- AOT: test projects are `IsPackable=false` and `IsAotCompatible=false` (`Directory.Build.props`),
  so a reflection-heavy test-only dependency neither reaches a shipped package nor violates ADR-0001.

## Architectural Risk

**Level:** MEDIUM.

**Affected:** `Tests/Verbara.Sdk.VoiceAi.Stt.Tests`, `Tests/Verbara.Sdk.VoiceAi.Tts.Tests`,
`Tests/Verbara.Sdk.TestInfrastructure`, `Directory.Packages.props`, CI wall-clock (ADR-0038 budget),
and the public repo's content surface (verbara-meta/ADR-0005). No `src/**` code, no public API, no
downstream cascade to Sdk.Pro or Platform.

**Mitigation:** (1) Adoption is per-provider and additive — a WireMock fixture must reach parity with
the fake it replaces before the fake is deleted, and the coverage floor
(`scripts/check-coverage-floor.py`) is the objective parity check; (2) the WebSocket split is fixed
in ADR-0041 D2/D3, so "WireMock everywhere" cannot be adopted by drift; (3) recordings pass a
redaction checklist before they are committed, and the first migrated provider establishes the
capture protocol before the remaining five follow; (4) the `test-determinism` cancellation
assertions are carried over verbatim and are the regression tripwire for the substrate swap; (5) CI
wall-clock is measured on the first migrated suite and the rollout stops if the delta is material
under ADR-0038.
