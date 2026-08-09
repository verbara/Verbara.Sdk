# ADR-0041: WireMock.NET as the HTTP provider test substrate; WebSocket providers stay in-process

- **Status:** Accepted (2026-08-09 — the D10 license gate, the D9 wall-clock measurement and the
  first migrated surface all cleared before acceptance; see the acceptance note below)
- **Date:** 2026-08-02
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0004 (central package management), ADR-0005 (Testcontainers is the
  integration-test substrate), ADR-0009 (three-tier test pyramid), ADR-0014 (raw HTTP /
  `ClientWebSocket` for VoiceAi providers), ADR-0038 (CI pipeline slimming — wall-clock budget),
  verbara-meta/ADR-0004 (deterministic-test-fences program), verbara-meta/ADR-0005 (public-repo
  content rule). Change: `wiremock-http-provider-substrate` (openspec).

## Context

The SDK ships 7 STT and 6 TTS provider clients. ADR-0014 established *why* they are hand-written
against raw `HttpClient` / `ClientWebSocket` rather than vendor SDKs: every candidate vendor SDK
carries reflection-based serialization and `Reflection.Emit` caches that ADR-0001 forbids. The
consequence ADR-0014 accepted, and did not solve, is that **this repo owns wire-protocol parity for
thirteen third-party APIs it does not control.**

Today that parity is asserted against hand-rolled per-provider fakes, in three unrelated styles:

- `MockHttpMessageHandler` — an in-process `HttpMessageHandler` returning one canned response. Two
  independent copies exist, one per test project, with different constructors (`string jsonBody` in
  the STT copy, `byte[] responseBytes` in the TTS copy). Used by Whisper, AzureWhisper, Google and
  Azure TTS.
- `HttpListener`-based fake servers — real loopback HTTP, used by Speechmatics TTS
  (`SpeechmaticsFakeServer`) and the LMNT HTTP path (`LmntHttpFakeServer`).
- `WebSocketTestServer` (`Tests/Verbara.Sdk.TestInfrastructure/WebSocket/`) plus a per-provider
  protocol fake — used by the eight WebSocket streaming surfaces. This one is deliberate
  infrastructure: it replaced `HttpListener`-based WebSocket fakes whose `AcceptWebSocketAsync` +
  `Abort()` dispose path hung on Linux, and it mirrors the production
  `Verbara.Sdk.Ari.Audio.WebSocketAudioServer` accept path.

**These fakes work, and they prove a lot.** Request shape (query strings, `Authorization` scheme,
provider-specific API-key headers, `application/ssml+xml`, multipart form data, XML escaping),
response mapping (interim vs final transcripts, confidence, binary frame yielding, filtering of
alignment/metadata/warning control frames), lifecycle (server close, server abort, abort mid-send,
error status) and the deterministic cancellation contract are all asserted per provider. Nothing here
is being replaced because it is weak.

What they cannot do is structural. **Every fixture is hand-authored by the author of the parser it
feeds**, so a shared misreading of a vendor schema is invisible: `DeepgramFakeServer.BuildResultJson`
emits a five-field minimal object where a real Deepgram `Results` message carries `speech_final`,
`channel_index`, `duration`, `start`, `metadata` and word-level arrays. A parser that depends on
field ordering, mishandles null-vs-absent, or would throw on an unmodelled sibling field passes
today. Two narrower limits follow from the substrate itself:

- `MockHttpMessageHandler` returns the same response for **every** request, regardless of method,
  URL or headers. A client that posted to the wrong path or dropped its auth header would still be
  handed the canned success body. Status-code sequences, chunked transfer and content-encoding are
  not expressible at all.
- TTS "audio" is `new byte[320]` — zeros. Real codec bytes never traverse the frame-chunking path, so
  a frame-boundary or byte-order defect is undetectable.

And there is no drift detector: when a vendor changes a response shape, nothing in this repo turns
red.

