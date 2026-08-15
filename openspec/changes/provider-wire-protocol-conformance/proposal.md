---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Every consumer whose synthesized audio never arrives — because the SDK POSTs to an endpoint the vendor does not serve — or arrives empty because the SDK waits for a binary frame the vendor never sends; every consumer of Speechmatics realtime STT, whose session the vendor closes as `not_authorised` before a single word is transcribed; and every reviewer who has to explain why a fully green suite shipped three TTS providers that cannot produce a byte of audio on their default configuration — a fourth, LMNT, only on the HTTP transport a caller has to opt into — and one STT provider that cannot open a session
decision_ref: Sdk/ADR-0048
---

# Proposal: provider-wire-protocol-conformance

## Why

Four of the six shipped TTS providers cannot deliver audio — three of them unconditionally, and LMNT
only for a caller who sets `Transport` to HTTP, since the option defaults to WebSocket and that route
is recorded **not verified** rather than broken — and the realtime STT provider whose
credential we hold cannot authenticate at all. That is not an inference from coverage gaps; every row
below was established against the vendor's live endpoint or against the vendor's own current
documentation, and the rows that pass were established the same way, so that "correct" and "broken"
are the same kind of claim. The `Evidence class` column is not decoration: a live probe carrying a
negative control, a live capture taken without one, a prior proof never re-run, and a first-hand
reading of the vendor's documentation are four different instruments with four different failure
modes, and collapsing them into a single green tick is the exact mistake this change exists to stop.

| Surface | Transport | Route | Frame / body format | Verdict | Evidence class | Date |
|---|---|---|---|---|---|---|
| Deepgram TTS | WebSocket | **OK** — `101 Switching Protocols` | **OK** — `Metadata` text → 37 binary frames → `Flushed` text | correct on both halves | live probe **with** negative control; frames captured | 2026-08-15 |
| Deepgram STT | WebSocket | **OK** — `101 Switching Protocols` | **not exercised** | route correct; frames uncharacterised | live probe **with** negative control; route only | 2026-08-15 |
| Azure TTS | HTTP | **OK** | **OK** | previously proven | prior proof, never re-probed | before this change |
| LMNT TTS (HTTP) | HTTP | **404** | blocked behind the route | broken | live probe | 2026-08-15 |
| Speechmatics TTS | HTTP | **404** | blocked behind the route | broken | live probe | 2026-08-15 |
| Cartesia TTS | WebSocket | **OK** | **BROKEN** — audio is base64 in a JSON **text** frame | broken, **silently** | vendor documentation read first-hand; no live frame capture | 2026-08-14 |
| ElevenLabs TTS | WebSocket | **OK** | **BROKEN** — audio is base64 in a JSON **text** frame | broken | vendor documentation read first-hand; no live frame capture | 2026-08-14 |
| Speechmatics STT | WebSocket | `101`, then vendor **closes `4001 not_authorised`** | never reached | **unusable as shipped** | live probe, three-row controlled comparison | 2026-08-15 |
| OpenAI Whisper STT · Azure OpenAI Whisper STT · Google STT | HTTP | a committed recording exists, taken live, **without a negative control** | — | route evidence of a weaker class | committed live capture (sidecar `class: recorded`) | per sidecar |
| Cartesia STT · AssemblyAI STT | WebSocket | — | — | **not characterised** | none — no credential exists in this environment | — |

All six TTS surfaces are characterised; no unknowns remain in that family. On the STT side the two
WebSocket recognizers we can authenticate against are now characterised too — one clean route, one
provider that cannot open a session. The last two WebSocket recognizers are recorded as *not
characterised* with the reason stated, which is a different and honest thing from recorded as good.

### The six defects, with their sites

