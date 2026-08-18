# provider-contract-fidelity — Delta

## ADDED Requirements

### Requirement: Provider wire contracts are asserted against recorded provider responses

Every VoiceAi STT and TTS provider client SHALL have at least one test that replays a **checked-in
recording of a real provider response** — not only hand-authored fixtures. A hand-authored fixture
proves the client agrees with its author; only a recording proves the client agrees with the vendor.
Hand-authored fixtures remain legal for shapes a recording cannot produce on demand (error statuses,
truncated streams, abnormal socket closure), and MUST be labelled as synthetic so the two classes are
never confused. Provider suites SHOULD additionally assert the telemetry the client emits on the
recorded path — `stt.transcriptions.started` / `stt.transcriptions.completed` /
`stt.transcription.latency_ms` for recognizers, `tts.syntheses.started` /
`tts.syntheses.completed` / `tts.synthesis.characters` / `tts.synthesis.latency_ms` /
`tts.synthesis.ttfa_ms` for synthesizers.

#### Scenario: A recorded response drives the transcript assertion

- **GIVEN** a checked-in recording of a real STT provider response, carrying every field the vendor actually sends
- **WHEN** the provider suite replays that recording through the substrate
- **THEN** the client parses it without error and the asserted transcript, confidence and final/interim flags match the recording's contents

#### Scenario: An unmodelled sibling field does not break the parser

- **GIVEN** a recorded response containing fields the client does not model
- **WHEN** the provider suite replays it
- **THEN** the client ignores the unmodelled fields and still yields the expected result — a regression that made an unmodelled field fatal turns the suite red

#### Scenario: Synthetic fixtures stay available for failure shapes

- **GIVEN** a failure shape a live capture cannot reliably produce (HTTP 429, a truncated audio stream, an abnormal socket close)
- **WHEN** the suite needs to assert the client's behaviour for it
- **THEN** a hand-authored fixture MAY be used, and it is labelled synthetic so it is not mistaken for a recording

### Requirement: HTTP-transport providers share one substrate

Provider clients whose production transport is HTTP request/response SHALL be exercised through a
single shared HTTP mock-server substrate (WireMock.NET, per ADR-0041), replacing the per-suite
`MockHttpMessageHandler` and `HttpListener` fakes. The substrate MUST match on method, path, query
string and headers so that a request the client sends to the wrong URL or with the wrong auth header
fails to match instead of silently receiving the canned response. Once no suite references them, the
duplicated `MockHttpMessageHandler` copies under `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Helpers/` and
`Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Helpers/` SHALL be removed rather than left as a second,
divergent way to fake HTTP.

#### Scenario: A misrouted request is not silently satisfied

- **GIVEN** a WireMock stub registered for the provider's real method, path and auth header
- **WHEN** the client issues a request to a different path, or omits the provider's API-key header
- **THEN** the request does not match the stub and the test fails — the previous handler returned the same canned response regardless of method, URL or headers

#### Scenario: Status-code and streaming behaviour are driven from the transport

- **GIVEN** a stub configured to return an error status, or a chunked body
- **WHEN** the client consumes the response
- **THEN** the client's error and frame-chunking paths are exercised at the transport level, not simulated by a handler that can only return one fixed response

### Requirement: WebSocket streaming providers keep the in-process WebSocket substrate

Provider clients whose production transport is a `ClientWebSocket` streaming session SHALL continue
to be exercised against `Verbara.Sdk.TestInfrastructure`'s `WebSocketTestServer` and their
per-provider protocol fakes. WireMock.NET MUST NOT be adopted for them: it is an HTTP/1.1
request-matching server, and bidirectional WebSocket framing is not its contract. This applies to
Deepgram, AssemblyAI, Cartesia and Speechmatics on the STT side and to Cartesia, Deepgram and
ElevenLabs on the TTS side. LMNT ships **both** transports (`LmntTransport.WebSocket` by default and
`LmntTransport.Http`); its two paths SHALL be split across the two substrates by transport, in one
suite. These suites SHALL still adopt recorded payloads per the recorded-response requirement — the
substrate is unchanged, the fixtures are not.

#### Scenario: A WebSocket provider is not migrated

- **GIVEN** a provider whose client opens a `ClientWebSocket` session
- **WHEN** its substrate is chosen
- **THEN** it uses `WebSocketTestServer`, and the change's task list records it explicitly as not-migrated rather than leaving the omission to be read as an oversight