The fix is to drive provider suites from **recorded real provider responses**, which needs a
substrate that can match on a request and replay a recorded response — the thing
`MockHttpMessageHandler` structurally cannot do. WireMock.NET is the standard .NET answer. Adopting
it is not a one-line pin: ADR-0004 puts every NuGet version in `Directory.Packages.props`, WireMock
is not there today, and adding it establishes a **test-dependency convention** for the whole repo.

Two constraints shape the decision. First, WireMock.NET is an HTTP/1.1 request-matching server;
bidirectional WebSocket framing is not its contract, and 8 of the 14 provider transport surfaces are
WebSocket streaming. A blanket "replace the fakes with WireMock" is not available. Second,
recordings of real provider traffic get committed to a **public MIT repository on github.com** —
they are a credential, PII and licensing surface, governed by verbara-meta/ADR-0005.

## Decision

Adopt WireMock.NET as the shared test substrate for the HTTP-transport provider surfaces only,
driven by checked-in recordings, and keep the WebSocket surfaces on the existing in-process server.

- **D1 — WireMock.NET is the substrate for HTTP request/response providers.** The 6 surfaces are:
  OpenAI Whisper, Azure OpenAI Whisper and Google Speech-to-Text (STT); Azure TTS, Speechmatics TTS
  and the LMNT HTTP path (TTS). Matching is strict by default — method, path, query and required
  headers — so a request sent to the wrong URL or without the provider's auth header fails to match
  instead of receiving a canned response.
- **D2 — WebSocket streaming providers are not migrated.** Deepgram, AssemblyAI, Cartesia and
  Speechmatics (STT) and Cartesia, Deepgram and ElevenLabs (TTS) stay on `WebSocketTestServer` and
  their per-provider protocol fakes. This is a property of the transport, not a staging decision, and
  it does not expire when WireMock ships more WebSocket surface: experimental support in a mock
  server is not a basis for a durable substrate decision, and `WebSocketTestServer` already disposes
  cleanly on the platform CI runs on.
- **D3 — A dual-transport provider is split by transport, not by suite.** LMNT ships both
  `LmntTransport.WebSocket` (default) and `LmntTransport.Http`. Its HTTP tests move to WireMock, its
  WebSocket tests stay on `LmntWsFakeServer`, both inside the one existing suite. Splitting a
  provider across two suites to keep each suite single-substrate would be organising the tests around
  the tooling rather than the product.
- **D4 — Recordings are the fixture of record; hand-authored fixtures remain legal and labelled.**
  Every provider gets at least one test replaying a recorded real response. Hand-authored fixtures
  stay for shapes a capture cannot produce on demand — HTTP 429, truncated streams, abnormal socket
  closure — and are labelled synthetic so the two classes are never confused. **This applies to the
  WebSocket suites too**: they keep their substrate and get recorded frames. The recording, not the
  mock server, is what closes the fidelity gap; WireMock is only what makes replay ergonomic on the
  HTTP side.
- **D5 — Recordings committed here are redaction-safe by rule.** No API keys, bearer tokens or
  signed URLs; no account, tenant, project or billing identifiers; no request/session identifiers
  correlating to a real account; source audio synthetic or public-domain only — never customer audio,
  never an identifiable person's voice. Capture must be permitted by the provider's terms, and
  provenance (provider, endpoint, capture date, source-audio origin) is recorded alongside each
  capture. A repo check enforces the credential rule; documentation alone does not.
- **D6 — Binary captures are size-capped and LFS-tracked above the cap.** `.gitattributes` tracks
  only `*.onnx` under Git-LFS today. Binary TTS captures that exceed the agreed per-file cap extend
  that rule rather than inflating every clone of a public repo.