| # | Class | Surface | Site | Deltas | Symptom |
|---|---|---|---|---|---|
| A1 | request never lands | Speechmatics TTS | `src/Verbara.Sdk.VoiceAi.Tts/Speechmatics/SpeechmaticsSpeechSynthesizer.cs` | 1 | POSTs `/generate` with the voice as a body field; the API selects the voice by **path segment**. `/generate/{voice}` → `200 audio/wav`; `/generate` → `404` |
| A2 | request never lands | LMNT TTS, HTTP path | `src/Verbara.Sdk.VoiceAi.Tts/Lmnt/LmntSpeechSynthesizer.cs:294` | **3** | hardcodes `…/v1/ai/speech/generate` + `FormUrlEncodedContent` → `404`. Documented `…/v1/ai/speech/bytes` + JSON body → `200 audio/mpeg` |
| **A3** | request never lands | **Speechmatics STT** | `src/Verbara.Sdk.VoiceAi.Stt/Speechmatics/SpeechmaticsSpeechRecognizer.cs:195` (`BuildUri`) | 1, with **two** measured remedies | puts the long-lived API key in `?jwt=`. The vendor accepts the upgrade (`101`) and then **closes the socket with `4001 not_authorised`**. No session ever starts |
| B1 | wrong frame type | Cartesia TTS | `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs:147` | 1 | yields only `WebSocketMessageType.Binary`; vendor sends audio as base64 in `chunk.data` on a text frame |
| B2 | wrong frame type | ElevenLabs TTS | `src/Verbara.Sdk.VoiceAi.Tts/ElevenLabs/ElevenLabsSpeechSynthesizer.cs:135` | 1 | yields only `Binary`; vendor sends audio as base64 in `AudioOutput.audio` on a text frame |
| C1 | fields ignored | Speechmatics STT | `src/Verbara.Sdk.VoiceAi.Stt/Speechmatics/SpeechmaticsSpeechRecognizer.cs:170` | 3 ignored inputs | space-joins tokens unconditionally, ignoring `word_delimiter` on `RecognitionStarted`, the per-result `attaches_to` marker, and the vendor's already-assembled `metadata.transcript`. The committed fixture's sentence emerges as `"El equipo revisó el informe esta mañana ."` |

Four properties of this table matter more than its length.

**A3 has no containment, which is what makes it the most severe defect in the programme.** A2 sits
behind a non-default option; B1 and B2 break one direction of one TTS provider; C1 mangles text that
still arrives. A3 makes the **entire** Speechmatics realtime STT provider unusable as shipped: there
is no option, no fallback path and no configuration that reaches a working session. The rejection is
at the protocol layer, *after* a successful handshake, which is why nothing upstream of the socket
notices.

It was established by a three-row controlled comparison — same credential, same host, seconds apart.

| Row | What was sent | Result |
|---|---|---|
| A | `?jwt=<long-lived API key>` — **what the SDK ships** | upgrade `101`, then closed **`4001 not_authorised`** |
| B | `Authorization: Bearer <the same API key>` header, no query parameter | **accepted** — reached `RecognitionStarted` |
| C | `?jwt=<60 s temporary key>`, minted via `POST /v1/api_keys?type=rt` with `{"ttl":60}` → `201` + `key_value` | **accepted** — reached `RecognitionStarted` |

Row B is the load-bearing row, and it exists for one reason: to kill the competing hypothesis that
the credential simply lacked realtime-STT entitlement. The same credential opened a session through
two different channels, so the entitlement explanation is dead and the defect is the SDK's **auth
scheme**, not the key. This is the same role the `eleanor` probe played for Speechmatics TTS below —
a probe whose only job is to make a wrong answer impossible.

Two remedies are *measured*, not one, so the fix is an API-design choice with a recorded rationale
rather than a forced move. The vendor's own documentation frames temporary keys as a **browser**
concern — avoiding exposure of a long-lived key in client-side code — which makes header auth the
plausible server-side choice; but that is documentation, and what was measured is that both rows
work. The decision between them is made in this change and recorded in `ADR-0048`.

**B1 fails silently, not loudly.** Cartesia's receive loop *does* read text frames — but only to match
`type` against `done`/`error` and break (`CartesiaSpeechSynthesizer.cs:155`). So the synthesizer
connects, streams, receives every audio chunk the vendor sends, reaches the terminator, and completes
**successfully** having written zero bytes to its channel. There is no exception, no timeout, and no
non-zero exit anywhere for a caller to observe.

**A2 is contained, and that containment is measured, not hoped for.** `LmntTtsOptions.Transport`
defaults to `LmntTransport.WebSocket`; only a caller who explicitly opts into `LmntTransport.Http`
(the documented workaround for blocked outbound WebSockets) reaches the broken path. The LMNT
WebSocket path is untouched by this finding. A2 also has no escape hatch: the URL is a string literal
in the method body, with the only override being a test-only fake-server seam, so no consumer can
configure their way past it.

