# ADR-0048: Provider wire conformance is established by a live probe with a negative control, never by a green suite

- **Status:** Accepted
- **Date:** 2026-08-15
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0014 (VoiceAi providers are hand-rolled `HttpClient` / `ClientWebSocket` code, so
  this repo — not a vendor SDK — owns wire-protocol parity for every provider), ADR-0041 (recording
  substrate; its **D4** makes recordings the fixture of record and states that the recording, not the
  mock server, is what closes the fidelity gap — keeping hand-authored fixtures legal, and labelled
  `synthetic`, for shapes a capture cannot produce on demand. D4 does not itself spell out *why* a
  hand-authored fixture is weaker; naming that mechanism, and extending the principle from fixture
  bytes to routes, frame types and field semantics, is this ADR's work),
  **ADR-0046** (provider DTO robustness fences — the *parse* layer, what to do with bytes that arrive)
  and **ADR-0047** (provider schema-drift train — *change over time*, not present-day wrongness):
  both numbers are **reserved by open OpenSpec changes** — `provider-dto-robustness-fences` and
  `provider-schema-drift-train` respectively — and **neither ADR file exists yet**, so every reference
  to them below names the layer that will own a concern, not a decision already recorded.
  ADR-0043 (longevity evidence is operator-run and off the PR
  path — the same placement this ADR gives the probe), ADR-0044 (in-process test servers and their
  dialling seams use an IPv4 loopback literal, never `localhost` — those `internal` fake-server
  seams are precisely what a live probe bypasses)

## Context

Every VoiceAi provider suite in this repo is green, and has been green for every one of the defects
below, on the day each was written and every day since. The natural reading of a green provider
suite is that the provider works. That reading was wrong, and it was wrong in a way that made more
testing worthless rather than merely insufficient: adding cases, adding edge conditions, adding
fixtures, tightening assertions — none of it could have detected any of these six defects, because
every one of those additions is written against the same fake.

The mechanism is that **a provider suite is a closed loop.** The fake server and the client under
test are written by the same author, from the same reading of the same vendor documentation, usually
at the same sitting. Whatever that author believed about the vendor's route, frame type or field
semantics is asserted on *both* sides of the test. The suite therefore compares the client against
the author's belief, never against the vendor. A shared misreading is invisible by construction —
green means "the client agrees with itself". ADR-0041 D4 reached the adjacent conclusion about
*fixture bytes* — that a recording, not a mock, is what closes the fidelity gap — without stating this
mechanism as its reason; what follows names the mechanism, shows it holding for routes, frame types
and field semantics as well, and demonstrates that coverage does not substitute for it at any depth.

All six defects below were found by the same instrument — attempting to capture a real recording
under ADR-0041 — and none was found by the test suite. Each is a green suite over broken shipped
code.

### Class A — the request never reaches the vendor's endpoint

**A1. `SpeechmaticsSpeechSynthesizer` cannot reach Speechmatics.** It POSTs to `/generate` and sends
the voice as a JSON body field. The API selects the voice by **path segment**: `/generate/{voice}`
returns `200 audio/wav`, `/generate` returns `404`. Everything else the client sends is already
correct — bearer auth, content type, sample rate. One delta, not three: a competing hypothesis was
tested rather than assumed, and died. The shipped default voice `eleanor` is absent from the
vendor's published four-voice list, which looked like a second defect; probed directly it returns
`200`. The published list is incomplete and the option default is fine.

Two things follow that are not one-line patches. `SpeechmaticsOptions.BaseUri` is **public** and
defaults to the entire URL `https://preview.tts.speechmatics.com/generate`; appending a voice
segment to a caller-supplied base URL changes the meaning of a public property, which is an API-design
decision (see D7). And the `<see href="https://docs.speechmatics.com/tts-api-ref"/>` on
`src/Verbara.Sdk.VoiceAi.Tts/Speechmatics/SpeechmaticsOptions.cs` line 23 is a dead link (404).

**Not tested:** whether the `language` and `sample_rate` body fields are accepted as sent. Only the
route was isolated.

**A2. `LmntSpeechSynthesizer`'s HTTP path cannot reach LMNT.**
`src/Verbara.Sdk.VoiceAi.Tts/Lmnt/LmntSpeechSynthesizer.cs:294` hardcodes
`https://api.lmnt.com/v1/ai/speech/generate` — there is no option for it; the only override is the
`internal` test-only seam — and POSTs `FormUrlEncodedContent`. That returns `404`. A controlled
comparison with the same credential seconds apart got `200 audio/mpeg` from the documented
`/v1/ai/speech/bytes` with a JSON body. **Three deltas, not one:** path, body encoding
(form → JSON), and response media type — the client assumes raw PCM it can chunk; the service
returns MP3. Contained: `LmntTtsOptions.Transport` defaults to `WebSocket`, so only callers who opt
into HTTP are affected, and the WebSocket path is untouched by this finding.

### Class B — the response arrives, and is read from the wrong WebSocket message type

`CartesiaSpeechSynthesizer` and `ElevenLabsSpeechSynthesizer` both yield audio only from
`WebSocketMessageType.Binary` frames. Both vendors deliver audio as base64 inside JSON **text**
frames (ElevenLabs `AudioOutput.audio`, Cartesia `chunk.data`), and **neither documents a raw-binary
mode at all** — read first-hand 2026-08-14. Cartesia additionally reaches its `done` terminator, so
it completes **successfully** having produced zero audio: a silent failure, not a loud one.

### Class C — the frame is read, but fields that govern correct assembly are ignored

`src/Verbara.Sdk.VoiceAi.Stt/Speechmatics/SpeechmaticsSpeechRecognizer.cs:170` space-joins transcript
tokens unconditionally, ignoring three things the vendor sends: the `word_delimiter` on
`RecognitionStarted`, the per-result `attaches_to` marker, and the already-assembled segment the
vendor publishes at `metadata.transcript`. A segment ending in punctuation comes out as
`... mañana .` — a spurious space before the period.

### Class D — the handshake succeeds and the credential is rejected in-band

**`SpeechmaticsSpeechRecognizer` cannot authenticate.**
`src/Verbara.Sdk.VoiceAi.Stt/Speechmatics/SpeechmaticsSpeechRecognizer.cs:195` builds
`{BaseUri}/{Language}?jwt={encodedKey}`, putting the long-lived API key straight into the `jwt`
query parameter. Speechmatics accepts the WebSocket upgrade — `101` — and then **closes the socket
with close code `4001 not_authorised`.** The rejection is at the protocol layer, after the handshake
already succeeded.

Three rows, same credential, same host, seconds apart:

| Row | Auth as sent | Outcome |
|---|---|---|
| A | `?jwt=<long-lived API key>` — what the SDK ships | closed, `4001 not_authorised` |
| B | `Authorization: Bearer <long-lived API key>` header, no query parameter | **accepted**, reached `RecognitionStarted` |
| C | `?jwt=<temporary key minted for 60 s>` (`POST https://mp.speechmatics.com/v1/api_keys?type=rt`, `Authorization: Bearer <API key>`, body `{"ttl":60}` → `201` carrying `key_value`) | **accepted**, reached `RecognitionStarted` |

Row B exists only to kill the competing hypothesis under D3 — that the credential simply lacked
realtime-STT entitlement — and it killed it: the same credential opened a session through two
different channels. **The defect is the SDK's auth scheme, not the key.** That is the same role the
`eleanor` row played in A1.

Two measured remedies exist, so the fix is an API-design decision with a recorded rationale rather
than a forced single move. Speechmatics' own documentation frames temporary keys as a *browser*
concern — "to avoid exposing your long-lived API key" — which makes header auth the plausible
server-side choice; but that is documentation, and the measurement is that both work.

**Severity: uncontained.** A2 is contained behind `LmntTtsOptions.Transport` defaulting to
`WebSocket`, and A1 is a wrong-but-fixable route. This one makes the **entire Speechmatics realtime
STT provider unusable as shipped**, on the default path, for every caller. There is no containment.

**Also observed, and routed elsewhere.** Every Speechmatics realtime session opens with an `Info`
frame carrying sixteen fields — `{message, type, reason, usage, quota, growth_rate_1m,
growth_rate_1m_limit, growth_rate_avg_5m, growth_rate_avg_5m_limit, burst_rate, burst_limit,
sustained_rate, sustained_limit, rate_limiting_enabled, last_updated, region}`.
`SpeechmaticsTranscriptMessage` (`src/Verbara.Sdk.VoiceAi.Stt/Internal/VoiceAiSttJsonContext.cs:116`)
declares `{message, results}` only, so the first frame of every session is one the parser does not
model. The DTO modelling is ADR-0046's layer; this ADR owns the observation, not the fix.

**Confirmed, not corrected.** The live `RecognitionStarted` top-level field set is
`{message, orchestrator_version, id, language_pack_info}`, and the committed
`Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Recordings/speechmatics-stt/recognition-started-frame.json`
matches it exactly, including nesting `word_delimiter` inside `language_pack_info`. That fixture is
confirmed by live observation.

**Not tested:** whether the transcription config the client sends after the session opens is
accepted as sent. Only auth and the session-start frame were isolated.

### The verification baseline: what is proven good, and how

The instrument is only worth adopting if a pass from it means something. Deepgram TTS was probed
live on 2026-08-15 and is correct on both halves.

| Property | Measured | Instrument |
|---|---|---|
| Route | `wss://api.deepgram.com/v1/speak`, SDK defaults (`model=aura-2-thalia-en`, `encoding=linear16`, `sample_rate=24000`) → `101 Switching Protocols` | live handshake |
| **Negative control** | `/v1/speak-does-not-exist` on the **same host** → `404 Not Found` | live handshake |
| Frame sequence | `Metadata` text frame → 37 binary frames × 1920 bytes (71 040 bytes ≈ 1.48 s of linear16@24 kHz) → `Flushed` text frame | live capture, not stored |
| Frame size margin | largest binary frame 1920 B against a 65 536 B receive buffer — **34×** headroom | live capture |
| Field sets | `Metadata` = `{type, request_id, model_name, model_version, model_uuid, additional_model_uuids[]}`; `Flushed` = `{type, sequence_id}` | live capture |
| Determinism | two runs, identical input → 1.48 s and 1.20 s of audio | two live runs |

The negative control is what makes the `101` mean anything. Deepgram *does* exhibit the exact
failure signature that exposed Speechmatics, on the same host, so the probe demonstrably could have
detected a wrong route. The pass is evidence, not an absence of evidence.

Four further results follow from that same session, and each changes something already committed:

- The live frame shape is **not** the Class B shape. No text frame carried a long string field, so
  there is no base64 audio hidden in JSON — `DeepgramSpeechSynthesizer` reading binary is correct
  here for a reason, not by luck.
- The live **`Metadata` and `Flushed`** field sets match those **two** committed synthetic
  (documentation-derived) fixtures **exactly**. That upgrades those two from "conforms to the docs"
  to "conforms to what the service actually sends", and confirms that `model_uuid` and
  `additional_model_uuids` really are sent and really are unmodelled by the SDK — so the
  unmodelled-sibling test asserts a real condition, not a hypothetical one. **The probe observed
  exactly those two frame types.** `warning-frame.json` and `audio-linear16-16khz.raw` sit in the
  same directory and were never touched by it: the `Warning` frame and the error paths were not
  exercised, and remain documentation-derived.
- Synthesis is **non-deterministic** (1.48 s vs 1.20 s from identical input). This retroactively
  justifies generating the `.raw` fixtures with `SyntheticPcm.Triangle` rather than capturing them:
  a captured audio fixture could not have been asserted byte-for-byte.
- The 34× frame-size margin is stated as a **measured margin, not a defect.** The receive loop
  ignores `result.EndOfMessage`, and a truncated text frame would throw an uncaught `JsonException`
  in `HandleTextFrame` — but `Metadata` is 291 bytes, so the vendor would have to grow that frame
  225× to reach the buffer. Not reportable.

**Deepgram STT's route is clean on the same instrument.** `wss://api.deepgram.com/v1/listen` with
the SDK's exact defaults (`encoding=linear16`, `sample_rate=16000`, `channels=1`, `model=nova-2`,
`interim_results=true`, `punctuate=true`) and the `Authorization: Token` header returned
`101 Switching Protocols`; the negative control `/v1/listen-does-not-exist` on the same host
returned `404 Not Found`. Deepgram authenticates in the **handshake header**, so here the `101`
*does* prove the credential was accepted — which is exactly the distinction D9 turns into a rule.
**Not tested: frames.** Deepgram is `not-cleared` under `docs/guides/provider-recording-protocol.md`
section 7, so no transcript was requested and no frame sequence was captured. The route is verified;
the frame format is not.

