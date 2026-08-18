# Provider wire conformance record

What is actually known about each AI-provider surface the SDK talks to, how it came to be known, and
when. One row per surface. **A surface with no row is the failure mode this file exists to prevent.**

This is a ledger, not a status badge. It is updated by running
[`scripts/probe-provider-conformance.py`](../../scripts/probe-provider-conformance.py) against the
live endpoint and writing down what came back — including the rows that say *not characterised*,
which are the useful ones.

Governing decisions: [ADR-0048](../decisions/0048-wire-conformance-by-live-probe-with-negative-control.md)
(a live probe with a negative control is what a conformance claim rests on) and
[ADR-0049](../decisions/0049-in-band-failure-must-reach-the-caller.md) (a failure the vendor states
must reach the caller). Capture rules — redaction, storage, per-provider terms — live in
[`provider-recording-protocol.md`](provider-recording-protocol.md); this file inherits them and
stores nothing.

## How to read a row

**Evidence class** — what the row rests on, in descending strength:

| Class | Means |
|---|---|
| `live + both controls` | Probed against the real endpoint with a wrong-path control **and** an invalid-credential control on the same host, in the same run |
| `live + route control` | Probed live; the wrong path was controlled, the credential was not. Says nothing about whether the probe can tell a good key from a bad one |
| `live, uncontrolled` | Probed live with no deliberately-wrong arm. Real evidence of a weaker class — a run with nothing to fail against cannot prove it *could* have failed |
| `documentation` | Read from the vendor's published contract. Every defect in this record was invisible to this class, and several were *created* by trusting it |
| `not characterised` | Nobody looked. Distinct from "looked and found nothing" |

The two controls answer two different questions, and one is not a weaker version of the other. A
wrong-path control proves the probe distinguishes **routes**. Only an invalid-credential control
proves it distinguishes **credentials**. A run carrying one of them is silent about whichever
question it did not ask.

**Validation point** — where the vendor decides a credential is bad. Always **measured**, never
inferred from where the credential sits in the request (ADR-0049 D3 forbids that inference because it
was made here and had to be withdrawn):

- `handshake` — a bad key is refused at the HTTP upgrade. `101 Switching Protocols` therefore proves
  the key was accepted.
- `in-band` — the upgrade succeeds regardless and the rejection arrives afterwards as a protocol
  message. `101` proves **nothing**. Speechmatics STT returns `101` to a rejected key and closes
  `4001` after it; a probe that stopped at the handshake would have recorded that provider as
  verified-good while it was entirely unusable.

The split is a WebSocket property. An HTTP surface answers in its response, so the response **is** the
first exchange and there is no depth to get wrong.

**Route** and **frames** are separate columns because they fail separately, and a verified route has
repeatedly been misread as a verified frame protocol. Four of the six TTS providers were broken —
two by route, two by frame handling — and none of the four was detectable from the other column.

## Text-to-speech

| Surface | Client type | Transport | Route | Frames | Validation point | Evidence | Date |
|---|---|---|---|---|---|---|---|
| Deepgram TTS | `DeepgramSpeechSynthesizer` | `wss://api.deepgram.com/v1/speak` | OK | OK | `handshake` | `live + both controls` | 2026-08-15 |
| Azure TTS | `AzureTtsSpeechSynthesizer` | `https://{region}.tts.speech.microsoft.com` | OK | OK | not measured | `live, uncontrolled` | 2026-08-03 |
| LMNT (HTTP) | `LmntSpeechSynthesizer` | `https://api.lmnt.com/v1/ai/speech/bytes` | **fixed** | n/a | in the response | `live + both controls` | 2026-08-15 |
| LMNT (WebSocket) | `LmntSpeechSynthesizer` | `wss://api.lmnt.com/v1/ai/speech/stream` | OK | **2 fixed, 1 open** | `in-band` | `live + credential control` | 2026-08-15 |
| Speechmatics TTS | `SpeechmaticsSpeechSynthesizer` | `https://preview.tts.speechmatics.com/generate/{voice}` | **fixed** | n/a | in the response | `live + both controls` | 2026-08-16 |
| Cartesia TTS | `CartesiaSpeechSynthesizer` | `wss://api.cartesia.ai/tts/websocket` | OK | **3 fixed, 1 open** | `handshake` | `live + both controls` | 2026-08-16 |
| ElevenLabs TTS | `ElevenLabsSpeechSynthesizer` | `wss://api.elevenlabs.io/v1/text-to-speech/{voiceId}/stream-input` | OK | **2 fixed, 1 open** | `in-band` | `live + both controls` | 2026-08-16 |

**Deepgram TTS** — the reference run, and the only TTS surface measured *not* to hide audio in a text
frame. Shipped defaults (`model=aura-2-thalia-en`, `encoding=linear16`, `sample_rate=24000`) returned
a `Metadata` text frame, then **37 binary frames of 1920 bytes** (71 040 B, 1.48 s), then `Flushed`
— exactly what the client expects. Controls: `/v1/speak-does-not-exist` on the same host → `404`; a
deliberately malformed key → `401` at the upgrade, on both `/v1/speak` and `/v1/listen`. That second
control is what moved this surface's validation point from *inferred* to *measured*; the inference
happened to be right, which is not the same as having been justified.

**Azure TTS** — proven working by a live capture, with no deliberately-wrong arm beside it. Kept in a
weaker class rather than promoted into the Deepgram column.