**A1 is one delta, because a competing hypothesis was tested rather than assumed.** The shipped
default voice `eleanor` does not appear in the vendor's published four-voice list, which reads like a
second defect. It was probed: `eleanor` returns `200`. The published list is incomplete and the option
default is fine. Bearer auth, content type and sample rate are all already correct on this client.
One delta, not three — and the `eleanor` probe existed solely to kill a wrong answer, which it did.

**NOT tested (A1):** whether Speechmatics **TTS** accepts the `language` and `sample_rate` **body**
fields as the client sends them. Only the route was isolated. That question stays open and is scoped
as a verification task, not asserted here.

### Two by-products of reaching a real Speechmatics session

Rows B and C did not merely prove auth; they put a live session in front of the parser, which
produced one finding for another change and one confirmation for this one.

**The first frame of every session is one the parser does not model.** Every Speechmatics realtime
session opens with an `Info` frame carrying **sixteen** fields — `{message, type, reason, usage,
quota, growth_rate_1m, growth_rate_1m_limit, growth_rate_avg_5m, growth_rate_avg_5m_limit,
burst_rate, burst_limit, sustained_rate, sustained_limit, rate_limiting_enabled, last_updated,
region}`. `SpeechmaticsTranscriptMessage` declares `{message, results}` and nothing else. This change
**records** the observation and stops there: modelling the DTO is read-path work and belongs to
`provider-dto-robustness-fences`, which owns exactly that question. Noting it here is what stops it
from being rediscovered by the next person to open a session.

**The committed `RecognitionStarted` fixture is confirmed by live observation.** The live top-level
field set is `{message, orchestrator_version, id, language_pack_info}` with `language_pack_info` an
object, and
`Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Recordings/speechmatics-stt/recognition-started-frame.json`
matches it exactly, including nesting `word_delimiter` **inside** `language_pack_info` rather than at
the top level. The fixture was right. It moves from documentation-derived to confirmed, and C1's fix
can rely on where that field actually sits.

### What makes "Deepgram is correct" a claim and not an absence of evidence

Deepgram TTS was probed live on 2026-08-15 with the SDK's exact shipped defaults.

| Measurement | Result |
|---|---|
| Route, `model=aura-2-thalia-en`, `encoding=linear16`, `sample_rate=24000` | `101 Switching Protocols` |
| **Negative control** — `/v1/speak-does-not-exist`, same host, same credential | `404 Not Found` |
| Frame sequence | `Metadata` text → **37** binary frames × **1920 B** (71 040 B = 1.48 s linear16 @ 24 kHz) → `Flushed` text |
| Largest binary frame vs. receive buffer | 1 920 B vs. 65 536 B — **34×** headroom |
| Live `Metadata` field set vs. its committed synthetic fixture | identical — `{type, request_id, model_name, model_version, model_uuid, additional_model_uuids[]}` |
| Live `Flushed` field set vs. its committed synthetic fixture | identical — `{type, sequence_id}` |
| Two runs, identical input | 1.48 s and 1.20 s of audio |
| **Committed fixtures the probe never touched** | `warning-frame.json` and `audio-linear16-16khz.raw` — the probe observed exactly **two** frame types, `Metadata` and `Flushed`. The `Warning` frame and every error path went unexercised |

The negative control is the load-bearing row. Deepgram's host **does** return `404` for a wrong path —
the exact signature that exposed Speechmatics and LMNT — so the `101` on the real route is evidence
that the probe *could* have failed and did not. Without that row, a green route check is
indistinguishable from a check that cannot fail.

Four consequences follow, and they are worth more than the pass itself:

- **Deepgram is definitively not Class B.** No text frame carried a long string field, so there is no
  base64 audio hidden in JSON on this surface. That is a positive finding about frame shape, not an
  assumption from silence.
- **Two of the synthetic fixtures are upgraded — two are not.** The committed Deepgram fixtures were
  documentation-derived; the live `Metadata` and `Flushed` field sets match **those two** committed
  fixtures exactly, which moves those two from "conforms to the docs" to "conforms to what the
  service actually sends". The `Warning` frame and the error paths were **not** exercised, so
  `warning-frame.json` and `audio-linear16-16khz.raw` remain documentation-derived and synthetic. The
  upgrade also confirms that `model_uuid` and `additional_model_uuids` really are transmitted and
  really are unmodelled by the SDK — so the unmodelled-sibling test asserts a real condition rather
  than a hypothetical one.
