# provider-contract-fidelity — Delta

## ADDED Requirements

### Requirement: A provider request reaches the endpoint the vendor actually serves
Every VoiceAi provider client SHALL address the route the vendor serves, and that route MUST be
established against the live endpoint rather than against a fake the SDK also authors. A fake answers
whatever its author believed; where author and client are the same person, a wrong route is green on
both sides. Measured on this basis: the Speechmatics TTS synthesizer POSTs to `/generate` with the
voice as a JSON body field, but the vendor selects the voice by path segment — `/generate/{voice}`
returns `200 audio/wav` and `/generate` returns `404`; the LMNT HTTP path POSTs form-encoded to
`/v1/ai/speech/generate`, which returns `404`, while the documented `/v1/ai/speech/bytes` with a JSON
body returned `200 audio/mpeg` on the same credential seconds apart. Where the corrected route changes
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
on, and MUST NOT assume a frame type the vendor does not document. Measured 2026-08-14: the Cartesia
and ElevenLabs TTS synthesizers yield audio only from `WebSocketMessageType.Binary` frames, while both
vendors deliver audio as base64 inside JSON **text** frames — ElevenLabs on `AudioOutput.audio`,
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
MUST NOT impose its own joining rule over them. Measured: the Speechmatics recognizer space-joins every
token unconditionally, ignoring three things the same session already carries — the `word_delimiter`
the vendor sends on `RecognitionStarted`, the per-result `attaches_to` marker that says a token binds
to its predecessor, and the assembled segment the vendor publishes at `metadata.transcript`. A segment
ending in punctuation therefore emerges with a spurious space before the period. Where the vendor
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
handshake. Measured 2026-08-15 on Speechmatics realtime STT: the recognizer places the long-lived API
key in the `?jwt=` query parameter, the vendor completes the WebSocket upgrade with `101`, and then
closes the socket with close code `4001 not_authorised` — the rejection is at the protocol layer,
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
(`Sdk/ADR-0049` D2). The motivating measurement is Cartesia: the synthesizer connects, sends its
request, reads the vendor's frames, reaches the vendor's `done` terminator and completes
**successfully** having yielded zero audio bytes, because every audio-carrying frame was a text frame
it discarded. ElevenLabs was measured doing the same on 2026-08-15 — text-only frames, then close
`1000` — so this is a shape, not one vendor's quirk. On the STT side Speechmatics and AssemblyAI reach
the caller as streams that complete normally and empty when the vendor has **refused the session**.
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
have this shape: Speechmatics and AssemblyAI both `continue` past any message that is not a transcript,
and ElevenLabs reads only binary frames while the vendor sends errors as text. In each case a session
the vendor **refused** reaches the caller as a normal, empty completion.

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
credential, not inferred from where the client places it (`Sdk/ADR-0049` D3, D4). Measured
2026-08-15, three of the five credential-controlled WebSocket surfaces validate in-band: Speechmatics
closes `4001`, ElevenLabs and AssemblyAI each return `101` and then an error frame, while both
Cartesia surfaces answer `401` at the handshake. Deepgram carries no invalid-credential control and
its validation point MUST therefore be recorded as **not established**, not inherited from its route
probe. Credential placement predicts nothing either: five send it in a request header and
Speechmatics sends it in the query string, yet Speechmatics is one of the in-band three. A wrong-path
control demonstrates a probe can distinguish routes; only an invalid-credential control demonstrates
it can distinguish credentials, and the two answer different questions.

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
presumed correct. Each row MUST state its evidence class — probed live with a negative control,
probed without one, documentation-derived, or not characterised — and the date it was established,
because these differ in strength and a table that flattens them misleads. Across the six TTS surfaces:
two correct on both halves, two wrong by route, two wrong by frame format — and **one frame half still
uncharacterised**, because Cartesia TTS's 2026-08-15 probe established route and auth with both
controls but sent a malformed synthesis request, so the vendor answered with an error frame rather
than audio. Cartesia's frame finding continues to rest on the vendor-documentation read of
2026-08-14 and MUST be recorded at that class. These rows were established on differing dates and by
differing methods, so each carries its own date and class and the set MUST NOT be presented under a
single measurement date. Across the seven STT recognizers the record
is deliberately uneven and MUST stay uneven: **all four WebSocket recognizers are now characterised**
— Deepgram route-verified with a negative control; Speechmatics found unable to authenticate at all;
Cartesia and AssemblyAI probed 2026-08-15 with two controls once credentials were created, the latter
yielding the swallow defect. Of the three HTTP batch recognizers, Google was promoted the same day to
a controlled probe, while OpenAI Whisper and Azure OpenAI Whisper still carry only a live capture taken
without a negative control, which is **uncontrolled** route evidence: neither not characterised, nor
equivalent to a controlled probe. Frame halves lag route halves and MUST be recorded separately —
Deepgram STT, Cartesia STT and Cartesia **TTS** all have verified routes whose frame inventories are
not characterised.

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