**LMNT (HTTP)** — shipped `POST /v1/ai/speech/generate`, which returns `404` — byte-identically to a
path that does not exist, because that is what it is. `/v1/ai/speech/bytes` returns `200 audio/mpeg`
with the same credential seconds apart; an invalid credential returns
`403 {"error":"Invalid API key"}`. The vendor documents a JSON body, so
the body encoding was originally recorded as a second delta; a controlled A/B showed a form-encoded
body to `/bytes` returns a **byte-identical** payload. The encoding was never a delta — that was
evidence about the documentation, not about the endpoint — and the form encoding is deliberately
kept, because swapping it would be an unmeasured change riding along with a measured fix.

**LMNT (WebSocket)** — three total-failure defects on one transport, none of them predicted:

1. The init message serialized `"model": null` whenever the option was unset, which is the default.
   The endpoint rejects an explicit null and closes `1002` having sent zero audio. *Fixed.*
2. The client half-closed the socket immediately after `eof`. Measured with that call as the only
   variable: **0 bytes** with it, **30 688 B (0.959 s)** without it. `eof` is already the
   end-of-input signal; the half-close was a second, contradictory one that the vendor reads as
   "abandon the request". *Fixed.*
3. The receive loop compares an error *message* against the literal string `"error"`, which no real
   message equals, so both live failures fall through, the close is swallowed, and the caller gets an
   empty stream and no exception. ***Fixed*** — under ADR-0050, the D1 remedy this pointed at (see
   *The silent-failure class, closed* below). It was left open here because it is a behaviour change and
   did not belong to a route fix.

**Speechmatics TTS** — the voice is selected by **path segment**, not by a body field: `/generate`
returns `404` (identically to a route that does not exist, because that is what it is),
`/generate/{voice}` returns `200 audio/wav` — 33 836 B of valid `RIFF`/`WAVE`. Invalid credential
`401` on the same host. Fixed. The `language` and `sample_rate` body fields were later observed
accepted as sent, and on **2026-08-18** the path-vs-body conflict was measured directly: the **path
wins and the body `voice` field is ignored**, so the field the route fix dropped was one the vendor
was never reading. Separately, and on a different axis from the route: **the voice segment has no
control that can fail.** Every segment tried returns `200 audio/wav`, including nonsense ones. The
*route* control does fail correctly — `/generatex/{voice}`, `/generate` and `/generate/` are all
`404` — so this surface's route evidence class stands; what is uncontrolled is a path *parameter*,
which is why the class column is unchanged and this paragraph exists.

**Cartesia TTS** — three defects, and the documented one was the least of them. The shipped request
omitted `context_id`, so the endpoint answered an error and sent no audio; the client half-closed
after the request, which alone cost everything (a control differing only in that step received 7
chunks, 32 694 B, in 1.022 s); and only then does the frame-type defect appear — audio arrives base64
in the `data` field of `type="chunk"` **text** frames, while the loop read only binary. All three are
*fixed*, in one commit, because fixing only the frame type would still have shipped a provider that
produces silence. A fourth was *open* and is now **fixed** under ADR-0050: an `error` frame ended the
stream with no exception (ADR-0049 D1). What this surface's row does **not** claim: the fixed client has not itself been run live. What
ran was a probe reproducing the corrected request — the same wire behaviour the client now produces,
but a reconstruction of it, not the artifact.

**ElevenLabs TTS** — measured across five arms with one variable each:

| Arm | Change | Result |
|---|---|---|
| A | shipped sequence, unmodified | **0 bytes**, 0 frames, close `1006` |
| B | half-close removed | 86 193 B (2.694 s) across 4 text frames, close `1000` |
| C | B, minus the serialized nulls | **byte-identical to B** |
| D | invalid credential | in-band error frame, then close `1008` |
| E | wrong path | `HTTP 403` at the handshake |

Two defects, both total, and they fire in sequence: the half-close, then the frame type. **0 binary
bytes** ever arrive, so the client's "only yield binary frames" comment is not a partial defect — the
branch it prefers receives nothing at all. Both are *fixed*, in one commit, for the same reason
Cartesia's are. A third was *open* and is now **fixed** under ADR-0050: the invalid-credential frame from arm D has no
`audio` member and was silently dropped, so a rejected key reached the caller as an empty stream and no
exception (ADR-0049 D1). As with Cartesia, the fixed client has not itself been run live — arm B is a
reconstruction of what it now sends, not the artifact. Arm C is worth its cost for what it
**refuted**: the
shipped frames carry `"flush": null` and `"voice_settings": null`, the exact shape that was a total
outage on LMNT, and ElevenLabs tolerates them. The class does not generalise, which is why the arm
was run instead of assumed. Note also that E answers `403` where every other surface answers `404` —
a wrong-path control has to be read, not pattern-matched.

## Speech-to-text