- **Synthesis is non-deterministic.** Identical input produced 1.48 s and 1.20 s of audio across two
  runs. This retroactively justifies generating `.raw` fixtures with `SyntheticPcm.Triangle` instead
  of capturing them: a captured audio fixture could not have been asserted byte-for-byte.
- **A measured margin, explicitly not filed as a defect.** The receive loop ignores
  `result.EndOfMessage`, so a truncated **text** frame would throw an uncaught `JsonException` in
  `HandleTextFrame`. The live `Metadata` frame is 291 bytes against a 65 536-byte buffer — the vendor
  would have to grow that frame 225× to reach the boundary. Recorded here as a margin, not a bug.

**Deepgram STT was probed on the same day, and its route is clean.**
`wss://api.deepgram.com/v1/listen` with the SDK's exact shipped defaults — `encoding=linear16`,
`sample_rate=16000`, `channels=1`, `model=nova-2`, `interim_results=true`, `punctuate=true` — and the
`Authorization: Token` header returned `101 Switching Protocols`; the negative control
`/v1/listen-does-not-exist` on the same host returned `404 Not Found`. **Frames were not exercised**,
so this is a route claim only, and Deepgram remains `not-cleared` under the recording protocol's § 7
terms review regardless of what the route check observed.

No output was stored or printed at any point during any probe, and correlating identifiers
(`request_id`, `model_uuid`) were never echoed, per the redaction rules in
`docs/guides/provider-recording-protocol.md` § 4.

### The finding that decides what a probe is allowed to stop at

Deepgram STT and Speechmatics STT were probed the same way and returned the same `101`. One of them
works and one of them is unusable. That is not a coincidence, and it generalises into the rule this
change is really about.

| Where the vendor authenticates | What a `101` proves | Sufficient probe |
|---|---|---|
| In the HTTP **upgrade headers** — Deepgram, `Authorization: Token` | the credential was accepted; a bad one never reaches `101` | handshake alone is **sufficient** |
| **In-band**, after the socket opens — Speechmatics, `?jwt=` / first protocol exchange | **nothing.** Rejection arrives afterwards as close code `4001` | handshake alone is **insufficient** |

Therefore a conformance probe **must reach the vendor's first protocol exchange**, not stop at the
upgrade. The consequence is worth stating plainly, because it is the strongest argument this change
has: had this programme stopped at the handshake — the obvious, cheap, natural place to stop —
Speechmatics STT would have been recorded as **verified good** while being entirely unusable. The
scoreboard would have gained a green row that was worse than an empty one, because an empty row
invites a probe and a green row forecloses it. This is a rule about the *method*, and it belongs in
`ADR-0048` alongside the negative-control requirement, for the same reason: both exist to make a
green result mean something.

### The root cause, and why this is one change and not six patches

No test in this repository has ever compared the SDK's wire behaviour against a real vendor endpoint.
Every provider suite drives a fake server written by the same author who wrote the client, so a
shared misreading of a vendor's contract passes green on both sides of the seam. `ADR-0041` made
exactly this argument about **fixtures**. These six defects show the argument extends to **routes**,
**auth schemes**, **frame types** and **field semantics** — and that no amount of coverage
substitutes, because the coverage and the defect share an author. A3 is the sharpest case: the
Speechmatics fake server accepts `?jwt=` because the person who wrote it read the same page as the
person who wrote the client, so the suite is green on a credential scheme the vendor rejects.

The six were found by one instrument, applied once: attempting to reach the vendor for real. None was
found by the test suite. Each is a green suite over broken shipped code. They are one change because
they have one cause and one remedy — a conformance probe that compares the client's actual bytes
against the vendor's actual endpoint, through the vendor's first protocol exchange — and splitting
them into six patches would ship the fixes while leaving the instrument that found them unbuilt,
which guarantees the seventh defect.

### Why the three open sibling changes cannot host this work

This routing was decided deliberately and checked against each sibling's own stated contract.