No output was stored or printed at any point, and correlating identifiers (`request_id`,
`model_uuid`) were never echoed, per the redaction rules in
`docs/guides/provider-recording-protocol.md` section 4. Azure TTS was previously proven working.

### The scoreboard

Every row carries the **evidence class** that produced it and the **date** it was produced. Without
those two columns the table flattens a live probe with a negative control, a prior working use never
re-probed, and a documentation read into one undifferentiated "OK", which is the exact conflation
this ADR exists to forbid (D2, D4, D8).

| Provider (TTS) | Route | Frame format | Evidence class | Date |
|---|---|---|---|---|
| Deepgram | OK | OK | live probe + negative control, frames captured | 2026-08-15 |
| Azure | OK | OK | prior working use; **not re-probed** under this instrument | before 2026-08-15 |
| LMNT (HTTP) | **404** | blocked behind the route | live probe + controlled comparison against `/v1/ai/speech/bytes` | 2026-08-15 |
| Speechmatics | **404** | blocked behind the route | live probe + controlled comparison (`/generate/{voice}`, `eleanor`) | 2026-08-15 |
| Cartesia | OK | **broken** — binary read vs base64-in-JSON | vendor documentation read first-hand; **no live frame capture** | 2026-08-14 |
| ElevenLabs | OK | **broken** — binary read vs base64-in-JSON | vendor documentation read first-hand; **no live frame capture** | 2026-08-14 |