| Surface | Client type | Transport | Route | Frames | Validation point | Evidence | Date |
|---|---|---|---|---|---|---|---|
| Deepgram STT | `DeepgramSpeechRecognizer` | `wss://api.deepgram.com/v1/listen` | OK | OK | `handshake` | `live + both controls` | 2026-08-16 |
| Speechmatics STT | `SpeechmaticsSpeechRecognizer` | `wss://eu2.rt.speechmatics.com/v2` | OK | **2 fixed, 4 open** | `in-band` | `live + both controls` | 2026-08-16 |
| Cartesia STT | `CartesiaSpeechRecognizer` | `wss://api.cartesia.ai/stt/websocket` | **fixed** | **2 fixed** | `handshake` (credential) + `in-band` (session) | `live + both controls` | 2026-08-16 |
| AssemblyAI STT | `AssemblyAiSpeechRecognizer` | `wss://streaming.assemblyai.com/v3/ws` | not controllable | **2 fixed** | `in-band` | `live + credential control` | 2026-08-17 |
| Google STT | `GoogleSpeechRecognizer` | `https://speech.googleapis.com` | OK | n/a (batch) | in the response | `live + both controls` | 2026-08-15 |
| OpenAI Whisper | `WhisperSpeechRecognizer` | `https://api.openai.com/v1/audio/transcriptions` | OK | n/a (batch) | not measured | `live, uncontrolled` | 2026-08-09 |
| Azure OpenAI Whisper | `AzureWhisperSpeechRecognizer` | Azure OpenAI deployment endpoint | OK | n/a (batch) | not measured | `live, uncontrolled` | 2026-08-09 |

**Deepgram STT** — shipped defaults (`encoding=linear16`, `sample_rate=16000`, `channels=1`,
`model=nova-2`, `interim_results=true`, `punctuate=true`) with the `Authorization: Token` header
returned `101`; wrong path `404`; malformed key `401` at the upgrade. Frames went unexercised until the
half-close runs of 2026-08-16, which streamed audio through this surface in four arms and recovered the
full transcript in three of them — that, and not the route evidence, is what the Frames column rests on.
The uncharacterised part is now the field set of each message type, not whether frames work at all.

**Speechmatics STT** — the route resolved, the upgrade completed, and the credential was then rejected
in-band with close code `4001 not_authorised`: the session never authenticated, and the provider was
unusable as shipped. This is the surface that produced the depth rule. **The credential channel is
fixed** — the long-lived API key now travels as `Authorization: Bearer` on the upgrade and no longer
as `?jwt=`. Three arms, same credential, same host, seconds apart:

| Arm | Credential channel | Outcome |
|---|---|---|
| A | `?jwt=<long-lived API key>` — what the SDK shipped | upgrade `101`, then closed `4001 not_authorised` |
| B | `Authorization: Bearer <same key>`, no query parameter | accepted, reached `RecognitionStarted` |
| C | `?jwt=<temporary key>` minted at the vendor's management endpoint | accepted, reached `RecognitionStarted` |

B is the remedy shipped; C was measured and **not** taken — it adds a request before every session, a
key lifetime to manage, and an HTTP dependency to a type that has none. C's real value is what it
**refutes**: the same credential opened a session through two channels, so the failure was never a key
missing a realtime entitlement. The vendor frames temporary keys as a browser concern, which makes
header auth the plausible server-side choice — but that sentence is *documentation*; what is *measured*
is only that both channels work.

When this was first written the fixed client had not itself been run live — arm B was a reconstruction
of what it now sends, not the artifact — and it said so, as the two TTS surfaces fixed the same week
still do. **That caveat is now retired**: the half-close re-probe below drove the *shipped*
`SpeechmaticsSpeechRecognizer` to a full transcript on 2026-08-16, which it could not have reached
without the `Authorization: Bearer` channel authenticating. The credential fix is verified through the
artifact, and leaving the caveat standing would have under-claimed a run that happened — the same
discipline that forbids upgrading a class without a run forbids withholding one after it. The row's
date is the date of a measurement either way, never the date a fix landed.

Observed live: the `Info` and `RecognitionStarted` frames. The session-opening `Info` frame carries
**sixteen** fields (`message`, `type`, `reason`, `usage`, `quota`, the four growth-rate members,
`burst_rate`, `burst_limit`, `sustained_rate`, `sustained_limit`, `rate_limiting_enabled`,
`last_updated`, `region`) against a DTO that declares `{message, results}`. That is **not** a parse
failure — the receive loop skips every non-transcript message by design — so it is recorded as a field
inventory for whoever models it, and the modelling itself belongs to `provider-dto-robustness-fences`.
`RecognitionStarted`'s live top-level set was exactly `{message, orchestrator_version, id,
language_pack_info}` with `word_delimiter` nested **inside** `language_pack_info`, which is precisely
what the committed fixture already held: the fixture was right, and its sidecar is upgraded from
documentation-derived to confirmed for those names and that nesting — and for nothing else.

On 2026-08-15 no transcript frame was observed — those sessions were opened to establish the remedy and
streamed no audio. The half-close runs of 2026-08-16 streamed audio and did observe one, which is why
this row now carries the later date; the credential arms above remain 08-15 measurements and are not
redated by it. The half-close — which cost the caller every final transcript — **is fixed**, and the
run that measured the fix through the shipped client is the same one that retired the credential fix's
caveat above (see the remediation section below). The swallowed `Error` frame (ADR-0049 D1, so a
rejected session reached the caller as an empty stream) is **fixed** under ADR-0050. **Three** defects
stay open on this surface, all in assembly rather than in failure reporting: the three signals the client
ignores — `word_delimiter`, `attaches_to`, and the vendor's already-assembled `metadata.transcript`.

**Cartesia STT — the session could not open at all, and the row that said otherwise was corrected
here; both defects are now fixed and the fix is measured through the shipped client.**
Wrong path `404` and invalid credential `401` still hold: credential validation is at the handshake.
But the previous row read "route and auth OK, frames not exercised", and the route half of that was an
artifact of stopping at `101`. Streamed against, the shipped request is **rejected in-band**: the
client connects to `wss://api.cartesia.ai/stt/websocket` with **no query string at all** and the vendor
closes `1008 Missing sample_rate`. Twelve runs on 2026-08-16, twelve rejections, no exceptions. Adding
`?model=…&language=…&encoding=pcm_s16le&sample_rate=16000` — the parameters the vendor's own rejection
names — opens a working session with the same key on the same host, which is the control that isolates
the defect to the missing query rather than to the account.

