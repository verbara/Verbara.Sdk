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

| Surface | Transport | Route | Frames | Validation point | Evidence | Date |
|---|---|---|---|---|---|---|
| Deepgram TTS | `wss://api.deepgram.com/v1/speak` | OK | OK | `handshake` | `live + both controls` | 2026-08-15 |
| Azure TTS | `https://{region}.tts.speech.microsoft.com` | OK | OK | not measured | `live, uncontrolled` | 2026-08-03 |
| LMNT (HTTP) | `https://api.lmnt.com/v1/ai/speech/bytes` | **fixed** | n/a | in the response | `live + both controls` | 2026-08-15 |
| LMNT (WebSocket) | `wss://api.lmnt.com/v1/ai/speech/stream` | OK | **2 fixed, 1 open** | `in-band` | `live + credential control` | 2026-08-15 |
| Speechmatics TTS | `https://preview.tts.speechmatics.com/generate/{voice}` | **fixed** | n/a | in the response | `live + both controls` | 2026-08-16 |
| Cartesia TTS | `wss://api.cartesia.ai/tts/websocket` | OK | **3 fixed, 1 open** | `handshake` | `live + both controls` | 2026-08-16 |
| ElevenLabs TTS | `wss://api.elevenlabs.io/v1/text-to-speech/{voiceId}/stream-input` | OK | **2 fixed, 1 open** | `in-band` | `live + both controls` | 2026-08-16 |

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
   empty stream and no exception. *Open* — it is a behaviour change and belongs to the ADR-0049 D1
   remedy, not to a route fix.

**Speechmatics TTS** — the voice is selected by **path segment**, not by a body field: `/generate`
returns `404` (identically to a route that does not exist, because that is what it is),
`/generate/{voice}` returns `200 audio/wav` — 33 836 B of valid `RIFF`/`WAVE`. Invalid credential
`401` on the same host. Fixed. **Not** verified on this surface: whether the `language` and
`sample_rate` body fields are accepted as sent. Only the route was isolated, so only the route is
claimed.

**Cartesia TTS** — three defects, and the documented one was the least of them. The shipped request
omitted `context_id`, so the endpoint answered an error and sent no audio; the client half-closed
after the request, which alone cost everything (a control differing only in that step received 7
chunks, 32 694 B, in 1.022 s); and only then does the frame-type defect appear — audio arrives base64
in the `data` field of `type="chunk"` **text** frames, while the loop read only binary. All three are
*fixed*, in one commit, because fixing only the frame type would still have shipped a provider that
produces silence. A fourth is *open*: an `error` frame ends the stream with no exception (ADR-0049
D1). What this surface's row does **not** claim: the fixed client has not itself been run live. What
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
Cartesia's are. A third is *open*: the invalid-credential frame from arm D has no `audio` member and
is silently dropped, so a rejected key still reaches the caller as an empty stream and no exception
(ADR-0049 D1). As with Cartesia, the fixed client has not itself been run live — arm B is a
reconstruction of what it now sends, not the artifact. Arm C is worth its cost for what it
**refuted**: the
shipped frames carry `"flush": null` and `"voice_settings": null`, the exact shape that was a total
outage on LMNT, and ElevenLabs tolerates them. The class does not generalise, which is why the arm
was run instead of assumed. Note also that E answers `403` where every other surface answers `404` —
a wrong-path control has to be read, not pattern-matched.

## Speech-to-text

| Surface | Transport | Route | Frames | Validation point | Evidence | Date |
|---|---|---|---|---|---|---|
| Deepgram STT | `wss://api.deepgram.com/v1/listen` | OK | not exercised | `handshake` | `live + both controls` | 2026-08-15 |
| Speechmatics STT | `wss://eu2.rt.speechmatics.com/v2` | OK | **1 fixed, 4 open** | `in-band` | `live + both controls` | 2026-08-15 |
| Cartesia STT | `wss://api.cartesia.ai/stt/websocket` | OK | not exercised | `handshake` | `live + both controls` | 2026-08-15 |
| AssemblyAI STT | `wss://streaming.assemblyai.com/v3/ws` | OK | first frame only | `in-band` | `live + both controls` | 2026-08-15 |
| Google STT | `https://speech.googleapis.com` | OK | n/a (batch) | in the response | `live + both controls` | 2026-08-15 |
| OpenAI Whisper | `https://api.openai.com/v1/audio/transcriptions` | OK | n/a (batch) | not measured | `live, uncontrolled` | 2026-08-09 |
| Azure OpenAI Whisper | Azure OpenAI deployment endpoint | OK | n/a (batch) | not measured | `live, uncontrolled` | 2026-08-09 |

