# provider-contract-fidelity Specification

## Purpose

Whether the SDK's AI-provider clients agree with the services they call, and whether the tests that
say so are built from those services rather than from their authors' reading of a document.

Those are one capability because they failed as one. Every wire defect this repo has found — routes
that returned `404`, audio read from a message type the vendor does not send, a credential placed on
a channel the vendor ignores, a failure the vendor stated that never reached the caller — shipped
past a green suite. The fake and the client had the same author, so a single misreading of the
vendor's contract was encoded twice and agreed with itself. A test written that way certifies the
misreading; it cannot detect it.

So this capability governs both halves, and holds them to different standards. **The client side**
is judged against the live endpoint, beside a control that is known wrong — a wrong path and an
invalid credential, on the same host in the same run, because those answer two different questions
and neither is a weaker form of the other. Where a vendor decides a credential is bad is measured,
never inferred from where the credential sits in the request. And a run that stopped at the
WebSocket upgrade has measured nothing on a vendor that validates in band. **The test side** is
judged on provenance: a recording of a real response outranks a hand-authored fixture, synthetic
fixtures are labelled as such, the substrate stays test-only and out of every shipped package, and
what lands in this public repo is redaction-safe.

Two properties the requirements below keep restating, because both were violated by hand first. A
vendor asserting something is evidence; a vendor not mentioning something is not. And an absent
result is a result: every provider surface carries a recorded status, including *not characterised*,
because a gap between rows reads as coverage until a user finds otherwise.

## Requirements
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

### Requirement: A provider request reaches the endpoint the vendor actually serves
Every VoiceAi provider client SHALL address the route the vendor serves, and that route MUST be
established against the live endpoint rather than against a fake the SDK also authors. A fake answers
whatever its author believed; where author and client are the same person, a wrong route is green on
both sides. Measured on this basis, against the clients as they then shipped: the Speechmatics TTS
synthesizer POSTed to `/generate` with the voice as a JSON body field, but the vendor selects the
voice by path segment — `/generate/{voice}` returns `200 audio/wav` and `/generate` returns `404`;
the LMNT HTTP path POSTed form-encoded to `/v1/ai/speech/generate`, which returns `404`, while the
documented `/v1/ai/speech/bytes` with a JSON body returned `200 audio/mpeg` on the same credential
seconds apart. Both routes are corrected. Where the corrected route changes
the meaning of an already-public option, the correction MUST be taken as an explicit API decision with
its compatibility consequence stated, not as a silent reinterpretation of a shipped property.

#### Scenario: A route that 404s in production is not green in the suite
- **GIVEN** a provider client whose production request path does not exist on the vendor's host
- **WHEN** the provider's test suite runs against its fake
- **THEN** the defect is detectable — the route is checked against the live endpoint and the finding is recorded per surface, rather than the suite passing because the fake serves whatever path the client asks for

#### Scenario: A configurable endpoint can express the vendor's route shape
- **GIVEN** a public option that holds a provider endpoint, and a vendor whose route carries a variable segment the option cannot express
- **WHEN** the route is corrected
- **THEN** the option's new meaning, the behaviour for a caller who already sets it, and the alternative that was rejected are all recorded — because redefining a shipped public property is an API decision and not a one-line patch

#### Scenario: A route with no option is still auditable
- **GIVEN** a provider whose production endpoint is a string literal at its call site with no option to override it
- **WHEN** that endpoint is wrong
- **THEN** no configuration can rescue it and the fix is a code change; the endpoint is therefore declared in one named place so a later reader can audit every provider's route without reading every call site

#### Scenario: What the probe did not test is recorded as untested
- **GIVEN** a probe that isolated the route and nothing else
- **WHEN** the finding is written up
- **THEN** the request fields the probe did not vary are recorded as **not verified** rather than implied correct — a probe that establishes the path says nothing about whether the body fields are accepted as sent