| Sibling | Contract | Why these defects do not fit |
|---|---|---|
| `wiremock-http-provider-substrate` (`Sdk/ADR-0041`, merged at 47/50 tasks) | establishes **where test bytes come from** — WireMock.NET for HTTP, in-process fakes for WebSocket, recorded or documentation-derived fixtures with provenance sidecars | it is a **test substrate**. A route fix is production behaviour, which that change explicitly cannot carry |
| `provider-dto-robustness-fences` (`Sdk/ADR-0046`) | **parsing what arrives** — read-path nullability, `[JsonRequired]`, unmapped-member tolerance | these are not parse defects. In Class A the bytes never arrive at all; in Class B they arrive on a frame type the reader discards before any deserializer runs. The one genuine parse finding this change surfaced — the unmodelled 16-field `Info` frame — is **handed to** this sibling, not fixed here |
| `provider-schema-drift-train` (`Sdk/ADR-0047`) | detecting vendor contract **changes over time** | these are static and present-day. Each was wrong on the day the code was written; there is no drift to detect |
| `websocket-fake-protocol-contract` | its Not-in-scope section excludes the other eight surfaces and forbids production code changes | doubly disqualified |

`0045`/`0046`/`0047` are claimed by the open changes above, so `0048` was the next free number; the
ADR now exists on disk as
`docs/decisions/0048-wire-conformance-by-live-probe-with-negative-control.md`.

### Why now

The instrument that found all six is already built and already used — it is the capture attempt that
`wiremock-http-provider-substrate` requires. Every additional day it stays a manual, undocumented
one-off is a day the next provider addition ships without a route check. And the fixes cannot wait on
the drift train: a drift detector compares today's contract against a recorded baseline, so pointing
one at Speechmatics today would faithfully report "no change" about an endpoint that has never once
worked.

## What Changes

Ordered by blast radius, largest first.

- **Speechmatics STT: authenticate by a scheme the vendor accepts.** `BuildUri` stops putting the
  long-lived API key in `?jwt=`. Two remedies are measured and both work, so this change picks one and
  records why: (a) send `Authorization: Bearer <API key>` as a request header on the WebSocket
  upgrade and drop the query parameter, or (b) mint a short-TTL temporary key over HTTPS at connect
  time and pass *that* in `?jwt=`. Header auth is the plausible server-side answer — the vendor
  documents temporary keys as a browser concern, and (b) adds an HTTP round trip, a second failure
  mode and a key lifetime shorter than a long call — but the choice, its rejected sibling and the
  reasoning are decided **in this change** and recorded in `ADR-0048`, not asserted in advance here.
  The fake server is corrected first, so it stops accepting a credential form the vendor rejects.
- **Speechmatics TTS: voice in the path — and a public-API decision, not a one-line patch.**
  `SpeechmaticsOptions.BaseUri` is **public**, `[Required]`, regex-validated, and defaults to the
  complete URL `https://preview.tts.speechmatics.com/generate`. Appending `/{voice}` to a
  caller-supplied base URL silently changes what that property *means* — from "the endpoint" to "the
  endpoint prefix" — and would break any consumer who already set it to a full URL. The change treats
  this as the API-design decision it is: the option's semantics, its validation, its XML doc and its
  `PublicAPI.*.txt` entry are decided together and recorded in `ADR-0048`, with the rejected
  alternatives named (append blindly; add a second `Voice`-bearing option and deprecate `BaseUri`;
  parse-and-rewrite the supplied URL). The dead `<see href="https://docs.speechmatics.com/tts-api-ref"/>`
  on `src/Verbara.Sdk.VoiceAi.Tts/Speechmatics/SpeechmaticsOptions.cs:23` — a 404 — is replaced in the
  same edit.
- **Cartesia and ElevenLabs: decode audio from JSON text frames.** Both loops learn to base64-decode
  `chunk.data` / `AudioOutput.audio` from text frames and emit the bytes downstream. Neither vendor
  documents a raw-binary mode at all (read first-hand 2026-08-14), and the epistemic rule applies in
  both directions: *a vendor asserting X is evidence; a vendor not mentioning Y is not*. What becomes
  of the existing `Binary` branch — retained as a tolerated-but-undocumented path with a comment
  saying so, or removed as dead — follows from that rule but is **decided in this change**, on the
  surface, and recorded there rather than settled here. Cartesia's silent-success path gains an
  observable decision for "terminator reached with zero audio produced", so this failure can never
  again be indistinguishable from a short utterance.