#### Scenario: A dual-transport provider is split, not duplicated

- **GIVEN** LMNT, which selects transport via `LmntTtsOptions.Transport`
- **WHEN** its suite is updated
- **THEN** the `LmntTransport.Http` tests run against WireMock and the `LmntTransport.WebSocket` tests stay on the WebSocket fake, both inside the existing suite

### Requirement: Recordings committed to this public repo are redaction-safe

Any recording checked into this repository SHALL be safe to publish on github.com under MIT.
Concretely, a recording MUST NOT contain a provider API key, bearer token, signed URL or any other
credential; MUST NOT contain account, tenant, project or billing identifiers, nor request/session
identifiers that correlate to a real account; and MUST be derived from **synthetic or public-domain
source audio** only — never customer audio, never a recording of an identifiable person. Capture
MUST be permitted by the provider's terms of service, and the recording's provenance (provider,
endpoint, capture date, source-audio origin) SHALL be recorded alongside it. Every recording SHALL
pass a redaction check before it is committed. Binary TTS captures SHALL be size-capped, and any
capture format that exceeds the cap SHALL be tracked via Git-LFS by extending `.gitattributes`
(which currently tracks only `*.onnx`).

#### Scenario: A capture carrying a credential is not committed

- **GIVEN** a raw capture whose headers or body echo the API key used to make the call
- **WHEN** the redaction check runs before commit
- **THEN** the capture is rejected until the credential is replaced with an obvious placeholder

#### Scenario: Source audio is synthetic

- **GIVEN** a TTS or STT recording destined for the repo
- **WHEN** its provenance is reviewed
- **THEN** the source audio is synthetic or public-domain and its origin is documented — no customer audio and no identifiable person's voice enters a public MIT repo

#### Scenario: An oversized binary capture goes to LFS

- **GIVEN** a binary TTS capture larger than the agreed per-file cap
- **WHEN** it is added
- **THEN** `.gitattributes` carries an LFS rule for its extension, so the public repo's clone size stays bounded

### Requirement: The provider test substrate stays test-only

The HTTP mock-server dependency SHALL be pinned in `Directory.Packages.props` (ADR-0004) and
referenced **only** from projects under `Tests/`. It MUST NOT appear as a `PackageReference` in any
`src/**` project and MUST NOT surface in any produced `.nupkg`. Test projects are already
`IsPackable=false` and `IsAotCompatible=false` via `Directory.Build.props`; the substrate is
therefore explicitly **exempt from the repo's AOT constraints** (ADR-0001), and that exemption
extends to test projects only — it grants no reflection latitude anywhere under `src/`. The
dependency's license MUST clear the `dependency-review` deny-list (AGPL / GPL / SSPL) before the pin
lands.

#### Scenario: A shipped package does not gain the dependency

- **GIVEN** the substrate pinned in `Directory.Packages.props` and referenced from test projects
- **WHEN** `dotnet pack` runs over the solution
- **THEN** no produced `.nupkg` declares the substrate as a dependency, because `src/**` never references it

#### Scenario: The AOT exemption does not travel

- **GIVEN** a reflection-heavy test-only substrate
- **WHEN** the AOT validation workflow publishes the shipped projects
- **THEN** it still succeeds with zero trim warnings — the substrate is confined to `IsAotCompatible=false` test projects and never reaches an AOT publish graph

## Architectural Risk

**Level:** MEDIUM.

**Affected:** `Tests/Verbara.Sdk.VoiceAi.Stt.Tests`, `Tests/Verbara.Sdk.VoiceAi.Tts.Tests`,
`Tests/Verbara.Sdk.TestInfrastructure`, `Directory.Packages.props`, CI wall-clock (ADR-0038), and
this public repo's content surface (verbara-meta/ADR-0005). No `src/**` code, no public API, no
cascade to Sdk.Pro or Platform.

**Mitigation:** migration is per-provider and additive — each WireMock fixture must reach parity with
the fake it replaces (coverage floor as the objective check) before that fake is deleted; the
HTTP/WebSocket split is fixed by ADR-0041 D2/D3 so it cannot erode by drift; recordings pass a
redaction checklist and the first migrated provider establishes the capture protocol before the rest
follow; the `test-determinism` cancellation assertions carry over verbatim as the swap's regression
tripwire; and CI wall-clock is measured on the first migrated suite, with the rollout halting if the
delta is material under ADR-0038.