### Requirement: A provider reads audio from the message type the vendor actually sends
A streaming provider client SHALL consume audio from the WebSocket message type the vendor delivers it
on, and MUST NOT assume a frame type the vendor does not document. Measured 2026-08-14, on the clients as they then shipped:
the Cartesia and ElevenLabs TTS synthesizers yielded audio only from `WebSocketMessageType.Binary`
frames, while both vendors deliver audio as base64 inside JSON **text** frames — ElevenLabs on `AudioOutput.audio`,
Cartesia on `chunk.data` — and neither documents a raw-binary mode at all. Absence of documentation is
not permission to infer one: a vendor asserting X is evidence, a vendor not mentioning Y is not, so a
frame type the vendor never describes MUST NOT be the only one a client can consume.

#### Scenario: Base64 audio on a text frame reaches the consumer
- **GIVEN** a vendor that carries audio as a base64 field inside a JSON text frame
- **WHEN** the synthesizer receives that frame
- **THEN** it decodes the field and writes the audio bytes to the consumer's stream, instead of discarding the frame as a control message and yielding nothing

#### Scenario: A vendor that genuinely sends binary keeps working
- **GIVEN** a vendor measured to deliver audio as binary frames — Deepgram TTS, probed live, one `Metadata` text frame then 37 binary frames of 1920 bytes then a `Flushed` text frame
- **WHEN** the text-frame audio path is added elsewhere
- **THEN** the binary path is unchanged and still yields the same bytes, because this requirement names the message type the vendor sends and does not replace one blanket assumption with another

#### Scenario: A frame type is claimed only on evidence
- **GIVEN** a provider surface whose documentation describes exactly one audio-carrying frame type
- **WHEN** the client is written or corrected
- **THEN** the documented type is the one implemented, and any additional type the client tolerates is recorded as tolerated-without-evidence rather than presented as a documented mode

### Requirement: A provider honours the assembly-governing fields the vendor sends
A recognizer SHALL assemble a transcript using the fields the vendor publishes to govern assembly, and
MUST NOT impose its own joining rule over them. Measured on the client as it then shipped: the
Speechmatics recognizer space-joined every token unconditionally, ignoring three things the same
session already carries — the `word_delimiter` the vendor sends on `RecognitionStarted`, the
per-result `attaches_to` marker that says a token binds to its predecessor, and the assembled segment
the vendor publishes at `metadata.transcript`. A segment ending in punctuation therefore emerged with
a spurious space before the period. All three signals are now read. Where the vendor
publishes its own assembled text, that text is the authority; anything the SDK derives locally MUST be
justified by something the published text does not carry.

#### Scenario: Punctuation attaches without a spurious separator
- **GIVEN** a transcript segment whose final result is punctuation marked as attaching to the previous token
- **WHEN** the recognizer assembles the segment
- **THEN** the punctuation is appended with no separator, so the committed fixture's segment ends `mañana.` rather than `mañana .`

#### Scenario: The vendor's own delimiter is used
- **GIVEN** a session whose start message declares a word delimiter
- **WHEN** the recognizer joins tokens for that session
- **THEN** it uses the declared delimiter — including the empty delimiter a non-spacing language pack declares — instead of a hardcoded space

#### Scenario: The vendor's assembled segment is preferred over a local re-assembly
- **GIVEN** a message that carries both per-token results and the vendor's assembled segment text
- **WHEN** the recognizer produces its result
- **THEN** the vendor's assembled text is what the consumer receives, and any value still derived from the per-token results — confidence, timing — is derived deliberately and stated, rather than the whole segment being rebuilt because the code always has