- **LMNT HTTP: three deltas, applied together.** Path `…/v1/ai/speech/generate` → `…/v1/ai/speech/bytes`;
  body `FormUrlEncodedContent` → JSON; and the response media type, which is the delta most easily
  missed — the client currently assumes raw PCM it can chunk on frame boundaries, while LMNT returns
  `audio/mpeg`. Whether the endpoint stops being a literal in the method body and becomes reachable
  from `LmntTtsOptions` — as it already is for the three TTS providers that expose a `BaseUri`
  option, namely Cartesia, Deepgram TTS and Speechmatics, and as it is **not** for ElevenLabs — is
  **decided in this change** and recorded on the task, because it adds public API surface. The two
  XML docs that ship the dead `…/v1/ai/speech/generate` route to consumers
  (`src/Verbara.Sdk.VoiceAi.Tts/Lmnt/LmntTtsOptions.cs:20` and the `LmntSpeechSynthesizer`
  class-level doc) are corrected in the same pass. The WebSocket path is not touched.
- **Speechmatics STT: assemble transcripts the way the vendor says to.** Honour `word_delimiter` from
  `RecognitionStarted` — which live observation confirms is nested **inside** `language_pack_info`,
  exactly where the committed fixture already puts it — honour the per-result `attaches_to` marker,
  and prefer the vendor's
  already-assembled `metadata.transcript` where it is present, falling back to token joining only
  when it is not. The unconditional `sb.Append(' ')` at
  `src/Verbara.Sdk.VoiceAi.Stt/Speechmatics/SpeechmaticsSpeechRecognizer.cs:170` goes.
- **A conformance probe, which is the part that outlives the six fixes.** A credential-gated,
  opt-in, trait-filtered suite that performs a **controlled comparison** against the live endpoint:
  same credential, same host, seconds apart, and — mandatory, not optional — a **negative control**
  that is known-wrong, so a pass is distinguishable from a probe that cannot fail. A probe without a
  negative control is a defect in the probe. For any vendor that authenticates in-band, the probe
  must also reach the **first protocol exchange**; stopping at the upgrade is the same defect wearing
  a different hat, and A3 is what it costs. Nothing is stored: no bodies, no audio, no correlating
  identifiers, per `docs/guides/provider-recording-protocol.md` § 4. It stays off the PR path,
  following the precedent `ADR-0043` set for longevity evidence, because it requires vendor
  credentials and bills the operator per invocation.
- **A Governance guard that a vendor endpoint cannot be a string literal in a method body.** A2
  existed because it could: no option, no constant, nothing for a reviewer to compare against a
  contract. The guard requires every provider route to be reachable from an options type, so the next
  wrong route is at least *visible* at review. It ships with an **enumerated allow-list**, each entry
  carrying its own one-line reason, in the manner `SyncFenceRegressionGuardTests.cs` already uses —
  not `LoopbackSeamGuardTests.cs`, whose scanner documents that it deliberately carries no ignore
  list: four pre-existing inlined endpoints that no task in this change remediates —
  `ElevenLabsSpeechSynthesizer.cs:161`, `LmntSpeechSynthesizer.cs:265` (the untouched WebSocket path),
  `AzureTtsSpeechSynthesizer.cs:84` (region-interpolated, and `AzureTtsOptions` carries no endpoint
  property) and `DeepgramSpeechRecognizer.cs:138` — plus the test-only fake-server seams. A guard that
  fails on day one against code nobody is fixing is a guard that gets deleted.
- **`Sdk/ADR-0048`** records the durable decisions: the scoreboard with its evidence classes, the
  negative-control requirement, the rule that a handshake alone is sufficient evidence only for a
  vendor that authenticates in the upgrade headers, the `BaseUri` semantics call, the Speechmatics
  STT auth-scheme choice with its measured alternative named, and the rule that a provider surface is
  not considered shipped-correct until its route, its auth and its frame shape have each been checked
  against the vendor once.