- **D7 — The substrate is test-only, and its AOT exemption does not travel.** The version is pinned
  in `Directory.Packages.props` (ADR-0004) and referenced only from projects under `Tests/`. No
  `src/**` project references it and no produced `.nupkg` declares it. Test projects are already
  `IsPackable=false` and `IsAotCompatible=false` via `Directory.Build.props`, so a reflection-heavy
  mock server is admissible **there and only there** — it grants no reflection latitude under `src/`,
  where ADR-0001, ADR-0003 and ADR-0024 continue to bind unchanged.
- **D8 — Adoption is per-provider, additive, and parity-gated.** A WireMock fixture must reach parity
  with the fake it replaces before that fake is deleted, with the coverage floor
  (`scripts/check-coverage-floor.py`) as the objective check. The first migrated provider establishes
  the capture and redaction protocol before the remaining five follow.
- **D9 — CI wall-clock is measured, not assumed.** A loopback HTTP server per fixture costs more than
  the socket-less `MockHttpMessageHandler`. The delta is measured on the first migrated suite and
  judged against ADR-0038's budget; a material regression stops the rollout rather than being
  absorbed silently.
- **D10 — The license gate is a precondition.** The pin lands only after WireMock.NET's license is
  confirmed clear of `dependency-review`'s deny-list (AGPL / GPL / SSPL).
- **D11 — A provider whose terms do not clearly grant redistribution gets an envelope capture, not a
  payload capture.** *(Scope note, 2026-08-03: when written, this addressed LMNT alone. The terms
  review of the four WebSocket-only vendors — absent from the original §3.4, which covered the six
  HTTP providers only — brought Deepgram (both directions), AssemblyAI and ElevenLabs under it as
  well. **Five of the eight WebSocket surfaces now take an envelope capture rather than a payload
  one**, so D11 is the common case for WebSocket providers, not the exception D4 assumes. Cartesia
  clears on a commercial tier; Speechmatics STT clears outright. Where a vendor publishes its frame
  schema, hand-authoring `synthetic` frames from that documentation is preferred over an envelope,
  since it raises no terms question and is the authority a parser should be checked against.)* D4 asks every provider for at least one replay of a recorded real response. The
  per-provider terms review (`docs/guides/provider-recording-protocol.md` §7) found that **LMNT does
  not clear that bar**: its ToS (2023-06-12) contains no clause addressing rights in generated audio,
  and its AUP (2023-08-28) restricts sharing synthesized speech outside the capturing entity. Rather
  than infer a redistribution licence out of silence, D4 yields for such a provider: commit the
  response **envelope** — status, headers, media type, content length, observed chunk boundaries — as
  the `recorded` artifact, and pair it with a body built locally from public-domain or synthetic
  audio in the same codec, committed as `synthetic`. The suite still gets strict matching, a real
  status/header set and real byte lengths through the frame-chunking path. Speechmatics TTS sits one
  step above LMNT — permitted by inference from a derivatives clause rather than by express grant —
  and drops to the same fallback if a reviewer is not comfortable with the inference. **One capture
  remains gated on a human read**: Google — the AI/ML Services enumeration could not be retrieved
  verbatim, and if Speech-to-Text is not listed there the verdict drops to `not-cleared`. The OpenAI
  gate **cleared on 2026-08-03**: `openai.com/policies/*` 403s to automated fetchers, but the same
  contract is published as a PDF on OpenAI's own CDN, which does not
  (`cdn.openai.com/osa/openai-services-agreement.pdf`, `ONLINE v.010126`). §4.1 assigns all of
  OpenAI's right, title and interest in Output to the customer, and §3.3's nine restrictions include
  none on publishing or redistributing it. The *Sharing and Publication Policy* it incorporates by
  reference still 403s, but it imposes attribution and disclosure conditions that the provenance
  sidecar discharges, not a prohibition — a residual, not a gate. **A 403 to a fetcher is not a
  closed door; look for the same document on a CDN, a PDF mirror or a regulatory filing before
  recording a finding as unverifiable.**