A second defect appeared inside that working session: the client's opening JSON frame is not a message
this vendor has. It answers

> `Invalid client message: Unrecognized text message "{…}". Expected one of: "finalize", "done", "close".`

So `CartesiaSttInitMessage` — `model`, `language`, `encoding`, `sample_rate` — is dead on the wire even
when the socket survives; those values belong in the query string, and the type exists to be ignored.
This is the same shape as Speechmatics TTS §4.5 (a configuration sent in a channel the vendor does not
read) and it is exactly what a `101`-deep probe cannot see.

**Both are fixed, and unlike the two diagnoses above the fix was measured through the shipped client
rather than through a probe.** The four parameters moved into the query string and the init frame was
**deleted** rather than left as an ignored message; `CartesiaSttInitMessage` and its source-generated
registration are gone. Three arms in one process, same key, same host, seconds apart:

| Arm | What it sends | Outcome |
|---|---|---|
| **control** | the URI as shipped, no query at all | `101`, then in band: `{"type":"error","code":400,"message":"Missing sample_rate: …"}`, close `1008 PolicyViolation` |
| **shipped** | `CartesiaSpeechRecognizer` as it now ships | **10/10** digits in one final transcript |
| **witness** | a raw socket carrying the same query | the transcript, then `{"type":"done"}`, then the vendor closes `1000` |

The control matters more than the result: it fires in the same run rather than on a remembered earlier
date, so the difference between the two arms is the query string and cannot be the account, the key,
the host or the day. The shipped arm was then repeated five consecutive times at the shipped 5-second
connect default — 10/10 in all five.

Two things the run measured that were not being looked for. The vendor **normalizes spoken digits to
numerals** (`"one two three…"` comes back `" 12345678910"`), so a word-matching metric reads a perfect
transcript as 0/10 — the same instrument defect §3.6d hit on Speechmatics, met again on a second
surface, which makes it a property of the metric rather than of one vendor. And the live frames match
the field set the fake's recordings were **authored** from — `type`, `request_id`, `text`, `is_final`,
`duration`, `language`, `words[]` with `start`/`end` — including the absence of `confidence` that
`CartesiaSttTranscriptMessage` models as nullable. For this one message the documentation-derived
route is no longer only what the vendor *says* it sends: it is what it sent.

**AssemblyAI STT** — invalid credential **`101` followed by an error frame**, which is what exposed the
in-band validation point and a matching client defect; real key `101` with first frame `Begin`
(`configuration`, `expires_at`, `id`, `type`). Two corrections to this row on 2026-08-16:

- **The wrong-path control does not discriminate on this host.** `wss://streaming.assemblyai.com/v3/ws-does-not-exist`
  upgraded `101` and served a normal session. A route control that cannot fail proves nothing, so this
  surface's evidence class drops to `live + credential control` and its Route column reads *not
  controllable* — not "OK". The earlier `404` recorded here was taken against a different host.
- **The vendor rejects any message shorter than 50 ms**, with
  `3007 Input Duration Violation: 20.0 ms. Expected between 50 and 1000 ms`. The client did not batch
  — `AssemblyAiSpeechRecognizer` sent one WebSocket message per frame the caller yields — so a caller
  feeding 20 ms frames, which is what an Asterisk AudioSocket source produces, failed every session.
  Silently: the receive loop filtered to transcript messages, so the error was dropped on the floor
  (§4.15 — the drop itself is **fixed** under ADR-0050; the coalescing fix below is what stops the error
  being provoked in the first place). Sessions here were driven at 100 ms to measure anything else at all, and that deviation is
  part of the result. **Fixed 2026-08-17** — the client now coalesces into the vendor's window; the
  measurement that fixed it, including which end of the window the *declared sample rate* is enforced
  on, is below.

**Google STT** — wrong path `404`, invalid credential `400 API_KEY_INVALID`, real key
`400 RecognitionAudio not set` — the last being the vendor accepting the credential and rejecting the
empty payload, i.e. past auth and into argument validation. Worth recording for the method: the
vendor's auth page does not list API keys, and reading that silence as a defect would have been
wrong. The `?key=` query parameter **is** supported on `speech:recognize`. The probe settled it; the
documentation could not have.

**OpenAI Whisper / Azure OpenAI Whisper** — each carries a committed recording whose provenance
sidecar declares `"class": "recorded"` — a live capture, taken without a negative control. Real route
evidence of its own weaker class. Not unverified, and not in the same column as Deepgram.

### The half-close is not a flush signal — measured on all four STT surfaces, 2026-08-16

Every streaming STT client in this SDK ends its input the same way: it streams binary audio and then
calls `CloseOutputAsync`. The comment above one of them states the belief the others share — *"signal
end-of-audio (half-close) so the server flushes any pending transcript"*. That belief was never
measured. It is false on three surfaces out of four, and on two of them it does not merely fail to
flush — it **destroys the transcript**.

One utterance, "one. two. three. … ten.", synthesized once and replayed byte-identically into every
arm, streamed in real time. The metric is how many of the ten spoken numbers survive into the
**final** transcripts, because a countable tail makes truncation measurable instead of impressionistic.
Three repetitions per arm; every cell below was identical across all three.