**Not in scope.** The STT family is now partly characterised, and the boundary is drawn by what a
credential existed for, not by convenience. **In scope:** Speechmatics STT, because A3 makes the
provider unusable and a defect of that severity is not deferrable to a follow-on; and C1 on the same
recognizer. **Probed and recorded:** Deepgram STT, route only. **Not characterised, and recorded as
such:** Cartesia STT and AssemblyAI STT — reason, no credential exists in this environment; not
probed, and nothing claimed about them. **Weaker evidence, distinctly labelled:** the three HTTP batch
recognizers — OpenAI Whisper, Azure OpenAI Whisper, Google STT — each carry a committed recording
whose provenance sidecar says `class: recorded`, i.e. a live capture. That is real route evidence and
must not be called unverified; it was taken **without a negative control**, so it is also not
equivalent to a controlled probe, and it is not upgraded here. Parse robustness stays with
`provider-dto-robustness-fences` — including the unmodelled `Info` frame this change only observes —
and drift detection stays with `provider-schema-drift-train`; where a fix here needs a fence (the
Class B decoders will parse new JSON shapes), the fence is placed by that change and this one only
names the seam. No captured vendor bytes are checked in: the recording protocol's § 7 verdicts mark
LMNT HTTP, Deepgram and ElevenLabs `not-cleared`, so their fixtures stay documentation-derived
regardless of what a probe observes. The `EndOfMessage` margin is documented, not fixed. No new
package dependency.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `provider-contract-fidelity`: **ADDED requirements only** — nothing existing is modified or removed.
  The capability was introduced by `wiremock-http-provider-substrate` to answer *where the test bytes
  come from*, and `provider-dto-robustness-fences` extends it to *what the parser must survive when the
  bytes change*. Neither reaches the question these six defects answer: **whether the client is talking
  to the right endpoint, with a credential the vendor accepts, on the right frame type, with the right
  field semantics — before any byte is parsed and independently of where the test bytes came from.** A
  perfect recording substrate replaying a perfect fixture through a perfectly fenced parser still
  yields zero audio on four of six TTS providers today and cannot open a Speechmatics STT session at
  all, because all three of those instruments sit *downstream* of the request. Fidelity to a vendor's
  contract is one capability with three layers, and this change supplies the outermost one.

## Impact

- `src/Verbara.Sdk.VoiceAi.Tts`: `Speechmatics/` (route construction, `BaseUri` semantics, XML doc
  link), `Lmnt/` (path, body encoding, response media type, the endpoint-into-options decision, and
  the two XML docs that ship the dead route), `Cartesia/` and `ElevenLabs/` (text-frame audio decoding
  plus the zero-audio decision).
- `src/Verbara.Sdk.VoiceAi.Stt`: `Speechmatics/` — the recognizer's **auth scheme** (`BuildUri`, and
  whichever of the two measured remedies is chosen: an upgrade header, or a minted short-TTL key that
  also adds an HTTPS call at connect time) and its transcript assembly. These are two independent
  edits to one file and land as two steps, auth first, because until auth is fixed the assembly fix
  cannot be exercised against anything real.
- `Tests/Verbara.Sdk.VoiceAi.Tts.Tests`, `Tests/Verbara.Sdk.VoiceAi.Stt.Tests`: the per-provider fakes
  must be corrected **before** the clients, or a corrected client fails against a fake that still
  encodes the misreading. Each fake correction is justified against the vendor's published contract in
  the test file itself, not against the new client code.
- `Tests/Verbara.Sdk.Governance.Tests`: the endpoint-literal scanner, its guard test, detector unit
  tests and a liveness self-test.
- A trait-gated conformance-probe suite, excluded by the default unit filter exactly as
  `Category=Functional` and `Category=Integration` already are, and absent from the PR path.
- `docs/decisions/`: ADR-0048 + its index row in `docs/decisions/README.md`.
  `docs/guides/provider-recording-protocol.md`: the controlled-comparison method and the
  negative-control requirement, the first-protocol-exchange rule, and the § 7 status of each surface
  as characterised or explicitly not characterised, alongside the existing § 4 redaction rules it
  already obeys. `CHANGELOG.md`: one `[Unreleased]` entry — user-visible, because three TTS providers
  change from producing nothing to producing audio on their default configuration, a fourth (LMNT) does
  so on its opt-in HTTP transport, and one STT provider changes from unusable to usable.
- **Public API surface — the only part of this programme that reaches it.** One change is certain:
  `SpeechmaticsOptions.BaseUri` (`PublicAPI.Unshipped.txt`, the `.get`/`.set` pair) changes meaning.
  Whether the analyzer entry sits in `Unshipped` or `Shipped` is bookkeeping and does not govern the
  real exposure: a consumer who already binds `BaseUri` from `appsettings.json` is affected at runtime
  regardless, because configuration binding is outside the analyzer's baseline entirely. Two further
  additions are *conditional on decisions made in this change* — an `LmntTtsOptions` HTTP-endpoint
  property, and whatever the chosen Speechmatics STT auth remedy needs (header auth needs nothing;
  minting a temporary key would need a mint-endpoint and TTL to be configurable). Each is a
  `PublicAPI.Unshipped.txt` addition recorded with the decision that creates it. Every other type
  touched here is `internal` or unchanged in signature.