**Four of six are broken** — two by route, two by frame format. As of 2026-08-15 there are no
unknowns left among the six: every one has been characterised, which is new. Two of the six,
however, are characterised from documentation alone, and under D8 that is not the same state as
Deepgram's.

| Provider (STT) | Route | Auth | Frames | Evidence class | Date |
|---|---|---|---|---|---|
| Deepgram (WS) | OK | OK — header, proven by the `101` | **not exercised** (`not-cleared`, protocol §7) | live probe + negative control | 2026-08-15 |
| Speechmatics (WS) | OK — upgrade accepted | **broken** — `4001 not_authorised` (Class D) | `RecognitionStarted` confirmed; `Info` unmodelled; assembly wrong (Class C) | live probe + three-row controlled comparison | 2026-08-15 |
| OpenAI Whisper (HTTP) | reached — committed live capture | reached | response body captured | live capture, **no negative control** | 2026-08-09 |
| Azure OpenAI Whisper (HTTP) | reached — committed live capture | reached | response body captured | live capture, **no negative control** | 2026-08-09 |
| Google STT (HTTP) | reached — committed live capture | reached | response body captured | live capture, **no negative control** | 2026-08-15 |
| Cartesia (WS) | — | — | — | **not characterised** — no credential in this environment | — |
| AssemblyAI (WS) | — | — | — | **not characterised** — no credential in this environment | — |