| Arm | What the client does after the last audio frame |
|---|---|
| **A** | bare half-close — **what every client ships** |
| **B** | the vendor's in-band terminator (`CloseStream` / `EndOfStream` / `Terminate` / `done`), no half-close |
| **C** | terminator, then half-close |
| **Z** | *control, known wrong*: transport aborted, no terminator, no close frame |

| Surface | A (shipped) | B (terminator) | C (both) | Z (control) |
|---|---|---|---|---|
| Deepgram STT | **10/10** | 10/10 | 10/10 | 8/10 |
| Speechmatics STT | **0/10** — zero finals | 10/10 | **0/10** | 0/10 |
| AssemblyAI STT | **0/10** — zero finals | 10/10 | **0/10** | 0/10 |
| Cartesia STT † | **5/10** | 7/10 | 7/10 | 5/10 |

† through the corrected query-string URL; the shipped URL never opens a session at all.

Four things this measured that reading the code could not:

1. **Two surfaces lose everything.** Speechmatics and AssemblyAI emit partials throughout the session
   and then, on the close frame, end it with **no final transcript at all** — 20 partials and zero
   `AddTranscript`, no `EndOfTranscript`. With the terminator instead, the same audio yields the
   complete final. A caller consuming only finals, which is the normal way to consume this API, gets
   nothing from either provider today.
2. **Arm C fails.** The obvious remedy — keep the half-close, add the terminator — was measured and
   it is wrong on both broken surfaces: `C ≡ A`. The half-close is not a redundant extra signal to be
   supplemented; it is the thing that has to go. Only on Cartesia is `C ≡ B`.
3. **Deepgram is exempt**, and that is a result rather than an untested assumption: `A ≡ B ≡ C`, with
   the control at 8/10 proving the instrument does detect a lost tail. Without arm Z, Deepgram's three
   identical rows would be indistinguishable from a probe that cannot see truncation.
4. **The 3-of-3 total-failure base rate from the TTS half-close sites did not transfer.** It ranges
   from no effect, through a 2-number truncation, to total loss. The task that opened this experiment
   said that rate must be measured rather than carried over; carrying it over would have produced a
   confident wrong answer about Deepgram.

Two properties of the instrument, recorded because both were nearly wrong:

- **The probe's own metric was defective at first.** It matched digit *words*, and Speechmatics applies
  inverse text normalization — it returns `"123456789 ten."` — so a **complete** transcript scored
  1/10. The numbers above come from a metric that counts numerals and words alike. A measuring
  instrument that scores the vendor's formatting instead of the vendor's behaviour is the same defect
  this record documents in clients, one level up.
- **Arm A was re-run as A2**, sending the identical close frame without awaiting the peer's close, so
  the reader kept consuming to the end. `A2 ≡ A` on both broken surfaces, 3 of 3. That rules out the
  competing explanation that the probe's client library discarded a queued final, and leaves the
  finding where it belongs: with the vendor's handling of a close frame.

### The remediation, and what re-probing the shipped client measured — 2026-08-16

All four clients now do the same thing at end of input: send the vendor's in-band terminator as a text
frame — `{"type":"CloseStream"}`, `{"message":"EndOfStream","last_seq_no":N}`, `{"type":"Terminate"}`,
`done` — and leave the output side open, letting the vendor end the session. This is arm B exactly, no
more; the timeout Cartesia's `CloseOutputAsync` needed is gone with the call it was guarding, and
nothing was added that arm B did not have. A drain deadline, breaking on the vendor's end-of-session
message, and a polite closing handshake were all considered and left out — each would have been
machinery no arm measured.

**Deepgram is remediated too**, though `A ≡ B ≡ C` there. Leaving the one measured-equivalent site
alone would have preserved a difference that means nothing and cost the next reader a re-derivation
before they dared touch it.

**The receive loops needed no change**, and that is worth recording because the plan said they would.
All four already looped while the socket was `Open` *or* `CloseSent`, so they were never the reason a
final arrived too late. What changed there is only the meaning of an unchanged line: the close frame
that ends the loop is now the vendor deciding the session is over, not the vendor answering a close we
sent before it had finished.

The re-probe drives the **shipped C# clients** — not a Python probe reconstructing what they send — over
the same utterance of ten spoken digits, re-synthesized for this run (7.20 s, 16 kHz, 100 ms chunks,
real-time paced), two repetitions per surface. The control arm restores the half-close in the same
source files and runs through the same harness minutes later:

| Surface | remediated | half-close restored (arm A), same harness, minutes later |
|---|---|---|
| Deepgram STT | 10/10, 10 finals | not re-run — §3.6d measured `A ≡ B` here across three repetitions |
| Speechmatics STT | **10/10**, 1 final, 17 partials | **0/10**, **zero finals**, 17 partials |
| AssemblyAI STT | **10/10**, 1 final, 10–14 partials | **0/10**, **zero finals**, 8–9 partials |
| Cartesia STT | **10/10**, 1 final, 0 partials — measured once the query string was fixed | — (see below) |

Three things this run establishes that the §3.6d probe could not:

1. **The improvement is attributable to the diff.** §3.6d compared arms inside one probe; this compares
   two builds of the shipped client. Because the control ran the same day through the same harness, a
   vendor-side difference between the two probe dates cannot explain the result. Without that arm this
   would have measured the day, not the change.