- AOT: no reflection introduced. The base64 decode is `Convert.FromBase64String` over spans, and every
  new DTO shape reached by the Class B decoders is registered in the existing source-generated
  `VoiceAiTtsJsonContext`. The probe suite is a test project and never ships in a package.

## Architectural Risk

**Level:** MEDIUM-HIGH — production behaviour changes on five shipped provider surfaces, one of them
a credential path, and this is the only change in the programme that moves public API semantics.

**Affected:** the outbound request path of two TTS providers, the inbound frame path of two more, one
STT recognizer's **authentication** and its transcript assembler, and one public option. The failure
mode of a wrong fix is asymmetric and that asymmetry drives the sequencing: the Class A and C surfaces
produce nothing or visibly wrong text today, so a wrong fix is loud and quickly attributable. Class B
is the dangerous one — Cartesia currently *succeeds* while producing zero audio, so a decoder that
emits subtly wrong bytes (wrong base64 field, a control frame decoded as audio, an MP3 payload chunked
as PCM) replaces a silent failure with a differently silent failure that sounds like noise instead of
nothing. A3 carries its own distinct risk: it touches a credential path, so the fix must not move a
long-lived key into anywhere it can be logged, and if the minted-key remedy is chosen it introduces a
key that can **expire mid-session** on a long call — a failure mode the `?jwt=` code has never had.
That asymmetry between the two measured remedies is itself an argument, and it is the argument the
in-change decision has to answer. The `BaseUri` change remains the only one that can break a consumer
who is doing nothing wrong.

**Mitigation:** each provider is a separate, independently revertible step, so a regression is
attributable to one surface. The fake-server correction lands before the client correction on every
surface, and each correction cites the vendor's contract rather than the new client code. Every fix
is negative-tested — revert it, watch the test fail; restore it, watch it pass — so no rule is
accepted on a green run alone; the negative control in the probe is the same discipline applied to
the live endpoint. Class B decoders assert on **byte content**, not merely on non-empty output, since
"produced some bytes" is precisely the assertion that would have passed while Cartesia produced
none. The `BaseUri` decision is made in `ADR-0048` with its alternatives named, before the code
changes, and shipped with a `CHANGELOG` entry that states the semantic change in the consumer's terms.
The A3 auth decision is made the same way and has an advantage the others lack: **both** candidate
remedies were observed to work against the live service, so the choice is between two measured
options rather than between one option and a hope, and the rejected one is recorded as measured-good
so a future reader can switch without re-running the probe.

**The residual risk this change does not close:** the conformance probe is credential-gated, manual
and off the PR path, so it proves a route was correct **on the day someone ran it** — the same
photograph limitation `ADR-0046` identifies in recordings, applied to routes. Between runs, a vendor
moving an endpoint is invisible here; closing that gap is `provider-schema-drift-train`'s job, on a
different instrument and a different cadence. Two further holes are named rather than assumed
covered. First, the corrected fakes are still authored by the same hand as the corrected clients, so
the shared-misreading failure mode is *reduced by one real observation per surface*, not eliminated —
what a green suite proves after this change is that the client matches what the vendor did once, not
what the vendor does. Second, **two** of the four TTS surfaces this change fixes — LMNT HTTP and
ElevenLabs — are `not-cleared` under the recording protocol's § 7 terms review, so for those two no
captured evidence of the fix may be checked in and the durable artifact is the probe transcript's
verdict, not bytes a future reader can re-run; Speechmatics TTS and Cartesia are
`permitted-with-conditions`, which is a different and weaker constraint. Third, the STT family is
only **partly** characterised: nobody has probed the other STT surfaces with a negative control.
Three of them — OpenAI Whisper, Azure OpenAI Whisper, Google STT — carry a live capture that was
never treated as a route check, and two more, Cartesia STT and AssemblyAI STT, have no credential
here and are recorded as not characterised. The base rate argues for finishing the job: six defects
were found in the eight surfaces anyone actually looked at, and the newest of them was found only
because a probe was pushed one exchange past the handshake.