The three HTTP batch rows are real route evidence — each carries a provenance sidecar with
`class: "recorded"`, i.e. bytes the service actually returned — but they were taken without a
negative control, so they are a **weaker, distinct** class than the two WebSocket probes and are not
interchangeable with them. They are not unverified. The two `not characterised` rows were not probed
and nothing is claimed about them.

## Decision

**A provider's wire behaviour is considered verified only when it has been compared against the
vendor's live endpoint, with a negative control, carried through to the vendor's first protocol
exchange, on the three properties below. A green suite driving a self-authored fake is not evidence
of conformance and must not be cited as such.** Concretely:

- **D1 — Conformance is a claim about the vendor, so only the vendor can settle it.** The verified
  surface is exactly three properties: **the route** (does the request reach the endpoint the vendor
  serves), **the frame type** (does the payload arrive on the message type the client reads), and
  **the fields that govern assembly** (does the client honour every field the vendor sends that
  changes the meaning of the result). Anything else — retries, error mapping, option validation — is
  legitimately fake-tested. These three are not.
- **D2 — A probe without a negative control is not evidence.** Every probe carries a companion
  request that is known-wrong on the same host with the same credential — a nonexistent path, a
  malformed parameter — and must fail. If the known-wrong request also passes, the probe cannot
  distinguish conformance from a service that accepts anything, and its pass is discarded. Deepgram's
  `101` counts as evidence *because* `/v1/speak-does-not-exist` returned `404` seconds later.
- **D3 — A plausible competing hypothesis is tested, not assumed.** Where two explanations fit the
  same failure, the probe kills one before the fix is scoped. The `eleanor` check existed solely to
  do that, and it did: it turned a three-delta rewrite into a one-delta route fix.
- **D4 — The governing epistemic rule, applied verbatim:** *"A vendor asserting X is evidence; a
  vendor not mentioning Y is not."* Silence never licenses a behaviour. Cartesia and ElevenLabs
  assert base64-in-JSON text frames and are silent on raw binary; the SDK reads only binary, so the
  shipped behaviour rests entirely on a silence. Under this rule that is not a supported assumption,
  and Class B is its cost.
- **D5 — Nothing captured live is stored; the *finding* is what gets committed.** Live output is not
  written to disk and not printed; correlating identifiers are never echoed
  (`docs/guides/provider-recording-protocol.md` section 4). What enters the repo is the derived
  fixture plus its provenance sidecar, and — where the vendor's output is non-deterministic — a
  synthetic payload generated to a documented shape rather than a capture that could never be
  asserted byte-for-byte.