2. **Cartesia's remediation could not be verified live at all, and how it failed was itself the
   finding — the query string has since been fixed and it now can be.** The client never opened a
   session (the missing query string, above): zero partials, zero finals, dead in 0.5 s — and it
   reported `error=none`. That is the ADR-0049 D1 silent-failure class observed end-to-end through the
   shipped client for the first time rather than through a probe. Its half-close fix was asserted by a
   fake and stood unmeasured on the wire; with the query string fixed the same shipped client returns
   **10/10 in a single final**, so the arm that was resting on a fake now rests on the wire and all
   four surfaces in the table are measured. No control column is available for this row: restoring the
   half-close here would restore it on top of a session that never existed when arms A/B/C were run,
   so what §3.6d recorded for Cartesia (`A` 5/10, `B` 7/10, `C ≡ B`) came from a probe holding the
   corrected URL, and the shipped client has only ever run arm B.
3. **The remediated Speechmatics path takes about five seconds longer** for the same audio (13.4 s vs
   8.2 s wall). Those seconds are the client waiting for the vendor to finalize instead of cutting it
   off — which is to say they are the transcript.

The four fakes assert the terminator is sent and that no close frame follows it. That assertion is only
meaningful because the fakes keep reading after the terminator: the first version of these tests
stopped there, and passed against a client that half-closed.

**What this trades, stated rather than left to be discovered.** With the half-close gone, no client in
this package sends a close frame at all, and each session now ends only when the **vendor** closes it.
The unbounded wait is not new — the old code also sat in `ReceiveAsync` waiting for a peer that might
never answer — but what backs it is weaker: RFC 6455 §5.5.1 *obliges* a peer to echo a close frame,
whereas nothing obliges a vendor to end a session after a terminator. **All four are now measured to
close, Cartesia included** — it was the one that could not be, and the query-string fix is what made
the measurement possible. On the wire it answers `done` with a `{"type":"done"}` frame and then closes
`1000` **158 ms** later; through the shipped client the session ends **172 ms** after the last audio
frame. The other three returned in 8–13 s, which is why the re-probe returned at all.

That is a measurement, not a guarantee, and it is worth being exact about which one. It says no surface
in this package currently acknowledges a terminator and then holds the session open — not that none
ever will. Cartesia is also where a sibling command exists, `finalize`, whose whole purpose is to flush
*without* ending the session; the client does not send it, and a vendor that started treating `done`
the way `finalize` behaves would produce exactly the hang this paragraph describes. So the bound stays
unshipped, and now for a stated reason rather than a missing one: **there is no surface to calibrate it
against.** A timeout picked without one would be the machinery this record exists to argue against. The
exposure it would cover is concrete — `VoiceAiPipeline` awaits one STT session per utterance, so a
vendor that acknowledged without closing would leave a call stuck in recognition rather than degrade it
— and the trigger to build it is the first surface measured doing that, not a calendar date.

### AssemblyAI's message window is two-sided, and it is enforced on the *declared* sample rate — 2026-08-17

The 50 ms floor above is only half of a constraint, and fixing it required measuring which number the
vendor does its arithmetic on. Same ten-digit utterance, same key, same host, arms minutes apart. The
8 kHz file is the 16 kHz one downsampled, so the two carry identical speech at 3155 ms.

| Arm | Audio | `sample_rate` declared | Message | Vendor reads it as | Result |
|---|---|---|---|---|---|
| J control | 16 kHz | 16000 | 1600 B | 50 ms | **10/10**, close `1000` |
| H truth | 8 kHz | **8000** | 800 B | 50 ms | **10/10**, close `1000` |
| I mismatch | 8 kHz | 16000 | 1600 B | 50 ms | **10/10**, close `1000` |
| K entangled | 8 kHz | 16000 | 800 B | **25 ms** | **`3007`**, 8/64 messages accepted, **0/10** |
| E ceiling | 16 kHz | 16000 | 64000 B | 2000 ms | **`3007`** — `Expected between 50 and 1000 ms` |

Read in order, those arms say four things the vendor's page does not:

1. **The window is enforced at both ends.** E rules out reading "between 50 and 1000 ms" as a floor
   with a decorative upper bound, so the client splits at the ceiling as well as coalescing to the
   floor. That split is reachable from a single caller frame, not only from accumulation: the
   AudioSocket codec's 3-byte length field admits payloads far larger than anything this repo emits.
2. **`8000` is accepted.** `AssemblyAiOptions.SampleRate` documented that the service "expects 16000",
   which was stronger than the evidence — H transcribes a telephony-rate session perfectly. The summary
   has been corrected to what was measured.
3. **K against H is the control that fixed the design.** Identical bytes of identical audio; the only
   difference is the declared rate, and one of them dies. So the duration the window is enforced on is
   computed from the *declaration*, not from the audio — which means a client that coalesced to 50 ms of
   the audio it was handed while still declaring 16000 would send what the vendor reads as 25 ms and
   lose every session. The remediated client derives the declared rate and the coalescing thresholds
   from one value, so that divergence is no longer expressible rather than merely corrected.
4. **The mismatch itself is harmless, and saying so bounds the claim.** Arm I declares 16000 over 8 kHz
   audio — the shipped defect — and still returns 10/10. The reconnaissance for this fix predicted a
   damaged transcript there; the measurement refused it. What was wrong with the shipped declaration is
   the arithmetic in point 3, not audio quality, and the fix is worth having for that reason alone.