**Deepgram STT** — shipped defaults (`encoding=linear16`, `sample_rate=16000`, `channels=1`,
`model=nova-2`, `interim_results=true`, `punctuate=true`) with the `Authorization: Token` header
returned `101`; wrong path `404`; malformed key `401` at the upgrade. Frames were **not** exercised,
and the row says so, so a verified route is not read as a verified frame protocol.

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

As on the two TTS surfaces fixed the same week, the fixed client has not itself been run live — arm B
is a reconstruction of what it now sends, not the artifact. The row's date stays **2026-08-15**, the
day its arms were measured, even though the fix landed on 08-16: a row's date is the date of its own
measurement, and advancing it for a code change would make the ledger claim a run that never happened.

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

**Not** observed: any transcript frame — the sessions that authenticated were opened to establish the
remedy and no audio was streamed — so the frame inventory beyond those two message types stays *not
characterised*, and with it the four defects still open on this surface: the swallowed `Error` frame
(ADR-0049 D1, so a rejected session still reaches the caller as an empty stream) and the three
assembly signals the client ignores — `word_delimiter`, `attaches_to`, and the vendor's
already-assembled `metadata.transcript`.

**Cartesia STT** — wrong path `404`, invalid credential `401`, real key `101`. Route and auth OK,
frames not exercised.

**AssemblyAI STT** — wrong path `404`; invalid credential **`101` followed by an error frame**, which
is what exposed the in-band validation point and a matching client defect; real key `101` with first
frame `Begin` (`configuration`, `expires_at`, `id`, `type`).

**Google STT** — wrong path `404`, invalid credential `400 API_KEY_INVALID`, real key
`400 RecognitionAudio not set` — the last being the vendor accepting the credential and rejecting the
empty payload, i.e. past auth and into argument validation. Worth recording for the method: the
vendor's auth page does not list API keys, and reading that silence as a defect would have been
wrong. The `?key=` query parameter **is** supported on `speech:recognize`. The probe settled it; the
documentation could not have.

**OpenAI Whisper / Azure OpenAI Whisper** — each carries a committed recording whose provenance
sidecar declares `"class": "recorded"` — a live capture, taken without a negative control. Real route
evidence of its own weaker class. Not unverified, and not in the same column as Deepgram.

## Still not characterised

Named here rather than left as absence, because absence is what this file exists to make visible:

- **Cartesia STT, Deepgram STT** — frame inventories. Route and auth measured, frames never exercised.
- **Speechmatics STT** — everything past `RecognitionStarted`. No transcript frame has been observed
  live; the assembly logic rests on the vendor's message set and the committed fixtures.
- **Speechmatics TTS** — whether the `language` and `sample_rate` body fields are accepted as sent.
  The route was isolated; the body fields rode along unmeasured.
- **LMNT (WebSocket)** — no wrong-path control recorded on this surface. Its credential control was
  run and is what established the in-band validation point, so the gap is route-discrimination
  only.
- **Azure TTS, both Whisper recognizers** — validation point, and route evidence at
  `live, uncontrolled`.
- **Every surface** — behaviour on inputs long enough to fragment a frame across the 64 KiB receive
  buffer. The probe sentence used throughout is far too short to reach it. The two Class B loops now
  assemble until `EndOfMessage` and their fakes can split a frame on demand, so the *client* side is
  handled and tested; what is still unmeasured is whether either **vendor** fragments in practice,
  which no fake can answer.

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
  indistinguishable from a working provider until a user finds otherwise.
- A row's date is the date **its own** measurement was taken. Do not flatten several rows onto one
  header date — it would assert a live measurement for surfaces that never got one.
- Never upgrade a class without a run. `documentation` → `live` is a probe, not an edit.
- When a probe contradicts this file, the probe wins and the row changes, including the date.