- **D6 — The probe is an operator-run instrument, off the PR path.** It is not a CI job, not a
  required check, and nothing in the merge path may depend on a third party being reachable. This is
  the same placement ADR-0043 gives longevity evidence, for the same reasons plus two more:
  credentials and vendor spend.
- **D7 — A route fix that changes the meaning of a public property is an API decision, not a patch.**
  `SpeechmaticsOptions.BaseUri` is public and its default is a whole URL; making the client append
  `/{voice}` redefines what a caller-supplied value means. Such a fix is designed and recorded
  explicitly — never applied inline because the route is obviously wrong.
- **D8 — Unprobed is UNVERIFIED, and that is a third state.** A provider that has not been probed is
  neither "working" nor "broken"; it is unverified, and it may not be described as conforming. Any
  new provider, and any change to an existing provider's route or frame handling, carries a live
  probe with a negative control before it can be called verified. Every status record therefore
  states its **evidence class and date**, not just a verdict: a live probe with a negative control, a
  live capture without one, a documentation read, a prior working use never re-probed, and *not
  characterised* are five different states and are never collapsed into "OK". Cartesia STT and
  AssemblyAI STT are **not characterised** — no credential exists in this environment, so they were
  not probed and nothing is claimed about them.
- **D9 — A probe must reach the vendor's first protocol exchange; the handshake is not the
  boundary.** Where a vendor authenticates in the HTTP upgrade headers, a `101` proves the credential
  was accepted and a handshake-only probe is sufficient — that is Deepgram, STT and TTS alike. Where
  a vendor authenticates **in-band**, the `101` proves nothing: Speechmatics completes the upgrade
  with a rejected credential and closes with `4001 not_authorised` afterwards. A probe therefore runs
  until the vendor's first protocol message — a session-start frame, an error frame, or a close code
  — and stopping at the upgrade is not evidence for an in-band-auth vendor. The cost of the weaker
  rule is measured, not hypothetical: **had this programme stopped at the handshake, Speechmatics
  STT would have been recorded as verified good while being entirely unusable.** Which regime a
  vendor is in is itself a probe finding and is recorded with the result, because it cannot be read
  off the transport.

## Consequences

- Positive: six TTS providers moved from *assumed working* to *characterised*, four of them with a
  named mechanism and a measured failure signature. That is not a coverage improvement; it is the
  difference between a claim and a measurement.
- Positive: the two WebSocket STT surfaces this environment holds credentials for are now
  characterised too — Deepgram's route verified with a negative control, Speechmatics' auth broken
  with a three-row comparison — where before they were unverified under D8.
- Positive: the mis-diagnosis is on the record. "The suite is green, so the provider works" is now a
  documented wrong answer for this class of defect, and the reason it is wrong — the fake and the
  client encode one belief — is stated where the next person will look. That is worth more than the
  six fixes.
- Positive: already-committed artefacts got stronger without being touched. The Deepgram `Metadata`
  and `Flushed` fixtures are now known to match the live field sets exactly; the Speechmatics
  `RecognitionStarted` fixture is confirmed against the live frame, `word_delimiter` nesting
  included; the unmodelled-sibling test is confirmed to assert a real condition; and the
  `SyntheticPcm.Triangle` `.raw` fixtures are retroactively justified by measured non-determinism
  rather than by convenience. The Deepgram `Warning` fixture is **not** among them — the probe never
  saw that frame.
- Positive: D2 makes a passing probe falsifiable. Without it, "the provider responded" and "the
  provider responds to anything" are the same observation.
- Negative: probes cost credentials and vendor spend and cannot run in CI (D6), so conformance decays
  silently between probes. ADR-0047's drift train is the partner instrument — but drift detection
  needs a verified present-day baseline to drift *from*, which is precisely what this ADR supplies
  and what ADR-0047 cannot produce on its own.
- Negative: probe findings are perishable and not reproducible by a contributor without credentials.
  The repo holds the derived fixture and the sidecar, never the transcript (D5), so a reader must
  take the measurement on the record rather than re-run it. This is the price of D5 and is accepted
  deliberately.
- Negative: D7 converts what looks like a one-line route fix into a public-API deliberation, and the
  Speechmatics fix cannot ship until that deliberation concludes. A wrong route stays wrong slightly
  longer in exchange for not breaking every caller who set `BaseUri`.