- **D12 — A provider that composes its own request URL gets an `internal` test-only origin seam, and
  the seam substitutes the origin only.** A loopback server can only be reached through
  `HttpClient.BaseAddress`; a provider that builds an absolute URL itself ignores it. Two of D1's six
  do: **Azure TTS** composes the host from `Region`, **Google STT** hardcodes
  `https://speech.googleapis.com/…`. Each takes the `internal` base-URI seam this repo already uses
  for `SpeechmaticsSpeechSynthesizer` (`_fakeBaseUri`) and `LmntSpeechSynthesizer`
  (`_fakeHttpBaseUri`) — precedent, not a new pattern. **The seam replaces the scheme/host/port and
  nothing else:** the route stays in production code, so D1's strict matcher asserts the path the
  provider really builds rather than one the test handed it. A seam that accepted a full URL would
  delete the assertion it exists to enable. Nothing becomes public API; the four remaining providers
  need no seam (Whisper and Azure OpenAI Whisper read `Options.Endpoint`; Speechmatics and LMNT
  already carry one).

## Acceptance note (2026-08-09)

Accepted after Phase A (§1–§3 of the change) shipped in PR #149 and the first surface — Azure TTS,
§4.4 — migrated. Three things are true at acceptance that were open at proposal:

- **D10 cleared:** `WireMock.Net` 2.13.0 is Apache-2.0, no AGPL/GPL/SSPL node in the resolved graph,
  0 vulnerable packages.
- **D9 measured, not assumed:** +0.6 ms per fixture construct/dispose, +1.3 ms with one request;
  projected **+30 ms** across the 23 tests on the six migrating surfaces, plus ~80 ms once per
  assembly for WireMock/Kestrel init. Far under ADR-0038's budget; the D9 stop-condition is not
  approached.
- **D12 was learned, not designed.** It is written above as a decision because the *proposal* asserted
  the opposite (no `src/**` change) and the first migration disproved it. Recorded here rather than
  left in a commit message because it binds the Google STT migration that has not happened yet.

One substrate placement in §2 also had to move for a reason worth carrying: WireMock lives in its own
`Tests/Verbara.Sdk.TestInfrastructure.Http` project, not in `TestInfrastructure`. Referencing WireMock
adds a `FrameworkReference` to `Microsoft.AspNetCore.App`, which stops ~30 `Microsoft.Extensions.*`
assemblies being copied to the output directory; coverlet's Cecil resolver then fails to resolve them
and **silently skips instrumenting** the modules that reference them. Measured cost when it landed in
the shared project: line coverage 80.42% → 61.96% with all 3 020 tests still green — a green suite
hiding a coverage-gate failure, caught only by the ratchet on PR #149.

## Consequences

- Positive: the fidelity gap closes where it actually is. A recorded response carrying the vendor's
  full field set catches the failure mode a hand-authored fixture cannot — a parser that agrees with
  its author and disagrees with the vendor.
- Positive: strict request matching turns a class of silent passes into failures. A client posting to
  the wrong path, or omitting its API-key header, currently still receives the canned success body.
- Positive: TTS finally moves real codec bytes through the frame-chunking path instead of 320 zeros.
- Positive: three HTTP-faking styles collapse to one, including two divergent copies of the same
  `MockHttpMessageHandler` class in two test projects.
- Positive: D7 keeps the open-core/AOT boundary explicit. Test-only dependency conventions are a
  place where "we already allow reflection here" quietly becomes "we allow reflection"; naming the
  confinement is cheaper than re-litigating it later.
- Negative: the substrate story becomes two-sided by design — WireMock for 6 surfaces,
  `WebSocketTestServer` for 8. A contributor must know a provider's transport before knowing which
  fixture to write. D2/D3 and the per-suite transport comment exist to make that discoverable rather
  than tribal.
- Negative: a new external test dependency and its transitive graph enter a repo where `NU1605` is
  fatal, plus an ongoing Dependabot surface.
- Negative: CI gets slower on the migrated suites. Six loopback servers replace four socket-less
  handlers, against a pipeline ADR-0038 was specifically written to speed up.