### Requirement: A provider authenticates on the channel the vendor accepts
A provider client SHALL present its credential on the channel the vendor accepts, and that acceptance
MUST be established by reaching the vendor's first protocol exchange rather than by a successful
handshake. Measured 2026-08-15 on Speechmatics realtime STT, against the client as it then shipped: the
recognizer placed the long-lived API key in the `?jwt=` query parameter, the vendor completed the
WebSocket upgrade with `101`, and then closed the socket with close code `4001 not_authorised` — the rejection is at the protocol layer,
after the handshake succeeded. The same credential was accepted twice on the same host seconds apart,
once as an `Authorization: Bearer` header with no query parameter and once as a short-lived key minted
from the vendor's management endpoint, both reaching `RecognitionStarted`. Two remedies therefore
exist, and choosing between them is an API decision with a recorded basis rather than a forced move.
Where a vendor authenticates in the HTTP upgrade headers, the handshake status is sufficient evidence
of acceptance; where it authenticates in-band, it is not.

#### Scenario: A rejection that arrives after the handshake is still caught
- **GIVEN** a vendor that completes the WebSocket upgrade and then rejects the credential in-band with a close code
- **WHEN** the surface is probed
- **THEN** the probe reads past the upgrade into the first protocol exchange so the close code is observed and the surface is recorded as broken — a probe stopping at the `101` would have recorded a provider that cannot open a single session as verified good

#### Scenario: Probe depth follows the vendor's authentication channel
- **GIVEN** one vendor that authenticates in the HTTP upgrade headers and one that authenticates in-band after the upgrade
- **WHEN** each is probed
- **THEN** the header-authenticating vendor's handshake status is recorded as sufficient evidence and the in-band vendor's is recorded as insufficient, rather than one probe depth being applied to both and the difference going unstated

#### Scenario: The credential is exonerated before the client is blamed
- **GIVEN** an authentication failure that a credential lacking the entitlement would explain equally well
- **WHEN** the finding is recorded
- **THEN** the same credential is first shown to succeed through another channel on the same host, so the defect is attributed to the client's authentication scheme rather than to the key — an unexcluded competing explanation is not a finding

#### Scenario: The remedy is chosen on measurement and the basis is recorded
- **GIVEN** more than one measured way to authenticate against the same vendor
- **WHEN** one is implemented
- **THEN** the choice, the alternative rejected by name, and what the vendor's documentation does and does not say about each are recorded — because the measurement establishes that both work and does not by itself select one

### Requirement: A conformance probe carries a negative control
A probe that claims a provider surface is correct SHALL include a control that is known to be wrong,
and MUST report both outcomes together. Without one, a pass is indistinguishable from a probe that
cannot fail. The worked example is the baseline this change rests on: `wss://api.deepgram.com/v1/speak`
with the SDK's shipped defaults returned `101 Switching Protocols`, and `/v1/speak-does-not-exist` on
the **same host** returned `404 Not Found` — so that host does exhibit the exact failure signature that
exposed the Speechmatics route, which is what makes the `101` evidence rather than absence of evidence.
A probe MUST also obey the repository's redaction rules: no provider Output is stored or printed, and
correlating identifiers are never echoed.

#### Scenario: A pass is accompanied by a demonstrated failure
- **GIVEN** a probe reporting that a provider route is correct
- **WHEN** the finding is recorded
- **THEN** it carries the negative control's result alongside it, so a reader can see the probe was capable of returning a failure on that host

#### Scenario: A probe without a negative control is not a verification
- **GIVEN** a surface probed with only the expected request
- **WHEN** its status is recorded
- **THEN** it is recorded as **uncontrolled** route evidence rather than as passing, because a request that succeeds proves nothing about a probe whose failure path was never exercised — uncontrolled is its own class, weaker than a controlled probe and stronger than not characterised, and it is the same class a live capture without a negative control carries

#### Scenario: Probing stores nothing
- **GIVEN** a probe run against a live vendor endpoint
- **WHEN** it completes
- **THEN** no synthesized audio, transcript or correlating identifier is written to disk or to the console, and the finding records only the shape of what arrived