- Negative: Class D is the first defect here with **no containment**. LMNT's is fenced by a default
  and Speechmatics TTS's is one synthesizer; a Speechmatics realtime STT caller has no configuration
  that makes the shipped client work. The severity ranking is a measured property of the defect, not
  a triage preference. It is also **silent**, and that is a second finding rather than a restatement
  of the first: the recognizer's receive loop keeps only `AddPartialTranscript` and `AddTranscript`
  and `continue`s past every other message. Skipping `Info` that way is deliberate and correct;
  skipping `Error` is not, and Speechmatics signals in-band rejection as an `Error` message. So a
  session the vendor closes as `not_authorised` reaches the caller as an `IAsyncEnumerable` that
  completes normally and empty — no exception, no log, nothing to alert on. The auth defect and the
  swallow compound: either alone would be visible, and together they produce a client that fails
  invisibly. This is also why no test caught it, and why the zero-output rule this ADR adopts for
  synthesizers is written to cover recognizers too.
- Neutral: this ADR says nothing about the parse layer. What to do with bytes that *do* arrive —
  nullability on the read path, `[JsonRequired]`, unmapped-member tolerance — is ADR-0046, and none
  of these six defects is a parse defect: the bytes never arrive, arrive on a frame type the client
  does not read, or arrive after the credential has been refused. The unmodelled sixteen-field
  `Info` frame is likewise ADR-0046's; this ADR records only that it is the first frame of every
  Speechmatics session and that the SDK does not model it.
- Neutral: it says nothing about change over time either. All six defects are static and were wrong
  on the day the code was written; detecting a vendor *changing* its contract is ADR-0047.
- Neutral: LMNT's exposure is contained by its own default. `LmntTtsOptions.Transport` defaults to
  `WebSocket`, and the WebSocket path is untouched by A2, so only callers who explicitly opt into
  HTTP are affected.
- Neutral: Speechmatics' `language` and `sample_rate` body fields remain untested. Only the route was
  isolated; they are unverified under D8, not assumed correct.

## Alternatives considered

- **Option B: trust the vendor's documentation and skip the live probe** — rejected, and rejected by
  the evidence rather than on principle. Documentation would not have caught Class B at all: Cartesia
  and ElevenLabs document base64-in-JSON text frames and no raw-binary mode whatsoever, yet both SDK
  clients read only binary — the defect is a client implementing a mode the vendor never described,
  which no amount of careful reading of that vendor's docs surfaces as a contradiction. Documentation
  is also not reliably *present*: Speechmatics' own reference link, cited in shipped XML docs at
  `src/Verbara.Sdk.VoiceAi.Tts/Speechmatics/SpeechmaticsOptions.cs:23`, returns 404, and its
  published voice list omits a voice that returns `200`. A source that is incomplete, partly
  unreachable, and silent exactly where the SDK is wrong cannot be the verification instrument.
- **Option C: fold these fixes into the existing sibling changes rather than open a new one** —
  rejected on layer. `wiremock-http-provider-substrate` (ADR-0041) is a **test substrate**; its
  contract governs where test bytes come from and it explicitly cannot carry a production route fix,
  and a route fix is production behaviour. `provider-dto-robustness-fences` (ADR-0046) is the
  **parse** layer, and none of these is a parse defect. `provider-schema-drift-train` (ADR-0047) is
  about **change over time**, and these were wrong on day one — there is no drift to detect.
  `websocket-fake-protocol-contract` excludes the other eight surfaces in its own Not-in-scope
  section and forbids production code changes outright. Routing this work into any of them would
  have required breaking that change's stated contract, which is a worse outcome than a fourth
  change.
- **Option D: strengthen the provider suites with contract tests against the existing fakes** —
  rejected, because that is the instrument that produced the defects. A contract test written against
  a fake asserts the author's reading of the vendor twice instead of once; it tightens the closed
  loop rather than opening it. It would raise confidence without raising conformance, which is
  strictly the worst available outcome — it makes the wrong answer harder to question.
- **Option E: run the live conformance probes in CI as a gate** — rejected. They require vendor
  credentials in the merge path, incur per-run vendor spend, and would make every merge depend on
  third-party availability; a vendor outage would become a red build with no defect behind it,
  reversing ADR-0038 and ADR-0043. The probe stays an operator-run instrument whose **findings** are
  committed as fixtures and sidecars, so CI verifies against evidence produced by the probe instead
  of re-running it.
- **Option F: adopt vendor SDKs so the vendor owns wire parity** — rejected, and already settled.
  ADR-0014 rejected vendor SDKs because every candidate carries reflection-based serialization that
  ADR-0001 forbids, and nothing here changes that calculus. This ADR is the acknowledged cost of
  ADR-0014: owning the wire means owning its verification, and this is the instrument that discharges
  that obligation.