**The trailing remainder is padded with silence, and as-is was measured working first.** A stream whose
length is not a multiple of the message size ends with less than a floor's worth of audio. Sending it
as-is worked three runs of three; a lone sub-floor message at the end of a stream is tolerated. It was
rejected anyway, because three consecutive sub-floor messages drew `3007` with **zero** finals — the
tolerance is real, thin, and nowhere stated, and when it breaks it costs the whole transcript rather
than the tail. This is the §3.6f trade again in the other direction: a measured tolerance is weaker
ground than a stated contract, since nothing obliges the vendor to keep being lenient. Zeros are silence
in signed 16-bit PCM, so padding cannot invent a word where dropping the remainder could clip one.

**Verified through the shipped client, with the before arm run the same day.** Not a probe
reconstructing what the client sends — the SDK's own `AssemblyAiSpeechRecognizer`, fed 20 ms frames of
8 kHz audio the way `VoiceAiPipeline` feeds it, with `AssemblyAiOptions.SampleRate` left at its 16000
default on purpose so that the caller's format having to win is part of what passes:

| Client | Result |
|---|---|
| remediated | **10/10**, 1 final, 2 partials |
| pre-fix, same harness, minutes later | **0/10** — zero finals, zero partials, **and no exception** |

That second row is the ADR-0049 D1 silent-failure class in its production form: 3.2 seconds of speech
in, an empty transcript out, and nothing raised to say why. It is also why the suite's assertions are on
the *bytes the client sent*. The fake previously discarded `result.Count` on the binary branch and kept
only a frame counter, and no test in this repo asserted on the size of audio sent to any provider — a
fake that cannot fail a client sending 20 ms messages is what let this ship. Reverting each half of the
fix in turn fails a non-empty set of tests, and the two halves are covered independently: restoring the
option as the declared rate fails exactly 1 test and nothing else, removing the coalescing fails 4 while
that rate assertion still passes, and sending the tail short fails 3 — a strict subset of those 4, since
padding only concerns the last message. The pre-fix client fails 5, the union of the first two.

## The silent-failure class, closed — 2026-08-17

Every *Open* marker above that read "ADR-0049 D1" pointed at one missing decision: what a client should
do when the vendor says a session failed. ADR-0050 settles it — a typed exception thrown from the
receive loop — and this section records what closing it actually took, because the shape found was wider
than the shape recorded.

**The class was three doors, not one.** Each surface above was written up by the door that produced its
measured symptom, which made the class look like a frame-filter problem. It was not:

| Door | What it looked like | How many of the 8 WebSocket clients had it open |
|---|---|---|
| The frame allow-list | an error frame falls into the same discard branch as lifecycle noise | 8 |
| The **close code** | `ws.CloseStatus` read, then thrown away — `1002`, `1008`, `4001` all indistinguishable from a finished session | 8 |
| `catch (WebSocketException) { break; }` | a socket dying mid-stream ends the stream as normal completion | 8 |

A surface whose allow-list was clean was still silent through the other two, so "which surfaces are
affected" went from the five with a filter defect to **eight** — every WebSocket speech client in the
SDK.

**What a caller sees now.** `SpeechProviderFailureException` when the vendor reported a failure,
carrying a `Signal` (`ErrorFrame`, `CloseCode`, `Handshake`, `Transport`) and the vendor's own code and
text; `SpeechProviderEmptyResultException` when the session ended clean and empty. Cancellation raises
neither. On the recognition side the empty case is deliberately narrower than on synthesis: zero
transcripts is a healthy session, zero *messages* is not.

**What is still not evidence.** Two error-frame branches — Deepgram TTS and Deepgram STT — are closed
against the vendor's published schema and not against a capture, because this vendor rejects a bad
credential with `HTTP 401` at the upgrade on both surfaces, so no session can produce the frame those
branches catch. The code and its tests say so at the branch. Every other frame and close code under test
is one a probe recorded on the live endpoint.

## Still not characterised

Named here rather than left as absence, because absence is what this file exists to make visible:

- **Cartesia STT, Deepgram STT** — full frame inventories. The 2026-08-16 half-close runs exercised
  transcript frames on both and the error path on Cartesia, so these are no longer *never exercised*;
  what remains uncharacterised is the complete field set of each message type, which those runs
  recorded only to the depth the experiment needed (`is_final`/`text` on Cartesia, `is_final` and the
  alternatives array on Deepgram). **Cartesia's `transcript` and `error` messages are now closed**:
  the query-string run observed both in full and they match the field set the fixtures were authored
  from. **Closed 2026-08-18:** the capture script grew a WebSocket session path, and the `transcript`,
  `flush_done`, `done` and `error` frames are now committed as `class: "recorded"` with the fake
  answering the terminator from the recording instead of closing bare. Two things the capture
  corrected, neither reachable without one: the vendor sends **`is_final` false** on `flush_done`
  where the authored fixture asserted true — so the shape that fixture existed to guard against is
  one the vendor does not produce, and it is kept separately as an explicitly authored adversarial
  case rather than deleted — and the `words[]` entries and `text` carry a **leading space** the
  authored frame did not. Field set and types matched the documentation exactly. Still authored: the
  **interim** transcript, because `ink-whisper` answered a 3.6-second utterance with a single final
  transcript in both an unpaced and a real-time-paced session. That is an observation about this
  utterance, not a claim the service never sends interim frames.