- Negative: recordings are a maintenance burden with a decay curve. A capture is a point-in-time
  photograph; nothing re-captures it, so a stale recording eventually asserts a wire format the
  vendor no longer sends. This closes the *shared-misreading* gap, not the *drift* gap — detecting
  drift would need live contract tests, which this ADR does not adopt.
- Negative: committing third-party API responses to a public MIT repo is a standing compliance
  surface. D5 and D6 bound it; they do not remove it.
- Neutral, with one exception: **two `src/**` files take an `internal` test-only seam** (D12). The
  proposal claimed no production code would be touched at all; the Azure TTS migration disproved that
  on 2026-08-03, before acceptance, and D12 records the corrected rule. The rest of the claim holds
  and is the part that mattered: no public API moves, no behaviour changes on any production path, and
  nothing cascades to Sdk.Pro or Platform (ADR-0040 is not engaged).
- Neutral: the `test-determinism` capability is untouched. Cancellation assertions carry over verbatim
  and are the regression tripwire for the swap, not a thing the swap gets to redesign.

## Alternatives considered

- **Option B: keep the hand-rolled fakes and just feed them recorded payloads** — rejected, but the
  cheapest option and genuinely close. It captures most of the value: D4 already applies this to the
  8 WebSocket surfaces, which proves the approach works without a new dependency. Rejected for the
  HTTP surfaces because `MockHttpMessageHandler` cannot match on a request at all — it returns one
  response for every call — so recorded payloads there would still leave misrouted and unauthenticated
  requests silently passing, and status-code/chunking behaviour still unexpressible. Keeping it would
  also preserve three faking styles and two divergent copies of the same class.
- **Option C: a different mock server** — rejected. `Microsoft.AspNetCore.TestHost` is already pinned
  in `Directory.Packages.props`, so a hand-rolled minimal-API stub host would add no dependency; it
  was rejected because it means writing and maintaining the request-matching and response-replay
  engine this change needs, which is exactly what WireMock.NET already is. A handler-level mock
  library (`RichardSzalay.MockHttp` and similar) was rejected for the same reason as Option B: it
  intercepts below the transport, so it cannot exercise real HTTP framing, chunked bodies or
  content-encoding.
- **Option D: record-and-replay via a custom `HttpMessageHandler` (VCR-style)** — rejected, and the
  closest call. It would give recorded fidelity with zero new dependencies, reusing the handler seam
  every HTTP provider already accepts for testing, and it would automate capture as a side effect of
  a live run. Rejected because it inherits Option B's ceiling — a handler is not a server, so real
  HTTP framing, transfer-encoding and per-request matching stay out of reach — while adding a
  cassette format and its redaction pipeline that this repo would then own and maintain. The
  redaction problem (D5) is the same work either way; the difference is whether the matching engine
  is maintained here or upstream.
- **Option E: contract tests against the live provider APIs** — rejected. It is the only option that
  actually detects vendor drift, which is this change's acknowledged residual gap. Rejected because
  it requires real API keys in CI (a secret surface on a public repo), bills per run, makes the test
  suite non-deterministic and network-dependent — violating ADR-0009's unit tier and
  verbara-meta/ADR-0004's determinism program — and would put a third-party outage on the critical
  path of every PR. Revisitable later as an opt-in, non-gating scheduled job.
- **Option F: WireMock.NET for everything, WebSocket surfaces included** — rejected. It is the
  tidiest-sounding outcome and the reason D2 is written as a decision rather than an omission.
  WireMock.NET's contract is HTTP/1.1 request matching; the 8 WebSocket surfaces need a server that
  holds a bidirectional session, receives client audio frames, and can close or abort abnormally on
  command — behaviours the existing suites already assert. `WebSocketTestServer` exists precisely
  because an earlier substrate mishandled that dispose path on Linux. Trading a validated
  purpose-built server for uniformity would be paying real reliability for cosmetic consistency.