- **Option G: stop the probe at the handshake** — rejected, and rejected by a counterexample rather
  than on principle. It is the cheapest possible probe: one upgrade request, one negative control,
  no credentials spent on a session, no vendor output to redact under D5, and for Deepgram it is
  genuinely sufficient because the credential is checked in the upgrade headers. It fails on
  Speechmatics realtime STT, which returns `101` to a request whose credential it is about to
  refuse, then closes with `4001 not_authorised`. Under this option Speechmatics STT scores a clean
  route and enters the scoreboard as verified good, while being unusable by every caller on the
  default path — a worse outcome than not probing it at all, because the record would then assert
  something false rather than nothing. D9 is this rejection stated as a rule.
- **Option H: treat the credential as the suspect and mint a temporary key for every session** —
  rejected as a *diagnosis*, retained as one of two candidate *fixes*. As a diagnosis it was killed
  by row B: the same long-lived key that fails in `?jwt=` succeeds in an `Authorization: Bearer`
  header, so entitlement was never the problem and a mint-then-connect flow would have "fixed" the
  symptom while leaving the wrong auth scheme in place and adding a second network round trip plus a
  key-lifecycle concern to every session. As a fix it remains measured-good (row C) and is weighed
  against header auth on API-design grounds under D7, with the vendor's own framing of temporary
  keys as a browser concern recorded as documentation, not as measurement.

## Addendum (2026-08-18) — what the probes settled after the decision was recorded

**Status:** Accepted · **Extends D2, D7 and D8. Supersedes no rule; corrects one illustration and
records three outcomes the decision demanded but could not yet contain.**

ADR-0048 was written on 2026-08-15, part-way through the programme it governs. The rules held. Three
things the rules *required someone to record later* are recorded here, and one example in D8 has been
overtaken by measurement.

### A1 — D8's example is stale; D8 itself is not

D8 closes by naming Cartesia STT and AssemblyAI STT as **not characterised**, "no credential exists
in this environment". Both were probed after that sentence was written — Cartesia STT on 2026-08-16,
AssemblyAI STT on 2026-08-17 — and both now carry rows with their own evidence class and date. The
rule D8 states is unchanged and the example is simply out of date; the authoritative, dated list of
what remains uncharacterised is the conformance record, never this file. This is the first
demonstration of D8's own consequence: an ADR that names surfaces goes stale at the speed of the
probe, which is why the record and not the ADR is the ledger.

### A2 — D2 has a boundary: a host that admits no failing route control

D2 requires a companion request that is known-wrong on the same host with the same credential, and
discards the probe's pass if that request also passes. AssemblyAI STT is the case where the discard
is not a temporary embarrassment but a standing property of the host.

**Measured 2026-08-16:** `wss://streaming.assemblyai.com/v3/ws-does-not-exist` — a path the vendor
does not document and does not serve — completed the upgrade with `101` and then served a **normal
session**. The route control cannot fail, so it controls nothing. The `404` recorded for this vendor
earlier in the programme was taken against a **different host** and never controlled this one; citing
it here would have been the D3 inference in its purest form.

What follows, stated as a rule rather than as a note about one vendor:

- **A control that cannot fail is not a control.** The probe's *route* claim is discarded. Its other
  claims are untouched — the credential control on this host does discriminate, so authentication and
  frame behaviour stand on their own evidence.
- **The surface is not demoted to unprobed.** It drops one rung on the evidence ladder, from
  `live + both controls` to `live + credential control`, and its Route column reads *not
  controllable*. That is a fourth thing a Route cell can say, alongside OK, **fixed** and blank, and
  it means "we looked, and this host will not let us check".
- **It is a property of the host on the day it was measured, not a verdict about the vendor.** If the
  service later serves a `404` for unknown paths, the control becomes available and the surface is
  re-probed. Recording it as permanent would be the same mistake in the other direction.

### A3 — D7's outcome for `SpeechmaticsOptions.BaseUri`, recorded as D7 requires

D7 says a route fix that changes the meaning of a public property is designed and recorded
explicitly. The design, and the alternatives rejected **by name**:

- **Chosen — redefine `BaseUri` as an origin** (scheme and host, no path) and have the synthesizer
  append `/generate/{Voice}`. The route becomes something the client owns and declares once; the
  property becomes something a caller can state independently of any route.