- **Speechmatics STT** — narrowed twice, and what is left is narrower than it was. A transcript frame
  was first observed live on 2026-08-16, and a fuller session on **2026-08-18** measured: the
  `word_delimiter` the vendor declares inside `RecognitionStarted.language_pack_info`; `attaches_to`
  on a punctuation result; `metadata.transcript` present on **all three finals and all eight
  partials**, never empty where the results array was non-empty; and finals carrying inter-segment
  glue whitespace that partials do not. Two frame kinds the client does not model were seen in the
  same run — `AudioAdded` (29 of them, `{message, seq_no}`) and `Info` (2, rate-limiting telemetry,
  15 fields). What stays uncharacterised is the remaining field set of those two, and every message
  type outside `{Info, RecognitionStarted, AddPartialTranscript, AddTranscript, EndOfTranscript,
  AudioAdded, Error}`.
- **Speechmatics TTS** — **the `voice`-in-body question is closed; a different gap opened in its
  place.** Measured 2026-08-18 by reintroducing the conflict deliberately: the path wins in both
  directions and the body field is ignored. Two instrument refutations came first and are worth
  keeping, because both would silently produce a wrong answer on any route like this one — byte
  identity is not a discriminator here (the same request twice returns the same length and a
  different hash), and byte length only becomes one after the within-voice spread is measured
  (lengths move in exact 1536-B steps and the spread reaches 4 608 B, so single samples per arm
  compare noise). With six samples per arm the ranges separate and **no sample ever landed in the
  opposite voice's range**. What is open now is that **the voice segment cannot fail**: unrecognised
  segments return `200 audio/wav` rather than an error, apparently falling back to whatever voice
  the account is entitled to, so a misconfigured `Voice` degrades silently and the caller is never
  told. The vendor does expose an authoritative, credential-gated `GET /voices`; the client does not
  consult it. This is route-independent — the route control fails correctly.
- **AssemblyAI STT** — route **not controllable**, which is different from unprobed. Measured
  2026-08-16, an undocumented path on this host completed the upgrade and served a normal session, so
  the wrong-path arm cannot fail and therefore controls nothing; the `404` recorded earlier in the
  programme came from a **different host** and never applied here. Frames and credential handling
  stand on their own evidence. See ADR-0048 A2 for what follows from a control that cannot fail.
- **LMNT (HTTP)** — the `lmnt-version` header **admits no control that can fail**, measured
  2026-08-18. Five values against the same request with every form field held at the shipped
  defaults — `1.0`, the `1.2` the vendor's docs show, the header **omitted entirely**, `9.9` and
  `banana` — all returned `200 application/vnd.lmnt.audio-int16` with a headerless PCM payload. The
  null comparison ran first and is what makes that readable: three identical requests varied by
  8 960 B, so length discriminates nothing here. The shipped `1.0` is therefore kept; the vendor
  documenting a newer value is evidence about the documentation, not the wire. Not licensed by this:
  calling the header *ignored* — only one route's success path was compared, across three dimensions.
- **LMNT (WebSocket)** — no wrong-path control recorded on this surface. Its credential control was
  run and is what established the in-band validation point, so the gap is route-discrimination
  only.
- **Azure TTS, both Whisper recognizers** — validation point, and route evidence at
  `live, uncontrolled`.
- **Frame fragmentation across the 64 KiB receive buffer** — **answered for both Class B surfaces on
  2026-08-18, and the premise it was filed under was wrong.** This was recorded as length-dependent
  and out of reach of the short probe sentence. It is not: ElevenLabs answered the **44-byte** probe
  sentence with a **75 015-byte** message, already over the buffer, and answered a 2 085-byte input
  with 58 such messages, the largest **293 720 B**. Cartesia never approaches it — 8 681 B is its
  largest message, across 559 of them on the long input. So one vendor fragments routinely and the
  other never does, neither inferable from the other, and the `EndOfMessage` assembly both loops
  gained was repairing a **live** defect rather than closing a margin. Note where it hid: this
  surface was on record as "~115 KB across 4 frames, ~29 KB average" — the average was reported, the
  maximum was not, and one of those four frames had been over the buffer all along. Still unmeasured
  on the **Class A** (binary-frame) surfaces, where frame size is chosen by the client and the
  measured headroom is 34×.

## Two properties this record keeps having to restate

**A vendor asserting X is evidence; a vendor not mentioning Y is not.** The Google `?key=` question
and the raw-binary-mode question were both nearly decided by a vendor's silence. Silence decides
nothing.

**A fake written by the client's author certifies the client's misreading.** Every defect above
shipped past a green suite, because the fake and the client were built from the same reading of the
same document. That is why the probe is committed code with its own tests rather than a procedure
someone remembers to follow — and the probe's own redactor failed the same way once, which is
recorded in its module docstring rather than quietly patched.

## Maintenance

- Add the row when the surface is added, even if the row reads `not characterised`. A missing row is
  indistinguishable from a working provider until a user finds otherwise. **This is enforced**: a
  governance guard parses `src/` for concrete `SpeechSynthesizer` / `SpeechRecognizer` subclasses and
  fails the build, naming the type and the file that declares it, if the **Client type** column does
  not mention it. The guard checks that a type is *present*, never what its row says — `not
  characterised` is a legal, passing status, because the point is that nobody can ship a provider
  whose conformance is simply unstated. It also runs in reverse: a row naming a type that no longer
  exists in `src/` fails too, since a row nobody is forced to update reads as coverage of a provider
  that shipped away.
- One client type may own several rows (`LmntSpeechSynthesizer` owns both LMNT transports). One row
  never owns several client types.
- A row's date is the date **its own** measurement was taken. Do not flatten several rows onto one
  header date — it would assert a live measurement for surfaces that never got one.
- Never upgrade a class without a run. `documentation` → `live` is a probe, not an edit.
- When a probe contradicts this file, the probe wins and the row changes, including the date.