### Requirement: A provider that produced no output does not report success
A provider client SHALL NOT complete normally when it produced nothing, and MUST make the empty
outcome observable to the caller and counted. This binds **recognizers as well as synthesizers**
(`Sdk/ADR-0049` D2). The motivating measurement is Cartesia, on the client as it then shipped: the
synthesizer connected, sent its request, read the vendor's frames, reached the vendor's `done`
terminator and completed **successfully** having yielded zero audio bytes, because every
audio-carrying frame was a text frame it discarded. ElevenLabs was measured doing the same on
2026-08-15 — text-only frames, then close `1000` — so this is a shape, not one vendor's quirk. On the
STT side Speechmatics and AssemblyAI reached the caller as streams that completed normally and empty
when the vendor had **refused the session**. All four are fixed; they are cited here as the evidence
the requirement rests on, not as a description of what ships.
A loud failure is recoverable and a silent one is not; a caller that receives an empty stream from a
successful call has no signal to act on.

#### Scenario: A completed synthesis that yielded nothing is surfaced
- **GIVEN** a synthesizer that reaches the vendor's terminator having emitted no audio bytes
- **WHEN** the call completes
- **THEN** the empty outcome is surfaced to the caller and counted, rather than presented as a normal completion of an empty stream

#### Scenario: The silent-failure shape is tested, not assumed fixed
- **GIVEN** the frame-type corrections applied to a streaming synthesizer
- **WHEN** its suite runs
- **THEN** a test asserts non-zero audio for a normal synthesis, so a future regression to the discard-everything behaviour turns the suite red instead of passing as a successful empty call

#### Scenario: A legitimately empty request stays distinguishable
- **GIVEN** a request whose input warrants no audio at all
- **WHEN** the client completes
- **THEN** that outcome is distinguishable from the failure above, so the requirement does not convert a valid empty result into a reported fault

### Requirement: A receive loop does not silently discard a frame that carries a failure
A provider receive loop SHALL surface any frame carrying a failure to the caller, and MUST NOT let
unanticipated frame types fall into a discard branch by default (`Sdk/ADR-0049` D1). Filtering
lifecycle frames the caller does not need stays legitimate — the Speechmatics `Info` frame is skipped
deliberately and correctly. What is forbidden is filtering by an allow-list of *content* types, since
every error a vendor defines then lands in the discard branch by construction. Three shipped clients
had this shape when the rule was written: Speechmatics and AssemblyAI both `continue`d past any
message that was not a transcript, and ElevenLabs read only binary frames while the vendor sends
errors as text. In each case a session the vendor **refused** reached the caller as a normal, empty
completion. All three are fixed under `Sdk/ADR-0050`; the rule outlives the instances.

#### Scenario: An in-band rejection reaches the caller
- **GIVEN** a vendor that accepts the WebSocket upgrade and then rejects the credential in a message
- **WHEN** the client receives that message
- **THEN** the failure is surfaced to the caller rather than skipped, so a refused session is distinguishable from a silent one

#### Scenario: Deliberate lifecycle filtering remains legal
- **GIVEN** a lifecycle frame the caller has no use for, such as a rate-limit or session-info notice
- **WHEN** the receive loop processes it
- **THEN** skipping it is permitted, because the discriminator is whether the frame carries a failure and not whether it appears on a content allow-list

#### Scenario: The discard branch is audited rather than assumed clean
- **GIVEN** the set of provider receive loops in the repository
- **WHEN** the audit runs
- **THEN** each surface records whether it has an allow-list discard branch, including the surfaces that do not, so an unexamined loop is never reported as a clean one

### Requirement: Where a vendor validates a credential is measured, never inferred
The recorded status of a surface SHALL state where its credential is validated — in the upgrade
handshake or in-band after it — and that MUST be established by a probe using a deliberately invalid
credential, not inferred from where the client places it (`Sdk/ADR-0049` D3, D4). A surface that
carries no invalid-credential control MUST have its validation point recorded as **not established**
— never inherited from its route probe, which exercises a different failure path.