- **Rejected — append the voice segment to whatever the caller supplies.** This is the *cheaper*
  option and it is worth being clear about what was given up: it would have been source- and
  behaviour-compatible for every caller who never set the property, because the old default already
  ended in `/generate`. It was rejected because it defines `BaseUri` as "the endpoint minus its last
  segment" — a value nobody can write down without knowing the client's route — and it hands a route
  fragment to configuration. A vendor route change would then break every caller who pinned the
  property, silently, at a layer none of them own.
- **Rejected — introduce a new option and obsolete `BaseUri`.** It carries both meanings forward at
  once, so the wrong one keeps working; and when the two disagree the failure mode is the same `404`
  this fix exists to remove.
- **Consequence for a caller who already sets it,** stated in the property's own XML docs rather than
  left to a release note: a value that still carries `/generate` now produces
  `/generate/generate/{Voice}`. Callers must supply the origin alone.

This reasoning is **design reasoning, not measurement**. What was measured is narrower and is stated
where it belongs: the shipped route returned `404`, and the corrected one returns `200`.

### A4 — D7's outcome for Speechmatics STT authentication

Header authentication was chosen: `Authorization: Bearer <ApiKey>` on the upgrade request, with
`ApiKey`'s meaning unchanged, one connection, and nothing to refresh. Mint-then-connect is recorded
under *Alternatives considered*, Option H, and remains **measured-good** — it is a rejected
alternative, not a broken one. The split between evidence and inference is the point and is repeated
here so it cannot be lost: that **both** schemes work is measured (rows B and C); that header auth is
the right server-side choice rests on API-design grounds plus the vendor's framing of temporary keys
as a browser concern, and that framing is **documentation, not measurement** — exactly the class D4
warns against treating as evidence.

### A5 — Two of these rules are now enforced by a guard rather than by memory

D1's "declare the route once" and D8's "every surface states an evidence class" were prose. Both are
now checked by governance guards that parse `src/` with Roslyn and fail the build:

- a **production endpoint written inline at a call site** fails, naming file and 1-based line. Two
  sites are exempt by an inline `// endpoint-allow: <CATEGORY> — <reason>` marker with a closed
  category set, and the exemption tally is an exact-count ratchet, so a third cannot appear without a
  reviewer seeing the number change.
- a **provider client type with no row in the conformance record** fails, naming the type and the file
  that declares it. The guard checks presence, never verdict: `not characterised` is a legal, passing
  status, because what must be impossible is shipping a provider whose conformance is simply unstated.
  It runs in reverse too — a row naming a type that no longer exists in `src/` also fails.

Both guards live in the governance test project, which carries **zero** `ProjectReference`s by design:
a guard that compiles against the thing it governs can be broken by the same edit it is meant to catch.

### A6 — D2's boundary again, one level down; and an instrument must be checked against its own noise floor

A2 recorded a host whose **route** control cannot fail. Speechmatics TTS supplies the same shape one
level down, on a path **parameter**: `/generate/{voice}` answers `200 audio/wav` for every segment
tried, nonsense included, so the voice arm of a comparison controls nothing. Its route control is
sound (`/generatex/{voice}`, `/generate` and `/generate/` all `404`), which is exactly why this is
worth writing down separately — **a surface can carry a passing negative control on the route and no
control at all on a parameter within it.** D2 should be read as applying per varied dimension, not
per surface. Where the vendor exposes an authoritative listing, as this one does at a
credential-gated `GET /voices`, that listing is the control the route refuses to be.

This also retires a claim D-series probing had accepted: the shipped default voice `eleanor` was
recorded as validated because it *returned 200*. Under a route that returns 200 for anything, that
observation had no discriminating power at the moment it was made. The conclusion drawn from it —
"the vendor's published list is incomplete, the option default is fine" — was therefore never
supported, and is withdrawn. What replaces it is not the opposite conclusion but a measurement: the
default appears in no list available to us, and its output ranges coincide with an unrecognised
segment's.

The methodological rule the same probe forced, which D9 did not state:

> **Establish that an instrument discriminates before reading anything from it.** Run the
> null comparison — the same input twice — and measure the within-condition spread first.

Two instruments failed this on one route. Byte identity: the same request twice returned identical
lengths and different hashes, so a hash comparison would have called every arm "different". Byte
length: usable, but only after measuring that lengths move in exact 1536-B quanta with a
within-voice spread up to 4 608 B — a first pass comparing one sample per arm produced an incoherent
verdict from pure noise and looked like a real result. The finding that survived was stated in the
form the spread permits: with six samples per arm the two voices' ranges are disjoint, both
directions of the conflict agree, and **no sample ever landed in the opposite voice's range**.