Both answers are common enough that neither is a safe default, and the split does not follow
anything a reader could predict from the code. Measured across the WebSocket surfaces on 2026-08-15
and re-measured 2026-08-19: some vendors reject at the upgrade with `401`/`403`, and others return
`101` and *then* reject — Speechmatics STT closes `4001`, ElevenLabs and AssemblyAI each send an
error frame before closing. This is why `Sdk/ADR-0048` §5.11 requires a WebSocket probe to read past
the upgrade: a run that stops at `101` records a passing authentication on a session the vendor is
about to refuse. Credential *placement* predicts nothing either — the surfaces that send the
credential in a header split across both answers, and so do the ones that send it in the query
string. A wrong-path control demonstrates a probe can distinguish routes; only an invalid-credential
control demonstrates it can distinguish credentials, and the two answer different questions. The
current per-surface split is the record's to state, not this requirement's.

#### Scenario: An auth claim rests on a credential-shaped control
- **GIVEN** a surface whose recorded status asserts that its credential is accepted
- **WHEN** that status is established
- **THEN** it carries the result of a probe run with a deliberately invalid credential on the same host, so a `101` that merely precedes a rejection is never recorded as a passing authentication

#### Scenario: A handshake-only result is qualified by the validation point
- **GIVEN** a surface probed only to the handshake
- **WHEN** its status is recorded
- **THEN** it counts as auth evidence only where the vendor was measured to validate in the upgrade headers, and otherwise records that the frames beyond the handshake were not exercised

### Requirement: Every provider surface carries a recorded wire-conformance status
Each VoiceAi provider surface SHALL carry a recorded status covering both its route and its frame
protocol, and an uncharacterised surface MUST be recorded as uncharacterised rather than omitted or
presumed correct. Each row MUST state the date its status was established and its evidence class
drawn from an ordered vocabulary, because these differ in strength and a table that flattens them
misleads. The vocabulary is, strongest first: `live + both controls`, `live + route control`,
`live + credential control`, `live, uncontrolled`, `documentation`, `not characterised`. The two
single-control classes MUST NOT be recorded as `live + both controls`: a wrong-path control and an
invalid-credential control answer different questions, so one is not a weaker sample of the other,
and a surface whose route is not controllable (some vendors accept any path) can reach
`live + credential control` and no higher.

Route and frame halves MUST be recorded separately and MAY sit at different classes, because frame
evidence lags route evidence structurally: a probe that stops at the WebSocket upgrade has
established a route and nothing about frames. A surface whose route is verified and whose frame
inventory is not is **not** a characterised surface.

**The census belongs in the record, not here.** The per-surface table, its counts, and its
`Still not characterised` list live in `docs/guides/provider-wire-conformance.md`; this requirement
governs their shape and never restates their contents. A requirement that embeds a count of which
surfaces are correct is wrong on the first day a surface is fixed, and nothing in CI compares the
two — which is how this requirement's own body came to assert a superseded 2026-08-15 census while
the record beneath it had moved on.

#### Scenario: An unprobed surface reads as unknown, not as working
- **GIVEN** a provider surface for which no live probe has been run
- **WHEN** its row is written
- **THEN** it says not characterised and why — no credential, no cleared terms, or not yet attempted — so a reader can tell "we checked and it is correct" apart from "we have never looked"

#### Scenario: Evidence classes are not flattened
- **GIVEN** one surface probed live with a negative control and another believed correct from an earlier, weaker check
- **WHEN** both are recorded
- **THEN** each carries its own evidence class and date, rather than both appearing as a single undifferentiated pass

#### Scenario: A live capture is not promoted to a controlled probe
- **GIVEN** a surface whose only route evidence is a live capture recorded without a negative control
- **WHEN** its row is written
- **THEN** it carries the **uncontrolled** class — neither demoted to not characterised nor placed in the same column as a probe whose failure path was demonstrated on the same host

#### Scenario: A new provider cannot ship with no status
- **GIVEN** a pull request adding a provider client
- **WHEN** it lands without a row in the conformance record
- **THEN** the guard fails naming the client and the file that declares it, so the status arrives with the provider instead of as follow-up work that never happens

