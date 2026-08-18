# Tasks — provider-wire-protocol-conformance

Execution follows Subagent-Driven Development with FCM batching:
**Phase A (batch)** = §1 characterisation · **Phase B (focused, one provider at a time)** = §2 Class B
frame-type fixes + §3 Class A route fixes + §4 the two Speechmatics STT defects, authentication and
assembly · **Phase C (batch)** = §5 the probe instrument + §6 governance, records and docs +
§7 verification.

The ordering is by failure visibility — silent before loud — not by defect class and not by severity.
§2 first because Cartesia and ElevenLabs are **configured, connect successfully and silently produce
nothing** — a caller sees a completed call with an empty stream and no error. §3 next because a `404`
is at least loud, and the LMNT half is opt-in behind `Transport = Http`. §4 last even though §4.1 is
the severest defect in the programme: Speechmatics realtime STT cannot authenticate at all and has
none of LMNT's containment, but it fails loudly with a close code, and a provider that has never
opened a session has no working caller to regress. Severity is recorded in §6.10 and §6.11;
sequencing is not severity.

Every fix in §2–§4 lands as its own commit per provider, so a bisect lands on one provider family.

**The evidence rule for this change, stated once:** a green provider suite does not close any task in
§2–§4. Every provider suite in this repository drives a fake written by the same author as the client,
so a shared misreading of a vendor's contract passes green on both sides — which is how all six of
these defects shipped, the Speechmatics STT credential included: its fake never checked the credential
at all. Close-out evidence is a probe against the live endpoint with a negative control that reaches
the vendor's first protocol exchange (§5, §5.11). A fixture whose provenance sidecar names the vendor
document it came from closes the **task**; it does not verify the **surface**, which stays *not
characterised* in §5.5.

## 1. Characterise what is still unknown — probe before patch

- [x] 1.1 Commit the current scoreboard into this change directory as working evidence, one row per
      surface with its evidence class **and its own date — the dates differ and MUST NOT be flattened
      into one**. The six TTS rows: Deepgram route OK / frame OK; Azure route OK / frame OK; LMNT
      (HTTP) route `404`; Speechmatics route `404`; Cartesia route OK / frame BROKEN; ElevenLabs route
      OK / frame BROKEN. Four of six broken — two by route, two by frame format — and **one frame half
      still uncharacterised**: Cartesia TTS's 2026-08-15 probe reached `101` and then sent a malformed
      synthesis request, so the vendor answered with an error frame and the frame inventory was never
      seen. Their provenance is not uniform: **Deepgram** is a live probe of 2026-08-15 carrying a
      negative control on the same host; **ElevenLabs** was probed live on 2026-08-15 with both controls
      and its frame finding is now measured, not documentation-derived; **Cartesia**'s frame finding
      remains the vendor-documentation read of 2026-08-14 and MUST stay at that class even though its
      route and auth were probed the next day; **Azure** was established in earlier work and was *not*
      re-probed in this pass;
      **LMNT** and **Speechmatics** carry the dates of their own route probes. Put the real date on each
      row — a single header date would assert a live 2026-08-15 measurement for four surfaces that never
      got one, which is exactly the conflation §1.9 and the spec's evidence-class rule exist to prevent.
      **Done, and landed straight in `docs/guides/provider-wire-conformance.md` rather than here.**
      A copy in this change directory would be archived with the change, and the one property this
      table must have is that it **outlives** the change that produced it — a scoreboard that ships
      into an archive folder is a scoreboard nobody consults. §5.5 was going to move it there anyway;
      writing it there once removes the window in which two copies disagree. Every row carries its own
      date, per the rule above
- [x] 1.2 The four **WebSocket streaming recognizers** are no longer unknown at all: all four were
      probed on 2026-08-15 with the §5 method, and §1.3–§1.5a record what each returned. Their classes
      still differ — Cartesia STT and AssemblyAI STT carry both controls, Deepgram STT carries a
      wrong-path control but **no invalid-credential control**, so its validation point is *not
      established* — and the table MUST keep that difference visible rather than giving the four one
      shared verdict on the strength of having all been touched
- [x] 1.3 **Deepgram STT — route verified 2026-08-15 with a negative control; frames not exercised.**
      `wss://api.deepgram.com/v1/listen` with the SDK's exact shipped defaults (`encoding=linear16`,
      `sample_rate=16000`, `channels=1`, `model=nova-2`, `interim_results=true`, `punctuate=true`) and
      the `Authorization: Token` header returned `101 Switching Protocols`; the wrong-path control
      `/v1/listen-does-not-exist` on the same host returned `404 Not Found`. **The `101` does not, on
      its own, prove the credential was accepted** — that inference was made here and `Sdk/ADR-0049` D3
      now forbids it. No invalid-credential control was ever run against Deepgram (ADR-0048 probed the
      route, not the key), so its validation point is recorded **not established** and §1.3a closes the
      gap. Frames were **not** exercised — Deepgram is `not-cleared` under
      `docs/guides/provider-recording-protocol.md` section 7 — and the row must say so, so a verified
      route is not read as a verified frame protocol
- [x] 1.3a **Run the missing invalid-credential control against Deepgram** — TTS and STT, same host,
      deliberately malformed key, alongside the wrong-path control already taken. It is the one surface
      in the scoreboard whose validation point rests on inference, and it is the surface every other
      row's "handshake vs in-band" framing was originally reasoned from, so leaving it uncontrolled
      leaves the weakest evidence under the most load. Two outcomes, both useful: a `401` at the
      handshake confirms what was assumed and costs one probe, or a `101` followed by an error frame
      makes it **four** in-band surfaces and puts `DeepgramSpeechRecognizer.cs:120` from §4.16 in the
      live-symptom set rather than the latent one. Record whichever, with its date.
      **Ran 2026-08-15: the first outcome. Deepgram validates at the handshake.** A deliberately
      malformed key returns `HTTP 401` at the WebSocket upgrade on **both** `/v1/listen` (STT) and
      `/v1/speak` (TTS); the valid key returns `101` on both, on the same host, in the same run. The
      §1.3 row's validation point moves from *inferred* to *measured* — the inference happened to be
      right, which is not the same as having been justified, and D3 is what makes the difference
      recordable. Consequence: `DeepgramSpeechRecognizer.cs:120` stays **latent** (no in-band failure
      frame can reach it, because failures never get in-band), so §4.16 keeps it as a code defect
      without a live symptom rather than promoting it
- [x] 1.4 **Speechmatics STT — probed 2026-08-15 to the first protocol exchange, and it does not
      authenticate.** The route resolves and the upgrade completes; the credential is then rejected
      in-band with close code `4001 not_authorised`. That is the defect fixed in §4.1–§4.4 — in this
      change, not a follow-on. Observed live: the `Info` frame (§4.5) and `RecognitionStarted` (§4.6).
      **Not** observed: any `AddTranscript` frame — the sessions that authenticated were opened to
      establish the remedy and no audio was streamed — so the frame inventory beyond those two message
      types stays **not characterised**, and the assembly finding from §4.7 onwards remains derived from
      the vendor's message set and the committed fixtures rather than from live transcript frames
- [x] 1.5 **Cartesia STT and AssemblyAI STT — credentials obtained 2026-08-15; both now probed with
      two controls.** This supersedes the original *not characterised, no credential* entry, which was
      true when written. `src/Verbara.Sdk.VoiceAi.Stt/Cartesia/CartesiaSpeechRecognizer.cs`: wrong path
      `404`, invalid credential `401`, real `101` — route and auth OK, **frames not exercised**.
      `.../AssemblyAi/AssemblyAiSpeechRecognizer.cs`: wrong path `404`, invalid credential **`101`
      followed by an error frame** (in-band auth), real `101` with first frame `Begin`
      `{configuration, expires_at, id, type}` — route OK, and the invalid-credential control is what
      exposed §4.15. Record both with their controls; do not carry the frame halves further than the
      evidence goes
- [x] 1.5a **Google STT — promoted from `uncontrolled` to a controlled probe, 2026-08-15.** Wrong path
      `404`, invalid credential `400 API_KEY_INVALID`, real key `400 RecognitionAudio not set` — the
      last of which is the vendor accepting the credential and rejecting the empty payload, i.e. past
      auth into argument validation. Its row moves out of the shared HTTP-batch line in §1.6, which
      now covers only the two Whisper recognizers. Note for the record that the SDK's `?key=` query
      parameter **is** a supported mechanism on `speech:recognize`: Google's own auth page does not
      list API keys, and reading that silence as a defect would have been wrong — the probe settled it
- [x] 1.6 The **two remaining HTTP batch recognizers** — `.../Whisper/WhisperSpeechRecognizer.cs` and
      `.../Whisper/AzureWhisperSpeechRecognizer.cs` — are a
      different shape (request/response, no frame protocol), and each already carries a committed
      recording under `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Recordings/` whose provenance sidecar
      declares `"class": "recorded"` — a **live capture**, taken **without a negative control**. That is
      real route evidence of its own weaker class: do not call it unverified and do not put it in the
      same column as §1.3. Either re-probe with a control or record the class as it stands.
      `.../Google/GoogleSpeechRecognizer.cs` **left this group on 2026-08-15** — see §1.5a, where it was
      re-probed with both controls — so this task covers two recognizers, not three, and the third is
      the worked example of what "re-probe with a control" produces
- [x] 1.7 Azure TTS is recorded as previously proven working. That is a **weaker evidence class** than
      the 2026-08-15 Deepgram probe: it was not re-probed with a negative control. Either re-probe it
      or record the weaker class explicitly — do not promote it by placing it in the same column
- [x] 1.8 The LMNT **WebSocket** path is untouched by the HTTP finding and is **not verified**.
      `LmntSpeechSynthesizer.cs` builds `wss://api.lmnt.com/v1/ai/speech/stream` at line 265 with no
      option to override it. "Not affected by this finding" is not "checked"; say the second thing only
      if it was done.
      **Superseded by the checking: it was done, and this row was the reason it happened.** The task
      said only that the surface was unverified; §3.6a/§3.6b/§3.6c then found **three** total-failure
      defects on it — `"model": null`, the half-close, and the discarded error frame — two now fixed
      and one open with the ADR-0049 D1 train. Its row in the conformance record reads route OK,
      validation point `in-band` (measured), and carries the open item; the outstanding gap is that no
      **wrong-path** control was ever run on this surface, which the record names rather than
      smooths over. Worth keeping as the cleanest instance of this section's whole premise: refusing
      to write "checked" was what produced the checking
- [x] 1.9 Where no capture credential exists for a surface, the answer is **not characterised — no
      credential**, recorded as such. `docs/guides/provider-recording-protocol.md` section 7 already
      carries the per-provider terms verdicts; a `not-cleared` terms verdict blocks storing Output but
      does **not** block a route probe that stores nothing, and the two must not be conflated
- [x] 1.10 Every probe run in this section obeys `docs/guides/provider-recording-protocol.md` section 4:
      no Output stored or printed, correlating identifiers never echoed. This is how the 2026-08-15
      Deepgram and Speechmatics runs were conducted and the instrument in §5 inherits it
- [x] 1.11 The output of this section is one table, committed, that §5.5 promotes into `docs/`. Every
      TTS and STT surface gets a row with route status, frame status, evidence class and date. A surface
      with no row is the failure mode this section exists to prevent.
      **Done — `docs/guides/provider-wire-conformance.md`, 14 rows: seven TTS surfaces and seven STT.**
      Seven TTS from six providers, because LMNT's HTTP and WebSocket paths are separate surfaces with
      separate defects and separate evidence, and collapsing them onto one provider row is how the WS
      path stayed unprobed until §1.8 forced it. The surfaces with nothing to report are the ones the
      file exists for, so they get a named section rather than an omission

## 2. Class B — audio arrives on a text frame (Cartesia, ElevenLabs)

- [x] 2.1 `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs` — the receive loop yields
      only `WebSocketMessageType.Binary` frames and treats every text frame as a control message,
      breaking only on `done` / `error`. Decode the vendor's base64 audio field from the JSON text frame
      and write those bytes to the channel. **Closes when** a synthesis over the documented text-frame
      shape yields non-zero audio, and when the frame inventory from a live probe matches what the loop
      now consumes. Absent a credential, the pinned vendor document named in the fixture's provenance
      sidecar closes **the task and not the verification**: documentation is what produced these defects
      and would not have caught any of the frame-type ones, so the surface stays *not characterised* in
      the §5.5 record and §7.7 applies
- [x] 2.1a **Cartesia TTS frame inventory — measured live 2026-08-15, so §2.1 no longer closes on a
      document.** The §1.1 row recorded the frame half as *uncharacterised* because the earlier probe
      sent a malformed request. Three findings, and the frame type was the least of them.
      **(a)** The shipped request omits `context_id`; the endpoint answers
      `{"type":"error","status_code":400,"done":true,"error":"context_id is invalid: …"}` and sends no
      audio. A prior hypothesis that `"continue": null` caused this was **refuted** by an A/B — both
      forms produced the identical error. **(b)** `SendRequestAsync` calls `CloseOutputAsync`
      immediately after the request; with it, **0 frames** arrive, and the control that differed only
      in that step received 7 chunks + `done`, 32 694 B, in 1.022 s. This is the §3.6c class, second
      confirmed instance. **(c)** Only then does the documented defect appear: audio arrives base64 in
      field `data` on `type="chunk"` **text** frames (keys `context_id, data, done, flush_id,
      status_code, step_time, type`; the terminator carries `context_id, done, status_code, type`),
      and the loop reads only `Binary`. Controls: wrong path → `HTTP 404` at the handshake, invalid
      credential → `HTTP 401` at the handshake, so Cartesia TTS is a **handshake**-validation surface
      for the ADR-0049 scoreboard, measured per D3. **None of (a)–(c) is fixed here** — §2.1/§2.2 own
      the fix and this task only replaces their evidence basis; (a) and (b) are new defects that were
      not in this change's scope when it was written and MUST be added to §2.1's closing conditions,
      because fixing only the frame type would still ship a provider that produces silence
- [x] 2.2 `src/Verbara.Sdk.VoiceAi.Tts/Internal/VoiceAiTtsJsonContext.cs` — `CartesiaTtsControlMessage`
      models only `type`. Add the audio-carrying member (or a separate chunk DTO), register it in the
      context, and keep the discriminator branch that already recognises `done` / `error`
- [x] 2.3 `src/Verbara.Sdk.VoiceAi.Tts/ElevenLabs/ElevenLabsSpeechSynthesizer.cs` — the loop carries the
      defect as a comment: *"Only yield binary frames; skip text messages (alignment, metadata)."*
      Decode `AudioOutput.audio` from the text frame instead. **Closes when** the committed fixture in
      2.6 round-trips through the synthesizer to the audio bytes it encodes.
      **Frame inventory measured live 2026-08-16, and §2.3 alone does not fix this provider.** A run
      reproducing `SendTextAsync` frame for frame received **0 binary bytes** and 4 text frames whose
      key set is `alignment, audio, isFinal, normalizedAlignment`; the base64 in `audio` decodes to
      86 193 B — 2.694 s of 16 kHz PCM — and the server closed `1000`. So the comment quoted above is
      not a partial defect: the binary branch it prefers receives nothing at all. **But the same run
      also found a second, independent total defect** — the half-close (§2.3a) — and it fires first.
      Fixing the frame type without it yields a provider that still produces silence, which is the
      LMNT sequencing lesson (§3.6c) arriving before the fix instead of after it. Both must land in
      the same commit, and this task's closing condition now includes a non-zero audio assertion with
      the shipped close sequence in place
- [x] 2.3a **ElevenLabs half-closes too, and it is total — 3 of 3 measured sites now.**
      `ElevenLabsSpeechSynthesizer.cs:113` calls `CloseOutputAsync(NormalClosure)` right after the
      empty-text chunk that is already the vendor's documented end-of-input signal — structurally
      identical to LMNT's `eof` + half-close. Measured 2026-08-16 with that call as the only
      variable: **A** (shipped sequence, half-close included) → **0 bytes, 0 text frames**, close
      **1006** abnormal; **B** (identical, half-close removed) → 86 193 B of audio across 4 text
      frames, close **1000**. **A third arm refuted a hypothesis worth recording:** `ElevenLabsTextChunk`
      has `bool? Flush` and a nullable `VoiceSettings`, and `VoiceAiTtsJsonContext` declares no
      `JsonSourceGenerationOptions`, so the shipped frames carry `"flush": null` and
      `"voice_settings": null` — the exact shape that was a total outage on LMNT (§3.6a). Arm **C**
      omitted both nulls and returned a **byte-identical** result to B: ElevenLabs tolerates them.
      The class does not generalise, which is why the arm was run instead of assumed.
      **Controls:** invalid credential → in-band text frame `{"message":"Invalid API key",
      "error":"invalid_api_key","code":1008}` then close `1008`, so ElevenLabs is an **in-band**
      validation surface for the ADR-0049 scoreboard (measured, per D3); wrong path → **HTTP 403** at
      the handshake, which distinguishes routes but is not the `404` the other surfaces answer
- [x] 2.3b **The Class B fix converts an ignored margin into a live defect, and fixing §2.3/§2.1
      without this ships a new one.** Verified 2026-08-16: **no receive loop in either
      `Verbara.Sdk.VoiceAi.Tts` or `Verbara.Sdk.VoiceAi.Stt` reads `result.EndOfMessage`** — zero
      occurrences. This is not an unknown pattern in the codebase: `AriClient.cs:165`,
      `AriOutboundListener.cs:246`, `WebSocketAudioSession.cs:74` and `OpenAiRealtimeBridge.cs:204`
      all loop `while (!result.EndOfMessage)` correctly. The VoiceAi packages are the inconsistent
      ones. §5.7 recorded this as a *margin* — true while audio arrived as binary frames sized by the
      client (Deepgram: 1920 B against a 64 KiB buffer, 34× headroom). **That margin does not transfer
      to text frames.** ElevenLabs returned ~115 KB of base64 across 4 frames — ~29 KB average, sized
      by the vendor, not by us — and one frame over the 65 536-byte buffer arrives fragmented, at
      which point the loop parses a fragment as if it were whole JSON and the caller gets either a
      `JsonException` or a silently dropped audio chunk. It is **length-dependent**, so the short
      probe sentence used throughout this change cannot trip it and a green suite will not either.
      **Closes when** both Class B loops assemble until `EndOfMessage` before parsing, with a fake
      test that deliberately splits a text frame across two receives, plus one long-input live run per
      Class B provider to observe whether the vendor fragments in practice. Must land **inside** the
      §2.3/§2.1 commits — shipping the frame-type fix without it is shipping a new defect.
      **Half done, and the halves are named so neither is claimed by the other.** Landed in the
      §2.3 and §2.1 commits: both Class B loops now assemble with `ArrayBufferWriter<byte>` until
      `EndOfMessage`, and both fakes gained a `TextFrameFragmentBytes` knob with a test that splits a
      text frame across reads at 16 bytes — a mutation check confirms each guard fails when the
      assembly is removed (§2.12, §2.13). **Still open:** the long-input live run per provider. That
      conjunct asks whether the vendor fragments *in practice*, which no fake can answer, so this
      task stays unticked until it is run
      **Closed 2026-08-18 — the live run was made and the answer is worse than the task assumed: one
      of the two vendors fragments, and it was already fragmenting on the short probe sentence.**
      Message size is the exact quantity that decides this, because a message larger than the
      65 536-byte receive buffer arrives across more than one `ReceiveAsync` with `EndOfMessage`
      false. Measured per message, no audio retained:

      | provider | input | messages | largest message | over 64 KiB |
      |---|---|---|---|---|
      | ElevenLabs | 44 B (the probe sentence) | 4 | **75 015 B** | **1** |
      | ElevenLabs | 2 085 B | 76 | **293 720 B** | **58** |
      | Cartesia | 44 B | 17 | 8 681 B | 0 |
      | Cartesia | 2 085 B | 559 | 8 681 B | 0 |

      **What this changes about the finding's own framing.** This task, and §5.7 before it, described
      the exposure as length-dependent and unreachable by the short probe sentence. That is false for
      ElevenLabs: the 44-byte sentence already produced a **75 015-byte** message, 1.14× the buffer.
      The earlier observation that recorded this surface as "~115 KB across 4 frames, ~29 KB average"
      is where it hid — the average was reported and the maximum was not, and one of those four frames
      was over the buffer the whole time. **An average concealed a threshold crossing**, which is the
      transferable lesson.
      **Cartesia caps its messages** at 8 681 B and never approaches the buffer, across 559 messages
      on the long input — so the two Class B surfaces are genuinely different and neither one's
      behaviour could have been inferred from the other. This is also why the fix that landed in §2.3
      and §2.1 was repairing a **live** defect on ElevenLabs rather than closing a margin.
- [x] 2.3c **The fake seam bypasses the credential entirely, at six sites — so no fake can catch an
      auth defect.** Every WebSocket client gates its auth header behind `if (_fakeServerPort is
      null)`, meaning under test the header is never set and the fake never sees one. Verified
      2026-08-16, and it is not the two sites review first found — it is six:
      `ElevenLabsSpeechSynthesizer.cs:49`, `CartesiaSpeechSynthesizer.cs:45`,
      `DeepgramSpeechSynthesizer.cs:68`, `AssemblyAiSpeechRecognizer.cs:52`,
      `CartesiaSpeechRecognizer.cs:51`, `DeepgramSpeechRecognizer.cs:46`. This is the same shape as
      §3.10's LMNT finding — a seam that takes over more of the request than it should, so the part it
      replaces is never exercised — and it is the structural reason Speechmatics STT could ship a
      credential defect past a green suite (§4.1: "its fake never checked the credential at all").
      **Fix:** reshape each seam to substitute the **origin only**, letting headers, query and route
      flow through shipped code, and have each fake assert the auth header and scheme arrived.
      **Closes when** all six are reshaped and each fake carries a credential assertion.
      **Done 2026-08-17, and the cost of the seam was measured before it was removed.** Control A:
      with every auth header in the six clients renamed to a header no vendor reads, the two suites
      returned **187 passed / 0 failed** — so `Authorization`, the exact defect §4.1 says shipped past
      a green suite, was invisible to every test. Control B, same mutation after the fix: **six
      failures, one per client** (`SynthesizeAsync_ShouldAuthenticateTheUpgrade_WhenOpeningASession`
      for Cartesia and ElevenLabs TTS, `…WithTheTokenScheme…` for both Deepgram surfaces,
      `…TheUpgrade…` for Cartesia STT, `…WithTheRawKey…` for AssemblyAI). That delta is the evidence,
      not the green suite. **The fix is the repo's own precedent, not a new invention:**
      `SpeechmaticsSpeechRecognizer` already had no test-only constructor — tests reach its fake by
      setting `BaseUri`, the seam an operator uses for a regional endpoint — so all six were converted
      to that shape and the `fakeServerPort` overloads are gone. **Two findings the task text did not
      predict.** The seam also replaced the *query*, and the two copies had already drifted: Deepgram
      STT's under-test copy omitted `model` and `language` entirely, so the suite watched a request
      production never sends, and ElevenLabs hard-coded `test-voice` over `_options.VoiceId`. Both are
      now one expression, with `StreamAsync_ShouldSendModelAndLanguage_WhenOpeningASession` (non-default
      `nova-3`/`pt`) and `SynthesizeAsync_ShouldPutTheConfiguredVoiceInTheRoute_WhenVoiceIdIsSet`
      asserting what was previously unassertable. **Scope decision, taken deliberately:** ElevenLabs
      TTS and Deepgram STT had no `BaseUri` at all, so the route could not flow through shipped code
      without adding one. Both gained a public `BaseUri` with the `^wss?://` validation the other
      providers carry — a real operator gap either way, since neither client could be pointed at a
      regional or self-hosted endpoint. **Deferred with a reason:** the vendor-specific *rejection*
      shape (Speechmatics answers a bad key with 101 then `4001 not_authorised`, which
      `SpeechmaticsFakeServer` deferred here) is not extended to the other five — that behaviour differs
      per vendor and there is no live evidence of each one's shape, so inventing five is worse than
      asserting none. LMNT stays with §3.10, which owns its two internal test constructors. Suites
      after the change: Tts 91 → **95**, Stt 96 → **100**
- [x] 2.4 ElevenLabs has **no server-message DTO at all** — `VoiceAiTtsJsonContext.cs` declares only the
      outbound `ElevenLabsTextChunk` / `ElevenLabsVoiceSettings`. Add a server DTO for the audio field
      and register it. Alignment members are optional: model them or ignore them, but tolerate them —
      the unmapped-member tolerance rule belongs to `provider-dto-robustness-fences` and must not be
      contradicted here
- [x] 2.5 Decide, and record, whether the binary branch stays. Neither vendor documents a raw-binary
      mode (both read first-hand 2026-08-14), but a vendor not mentioning a mode is not evidence the
      mode does not exist — so keeping the branch as *tolerated without evidence* costs nothing and
      removing it could break an undocumented path. State which was chosen and on what basis.
      **Evidence added 2026-08-16, and it does not settle the question — it sharpens it.** Both
      providers were measured emitting **zero** binary frames on a successful synthesis (ElevenLabs
      §2.3a: 0 B binary / 4 text frames; Cartesia §2.1a: 7 text chunks + `done`, no binary). That
      confirms the branch is dead on the default configuration; it is still not evidence that no
      configuration reaches it, which is the same *absence-of-mention* trap this task was written to
      avoid. Recommendation unchanged: keep the branch, and record it as tolerated-without-evidence
      rather than justified — now with the measurement attached
- [x] 2.6 `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/elevenlabs-tts/audio-output-frame.json`
      **already carries** the base64 `audio` field plus the `alignment` / `normalizedAlignment`
      structure. It is committed evidence of a shape the shipped client cannot consume. Wire it into the
      test rather than authoring a new fixture — a fixture that already exists and is already unusable
      is the strongest available proof of the defect
- [x] 2.7 `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/cartesia-tts/audio-chunk-pcm-s16le-8khz.provenance.json`
      records the divergence in its own notes and defers it: *"this fixture is seeded as binary frames
      because that is what the client under test consumes … the divergence needs its own change, not a
      silent fixture edit."* **This is that change.** Re-seed the fixture to the documented text-frame
      shape and update the sidecar's `notes` and `source_schema.method` to say so; the `.raw` bytes
      stay `SyntheticPcm.Triangle`-generated and byte-asserted
- [x] 2.8 `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Cartesia/CartesiaFakeServer.cs` and
      `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/ElevenLabs/ElevenLabsFakeServer.cs` must send what the vendor
      documents, not what the client currently reads. Scope discipline: `websocket-fake-protocol-contract`
      owns the fake-protocol contract in general and forbids production changes — the fake edits here
      are only those the production fix in 2.1 / 2.3 requires. Do not widen into that change's scope
- [x] 2.9 A regression test per provider, `Method_ShouldExpected_WhenCondition`: a normal synthesis
      yields non-zero audio. This is the assertion the current suites do not make, which is why a
      synthesizer that produces nothing passes today
- [x] 2.10 The silent-completion **signal**, which does not fall out of the frame-type fix in 2.1 and is
      a separate decision: `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs` reaches
      the vendor's `done` terminator having written zero bytes and completes normally. Make that outcome
      observable to the caller **and counted**. Two things to decide and record: (a) the observable
      form — a throw at stream completion versus a typed empty-result signal — on an
      `IAsyncEnumerable` surface where the caller may already be enumerating; (b) where the count lives,
      because `src/Verbara.Sdk.VoiceAi.Tts/Diagnostics/` holds only `TtsHealthCheck.cs` — but the
      instrument already exists one package over: `src/Verbara.Sdk.VoiceAi/Diagnostics/SpeechSynthesisMetrics.cs:13`
      declares `Meter("Verbara.Sdk.VoiceAi.Tts", "1.0.0")` and already carries
      `SynthesesCompleted` / `SynthesesFailed`. So this is a **new counter on an existing Meter**, and
      the real decision is narrower and sharper than "where does the count live": a zero-byte synthesis
      today increments `SynthesesCompleted`, and D2 says it must not. Decide whether it moves to
      `SynthesesFailed` or gets its own counter, and note that changing which counter fires is an
      observable change for anyone already listening on that Meter name. A request whose input legitimately warrants no audio must stay distinguishable —
      name the discriminator explicitly (frames arrived and were discarded, versus no frames arrived).
      This is the task the spec's zero-audio requirement is implemented by; without it the requirement
      has no code behind it.
      **Settled by `Sdk/ADR-0050` and shipped.** (a) The observable form is a **throw**, from the
      background receive loop into the caller's `MoveNextAsync` via `channel.Writer.TryComplete(ex)` —
      two types, `SpeechProviderFailureException` when the vendor reported a failure and
      `SpeechProviderEmptyResultException` when the session ended clean and empty, both rooted at
      `System.Exception` because the core package has no exception base of its own (E1/E3/E4). (b) Neither
      of the two counters this task offered: `SynthesesCompleted` and `SynthesesFailed` both keep firing
      as they do, and once the clients throw, `SynthesesFailed` absorbs every provider failure — which
      changes what that counter means for anyone already listening, unavoidably and correctly, because it
      was reporting failed sessions as successful. A **third**, additive counter
      (`tts.syntheses.silent`, tagged `voiceai.provider`) covers only the residual the clients cannot
      reach: an implementation of the public base that returns silence without raising (E9). (c) The
      discriminator this task asked to be named explicitly turned out to be **unimplementable as
      stated** once the throw is in place — a client that throws on a discarded frame never reaches the
      zero-audio check, so "frames arrived and were discarded" is no longer a state that can be reported
      from there. `Sdk/ADR-0050` E8 records the substitution rather than reinterpreting D2 silently: the
      operative test is *did the vendor report a failure* → the first type, *did the session end clean
      and empty uncancelled* → the second, *was it cancelled* → neither (E6)
- [x] 2.10a The signal from 2.10 is **not Cartesia-only — measured, not assumed.** The open question
      this task originally carried was answered by the 2026-08-15 probe: ElevenLabs emits only text
      frames (`{alignment, audio, isFinal, normalizedAlignment}`, audio base64) and then closes
      **`1000` normal**. `src/Verbara.Sdk.VoiceAi.Tts/ElevenLabs/ElevenLabsSpeechSynthesizer.cs` reads
      only `WebSocketMessageType.Binary`, so it completes **successfully with zero bytes**, exactly as
      Cartesia does. Both synthesizers get the same observable form and the same counter — there is no
      longer a provider for which the spec's zero-audio requirement is satisfied by luck.
      Additionally, and worse than first recorded: ElevenLabs sends its **auth error** as text too
      (`{code, error, message}`), so a bad credential loses the audio and the reason in the same
      branch. Fixing the frame type in §2.1 fixes both, but assert them as two separate tests — a
      normal synthesis yields audio, and a rejected credential surfaces an error.
      **Shipped under `Sdk/ADR-0050`, and it went wider than "not Cartesia-only":** all four TTS
      surfaces and all four STT surfaces got the same three doors closed and the same two types, because
      the audit found the shape at eight of eight clients rather than at the two this task names. The two
      separate tests exist per surface (`…ShouldThrowErrorFrameFailure_When…` and
      `…ShouldThrowEmptyResult_When…`), and on the recognition side the second one is deliberately *not*
      symmetric: zero transcripts is a healthy session there, zero **messages** is not (E5), so each STT
      surface also carries a lifecycle-only test asserting silence stays silent. Two door-1 branches are
      closed **without** a measured frame shape and say so in the code — Deepgram TTS and Deepgram STT,
      because §1.3a measured this vendor rejecting a bad credential with `HTTP 401` at the upgrade on both
      surfaces, so no live session can produce the in-band frame those branches catch
- [x] 2.11 Cartesia and ElevenLabs land as **two separate commits**. Cartesia additionally reaches its
      `done` terminator and completes successfully with zero audio — call that out in its commit body,
      because it is the silent-failure case and it is the reason this section is first
- [x] 2.12 **Assemble to `EndOfMessage` before parsing — on both providers, and not optional.** Neither
      receive loop did: each parsed one `ReceiveAsync` result as if it were a whole message. The
      vendor sizes these frames, not the client — ElevenLabs' measured run averaged ~29 KB of base64
      per frame against a 64 KiB buffer, and Cartesia's carried 32 694 B across seven — so a long
      enough input fragments and the parser is handed a truncated document. Length-dependent, which is
      why no fixture in either suite ever reached it. Fixing only the frame type would have *created*
      this defect where none was reachable before, so it lands in the same commits. Both fakes gained
      a `TextFrameFragmentBytes` knob so the failure is reachable without a 64 KB fixture
- [x] 2.13 **Non-vacuity, by mutation rather than inspection.** Cartesia: restoring the half-close
      fails 1 test; ignoring `EndOfMessage` fails 1; removing the `chunk` decode fails 5; sending an
      empty `context_id` fails 2. ElevenLabs: restoring the half-close fails 1; ignoring
      `EndOfMessage` fails 1; reverting to binary-only receive fails 5. Every mutation reverted and
      both suites green afterwards — 91/91 in `Verbara.Sdk.VoiceAi.Tts.Tests`
- [x] 2.14 **What §2.1's closing evidence is, and what it is not.** Its two conjuncts are met: a
      synthesis over the measured text-frame shape yields non-zero audio (the fake replays
      `chunk-frame.json` verbatim, unmodelled fields included), and the live frame inventory from
      §2.1a matches what the loop now consumes. **Not** done, and deliberately not claimed: the
      shipped client itself was never run against the live endpoint after the fix. What ran live was
      a probe reproducing the corrected request — `context_id` added, half-close dropped — which is
      the same wire behaviour this client now produces, but it is a reconstruction and not the
      artifact. §5.5 records Cartesia TTS accordingly and §7.7 applies
- [x] 2.15 **Drift caused into a neighbouring open change — recorded here, not fixed here.**
      `openspec/changes/provider-dto-robustness-fences/proposal.md` counts response members by name
      and states *"Tts contributes exactly one response member (`CartesiaTtsControlMessage.Type`)"*.
      That type no longer exists: §2.2 replaced it with `CartesiaTtsServerMessage`, which contributes
      one non-nullable response member and four nullable ones, and §2.4 added
      `ElevenLabsAudioOutput` with two more. Its **24** is now wrong in both the name and the
      number. Editing another change's proposal from this one is the scope-widening §2.8 forbids, so
      the correction belongs to whoever next picks that change up — it must **re-run its inventory**
      rather than adjust the figure by hand, because this commit is unlikely to be the only source
      of drift since it was counted.
      **Closed 2026-08-17.** Recorded, not fixed, and every claim above re-verified against the tree
      first rather than trusted from when the task was written: `CartesiaTtsControlMessage` is gone —
      the name survives only in a doc comment on its replacement — and `CartesiaTtsServerMessage`
      declares exactly one non-nullable response member (`Type`) plus four nullable ones (`Data`,
      `Error`, `Done`, `StatusCode`); `ElevenLabsAudioOutput` adds two nullable ones (`Audio`,
      `IsFinal`). The neighbour's `proposal.md` still reads **24** with *"Tts 1"* and still names the
      deleted type, so both the name and the number are stale, as stated. Deliberately **not** corrected
      to a new figure: publishing a hand-adjusted count would present a number nobody inventoried as if
      someone had, and this change is unlikely to be the only drift since that census — a re-run is the
      only honest remedy, and it belongs to that change. `openspec/changes/provider-dto-robustness-fences/`
      is untouched by this change, which is the §2.8 boundary holding rather than being waived

## 3. Class A — the request never reaches the vendor (LMNT HTTP, Speechmatics TTS)

- [x] 3.1 `src/Verbara.Sdk.VoiceAi.Tts/Speechmatics/SpeechmaticsSpeechSynthesizer.cs` — the request is
      built from `_options.BaseUri` with the voice carried in the JSON body
      (`SpeechmaticsTtsRequest.Voice`). The vendor selects the voice by **path segment**:
      `/generate/{voice}` returns `200 audio/wav`, `/generate` returns `404`. **Closes when** the
      corrected route returns a success status against the live endpoint with the negative control still
      `404` on the same host
- [x] 3.2 The public-API decision, taken explicitly and not smuggled in under a route fix:
      `SpeechmaticsOptions.BaseUri` (`src/Verbara.Sdk.VoiceAi.Tts/Speechmatics/SpeechmaticsOptions.cs`
      line 27) is **public** and defaults to the whole URL including `/generate`, so appending a voice
      segment to a caller-supplied value changes what a shipped property means. Enumerate at least:
      (a) redefine `BaseUri` as an origin/prefix and append `/{voice}`; (b) append the segment to
      whatever the caller supplies; (c) introduce a new option and obsolete `BaseUri`. Record the choice,
      the rejected alternatives by name, and the consequence for a caller who already sets it
- [x] 3.3 Everything else the client sends is already correct — bearer auth, content type, sample rate.
      Whether `voice` should *also* remain in the body, and whether the `language` and `sample_rate`
      body fields are accepted as sent, are **not verified**: only the route was isolated. Record them as
      not verified; do not resolve them by inference
      **Half of this is now measured — 2026-08-17, and it stays open for the other half.** The
      `speechmatics-tts` fixture capture for `wiremock-http-provider-substrate` §4.5 sent the shipped
      defaults' whole body — `text`, `language` **and** `sample_rate` — to `/generate/eleanor` and got
      **200 `audio/wav`, 72 236 bytes**. So the two body fields are accepted as sent; that clause is
      answered by observation, not inference. What is still unmeasured is the `voice`-in-body question,
      and the fix made it harder to reach rather than easier: the client no longer sends `voice` at all,
      so which one wins when path and body disagree cannot be observed without deliberately
      reintroducing the conflict in a probe. Until someone does, that is unknown, not fine
      **Closed 2026-08-18 — the path wins and the body `voice` field is ignored, established in both
      directions.** The conflict was reintroduced deliberately in a scratchpad probe, as this task
      required. Getting an answer took three attempts, and the first two refuted their own instrument:
      (1) **byte identity is not a discriminator on this route** — the same request sent twice returned
      the same byte count and different SHA-256, so the synthesis is not reproducible bit-for-bit;
      (2) **byte length is a discriminator, but only after the noise floor is measured** — a first pass
      compared single samples per arm and produced an incoherent verdict, because lengths move in exact
      multiples of 1536 B (768 samples, 48 ms at 16 kHz) and the within-voice spread reaches 4 608 B.
      Six samples per arm, with the within-voice range measured first, made the ranges disjoint:
      `eleanor` [84 524, 89 132] vs `sarah` [75 308, 76 844]. Then both directions agree — path
      `eleanor` + body `sarah` landed 5/6 in `eleanor`'s range and **0/6** in `sarah`'s; path `sarah` +
      body `eleanor` landed 4/6 in `sarah`'s range and **0/6** in `eleanor`'s. The stray samples sit one
      1536-B chunk outside a range estimated from six draws; **no sample ever crossed to the opposite
      voice**, which is the statistic that carries the claim. So the client dropping `voice` from the
      body in §3.1 removed a field the vendor was never reading. No audio bytes were retained.
- [x] 3.4 The competing hypothesis is closed and must be recorded as closed so it is not reopened: the
      shipped default voice `eleanor` is absent from the vendor's published four-voice list **but
      returns 200**, so the published list is incomplete and `SpeechmaticsOptions.Voice` is fine. One
      delta, not three. Do not change the default
      **RETRACTION (2026-08-18) — this task's premise is refuted, so the record it asks for must not be
      written.** The inference was: `eleanor` is absent from the vendor's published four-voice list *but
      returns 200*, therefore the list is incomplete and the default is fine. The middle step does not
      hold, because **the route returns `200 audio/wav` for every voice segment tried**, including
      `does-not-exist` and `zzzzzzzz`. A 200 on this route carries no information about whether a voice
      exists, so it could never have supported the conclusion — the check that was run was not a check.
      What the probe found instead, on 2026-08-18:
      - There is an authoritative listing, **`GET /voices`**, credential-gated (`401` unauthenticated).
        For the account used here it returns exactly one voice, and it is **not** `eleanor`.
      - Output size ranges separate `sarah` from the rest, and `eleanor`'s range coincides with both the
        one listed voice's and `does-not-exist`'s. The economical reading is that an unrecognised
        segment falls back to the account's entitled voice, and that `eleanor` is taking that path.
      - `eleanor` therefore appears in **no** source available to us: not the vendor's published list,
        not the account listing. It has zero evidence behind it, where the listed voice has two.
      **Consequence, and why the code is not changed here.** A wrong `Voice` fails *silently*: the caller
      gets 200 and audio in some other voice and is never told. That is the same silent-failure class
      ADR-0050 addresses. But which value to default to depends on account entitlement, and one
      account's `/voices` is not grounds for changing a shipped public-API default. Recorded as a live
      decision for the operator rather than resolved unilaterally; the route-level negative control is
      unaffected and still fails correctly (`/generatex/{voice}`, `/generate` and `/generate/` all
      `404`). **Do not** rewrite the `speechmatics-tts` fixture provenance: it records the voice that
      actually produced those bytes, which remains true.
- [x] 3.5 `src/Verbara.Sdk.VoiceAi.Tts/Speechmatics/SpeechmaticsOptions.cs` line 23 — the
      `<see href="https://docs.speechmatics.com/tts-api-ref"/>` is a **dead link (404)**. Replace it
      with a live URL or remove the `href`; XML docs ship to consumers of a public MIT package, so a
      dead reference is a shipped defect, not a cosmetic one
- [x] 3.6 `src/Verbara.Sdk.VoiceAi.Tts/Lmnt/LmntSpeechSynthesizer.cs` line 294 hardcodes
      `https://api.lmnt.com/v1/ai/speech/generate` — there is no option for it — and posts
      `FormUrlEncodedContent`. That returns `404`. A controlled comparison with the same credential
      seconds apart got `200 audio/mpeg` from the documented `/v1/ai/speech/bytes` with a **JSON** body.
      **Three deltas: path, body encoding, response media type.** Fix all three or state which was
      deferred and why.
      **Done 2026-08-15 — but "three deltas" was wrong, and the probe is what says so.** A form-encoded
      body posted to `/v1/ai/speech/bytes` returns `200` with a payload byte-identical to the JSON one,
      so **body encoding was never a delta**; it was inferred from the vendor documenting JSON, which is
      evidence about the docs, not about the endpoint. Two real deltas: route and format (§3.7). The
      route moved to `/v1/ai/speech/bytes`; the form encoding is **deliberately kept**, because swapping
      it would be an unmeasured change riding along with a measured fix. §3.8 is closed as unnecessary
      on that basis, not deferred
- [x] 3.6a **The default transport is broken too, and nothing in this change predicted it.** §3.6–§3.11
      were written on the premise that only the HTTP fallback was defective. Probing the WebSocket
      surface — done only because §3.7 forced the question "what does `raw` mean *here*" — showed the
      init message serializes `"model": null` whenever `LmntTtsOptions.Model` is unset, which is the
      default. The endpoint validates `model` against a literal set, rejects an explicit null, and
      closes `1002 protocol error` having sent **zero audio frames**. Fixed by
      `[JsonIgnore(Condition = WhenWritingNull)]` on `LmntInitMessage.Model`, with two regression tests
      (field absent when unset, present when set) and a mutation check confirming the guard fails
      without the attribute. **How it hid:** `LmntWsFakeServer` records the init message and then
      replies with audio regardless of what it says, so the suite asserted the message was *sent*, never
      that it was *acceptable* — the §3.12 property, one level further in than §3.12 states it
- [x] 3.6b **The error frame that says why is thrown away — not fixed here, and it is `Sdk/ADR-0049`
      D1 measured on a sixth surface.** `LmntSpeechSynthesizer.ReceiveWsFramesAsync` terminates on
      `notification.Error == "error"`, comparing an error *message* against the literal string
      `"error"`, which no real message equals. Both live failures — `{"error":"model: Input should be
      'aurora', …"}` from §3.6a and `{"error":"Invalid API key"}` from the invalid-credential control —
      therefore fall through, the socket then closes `1002`, `catch (WebSocketException) { break; }`
      swallows it, and the caller gets an **empty stream and no exception**. That is D1 (silent discard
      of a failure frame) and D2 (zero output as success) together, with a D4 control, on the LMNT WS
      surface. Fixing it changes behaviour — synthesis that silently yields nothing would start
      throwing — so it belongs with the ADR-0049 train and its D1 remedy, not inside a route fix.
      **Closes when** ADR-0049's D1 remedy covers this site, or a decision records why it does not.
      **Closed the first way: `Sdk/ADR-0050` is that remedy and it covers this site.** All three doors
      this task describes are now shut on the LMNT WS surface — the `== "error"` comparison is gone in
      favour of the error frame being carried out as `SpeechProviderFailureException` with the vendor's
      own text, the `1002` close code is read instead of discarded, and
      `catch (WebSocketException) { break; }` became a throw. The measured frames from §3.6a and from the
      invalid-credential control are what the new tests send. One behaviour of this surface is
      deliberately left alone and recorded rather than fixed: on the **HTTP** transport a non-2xx status
      still surfaces as `HttpRequestException` rather than a `SpeechProvider*` type. There is no measured
      defect behind that, and retyping it would be a second behavioural break riding along with this one
- [x] 3.6c **A third total defect on the same transport, found only because the fix for the second one
      was audited against the client instead of against the probe.** §3.6a was verified with a probe
      that reproduced the init message but *not* the client's close sequence, so it proved the init
      message was acceptable and nothing more. `grep CloseOutputAsync src/` then showed
      `SendWsRequestAsync` half-closes the socket immediately after `eof` — a step the probe never
      made. Re-probed with that step as the only variable, 2026-08-15: **A** (init/text/flush/eof +
      `CloseOutputAsync`, exactly what shipped) → **0 binary bytes, 0 text frames**, receive loop ends
      `ConnectionClosedPrematurely`; **B** (identical, half-close removed) → **30 688 B = 0.959 s of
      16 kHz PCM**, server closes `NormalClosure` itself. The vendor reads the client's Close frame as
      "abandon the request". `eof` is already the end-of-input signal; the half-close was a second,
      contradictory one. Removed, with a regression guard and a mutation check (restoring the call
      fails the guard). **Two consequences worth keeping.** (a) The verification rule tightens: a probe
      that reproduces the *message* is not a probe of the *client* — it must reproduce the whole
      sequence, close included, or it certifies only the part it copied. (b) The §3.12 property again,
      and the fake could not carry it: `LmntWsFakeServer` cannot reproduce the vendor's reaction
      without racing its own send, so it records `ClientSentCloseFrame` and the test asserts on what
      the client *sent*. Reading it after the stream completes is ordered by causality, not luck —
      the stream cannot complete until the server closes, the server closes only after sending audio,
      and a client Close necessarily precedes that audio. **The same defect is measured on Cartesia
      (§2) and unmeasured on ElevenLabs TTS and four STT clients — see §3.6d**
- [x] 3.6d **The half-close is a class, not an LMNT bug, and six sites are unmeasured.**
      `grep -rn CloseOutputAsync src/Verbara.Sdk.VoiceAi.Tts src/Verbara.Sdk.VoiceAi.Stt` returns, besides
      the three now measured (LMNT §3.6c **fixed**, Cartesia TTS §2.1a and ElevenLabs §2.3a
      measured-not-fixed): `DeepgramSpeechRecognizer`, `SpeechmaticsSpeechRecognizer`,
      `AssemblyAiSpeechRecognizer`, `CartesiaSpeechRecognizer`. **Three of three measured sites were
      total** — zero bytes, no exception — so the base rate here is not "occasionally harmful". The
      four remaining are *not characterised*: that is a statement about the evidence, not a prediction
      of breakage. **A correction to this task's own first draft:** it said §1 recorded ElevenLabs as
      *measured-good*. §1 records its **route** as good and its **frame** as broken; what was
      unmeasured was the close sequence. The overstatement did not change the conclusion — the probe
      ran and found the defect — but the row it misquoted is the kind of thing this change exists to
      keep honest. **Closes when** each remaining site has an A/B run with the half-close as the only
      variable, or a decision records why a site is exempt.
      **Two corrections to this task, both found by review on 2026-08-16 and both changing what the
      work is.** (i) *"Speechmatics is blocked until §4.1 lands"* — **false**. §4.1's row B already
      measured that `Authorization: Bearer` reaches `RecognitionStarted` with the same credential, so
      the close sequence is measurable **today** through that channel. What waits on the §4.1 code fix
      is only the *shipped-path* close-out row (§7.5); record the result as measured-on-row-B and do
      not let it stand as shipped-path evidence. (ii) **The A/B design named here is the wrong
      experiment for STT, and would have produced a confident wrong answer.** In all three TTS sites
      the vendor had an in-band end-of-input signal (LMNT `eof`, ElevenLabs empty-text chunk, Cartesia
      the request itself) and the half-close was a redundant, contradictory *second* signal — which is
      why removing it was the whole fix. In all four STT clients the bare `CloseOutputAsync` is the
      **only** end-of-input signal (verified 2026-08-16: `DeepgramSpeechRecognizer.cs:92`,
      `AssemblyAiSpeechRecognizer.cs:103`, `SpeechmaticsSpeechRecognizer.cs:122`,
      `CartesiaSpeechRecognizer.cs:130` each stream binary audio and then close, with no `CloseStream`,
      `Terminate`, `EndOfStream`+`last_seq_no` or `finalize` message anywhere). An arm that only
      removes it leaves a session with no end signal at all and measures a **hang**, not the defect.
      The experiment is therefore **three arms**: A = shipped (audio → bare half-close); B = audio →
      the vendor's in-band terminator, no half-close; C = terminator + half-close. And the expected
      failure mode is not silence: STT streams partials during the session, so what breaks is
      **truncated or missing finals**. The 3-of-3 total-failure base rate from TTS **does not
      transfer** — it must be measured, not carried over.
      **Ran 2026-08-16, all four STT sites, three arms plus a known-wrong control, three repetitions
      each, one utterance synthesized once and replayed byte-identically into every arm.** Metric: how
      many of ten spoken numbers survive into the *final* transcripts. Every cell was identical across
      its three repetitions.

      | Surface | A shipped | B terminator | C both | Z control |
      |---|---|---|---|---|
      | Deepgram STT | **10/10** | 10/10 | 10/10 | 8/10 |
      | Speechmatics STT | **0/10**, zero finals | 10/10 | **0/10** | 0/10 |
      | AssemblyAI STT | **0/10**, zero finals | 10/10 | **0/10** | 0/10 |
      | Cartesia STT (via corrected URL) | **5/10** | 7/10 | 7/10 | 5/10 |

      Four results, three of which contradict something this task or the code assumed:
      (a) **two surfaces lose the transcript entirely** — Speechmatics and AssemblyAI stream partials
      and then answer the close frame with no final at all (20 partials, zero `AddTranscript`, no
      `EndOfTranscript`), where the terminator yields the complete final from the same audio. The
      predicted failure mode was *truncated* finals; the measured one is *no* finals, which is worse
      and is invisible to any caller that treats an empty result set as "the user said nothing";
      (b) **arm C fails** — the obvious remedy of keeping the half-close and adding the terminator is
      `C ≡ A` on both broken surfaces. The half-close is not a redundant second signal to supplement,
      it is the thing to remove. Only Cartesia has `C ≡ B`;
      (c) **Deepgram is exempt** and the exemption is earned rather than assumed: `A ≡ B ≡ C` with the
      control at 8/10, so the instrument demonstrably detects a lost tail. Without arm Z, three
      identical rows would be indistinguishable from a blind probe;
      (d) the TTS **3-of-3 total-failure base rate did not transfer** — the range runs from no effect
      to total loss. This task said not to carry it over; carrying it over would have produced a
      confident wrong answer about Deepgram.
      Two instrument corrections, recorded because both were nearly wrong: the metric first matched
      digit *words* and scored a **complete** Speechmatics transcript 1/10, because that vendor applies
      inverse text normalization and returns `"123456789 ten."` — it was measuring the vendor's
      formatting, not its behaviour; and arm A was re-run as **A2**, sending the identical close frame
      without awaiting the peer's close so the reader kept consuming, giving `A2 ≡ A` 3-of-3 on both
      broken surfaces and ruling out "the probe's client library dropped a queued final"
- [x] 3.6e **Remediate the half-close across the four STT clients — and the shape of the fix is what
      §3.6d measured, not what it predicted.** Each of `DeepgramSpeechRecognizer.cs:92`,
      `AssemblyAiSpeechRecognizer.cs:103`, `SpeechmaticsSpeechRecognizer.cs:120` and
      `CartesiaSpeechRecognizer.cs:130` ends input with a bare `CloseOutputAsync`. Replace it with the
      vendor's in-band terminator and **remove** the half-close on the two surfaces where `C ≡ A`
      proves it destructive; Deepgram may keep either, and the decision should say which and why
      rather than leaving a measured-equivalent site to look unexamined. The client must then keep
      reading until the vendor's own end-of-session message (`EndOfTranscript`, `Termination`, the
      Cartesia `done` echo) rather than until the socket dies — which is a second change to the
      receive loops, and it is what makes the terminator arrive in time to matter. **Closes when** all
      four are remediated, each with a fake asserting the terminator is sent and the half-close is
      not, and a re-probe of the shipped path showing the finals arrive. Note the ordering
      constraint: on Speechmatics and AssemblyAI this cannot be verified by any fake alone, because a
      fake that ends the session on a close frame is asserting the current defect as the contract.
      **Done 2026-08-16.** All four now send the terminator as a text frame and leave the output side
      open — arm B exactly, nothing added that no arm measured (a drain deadline, breaking on the
      vendor's end-of-session message, and a closing handshake were each considered and left out).
      Deepgram is remediated with the rest rather than kept as the one measured-equivalent site whose
      difference a later reader would have to re-derive. **The predicted second change to the receive
      loops was not needed**: all four already looped while the socket was `Open` *or* `CloseSent`, so
      the loops never were the reason a final arrived too late; only the meaning of the unchanged
      close-frame `break` changed, and the comments say so. Cartesia's `CloseOutputAsync` timeout is
      gone with the call it guarded. Eight fakes-backed tests (two per provider: the terminator is
      sent, no close frame follows) — and the **first version of them was blind**: the fakes stopped
      reading at the terminator, so a client that half-closed still passed. Fixed with a
      `WebSocketTestServer.SessionCompleted` join point plus `or CloseSent` in the fake receive loops,
      then re-verified by injecting arm A and arm C into all four clients: 8 destructive arms, 8
      detections (A → 2 failures per provider, C → 1). Suite 3080 tests green. **Live re-probe of the
      shipped clients**, same digits utterance re-synthesized, 100 ms chunks, real-time paced, with
      arm A restored in the same source files through the same harness minutes later as the control:
      Speechmatics 0/10 → **10/10** (zero finals → one), AssemblyAI 0/10 → **10/10** (zero finals →
      one), Deepgram 10/10 unchanged as §3.6d predicted. **Cartesia's live verification is deferred to
      §3.17** — the shipped client cannot open a session at all, so there is no wire to measure the fix
      on; its half-close fix rests on the fake alone until then, and the way it fails (zero output,
      `error=none`, dead in 0.5 s) is the D1 silent-failure class seen end-to-end through a shipped
      client for the first time rather than through a probe.
      **That deferral is discharged**: §3.17 fixed the query string the same day and the shipped
      Cartesia client then recovered 10/10 in a single final, so the fourth arm rests on the wire and
      not on a fake. One asymmetry stays on the record rather than being smoothed over — the other
      three carry a half-close-restored control run minutes later, and Cartesia has none, because
      restoring arm A here would restore it on top of a session that did not exist when arms A/B/C
      were measured. What §3.6d recorded for this surface (`A` 5/10, `B` 7/10, `C ≡ B`) came from a
      probe already holding the corrected URL; the shipped client has only ever run arm B
- [x] 3.6f **The terminator path has no bound, and §3.6e is what removed the one it had.** With the
      half-close gone, no client under `src/Verbara.Sdk.VoiceAi.Stt/` sends a close frame at all, and
      each `ReceiveLoopAsync` exits only on the vendor's close frame, a `WebSocketException`, or the
      caller's token — so a vendor that acknowledges the terminator and keeps the session open wedges
      `StreamAsync` indefinitely. Measured A/B against a fake that answers the terminator and never
      closes: at `a2a925f8^` `StreamAsync` returned in 29 ms because the half-close obliged the peer
      to echo a close; at `a2a925f8` it never returned. The wait itself is not new — the old code also
      sat in `ReceiveAsync` waiting on a peer that might never answer — but its backing is weaker:
      RFC 6455 §5.5.1 *obliges* the close echo, nothing obliges a vendor to end a session. Three of
      the four surfaces were measured ending it (the re-probe returned in 8–13 s); **Cartesia was
      not**, and Cartesia is precisely where a sibling command exists — `finalize` — whose purpose is
      to flush *without* ending the session. The exposure is not degradation: `VoiceAiPipeline` awaits
      one STT session per utterance under the pipeline-lifetime token, so a stalled session leaves the
      call stuck in recognition. Deliberately **not** fixed by inventing a timeout in §3.6e — a
      deadline chosen ahead of the measurement that would set it is the machinery this change argues
      against everywhere else. **Closes when** either §3.17's live Cartesia run shows the vendor
      closing after `done` (recording that all four are measured and the bound is unnecessary), or a
      surface is measured acknowledging without closing and a drain deadline lands with the arm that
      justified its value — not a round number.
      **Closed 2026-08-16 by the first condition, and no code shipped.** §3.17's live run measured the
      missing fourth surface: Cartesia answers `done` with a `{"type":"done","is_final":false,…}` frame
      and closes `1000` **158 ms** later on a raw witness socket; through the shipped client the
      session ends **172 ms** after the last audio frame. All four are now measured ending the session,
      so the bound is not shipped — and the reason is now a measurement rather than its absence:
      **there is no surface to calibrate a deadline against.** Recorded with the limit of what that
      shows, because it is narrow: it says no surface *currently* acknowledges and holds, not that none
      will. `finalize` remains the counterexample-shaped risk on this very surface — a sibling command
      whose documented purpose is to flush without ending the session — so the trigger to build the
      bound is the first surface measured behaving that way, and it is written into the guide's trade
      paragraph rather than left in this file, since that is where the next reader will be
- [x] 3.17 **Cartesia STT cannot open a session at all — Class A, measured 2026-08-16, twelve runs.**
      `CartesiaSpeechRecognizer.BuildUri()` returns `_options.BaseUri` verbatim —
      `wss://api.cartesia.ai/stt/websocket`, with **no query string** — and the vendor closes the
      session `1008 Missing sample_rate` every time. This is in-band: the upgrade succeeds, which is
      why a `101`-deep probe recorded this surface as "route OK" on 2026-08-15 and why the row is
      corrected rather than extended. Positive control on the same host with the same key: adding
      `?model=…&language=…&encoding=pcm_s16le&sample_rate=16000` opens a working session that
      transcribes, which isolates the defect to the missing query rather than to the account.
      **A second defect inside that working session:** the client's opening JSON frame is not a message
      this vendor has — it answers `Invalid client message: Unrecognized text message "{…}". Expected
      one of: "finalize", "done", "close"`. So `CartesiaSttInitMessage` (`model`, `language`,
      `encoding`, `sample_rate`) is dead on the wire even when the socket survives; those four values
      belong in the query string. Same shape as §4.5 — a configuration sent through a channel the
      vendor does not read. **Closes when** the query carries the four parameters, the init frame is
      deleted rather than left as an ignored message, the fake asserts the query rather than the body,
      and a live run reaches a transcript through the shipped path. **This task also blocks §3.6e's
      Cartesia arm**: with no session there is no wire to measure the half-close remediation on, so
      that re-probe has to happen as part of closing this one rather than leave the only one of the
      four resting on a fake. Re-probing the shipped client on 2026-08-16 also produced the first
      end-to-end observation of the D1 silent-failure class through a shipped client rather than a
      probe: zero results, dead in 0.5 s, `error=none`.
      **Done 2026-08-16.** `BuildUri` now takes the `AudioFormat` and both branches — production and
      fake-server — carry the same query, so the expression under test is the expression that ships;
      `sample_rate` comes from the format rather than from options because it describes the audio
      actually being sent. `CartesiaSttInitMessage` and its `[JsonSerializable]` registration are
      deleted. The fake records the upgrade's request-target and the body-assertion test is replaced
      by two: the query carries the four parameters, and no configuration frame is sent at all. Both
      were verified by reverting each half of the fix in turn — one failing test each, never both, so
      neither assertion is passing for the wrong reason. **The live run closes the criterion**: three
      arms in one process, same key, same host, seconds apart — the URI as previously shipped rejected
      `1008 PolicyViolation` behind an in-band `{"type":"error","code":400}`; the shipped client
      recovered **10/10** digits in one final; a raw witness socket carrying the same query saw the
      transcript, then `{"type":"done"}`, then a `1000` close. Five further consecutive runs at the
      shipped 5-second connect default: 10/10 each. Two side observations, both recorded in the guide
      — the vendor normalizes spoken digits to numerals, so a word-matching metric reads a perfect
      transcript as 0/10 (the §3.6d instrument defect met again on a second surface, which makes it a
      property of the metric); and the live `transcript` frames match the field set the fixtures were
      *authored* from, absent `confidence` included, so for that one message the documentation-derived
      route is confirmed against the wire rather than merely against the page
- [x] 3.17a **The Cartesia STT fixtures are documentation-derived for a reason that no longer holds,
      and the vendor's `done` frame has been observed but cannot be committed.** The three sidecars
      under `Recordings/cartesia-stt/` state that the blocker is the absence of a capture credential;
      §3.17's live run used one, so that is stale. The real blocker is narrower and now named in the
      fake's class remark: `scripts/capture-provider-recording.py` speaks HTTP only — its `PROVIDERS`
      table holds request/response plans, no WebSocket session plan — so there is no path that yields
      a fixture whose provenance cites the canonical capture script. Consequence today: the fake still
      closes on the terminator **without** the `{"type":"done","is_final":false,…}` acknowledgement the
      service was measured sending ~158 ms before its close, so nothing asserts the client tolerates
      that frame (the `flush_done` fixture covers the more dangerous `is_final: true` shape, which is
      why this is a gap and not a hole). Deliberately not closed by hand-writing the frame from a run:
      a fixture whose provenance can only cite a deleted harness is the artifact this protocol exists
      to prevent. **Closes when** the capture script grows a WebSocket plan for this surface and the
      `done` frame — and, while the credential is there, the `transcript` and `error` frames — land as
      `class: "recorded"` with the sidecars corrected
      **Closed 2026-08-18. The capture script speaks WebSocket now, three frames are `recorded`, and
      the two that are not are each unrecorded for a stated reason.**
      `scripts/capture-provider-recording.py` gained a minimal RFC 6455 client — written rather than
      imported, because the module's stdlib-only rule is what keeps it runnable and `websockets`
      would put it behind a `pip install`. The codec is split from the socket so the parts that fail
      silently (accept-token derivation, masking, extended lengths, partial frames, continuation
      finality, refusing a masked server frame) are pure functions under nine unit tests; the script
      suite is **126 tests, green**. Alongside it, a *session plan*: a request has one response, but a
      session has several frames of interest and which is which is decided by reading them, so a
      session plan names frames by a predicate and the capture writes one fixture per frame it
      actually saw.
      **Captured live, `class: "recorded"`:** `transcript-frame-final`, `flush-done-frame`,
      `done-frame` (one paced session) and `error-frame` (a second session driven to its error path
      by sending a text message the protocol rejects). The fake now answers the terminator with the
      recorded `done` frame instead of closing bare, which is the consequence this task named, and a
      test asserts the acknowledgement changes nothing.
      **Two findings the capture produced that the documentation-derived fixtures had wrong, and
      neither was reachable without a capture:**
      1. **`flush_done` carries `is_final` FALSE.** The authored fixture asserted `true`, and its
         whole stated value was that `true` is the shape a broken type filter leaks through as an
         empty final result. So the vendor does not send the dangerous shape. The authored frame is
         **kept**, moved to `flush-done-frame-final-flag.json` and honestly relabelled: the vendor's
         own docs declare the field, so a filter that trusts it must still survive it, and replacing
         the only adversarial case with the benign recording would have reduced coverage while
         looking like an upgrade. The test that needs the adversarial shape now names it explicitly.
      2. **The service's `words[]` entries and `text` carry a LEADING SPACE** (`" El"`, `" sistema"`)
         which the authored fixture did not. Field *set* and *types* matched the documentation
         exactly — so the vendor does honour its own docs on shape — but not the values.
      **Not captured, and this is the observation rather than a shortfall:** `transcript-frame-interim`
      stays authored, because `ink-whisper` answered a 3.6-second utterance with a single
      `is_final: true` transcript in **both** an unpaced session and one paced at real time. Interim
      frames were not reachable by pacing this utterance; a longer or multi-segment one is the
      untried next step, and its sidecar now says exactly that instead of the stale "no credential
      exists". Also recorded from a first run: sending `language=en` against Spanish audio still
      transcribed correctly and echoed `"language": "en"` back, so that parameter is echoed rather
      than enforced — the shipped plan sends `es` to match the scenario.
- [x] 3.18 **AssemblyAI rejects every message shorter than 50 ms, and the client cannot produce longer
      ones.** Measured 2026-08-16: a 20 ms message is answered
      `3007 Input Duration Violation: 20.0 ms. Expected between 50 and 1000 ms`, three of three, and
      the session ends. `AssemblyAiSpeechRecognizer` sends **one WebSocket message per frame the caller
      yields** and batches nothing, so a caller feeding 20 ms frames — which is exactly what an
      Asterisk AudioSocket source produces — fails every session. It fails *silently*, because the
      receive loop filters to transcript messages and drops the error (§4.15). Everything else measured
      on this surface was driven at 100 ms for that reason, and that deviation is part of the result
      rather than a footnote to it. **Closes when** the client coalesces caller frames into messages
      inside the vendor's stated 50–1000 ms window, with a test that feeds 20 ms frames and asserts
      the messages leaving the client are ≥ 50 ms — the assertion has to be on what is sent, since a
      fake that accepts anything is what let this ship.
      **Closed 2026-08-17.** The client coalesces to the floor and splits at the ceiling — a single
      2000 ms message draws the same `3007`, so the window is two-sided and only the small end had been
      described here. Four tests assert on the bytes the client sent, which the fake could not see
      before: it discarded `result.Count` on the binary branch, and no test in this repo asserted on the
      size of audio sent to any provider. Verified through the shipped client — 20 ms frames of 8 kHz
      audio, `10/10` digits in one final, against `0/10` with zero finals and zero partials from the
      pre-fix client in the same harness minutes later, which is the D1 silent-failure class observed in
      production form. Three reverts, three non-empty failure sets (1 / 4 / 3) — the rate half fails one
      test and nothing else, and the tail-padding set is a strict subset of the coalescing set, which is
      what independence of the two halves looks like rather than three disjoint sets. Two findings
      worth carrying out of it: the task described the floor as the whole constraint, and the
      **declared** sample rate turned out to be the number the window is enforced on — see 3.18a
- [x] 3.18a **The same fix had to correct a second defect: this client ignored the `AudioFormat` it was
      handed.** `BuildUri` declared `AssemblyAiOptions.SampleRate` (default 16000) while the shipped
      pipeline feeds `AudioFormat.Slin16Mono8kHz` — the only one of the four streaming STT clients not
      to declare `format.SampleRate`. Not cosmetic, and not what reconnaissance predicted either. The
      control that settled it: identical 800-byte messages of identical 8 kHz audio, declared `8000`
      transcribe `10/10` and declared `16000` die `3007 Input Duration Violation: 25.0 ms` — so the
      vendor computes a message's duration from the *declaration*, which means coalescing to 50 ms of
      the audio actually held would have lost every session while the declaration stayed wrong. The two
      defects were therefore not separable. Equally worth recording: the mismatch **on its own is
      harmless** — declared 16000 over 8 kHz audio still returned `10/10`, refusing the recon's
      inference that it damaged transcripts — so the claim shipped is about duration arithmetic, not
      audio quality. Closed with the format winning and the option kept as the documented fallback
      (Speechmatics' shape); `AssemblyAiOptions.SampleRate`'s "expects 16000" summary corrected to what
      `8000` was measured doing
- [x] 3.19 **AssemblyAI's wrong-path control does not discriminate, so route claims there rest on
      nothing.** `wss://streaming.assemblyai.com/v3/ws-does-not-exist` upgraded `101` and served a
      normal session (2026-08-16). A control that cannot fail is not a control, so this surface's
      evidence class drops from `live + both controls` to `live + credential control` and its Route
      column reads *not controllable*. The `404` previously recorded for it was taken against a
      different host. **Closes when** either a route control that can fail is found on this host, or
      ADR-0048 records that this vendor admits no such control and says what follows from that
      **Closed by ADR-0048 §A2 — the second branch of this task, not the first.** No route control that
      can fail was found on this host, so the ADR records that the vendor admits none and states what
      follows, as a rule rather than as a note about one vendor: a control that cannot fail discards the
      probe's **route** claim and leaves its other claims standing on their own evidence; the surface
      drops one rung to `live + credential control` rather than being demoted to unprobed; and the
      absence is a property of the host **on the day it was measured**, re-testable if the service later
      serves 404s for unknown paths. Recording it as permanent would be the same inference in the other
      direction. The conformance record now carries it in the residue section as well, so the Route
      column's *not controllable* is explained where a reader meets it
- [x] 3.7 The media-type delta is the one with consumer-visible consequence: `SynthesizeHttpAsync`
      chunks the response body straight out as if it were raw PCM, and MP3 is not chunkable that way.
      `LmntTtsOptions.Format` defaults to `raw`, but whether sending `format: "raw"` on the JSON body
      yields L16 rather than MP3 is **not verified**. Resolve that by probe before choosing between
      decoding, rejecting, or documenting the format — it decides the shape of the fix.
      **Probed 2026-08-15. `raw` does not mean one thing — it means two, by transport.** Over HTTP
      `/v1/ai/speech/bytes` it is an MP3 frame stream (MPEG-2 Layer III, 16 kHz, 96 kbps, mono; a frame
      walk consumes 100% of the bytes) served under `Content-Type: application/vnd.lmnt.audio-fp32`, a
      header that describes neither MP3 nor the payload. Over the WebSocket stream the same `raw`
      **is** 16-bit PCM (15 344 samples at 16 kHz, peak 21 949, 99% non-zero). `format=pcm_s16le`
      returns headerless int16 on **both** and is now the default — one value, correct everywhere,
      no decoder added. Recorded as a vendor inconsistency in `LmntTtsOptions.Format` XML docs.
      Two controls ran: `format=mp3` (confirms the classifier separates the two) and an
      invalid-credential control (§3.7a). Also measured: the accepted format set is
      `aac, mp3, raw, wav, ulaw, webm, pcm_s16le`, and `ulaw` arrives inside a RIFF/WAV container
      rather than as bare G.711 — a second trap for a telephony caller, documented, not fixed here
- [x] 3.7a **Invalid-credential control (D4), both LMNT surfaces, 2026-08-15.** HTTP `/v1/ai/speech/bytes`
      answers `403 {"error":"Invalid API key"}` — an application-level JSON body, unlike Speechmatics
      TTS's `401 text/html` from nginx at the edge. WebSocket answers a text frame
      `{"error":"Invalid API key"}` then closes `1002`, i.e. **in-band**, making LMNT WS an in-band
      validation surface for the ADR-0049 scoreboard — measured, not inferred from credential
      placement, per D3. The WS control was run with `model` omitted so the credential was the only
      variable; run with the shipped init it would have been masked by §3.6a's model error, which is
      the control-hygiene point ADR-0049 D4 is about
- [x] 3.7b **A correction about the instrument, recorded because the mistake is instructive.** The first
      probe's magic-byte classifier reported the HTTP `raw` payload as MP3. That was then dismissed as a
      false positive on the strength of the vendor's `Content-Type: application/vnd.lmnt.audio-fp32`
      header, and a second probe was written to characterise it as fp32 — which refuted fp32 outright
      (peak 3.4e38, only 58% of samples inside [-1,1]). A third probe walked the MP3 frame headers and
      consumed 100% of the bytes: the **first reading was right and the vendor's header is wrong**. The
      lesson is the change's own epistemic rule applied to a header instead of a doc — a vendor
      asserting a media type is evidence about the assertion, not about the bytes. Only the frame walk,
      with `format=mp3` as its control, settled it
- [x] 3.7c **The version header the SDK sends is not the version the vendor's docs show — observed
      2026-08-17, not fixed, and the observation belongs here rather than where it was found.**
      `LmntSpeechSynthesizer` sends `lmnt-version: 1.0` (`LmntTtsOptions.ApiVersion`, whose XML docs
      state that default). `docs.lmnt.com`'s *Synthesize speech (bytes)* page shows `1.2` on the same
      header, read the same day. Noticed while capturing the `lmnt-http` fixture for
      `wiremock-http-provider-substrate` §4.6, which recorded it and deliberately left it alone: a test
      substrate cannot answer it, and §4.6's own rule was not to ride an unmeasured change along with a
      measured one.
      **What is measured:** the route answered **200** to `lmnt-version: 1.0` on 2026-08-17, so `1.0`
      is not rejected today. **What is not measured, and must not be assumed either way:** whether
      `1.0` is deprecated, whether `1.2` changes the response contract at all, and whether the header
      is even consulted for this route — a vendor documenting a newer version is evidence about the
      documentation, not about the wire, which is exactly the distinction §3.7b was written to enforce.
      Resolve by probing both header values against the same request with everything else held fixed
      and comparing status, declared media type and payload classification; bump the default only if
      that comparison says something.
      **One corroborating data point from the same capture:** at the shipped `format=pcm_s16le` the
      response declares `application/vnd.lmnt.audio-int16`, and there the header *is* accurate — which
      sharpens §3.7b rather than softening it. The vendor's media type is right at one format and
      wrong (`…-fp32` over MP3) at another, so it can never be treated as evidence about the bytes;
      only a classifier can.
      **Closed 2026-08-18 — the comparison was run and it says nothing, so the default is not bumped.**
      Five arms against `POST /v1/ai/speech/bytes`, every form field held at the SDK's shipped defaults
      and only the header varied, three runs per arm: `1.0` (shipped), `1.2` (docs), **header omitted
      entirely**, `9.9`, and `banana`. All five returned **`200`**, all five declared
      `application/vnd.lmnt.audio-int16`, and all five payloads classified headerless PCM. The null
      comparison ran first per ADR-0048 A6 and is what makes the negative result readable: three
      identical requests varied by **8 960 B** in length, a spread as wide as any between-arm
      difference, so length is not a discriminator on this route and no length claim is made.
      **What this licenses:** the header produces no observable difference in status, declared media
      type or payload classification, and — the sharper half — **it admits no control that can fail**:
      `banana` is accepted exactly like `1.0`. That is the A6 shape again, on a header this time. So
      the vendor's docs showing `1.2` remains evidence about the documentation only, which is precisely
      what §3.7b was written to enforce, and bumping `LmntTtsOptions.ApiVersion` would be an unmeasured
      change dressed as a fix. **What this does not license:** claiming the header is *ignored*. Three
      dimensions on one route's success path were compared; response semantics on failure paths, or on
      other routes, were not. Left at `1.0`. No audio bytes were retained — LMNT is `not-cleared`.
- [x] 3.7d **Speechmatics TTS is the only synthesizer that does not honour the empty-result contract
      its own base class declares — found 2026-08-17 reviewing the `wiremock-http-provider-substrate`
      §4.5 migration, and it is a `src/**` defect, so that test-only change deliberately does not fix
      it.** The contract itself is `Sdk/ADR-0050`.
      `SpeechSynthesizer.SynthesizeAsync` declares two promises in XML docs that ship to consumers of a
      public MIT package: a session that ends cleanly having produced no audio throws
      `SpeechProviderEmptyResultException`, and `text` that is empty or whitespace yields nothing
      *without asking any provider for anything*. Five of the six synthesizers keep both — ElevenLabs
      (`:115`), Cartesia (`:131`), Deepgram (`:137`) and LMNT on both transports (`:203`, `:437`) — each
      pairing a `yieldedAudio` flag with an `IsNullOrWhiteSpace` early-out. `SpeechmaticsSpeechSynthesizer`
      keeps neither: its read loop is `if (read == 0) yield break;` with no accounting, and the file
      contains no whitespace guard anywhere. Its closest analogue is LMNT's *own* HTTP loop
      (`:418`–`:445`), which does both — so this is not a transport limitation.
      **What it costs.** A `200 audio/wav` carrying zero bytes completes as though the vendor had
      spoken: a truncated synthesis is indistinguishable from a complete one, which is the silent door
      ADR-0050 exists to close. It was closed on eight WebSocket surfaces; this HTTP-only surface was
      never in that scope. And whitespace text becomes a request the caller was promised was never made.
      **Do not fix it blind.** The contract fires on a *clean* empty session, so confirm against the
      live route what whitespace or unspeakable `text` actually returns — `200` with an empty body,
      `200` with a WAV header and no samples, or `4xx`. A 44-byte WAV header with zero samples is not
      zero bytes, and would require the guard to count *samples*, not bytes.
      **A third observation from the same read, lower confidence and deliberately not asserted:** that
      loop's `catch (OperationCanceledException) { yield break; }` (`:106`) ends the stream normally
      where the base contract documents `OperationCanceledException` reaching the caller (E6, the
      barge-in case). LMNT's HTTP loop (`:428`) does the same, so it is shared rather than a Speechmatics
      outlier, and whether the caller still observes the cancellation depends on whether it passes the
      token to its own `await foreach`. Measure it before calling it a defect.
      Lands as its own PR: shipped-behaviour change, carrying the two missing tests and a negative
      control that fails if the guard is removed.
      **Closed 2026-08-18. Both halves shipped — and the measurement this task demanded moved which
      half is which.** The live route was probed before a line was written, and it refuted the shape
      this task assumed. `POST /generate/{voice}` answers empty, whitespace *and* punctuation-only
      `text` with `200 audio/wav` and 7 724 bytes, of which 3 817 of 3 840 samples are non-zero —
      0.24 s of audible audio, not silence and not an empty body. So the **whitespace early-out fixes
      a defect this vendor reproduces on demand** (a caller promised silence was billed for a request
      and handed speech), while the **empty-result guard closes a gap in a contract this package
      publishes rather than a failure this vendor is currently observed to produce**. Both are worth
      shipping; only one of them was the live defect, and the code, the tests and the CHANGELOG each
      say which. Neither RIFF size field is usable as a length, incidentally — both are streaming
      placeholders (`0xFFFFFFF7` and `0xFFFFFFD3`).
      **The guard counts bytes, not samples** — the boundary this task warned about is therefore still
      open by choice, not by oversight: a header-only response with zero samples would pass it, and
      catching that means parsing the container, which this provider deliberately does not do (it
      yields the vendor's bytes unexamined). No measurement says the vendor emits one.
      **Retraction — the `PackageVersion` bump this task asked for does not apply.** Evidence:
      `git log -p -- Directory.Build.props` shows the last bump was #132 (2.3.2 → 2.4.0) and #191 —
      the eight-surface ADR-0050 break itself — did not bump at all. In this repo PRs accumulate under
      `[Unreleased]` and the release commit bumps. 2.4.0 stands; the CHANGELOG entry carries the
      BREAKING framing instead.
      **Still open, deliberately: the third observation above.** `catch (OperationCanceledException)
      { yield break; }` was left exactly as it was on both this loop and LMNT's. It was never measured,
      this PR measured nothing new about it, and changing a cancellation path on two providers on the
      strength of a code read is the kind of move this task itself said not to make. It needs its own
      task and its own probe.
- [x] 3.8 A JSON body needs a request DTO: add it to
      `src/Verbara.Sdk.VoiceAi.Tts/Internal/VoiceAiTtsJsonContext.cs` and register it;
      `FormUrlEncodedContent` and its `Dictionary<string, string>` go away. The DTO is AOT-source-gen
      only — no reflection, no anonymous objects.
      **Closed 2026-08-15 as not needed.** This task existed only to serve the JSON body §3.6 assumed
      was required; the probe showed form encoding returns `200` with an identical payload, so the DTO
      would be new public-surface churn justified by nothing measured. `FormUrlEncodedContent` stays.
      A different edit did land in `VoiceAiTtsJsonContext.cs` — see §3.6a — but for the WS init
      message, not an HTTP request DTO
- [x] 3.9 The 3.5 rule — XML docs ship to consumers of a public MIT package, so a wrong reference in
      them is a shipped defect — applied to LMNT, where it is the `404` route itself that is documented:
      `src/Verbara.Sdk.VoiceAi.Tts/Lmnt/LmntTtsOptions.cs` line 20 tells every consumer the HTTP
      transport "Uses `https://api.lmnt.com/v1/ai/speech/generate`", and
      `src/Verbara.Sdk.VoiceAi.Tts/Lmnt/LmntSpeechSynthesizer.cs` line 26 repeats it in the class-level
      `<remarks>`, adding "with form-encoded body fields". Correct both to `/v1/ai/speech/bytes` and a
      JSON body inside the 3.6 commit. A corrected route still documented as the broken one ships the
      defect to every reader of the package.
      **Done 2026-08-15**, with one departure: "and a JSON body" is not applied, because §3.6 measured
      the form body to be correct. Both sites now name `/v1/ai/speech/bytes` and keep "form-encoded".
      Two further XML-doc defects were fixed in the same pass, both of the same class — docs that
      shipped an untrue statement to consumers of a public MIT package: `Format` advertised `raw` as
      "raw PCM — telephony-friendly", which is false on the HTTP transport, and `Model` told the reader
      to "verify available model identifiers at integration test time" when the API enumerates them on
      rejection (`aurora`, `blizzard`, `blizzard-2.0`, `blizzard-2.1`, `blizzard-dialogue`, as of
      2026-08-15)
- [x] 3.10 Decide whether the HTTP base URI becomes an option — this change decides it, it is not
      pre-decided here. `LmntTtsOptions` has none today, and the WebSocket URI at line 265 is hardcoded
      for the same reason. Note the true baseline before arguing from consistency: only three TTS
      providers expose a `BaseUri` option at all — Cartesia, Deepgram TTS and Speechmatics TTS —
      so "like every other provider" is not an available argument. If the decision covers both LMNT
      URIs, say so and change both; otherwise change only the HTTP one and leave the WS path untouched,
      since §1.8 records it as unverified rather than known-good.
      **Decided 2026-08-15: no. Neither URI becomes an option.** Rejected alternatives, by name:
      (a) *a public `BaseUri` mirroring Speechmatics TTS* — Speechmatics' had to change because it was
      already public and its default host was wrong; LMNT has no such obligation, and adding public
      surface to a MIT package is a permanent commitment bought with nothing measured;
      (b) *one option covering both URIs* — the WS path is now measured, but changing it is out of this
      commit's scope and coupling the two would drag it in. What did change is the **shape** of the
      existing internal test seam: it took a full URL, so the fake supplied the route and the client's
      route was never exercised. It now takes an origin, and the client always appends `HttpRoute`.
      That is the property that failed here, and it costs no public API. Consequence for callers:
      none — nothing public was added, removed or renamed
- [x] 3.11 Blast radius, stated honestly in the commit and the CHANGELOG: `LmntTtsOptions.Transport`
      defaults to `WebSocket`, so only callers who opt into HTTP are affected. Speechmatics TTS, by
      contrast, has never worked for anyone.
      **This is wrong, and the correction is the finding.** Probing the WebSocket surface — which no
      task in this change asked for, because §3.6–§3.11 all assumed the default transport was fine —
      showed the default is *also* completely broken, and for an unrelated reason. `LmntTtsOptions.Model`
      defaults to `null`, `VoiceAiTtsJsonContext` declares no `JsonSourceGenerationOptions`, so the init
      message serializes `"model": null`; the endpoint validates that field against a literal set,
      rejects an explicit null, and closes `1002 protocol error` with **zero audio frames**. So LMNT TTS
      has never worked for anyone either, on **either** transport, at shipped defaults. The honest
      blast radius is: every LMNT caller. See §3.6a. Speechmatics TTS is no longer the only
      never-worked provider in this change — it is one of two
- [x] 3.12 `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Lmnt/LmntFakeServer.cs` and
      `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Speechmatics/SpeechmaticsFakeServer.cs` — the
      fakes must **match on method and path** so a misrouted request fails to match instead of being
      served anyway. Without that, this entire defect class stays invisible to the suite no matter how
      much coverage is added. This is the same property `wiremock-http-provider-substrate` requires of
      its HTTP substrate; reuse it rather than reimplementing it.
      **Speechmatics half done** (2026-08-16): `SpeechmaticsFakeServer` now matches on method and
      path and returns `404` otherwise, with three regression tests. `LmntFakeServer` lands in the
      §3.6 commit under §3.13.
      **LMNT half done (2026-08-15).** `LmntHttpFakeServer` now records method and path, serves only
      `POST /v1/ai/speech/bytes`, answers everything else the live `404 {"detail":"Not Found"}`, and
      counts unmatched requests so a route assertion cannot pass on a stale recorded body. A mutation
      test measured what the old fake was hiding: restoring the `/generate` route fails **five** of the
      HTTP tests, not one — the whole HTTP suite had been green against an endpoint that returns 404
- [x] 3.13 LMNT and Speechmatics TTS land as **two separate commits**
- [x] 3.14 **The capture instrument carries the same broken request as the client — fix it in the
      §3.1 commit, not after.** `scripts/capture-provider-recording.py` line 851 puts `"voice":
      "eleanor"` in the JSON body and line 861 targets `https://preview.tts.speechmatics.com/generate`,
      so the plan reproduces the 404 request byte for byte. This is the defect one level up: the tool
      built to establish what the vendor does encodes the same assumption the client got wrong, so it
      cannot contradict it. Run it before the fix and it records a 404; run it after the fix without
      updating it and it records the route the client no longer sends — and either artifact becomes the
      fixture `wiremock-http-provider-substrate` §4.5 is waiting on, pinning the defect into the
      substrate that exists to catch it. **Closes when** the plan's URL and body match the request
      §3.1 makes the client send, and a run produces a `200 audio/wav` artifact rather than a 404
- [x] 3.15 **Same for LMNT, inside the §3.6 commit.** `lmnt_http_plan`
      (`scripts/capture-provider-recording.py` line 910) hardcodes the 404 route at lines 933–934
      (`url` and `endpoint_template`, both `https://api.lmnt.com/v1/ai/speech/generate`) and posts
      form-encoded fields, so it carries both halves of the §3.6 defect — wrong route *and* wrong body
      encoding. Update both with the route fix, and record §3.7's response-media-type finding in the
      plan rather than leaving the capture to discover it again. **Closes when** the plan matches the
      corrected client request and its artifact is a success response, unblocking
      `wiremock-http-provider-substrate` §4.6
- [x] 3.15a **The capture script has its own test suite, and it pinned the broken plans** —
      `scripts/tests/test_capture_provider_recording.py`, 157 tests, green against both defects. Found
      on 2026-08-16 by CI, not locally: the §3.14/§3.15 edits landed with a verification list that ran
      the .NET suite and `openspec validate` and never ran this one. Three tests failed, and what they
      asserted is the point: Speechmatics' route was pinned to `/generate` (the 404) and its `voice`
      to a body field, and LMNT's format was pinned to `raw`. **LMNT's route was not pinned at all** —
      no test in the suite asserted `plan["url"]`, which is how the 404 route stayed green through
      every run. Fixed by pinning the measured values and adding the two missing route assertions
      (`test_ShouldPostToTheBytesRoute_NotGenerate`,
      `test_ShouldSelectTheVoiceByPathSegment_NotByBodyField`); 159 pass. This is the §3.12 property
      at the instrument's own test layer — the same shape as the C# fakes and as §5.4's redactor,
      making three levels at which a checker was more permissive than the thing it checked
- [x] 3.16 Neither §3.14 nor §3.15 was in this change when it was written — both were found by auditing
      `wiremock-http-provider-substrate`'s three blocked tasks on 2026-08-15, after this change had
      already been merged. Sweep `scripts/capture-provider-recording.py` for the **remaining** plans and
      record, per plan, whether its request matches what the shipped client sends. Two were wrong out of
      the two that were checked; the rest are unexamined, which is not the same as correct.
      **Closed 2026-08-17. `PROVIDERS` holds exactly five plans; §3.14 and §3.15 took the two that were
      wrong, and the remaining three all match** — field by field, not by inspection of the docstring:

      | Plan | Compared against | Verdict |
      |---|---|---|
      | `openai-whisper` | `WhisperSpeechRecognizer` | **matches.** URL equals `WhisperOptions.Endpoint`'s default; part order `file`, `model`, `language` identical; file part carries no `Content-Type` (`ByteArrayContent`), text parts `text/plain; charset=utf-8` (`StringContent`); `whisper-1` and `es` are the option defaults; `Bearer` header |
      | `azure-openai-whisper` | `AzureWhisperSpeechRecognizer` | **matches.** `{base}/{deployment}/audio/transcriptions?api-version=` built the same way; `api-key` header, not `Authorization`; **only** `file` + `model`, and `model` is hardcoded `whisper-1` on both sides — the client sends no `language` part and the plan sends none |
      | `google-speech` | `GoogleSpeechRecognizer` | **matches.** Origin + `/v1/speech:recognize?key=` identical to `ProductionOrigin` + `RecognizePath`; body key order `config{encoding, sampleRateHertz, languageCode, model}`, `audio{content}` is the DTO's own declaration order; compact separators; `application/json; charset=utf-8`; raw LINEAR16 base64 with **no** RIFF header, which is what the recognizer sends |

      Two divergences found, both recorded because neither is a client defect and both would otherwise
      read as oversights. **(i)** `deployments_base()` normalizes a resource-root Azure endpoint to
      `…/openai/deployments`, so the plan is *more forgiving* than the client:
      `AzureWhisperOptions.Endpoint` has no default (`default!`) and is documented as already carrying
      that segment, so a client configured with the bare resource root builds a URL missing it and 404s
      while the plan silently corrects the same input. The capture cannot detect that misconfiguration —
      the one place the instrument is deliberately not the client. **(ii)** the Google plan serializes
      with `ensure_ascii=False`; `VoiceAiSttJsonContext` declares no `JsonSourceGenerationOptions`, so
      `System.Text.Json` escapes non-ASCII to `\uXXXX`. No divergence today — every value in that body is
      ASCII (`LINEAR16`, `es-CO`, `default`, base64) — and latent the moment a non-ASCII value enters it.
      The Google plan is also the one that faithfully reproduces a request already **known** to fail: the
      `?key=` auth the recognizer sends is undocumented for `speech:recognize`, which the plan states and
      offers a `Bearer` path around, recording in the sidecar that the captured auth is not production's.
      **Bounded on purpose:** this is a static comparison of each plan against the shipped
      request-building code. It establishes that instrument and client agree; it cannot establish that
      either is what the vendor wants — that is §7.5's live re-probe. Residual gap, and it is not
      "correct": **`azure-tts` has no plan at all.** It is the sixth HTTP surface, its committed
      recording under `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/azure-tts/` was produced by hand,
      and so its provenance cannot cite the canonical script — the same class of gap as §3.17a, one
      surface over

## 4. Speechmatics STT — the session never authenticates, and the assembly ignores vendor fields

Two defects in one file. §4.1–§4.6 are Class D — **the credential is rejected in-band, so no session
ever opens**; §4.7–§4.14 are Class C — the frame is read but assembly-governing fields are ignored.
The second is only reachable once the first is fixed, which is why they share a section and not a
commit.

- [x] 4.1 `src/Verbara.Sdk.VoiceAi.Stt/Speechmatics/SpeechmaticsSpeechRecognizer.cs` line 195 —
      `BuildUri()` puts the long-lived API key straight into the query string
      (`{BaseUri}/{Language}?jwt={encodedKey}`). Speechmatics accepts the WebSocket upgrade (`101`) and
      then **closes the socket with close code `4001 not_authorised`** — the rejection is at the
      protocol layer, after the handshake succeeded. Controlled comparison 2026-08-15, same credential,
      same host, seconds apart:

      | Row | Credential channel | Outcome |
      |-----|--------------------|---------|
      | A | `?jwt=<long-lived API key>` — what the SDK ships | closed `4001 not_authorised` |
      | B | `Authorization: Bearer <API key>` header, no query parameter | accepted, reached `RecognitionStarted` |
      | C | `?jwt=<temporary key, 60 s TTL>` minted at the vendor's management endpoint | accepted, reached `RecognitionStarted` |

      **Closes when** a session opened by the shipped code path reaches `RecognitionStarted` against the
      live endpoint. Severity, stated plainly: unlike LMNT (contained behind `Transport`) and unlike
      Speechmatics TTS (a wrong but fixable route), this makes the **entire** Speechmatics realtime STT
      provider unusable as shipped. There is no containment
- [x] 4.2 Two remedies are **measured** (rows B and C), so the fix is an API-design choice with a
      recorded basis and not a forced single move. This touches how every caller authenticates, so it
      gets its own decision note in the change record and a paragraph in §6.6. Enumerate at least:
      (a) **header auth** — `Authorization: Bearer`, `ApiKey`'s meaning unchanged, one connection,
      nothing to refresh; (b) **mint-then-connect** — an HTTPS POST to the vendor's realtime key
      endpoint carrying the API key, then connect with the returned short-lived key, which adds a call
      before every session, a key lifetime to manage, and an HTTP dependency to a type that has none
      today. The vendor's own documentation frames temporary keys as a **browser** concern (avoiding
      exposure of a long-lived key in a page), which is why header auth is the plausible server-side
      choice — but that sentence is documentation and the measurement is that both work. Mark which
      part of the rationale is measured and which is inferred
- [x] 4.3 The competing hypothesis is **closed** and must be recorded as closed so it is not reopened:
      the failure is not a credential lacking realtime-STT entitlement, because the same credential
      opened a session through two different channels. Row B exists precisely to kill that explanation —
      the same role `eleanor` plays for Speechmatics TTS in §3.4. The defect is the SDK's auth scheme,
      not the key
- [x] 4.4 `src/Verbara.Sdk.VoiceAi.Stt/Speechmatics/SpeechmaticsOptions.cs` line 16 documents `ApiKey`
      as "Passed as the `jwt` query parameter" — the broken scheme, shipped to consumers in XML docs,
      the same shipped-defect rule as §3.5 and §3.9. Correct it to whatever §4.2 chooses, in the same
      commit. While there, check the `<see href="https://docs.speechmatics.com/rt-api-ref"/>` on line 12
      the way §3.5's TTS link was checked; it is **not** asserted dead here, only unchecked
- [x] 4.5 Record the **`Info` frame** — unmodelled, and first in every session. Every Speechmatics
      realtime session opens with an `Info` message carrying sixteen fields: `message`, `type`,
      `reason`, `usage`, `quota`, `growth_rate_1m`, `growth_rate_1m_limit`, `growth_rate_avg_5m`,
      `growth_rate_avg_5m_limit`, `burst_rate`, `burst_limit`, `sustained_rate`, `sustained_limit`,
      `rate_limiting_enabled`, `last_updated`, `region`. `SpeechmaticsTranscriptMessage` declares only
      `message` and `results`. **This is not a parse failure and must not be recorded as one**: the
      receive loop in `SpeechmaticsSpeechRecognizer.cs` tests `msg.Message` for `AddPartialTranscript`
      and `AddTranscript` and `continue`s on everything else, so the `Info` frame is skipped **by
      design**, exactly as the comment above that branch says. The observation worth carrying forward is
      only the *field inventory* — useful to whoever models the frame later, not evidence of a defect
      here. The DTO modelling belongs to `provider-dto-robustness-fences` — route it there under §4.14
      and **do not edit that change's artifacts from here**
- [x] 4.6a Record the **swallowed `Error` frame — this one *is* a defect, and it is what makes §4.1
      silent.** The same `continue` that correctly skips `Info` also skips `Error`. Speechmatics signals
      in-band failure as a message, so a session the vendor rejects yields no exception, no log and no
      transcript: the caller observes an `IAsyncEnumerable` that completes normally and empty. That is
      why the `4001 not_authorised` defect in §4.1 presents to a consumer as "STT returns nothing"
      rather than as an error, and it is why a green suite never caught it. Surface `Error` (and decide
      the same question for `Warning`) so an in-band rejection reaches the caller. This is the STT
      counterpart of the TTS silent-completion signal in §2.10, and it binds to the same spec
      requirement — a provider that produced nothing does not report success.
      **Shipped under `Sdk/ADR-0050`.** `Error` now leaves the receive loop as
      `SpeechProviderFailureException` carrying the vendor's `type` as the code and its `reason` as the
      message — the measured `not_authorised` rejection from §4.1 is what the regression test sends, so
      the frame that made §4.1 silent is the frame under test. **`Warning` was decided the other way, and
      deliberately:** it stays a non-result, non-failure lifecycle message. A warning is the vendor
      continuing to work, and E4's two types are evidence about whether the session failed, not a
      severity ladder — promoting `Warning` to an exception would end sessions the vendor intended to
      keep. It is therefore skipped by the same branch as `RecognitionStarted`, `EndOfTranscript` and
      `Info`, which the source comment now names explicitly instead of leaving the reader to infer
- [x] 4.6 Record that the live `RecognitionStarted` field set **confirms** the committed fixture: the
      live top-level set is `{message, orchestrator_version, id, language_pack_info}` with
      `language_pack_info` an object, and
      `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Recordings/speechmatics-stt/recognition-started-frame.json`
      matches it exactly, correctly nesting `word_delimiter` **inside** `language_pack_info`. The
      fixture was right; nothing in it changes. Upgrade its provenance sidecar's evidence class the way
      §5.9 does for the Deepgram TTS sidecars — from documentation-derived to "conforms to what the
      service actually sends"
- [x] 4.7 `src/Verbara.Sdk.VoiceAi.Stt/Speechmatics/SpeechmaticsSpeechRecognizer.cs` line 170 —
      `if (sb.Length > 0 && !string.IsNullOrEmpty(alt.Content)) sb.Append(' ');` space-joins every token
      unconditionally. Three vendor-supplied signals are ignored; each is a separate sub-task below.
      **Closes when** the committed fixtures assemble to text with no spurious separator
- [x] 4.8 `word_delimiter` — sent on `RecognitionStarted` inside `language_pack_info`, and discarded:
      the recognizer drops every non-transcript message and `VoiceAiSttJsonContext.cs` has no DTO for
      the start message at all. Add the DTO, capture the delimiter at session start, and join with it.
      `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Recordings/speechmatics-stt/recognition-started-frame.json`
      already carries `"word_delimiter": " "` nested inside `language_pack_info`, and §4.6 confirms that
      shape against the live session
- [x] 4.9 `attaches_to` — `SpeechmaticsResult` in
      `src/Verbara.Sdk.VoiceAi.Stt/Internal/VoiceAiSttJsonContext.cs` models only `alternatives`. Add
      `type` and `attaches_to`; a result marked as attaching to its predecessor gets no leading
      delimiter. `.../Recordings/speechmatics-stt/add-transcript-frame.json` already carries a
      `"type": "punctuation"` result with `"attaches_to": "previous"`
- [x] 4.10 `metadata.transcript` — `SpeechmaticsTranscriptMessage` models only `message` and `results`,
      while the vendor publishes the assembled segment. Decide and record: the vendor's assembled text
      is the authority for the transcript, and local assembly survives only for what that text does not
      carry. The same fixture carries `"transcript": "El equipo revisó el informe esta mañana."`
- [x] 4.11 Confidence today is the mean of `alternatives[0].confidence` across results. If the transcript
      text stops coming from local assembly, confidence still must — say so in the code and in the
      change record rather than letting it quietly change meaning
- [x] 4.12 Regression test for the reported shape, asserted against the committed fixture and its actual
      text: `.../Recordings/speechmatics-stt/add-transcript-frame.json` carries
      `"transcript": "El equipo revisó el informe esta mañana."`, so the assembled segment must end
      `… mañana.` and not `… mañana .`. Use that sentence, or author a new fixture and say so — do not
      assert a sentence no committed fixture contains
- [x] 4.13 A second test with a non-space delimiter — a language pack declaring an empty
      `word_delimiter` assembles with no separators — so the fix is *use the vendor's delimiter* and not
      *special-case punctuation*
- [x] 4.14 The new and widened DTOs from §2.2, §2.4, §3.8 and §4.8–§4.10 — plus the unmodelled `Info`
      frame observed in §4.5, which is routed to that change and not modelled here — land inside the
      reachability
      closure `provider-dto-robustness-fences` counts (its §1.2 figures) and inside its coverage guard
      (its §8.3). Flag it in this change's record so those numbers are re-derived; **do not edit that
      change's artifacts from here**
      **Closed 2026-08-18 as one PR — and the live probe that opened it changed two of the decisions.**
      A session was streamed through `wss://eu2.rt.speechmatics.com/v2/en` carrying one synthesised
      English utterance, and every transcript frame it produced was read before a line was written.
      Nothing of the vendor's output was stored, so the fixtures stay `class: "synthetic"`; what the
      session settled is the *shape*, which is now recorded in all four provenance sidecars.
      **What it confirmed.** `word_delimiter` arrives nested in `language_pack_info` exactly as the
      fixture claims; `attaches_to: "previous"` arrives on the punctuation result; `metadata.transcript`
      is present on **every** `AddTranscript` **and** `AddPartialTranscript`. The defect reproduces
      live: the vendor's own `"…this morning, and it"` reached the caller as `"…this morning , and it"`.
      **§4.10 decided: the vendor's `metadata.transcript` is the transcript, trimmed.** The trim is not
      cosmetic and not a widening of the rule — finals arrive glued (`"The "`, `"team reviewed "`,
      `"…looks good. "`) so they concatenate without a separator, and a per-result value carrying that
      trailing space would make this the only provider in the SDK that emits one. Local assembly
      survives strictly as the fallback for a message with no `metadata.transcript`, and is fixed there
      too (delimiter + `attaches_to`).
      **The finding that mattered most, and it was a failure of this change's own tests.** With all
      three signals implemented, a mutation that ignored `metadata.transcript` entirely and always
      assembled locally left **every test green** — because on the committed fixtures the vendor's
      trimmed text and the corrected local assembly agree character for character, and across eleven
      live messages they agreed too. The §4.10 authority rule was therefore unobservable: exactly the
      silent-pass shape this change exists to remove, reproduced inside it. A test built on a
      *constructed* divergence now stands behind the rule, and it states in its own remarks that no
      measured frame diverges — so no reader can take it as evidence that Speechmatics rewrites
      segments. All three signals are now individually mutation-checked: ignore the vendor transcript,
      `attaches_to`, or the declared delimiter and exactly one distinct test fails for each.
      **A test that pinned the defect as behaviour had to be inverted**, not merely supplemented:
      `StreamAsync_ShouldSpaceJoinTokens_WhenFrameCarriesPunctuationAttachedToPrevious` asserted the
      space-join deliberately, because §4.5 was test-only. It is now
      `StreamAsync_ShouldYieldTheVendorsAssembledSegment_…` and keeps its negative control — the
      recording must still exercise the divergence, or the assertion proves nothing.
      **§4.11 held:** confidence still comes from `alternatives[0].confidence`, averaged, and a test
      pins that separating it from the text walk did not change what the number means.
      **§4.14 is a flag, not code:** the four new DTOs — `SpeechmaticsRecognitionStartedMessage`,
      `SpeechmaticsLanguagePackInfo`, `SpeechmaticsTranscriptMetadata`, plus `type`/`attaches_to` on
      `SpeechmaticsResult` — enter the reachability closure `provider-dto-robustness-fences` counts
      (its §1.2) and its coverage guard (its §8.3); those figures must be re-derived there. Not edited
      from here. The unmodelled `Info` frame is likewise routed there, and the live session sharpens
      what it is: two arrived, carrying `{type, reason, region, quota, usage, rate_limiting_enabled,
      burst_limit, burst_rate, sustained_limit, sustained_rate, growth_rate_1m, growth_rate_1m_limit,
      growth_rate_avg_5m, growth_rate_avg_5m_limit, last_updated}` — a rate-limiting telemetry frame,
      not an error. A second unmodelled kind appeared that no task names: **`AudioAdded`**, 29 of them,
      `{message, seq_no}` — the per-chunk acknowledgement. Neither is a result nor a failure and both
      fall through the existing skip, so nothing is broken; both belong in the DTO-fence census.
      **No `PackageVersion` bump** — 2.4.0 stands, `[Unreleased]` accumulates.
- [x] 4.15 **AssemblyAI STT — the seventh defect, and the one that makes the swallow a class.**
      `src/Verbara.Sdk.VoiceAi.Stt/AssemblyAi/AssemblyAiSpeechRecognizer.cs:137` reads
      `if (!string.Equals(msg.Type, "Turn", StringComparison.Ordinal)) continue;` — structurally the
      same filter as the Speechmatics one in §4.6a, written by someone who believed they were skipping
      lifecycle noise. AssemblyAI signals in-band failure as a frame whose type is not `Turn`, measured
      2026-08-15 with an invalid-credential control: `101` upgrade, then `{error, error_code, type}`
      carrying "Unauthorized Connection: Invalid API key". The recognizer discards it, so a rejected
      session reaches the caller as a stream that completes normally and empty. Fix it the same way
      §4.6a fixes Speechmatics and land them in the **same commit** — one remedy, one shape, so the
      next reviewer sees a rule rather than two coincidences. `Termination` stays a legitimate skip;
      the discriminator is whether the frame carries a failure, not whether it is on the content
      allow-list (`Sdk/ADR-0049` D1).
      **Shipped under `Sdk/ADR-0050`, in the same commit as §4.6a — and wider than "the same commit":**
      the remedy landed at all eight WebSocket clients at once, so what a reviewer sees is one rule
      applied uniformly rather than two coincidences or eight variations. The measured
      `{error, error_code, type}` frame is what this surface's regression test sends, `Termination`
      remains a legitimate skip, and the lifecycle-only test next to it proves the skip still holds
- [x] 4.16 Audit **every** provider receive loop in `src/Verbara.Sdk.VoiceAi.Stt/` and
      `src/Verbara.Sdk.VoiceAi.Tts/` for the allow-list filtering shape — a `continue` or a
      message-type equality test that lets unanticipated frames fall into a discard branch. A first
      pass already found **five** sites, not the three with a live symptom: beyond Speechmatics,
      AssemblyAI and ElevenLabs-by-frame-type, `CartesiaSpeechRecognizer.cs:165` (`Type !=
      "transcript"`) and `DeepgramSpeechRecognizer.cs:120` (`Type != "Results"`) are the same
      construction. Those two are **latent, not clean** — their vendors validate credentials at the
      handshake so no auth frame reaches the branch today, but every other error either vendor defines
      does, and a vendor moving validation in-band converts them with no line changing. Finish the
      sweep across the remaining surfaces and record the result per surface even where the answer is
      "no such branch", because a clean loop is evidence and an unexamined one is not.
      **Sweep finished 2026-08-17. Thirteen surfaces, and the count of affected ones went from five to
      eight.** Every WebSocket client had all three doors open, not just the allow-list one this task
      describes: the frame filter (door 1), the **discarded close code** (door 2 — `ws.CloseStatus` read
      and thrown away at all eight), and `catch (WebSocketException) { break; }` (door 3), which turns a
      socket dying mid-stream into normal completion. Doors 2 and 3 are why the sweep matters more than
      its first pass suggested: a surface whose frame filter is clean is still silent through the other
      two. Per surface —
      **remediated (8):** Cartesia TTS, ElevenLabs TTS, LMNT TTS (WS), Deepgram TTS, Deepgram STT,
      Speechmatics STT, AssemblyAI STT, Cartesia STT.
      **no such branch (5), and each verified rather than assumed:** Azure TTS, Speechmatics TTS (HTTP),
      Google STT, Whisper STT, Azure Whisper STT hold no receive loop at all — request/response over
      HTTP, no `ReceiveAsync`, no message-type equality test, no `continue` discard. Each calls
      `EnsureSuccessStatusCode()`, so a rejected request already reaches the caller as
      `HttpRequestException`. What is *not* closed on those five is the zero-output-on-`2xx` case: a
      vendor answering `200` with no audio still completes silently. That residual is what the E9 counter
      `tts.syntheses.silent` exists to make visible, and it is recorded rather than retyped — no measured
      defect stands behind it and no probe has produced one
- [x] 4.17 Remediate the two **latent** sites from §4.16 (`CartesiaSpeechRecognizer.cs:165`,
      `DeepgramSpeechRecognizer.cs:120`) under the same D1 shape as §4.6a and §4.15. No measured defect
      forces these — that is precisely the argument for doing them here rather than after one bites,
      and `Sdk/ADR-0049` binds all five sites, not the three with symptoms. If they are deferred
      instead, the deferral is recorded with that reasoning rather than left as silence.
      **Done, not deferred — under `Sdk/ADR-0050`, same shape and same commit as §4.6a and §4.15.** Both
      latent sites now throw on a failure frame, and both are tested. What the tests cannot do is send a
      *measured* frame: §1.3a established that Deepgram validates credentials with `HTTP 401` at the
      upgrade, so its in-band frame shape is documented rather than observed, and the fake and the test
      both say so in as many words rather than letting a later reader mistake a published schema for a
      capture. Cartesia STT is the opposite case — its door-1 frame
      (`{"type":"error","code":400,"message":"Missing sample_rate: …"}`) and its `1008` close were both
      measured on the live endpoint, so the latent site turned out to have a real signal waiting behind it

- [x] 4.18 **The fake had no way to see the credential, and that is why it certified the defect.**
      `SpeechmaticsFakeServer` captured the request URI and the `StartRecognition` body and nothing
      else, so a suite could be entirely green while the client authenticated in a way the service
      rejects. The shared substrate was the reason: `WebSocketTestServer.ReadUpgradeRequestAsync`
      parsed the headers for `Sec-WebSocket-Key` alone and discarded the rest, so **no** fake on that
      substrate could assert on a credential. It now returns the whole header set and
      `WebSocketTestSession` exposes it, which is the seam §2.3c's **six** sites need — none of which
      this task discharges. Speechmatics is not one of the six: it had no auth header to gate, its key
      was in the URL on both paths, so all six remain open.
      Capturing is as far as this task goes: making the fake **reject** an unauthenticated connection
      the way the service does — `101`, then close `4001` — waits for §4.6a, because until the receive
      loop surfaces an in-band failure a rejecting fake would assert that the client completes silently
      and empty, which is the defect and not the contract
- [x] 4.19 Non-vacuity by mutation, not by inspection. Three mutations against the 11 tests in
      `SpeechmaticsSpeechRecognizerTests`, each reverted. Named tests, because a bare count is the kind
      of claim this change exists to stop:

      | Mutation | Failed | Which |
      |---|---|---|
      | (a) restore `?jwt=` and drop the header — the shipped defect | **3** | `…ShouldKeepTheCredentialOutOfTheUrl…`, `…ShouldAuthenticateWithABearerHeader…`, `…ShouldSendStartRecognition…` |
      | (b) gate the header behind `_fakeServerPort is null` — the §2.3c shape, which is exactly how a credential becomes invisible to its own fake | **2** | `…ShouldKeepTheCredentialOutOfTheUrl…`, `…ShouldAuthenticateWithABearerHeader…` |
      | (c) send the header **and** `?jwt=` | **2** | `…ShouldKeepTheCredentialOutOfTheUrl…`, `…ShouldSendStartRecognition…` |

      Recorded as a correction, because the first pass through this task claimed "3 tests each" for
      all three arms and a re-run gave 3, 2, 2. The overstatement was small and it was still an
      overstatement — the same failure this change documents in vendors' own claims. The surviving
      point about (c) is narrower than first written: a header-only assertion **would** pass it, which
      is why the URL check is its own test, but (c) does not depend on that test to be caught — the
      pre-existing `…ShouldSendStartRecognition…` assertion on the request-target catches it too
- [x] 4.20 What this evidence **is and is not**, stated so it is not overread. The three arms were
      measured against the live endpoint on 2026-08-15 with a probe; the **fixed client has never been
      run live**. What ran was a request reproducing what it now sends — the same wire behaviour, but a
      reconstruction of it, not the artifact. Closing §4.1 by its own stated bar ("a session opened by
      the shipped code path reaches `RecognitionStarted` against the live endpoint") therefore still
      wants one live run of the shipped path, which §5's probe suite is the place for. Also checked
      under §4.4 rather than left unchecked: `https://docs.speechmatics.com/rt-api-ref` resolved `200`
      with no redirect on 2026-08-16, and a nonexistent path on the same host answered `404` — the
      control that makes the `200` the page rather than a soft-404. The link stays

- [x] 4.21 **Three further corrections, found by reviewing this section's own prose against the tree
      before it shipped** — recorded rather than quietly fixed, because a change about overclaiming
      that overclaims is worth nothing:
      (a) §4.18 first said this task served "the remaining five sites in §2.3c". §2.3c enumerates
      **six**, Speechmatics is not one of them — it had no auth header to gate — and this task
      discharges none, so all six stay open;
      (b) the conformance record's Speechmatics STT row was dated 2026-08-16. No measurement of that
      surface was taken on 08-16; the three arms are 08-15 and the only 08-16 event is a doc-link check
      against a different host. The record's own rule is that a row's date is the date its own
      measurement was taken, so the row is back to 2026-08-15 with the fix noted in its prose;
      (c) four Speechmatics STT provenance sidecars and two test files explained their `synthetic`
      class with "no capture credential exists in this environment". That sentence is now false for
      this surface — a working credential opened a live session on 2026-08-15 — so all six say what is
      actually true instead: no capture **run** has been made, because the sessions that opened
      streamed no audio and elicited no transcript frame. The identically-worded Cartesia STT sidecars
      are deliberately **left alone**: §7 records that provider as permitted *with a tier condition*,
      so the sentence still holds there. A sentence is not stale everywhere just because it went stale
      somewhere

- [x] 4.22 **The patch-coverage gate found a fourth correction, and it was a real one.** CI reported
      75% patch coverage (3 of 4 changed executable lines, floor 85%) on the first push of the fix.
      The uncovered line was `BuildUri`'s production branch — `new Uri($"{BaseUri}/{Language}")` —
      because the method opened with `if (_fakeServerPort.HasValue)` and every test took the other
      branch. So the URI expression that **ships** was executed by nothing, and the assertions written
      in §4.5 to prove the credential is not in the URL were proving it about a line only tests run.
      That is §2.3c's shape in a second place: a test seam that takes over more of the request than it
      should, leaving the replaced part unexercised. The remedy was to delete the seam rather than
      cover it — the branch **and** the `internal` fake-port constructor are gone, and
      `SpeechmaticsSpeechRecognizerTests` reaches its fake by setting `BaseUri` to
      `ws://127.0.0.1:{port}/v2`, which the option's own validation already admits and which is the
      same knob an operator turns to pick a region. All 11 tests now execute the shipped expression;
      no assertion changed. Two consequences worth stating: mutation (b) of §4.19 is no longer
      **expressible** in this file, which is a stronger result than its passing was — the shape cannot
      be reintroduced without re-adding a seam — and §2.3c's six sites now have a cheaper prescription
      available than the "substitute the origin only" one written there: where a client already
      exposes a base-URI option, deleting the seam costs one line in a test helper. §2.3c stays open
      and its site list is unchanged. Recorded here rather than folded silently into the fix, because
      a gate catching what a review missed is evidence about the review

## 5. The conformance probe as a committed instrument

- [x] 5.1 Codify the method that produced every finding in this change: controlled comparison against
      the live endpoint — same credential, same host, seconds apart — with a **negative control that is
      known wrong**, so a pass is distinguishable from a probe that cannot fail. Nothing is stored.
      **Done — `scripts/probe-provider-conformance.py`.** The module docstring states the three rules
      it enforces structurally and why each is there: every one of them was broken by hand first. The
      split it makes is deliberate — the parts of the method that can be wrong **without a network**
      (what may be printed, which controls must be present, how deep a run must reach) are the parts
      that actually failed in practice, so they are ordinary unit-tested code; the network calls sit
      below them and stay thin. Nothing is stored: `render()` redacts, serializes and truncates, and
      is the only sanctioned way to print a vendor payload
- [x] 5.2 Decide where it lives and record why. A probe needs live credentials and network egress, so it
      cannot be a required PR check; ADR-0043 is the precedent for evidence produced off the PR path and
      read by a human. Candidates: a script under `tools/` plus a `Category`-gated test excluded by the
      unit-lane filter, or a scheduled workflow. Name the rejected option.
      **Decided: `scripts/probe-provider-conformance.py` + `scripts/tests/`.** The premise held — the
      probe's *live* half cannot be a PR gate — but it turned out to be the smaller half. The rules
      above the network are gated on every PR by the existing required check
      (`python3 -m unittest discover scripts/tests`, the same lane as
      `check-recording-redaction.py`), which is the strongest available placement for the part that
      demonstrably broke. **Two options rejected by name.** (a) *A scheduled workflow* — it burns
      live credentials unattended against paid endpoints, and ADR-0043's precedent is evidence
      produced off the PR path and **read by a human**, not evidence produced on a timer and read by
      nobody; a probe whose output nobody reads is a cost, not a control. (b) *A `Category`-gated C#
      test under `Tests/`* — it would put live provider credentials inside the test host and inside
      whatever a future `dotnet test` invocation inherits, and it would make the SDK's own test
      assembly the thing that talks to a paid vendor. The Python placement keeps the credential in
      one deliberately-run process. Consequence to accept: the live half has **no** automated gate
      and is run by hand, per surface, with its result written into
      `docs/guides/provider-wire-conformance.md` (§5.5)
- [x] 5.3 The negative control is mandatory and is part of the recorded output, not a step someone
      remembers to run. Worked example to encode: `wss://api.deepgram.com/v1/speak` with the SDK's
      shipped defaults (`model=aura-2-thalia-en`, `encoding=linear16`, `sample_rate=24000`) returned
      `101 Switching Protocols`; `/v1/speak-does-not-exist` on the same host returned `404 Not Found`.
      **Done — and made structural rather than procedural.** `ProbeSpec.__post_init__` raises unless
      **both** control kinds are present, so a one-control probe cannot be constructed, let alone run;
      the error message carries the reason (D4) so the next author meets the argument rather than the
      rule. `Control.expected` holds the **measured** vendor answer, not an assumed one, so a control
      that silently starts passing is loud. The Deepgram example above is encoded in `WORKED_EXAMPLES`
      with both arms, and a test asserts every encoded example carries both kinds
- [x] 5.4 The probe inherits `docs/guides/provider-recording-protocol.md` section 4 verbatim: no Output
      stored or printed, correlating identifiers (`request_id`, `model_uuid`) never echoed. This is how
      the 2026-08-15 run was conducted; the instrument must not be able to do otherwise.
      **A defect in that instrument, found by using it and recorded rather than quietly patched.**
      The ad-hoc redactor used during the 2026-08-15 runs matched only *string-valued* identifier
      fields, so `additional_model_uuids` — an **array** of them — passed straight through and a raw
      identifier reached tool output. "Never echoed" was true of the rule and false of the code. The
      committed instrument MUST redact by key regardless of the JSON value's type, walking arrays and
      nested objects, and MUST have a test that feeds it an array-valued identifier field. This is the
      §3.12 property applied to the probe itself: the checker was more permissive than the rule it
      was checking.
      **Done — `redact()` keys off the field name alone and walks dicts, lists and tuples at any
      depth.** The headline regression test feeds it exactly the shape that leaked
      (`{"additional_model_uuids": [...]}`), and a subtest sweep asserts the field is redacted for a
      string, a list, a nested object, an int, `None` and `True` — a redactor keyed on the value's
      type has one blind spot per type it forgot, and keying on the name has none. Key matching is
      case-insensitive because header-style keys arrive in whatever case the vendor chose.
      **Mutation-checked:** restoring the `isinstance(v, str)` gate fails 6 tests and the self-check
      names the 2026-08-15 leak by date. Not a third instance of the defect:
      `scripts/check-recording-redaction.py` is a regex-over-text scanner, so it never had a
      value-type branch to get wrong — it is the *pattern* this instrument borrowed, not another
      victim of it
- [x] 5.5 Promote §1's table into `docs/` as the per-surface conformance record: route status, frame
      status, evidence class, date, negative control present. This is the artifact that makes *not
      characterised* a visible state rather than a gap between rows.
      **Done — `docs/guides/provider-wire-conformance.md`**, indexed in `docs/guides/README.md` and
      sited next to `provider-recording-protocol.md`, whose §4 it inherits and whose per-provider
      terms verdicts it does not duplicate. Fourteen surfaces (seven TTS, seven STT), each with its
      **own** date. Three decisions worth stating because each was a way to get it wrong: route and
      frame status are **separate columns**, since four of six TTS providers were broken and no
      column predicted the other; the evidence class is a five-value ladder where
      `live + route control` and `live + both controls` are **different rows**, not the same row with
      a footnote; and *Still not characterised* is a **section with names in it**, because a gap
      between rows reads as coverage. Rejected: `docs/research/`, which is dated exploratory findings
      — a ledger filed there lets a stale row read as current
- [x] 5.6 Record the Deepgram TTS measurements as the instrument's worked example: `Metadata` text
      frame, then **37 binary frames of 1920 bytes** (71040 bytes, 1.48 s of linear16 @ 24 kHz), then a
      `Flushed` text frame — exactly what `DeepgramSpeechSynthesizer` expects, and explicitly **not**
      the Class B shape: no text frame carried a long string field, so there is no base64 audio hidden
      in JSON on this surface.
      **Done — encoded in `WORKED_EXAMPLES`, not left in prose.** Two tests hold it in place: one
      asserts the frame shape survives in the record, and one asserts the string `Class B` is still
      there, because the **negative** finding is the perishable half. A surface measured *not* to hide
      base64 audio in JSON decays into silence the moment nobody restates it, and silence about a
      surface is indistinguishable from never having looked
- [x] 5.7 Record one margin as a **margin, not a defect**: the receive loops ignore
      `result.EndOfMessage`, so a text frame exceeding the 65536-byte receive buffer would throw an
      uncaught `JsonException` in the text handler. Largest binary frame measured 1920 bytes (34x
      headroom) and the `Metadata` frame 291 bytes — the vendor would have to grow that frame 225x to
      reach the buffer. State the numbers; do not file it as a bug and do not let a future reader mistake
      the note for one.
      **Recorded — and now superseded in part by §2.3b, which is the whole point of writing the
      numbers down.** This task was **correct for the surface it measured** and MUST stay on the
      record as correct: against frames the *client* sizes — Deepgram's 1920-byte binary chunks, a
      291-byte `Metadata` frame — 34× and 225× headroom is a margin and filing it as a bug would have
      been wrong. What the margin rests on is the sizing party, and that is exactly what the Class B
      fix changes: text frames are sized by the **vendor**, and ElevenLabs returned ~29 KB average
      across 4 frames on a short probe sentence. The margin does not transfer, so §2.3b files the
      text-frame case as a live defect without contradicting this one. **The transferable lesson is
      the one this pair demonstrates:** a margin is a claim about a measured distribution, and it
      expires the moment the fix changes who sets that distribution. State the condition alongside
      the number, or the number outlives its own premise
- [x] 5.8 Record that synthesis is **non-deterministic**: two runs with identical input produced 1.48 s
      and 1.20 s of audio. This retroactively justifies generating the `.raw` fixtures with
      `SyntheticPcm.Triangle` rather than capturing them — a captured audio fixture could not have been
      asserted byte-for-byte, which is exactly what those fixtures do today.
      **Recorded in `deepgram-tts/metadata-frame.provenance.json`**, next to the measurement that
      produced it rather than in prose that outlives its context. Worth naming as a class, because it
      inverts the usual preference: for a **schema** claim a capture beats a document, but for an
      **audio** fixture a capture is strictly worse than a generator — it cannot be asserted
      byte-for-byte, so it can only be checked loosely, and a loose assertion on a fixture is close to
      no assertion. The right artifact depends on which property is under test, not on which is more
      "real"
- [x] 5.9 Upgrade the evidence class of the Deepgram TTS sidecars —
      `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/deepgram-tts/metadata-frame.provenance.json` and
      `flushed-frame.provenance.json`. The live field sets match the committed synthetic
      (documentation-derived) fixtures **exactly**: `Metadata` = `{type, request_id, model_name,
      model_version, model_uuid, additional_model_uuids[]}`, `Flushed` = `{type, sequence_id}`. Edit the
      sidecars' `source_schema` / `notes` to record "conforms to what the service actually sends" in
      place of "conforms to the docs"; the frame JSON and the `.raw` bytes are unchanged. Upgrade
      **those two sidecars only**: the probe observed exactly two frame types, so
      `warning-frame.provenance.json` and `audio-linear16-16khz.provenance.json` keep their current
      class — the Warning frame and the error paths were never exercised.
      **Done — `source_schema.method` on both sidecars now carries the confirmation, its date, and
      both controls; `class` stays `synthetic` and the frame JSON, `bytes` and `sha256` are
      untouched.** The distinction the edit preserves is the one that makes the upgrade meaningful:
      what was promoted is the **schema** claim, not the payload. The values remain our own fiction
      and nothing of the vendor's output was stored, so the protocol's no-storage rule holds while the
      fixture stops resting on a page with no revision marker. The two-of-four scope is stated inside
      the sidecars themselves, so a later reader cannot promote the other two by proximity
- [x] 5.10 The same measurement confirms `model_uuid` and `additional_model_uuids` are really sent and
      really unmodelled by `DeepgramTtsServerMessage` — so the unmodelled-sibling test asserts a real
      condition rather than a hypothetical one. Note that where the test lives. Do **not** add
      `[JsonRequired]`: that instrument belongs to `provider-dto-robustness-fences` and its arity
      condition is not met on a union DTO.
      **The test is `DeepgramSpeechSynthesizerTests.SynthesizeAsync_ShouldNotThrow_WhenServerSendsMetadataFrame`**
      (`Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Deepgram/DeepgramSpeechSynthesizerTests.cs:223`), with the
      field-set half asserted at `:156` by
      `RecordedFixtures_ShouldCarryDocumentedFieldsAndExactByteLength_WhenReadFromRecordingsTree`.
      What the probe changed is the test's **standing**, not its code: it was written against a
      documented field set and now guards a measured one. Its comment already says the hand-authored
      four-field literal it retired would have passed a parser that threw on an unmodelled sibling —
      that is the assertion the live run promoted from plausible to real. No `[JsonRequired]` added,
      per the boundary above
- [x] 5.11 Encode the **depth** rule the Speechmatics run produced — it governs what a probe must *do*,
      not merely what it must compare against. A handshake-only probe is **sufficient** for a vendor
      that authenticates in the HTTP upgrade headers (Deepgram: the `101` proves the credential was
      accepted) and **insufficient** for a vendor that authenticates **in-band** (Speechmatics: the
      `101` proves nothing and the rejection arrives afterwards as close code `4001`). A conformance
      probe must therefore reach the vendor's first protocol exchange, not stop at the upgrade. Had this
      programme stopped at the handshake, Speechmatics STT would have been recorded as verified good
      while being entirely unusable — state that consequence in the instrument and in §6.6; it is the
      strongest argument either has.
      **Done in the instrument** — `ProbeSpec.verdict_allowed(reached_first_exchange=…)` returns
      `(False, reason)` for a WebSocket run that stopped at the upgrade, and the reason names
      Speechmatics and close code `4001` so the next author meets the case rather than the rule.
      Three branches, each measured rather than assumed: HTTP always allows (the response **is** the
      exchange); `handshake` allows a `101`-only run **because that surface's validation point was
      itself measured**; `in-band` and `unmeasured` refuse. The last of those is the load-bearing one
      — *unmeasured* is not *probably handshake*, and treating it as such is precisely the D3
      inference. `UNMEASURED` is the dataclass **default**, so the refusal is what a new surface gets
      for free. Mutation-checked: allowing a handshake-only verdict fails 3 tests and the self-check.
      **§6.6's half stays open** — it owns the ADR text, and this task only owes it the instrument
- [x] 5.12 **The instrument's own tests are the gate, and they are non-vacuous — checked by mutation,
      not by inspection.** `scripts/tests/test_probe_provider_conformance.py` (178 tests in the
      `scripts/tests` lane, all green) was run three times against a deliberately broken instrument,
      once per rule: type-gating the redactor fails 6 tests; dropping the both-controls refusal fails
      3; letting a handshake-only run count as a verdict fails 3. Each also fails
      `--self-check`, which is the liveness fence borrowed from `check-recording-redaction.py` and
      exists for the same reason — a rule edited into uselessness would otherwise let every run report
      clean. This task is added rather than assumed because "the tests pass" is the claim this entire
      change exists to distrust: every defect in §1–§4 shipped past a green suite

## 6. Governance, decision record and docs

- [x] 6.1 A Governance scanner in `Tests/Verbara.Sdk.Governance.Tests/`: a provider's production
      endpoint must be declared once — in its options type or a single named constant — and not inlined
      at a call site. `LmntSpeechSynthesizer.cs:294` is the motivating case: a route no configuration
      can reach and no reader can audit without opening the file. Roslyn-syntactic like every scanner in
      that project, never regex over raw text; 1-based lines; both arguments null-guarded.
      It ships with an **explicit allow-list**, or it is red on arrival: four inlined-endpoint sites
      pre-date this change and **no task in it remediates them**. Enumerate them with a one-line reason
      each. **There is no in-repo precedent to copy for the allow-list shape** — grepped 2026-08-15, no
      Governance guard currently ships an enumerated exemption list, and `LoopbackSeamScanner.cs:28`
      states positively that it carries *no ignore list*. This scanner therefore establishes the shape
      rather than following one, and that is a decision to make deliberately, not a detail to improvise
      while writing it. The four sites are —
      `src/Verbara.Sdk.VoiceAi.Tts/ElevenLabs/ElevenLabsSpeechSynthesizer.cs:161` (URI assembled from
      voice id and model options; the type exposes no base-URI option and this change does not add one),
      `src/Verbara.Sdk.VoiceAi.Tts/Lmnt/LmntSpeechSynthesizer.cs:265` (the WebSocket route, recorded
      **not verified** in §1.8 and untouched unless §3.10 decides to cover it),
      `src/Verbara.Sdk.VoiceAi.Tts/Azure/AzureTtsSpeechSynthesizer.cs:84` (origin interpolated from
      `_options.Region`; `AzureTtsOptions` exposes `ApiKey`, `Region`, `VoiceName`, `Language` and
      `OutputFormat` and **no endpoint property**, so the route is not reachable from an options type
      even though it is region-parameterised — the surface passed both halves of its probe and this
      change does not touch it), and
      `src/Verbara.Sdk.VoiceAi.Stt/Deepgram/DeepgramSpeechRecognizer.cs:138` (route verified live in
      §1.3; hoisting it into a declaration is a separate refactor). An entry is a recorded exemption and
      not an absolution: each names the condition that removes it, so the list shrinks instead of
      becoming permanent
      **Done — and the task's own premise did not survive contact with the tree, in two ways worth
      naming rather than quietly correcting.**
      **(1) The four-site allow-list was stale.** Re-derived 2026-08-18 against the current tree, only
      **two** of the four survive. `ElevenLabsSpeechSynthesizer:161` and `DeepgramSpeechRecognizer:138`
      were remediated — both routes now live in their options types (`ElevenLabsOptions.BaseUri`,
      `DeepgramOptions.BaseUri`), and each carries an XML doc narrating the fix. So did the motivating
      case itself: `LmntSpeechSynthesizer:294` is now `private const string HttpOrigin`. This change's
      own route work remediated three of the four sites the task said "no task in it remediates".
      The scanner therefore ships with **two** exemptions, not four — Azure TTS (`REGION-TEMPLATED`)
      and the LMNT WebSocket route (`PENDING-VERIFICATION`).
      **(2) "No in-repo precedent for the allow-list shape" was wrong.** `SyncFenceScanner` ships an
      inline `// fence-allow:` marker with a CLOSED category enum, an em-dash separator and a mandatory
      reason, where a bare marker or an unknown category is not a valid exemption. That is precisely an
      exemption shape, and it is a better one than an external enumerated list: it sits at the violating
      site — the same locality complaint that motivates this whole rule — and it is deleted by the same
      edit that removes the site, which a list is not. So this scanner **follows** the shape instead of
      establishing one. The only thing borrowed from the ratchet half (`sync-fence-baseline.json`) is
      the exact-count assertion, and it counts *sites excused by a valid marker*, never markers, so the
      tally cannot be padded by a comment with nothing behind it.
      **One measured false-positive class, found by running it rather than by reasoning about it:** the
      first run over `src/` reported **ten** sites, of which **eight** were the `ErrorMessage` of a
      `[RegularExpression]` attribute ("BaseUri must start with wss:// or ws://."). The fix is a
      host-shape rule — the character after `://` must be able to begin a host — not an attribute
      carve-out, and every occurrence of every scheme is examined rather than the first, because that
      prose quotes two. Both shapes are pinned by tests
- [x] 6.2 A second scanner: every provider client type has a row in the §5.5 conformance record. Fails
      naming the client and the file that declares it, so a new provider ships with a status — including
      the status *not characterised*, which is a legal value
      **Done — `ConformanceRecordScanner` + `ConformanceRecordGuardTests`.** A provider client type is
      a non-abstract class whose base list names `SpeechSynthesizer` or `SpeechRecognizer`; the record
      gains a **Client type** column and the guard fails naming the type and the declaring file.
      Fourteen types, fourteen rows (`LmntSpeechSynthesizer` owns two — one class, two transports).
      **A weakness caught by measuring instead of assuming:** the first implementation searched the
      whole record for the type name in backticks. Measured against the real file, **six of the
      fourteen** types are already named that way in the narrative prose — `AssemblyAiSpeechRecognizer`
      three times, `LmntSpeechSynthesizer` three times — so a provider could have passed on a mention
      in a paragraph about some other defect. A guard that accepts prose as a row certifies exactly the
      omission it exists to catch. It now reads the **second cell** of a table row and nothing else.
      Two deliberate shapes: the check is **presence, never verdict** (`not characterised` passes, which
      is the whole reason that value exists), and the exclusion is by **package** —
      `Verbara.Sdk.VoiceAi.Testing`, whose charter is in-memory doubles — rather than by a `Fake` name
      prefix, because a package boundary is a decision somebody made and a naming convention is one
      somebody can drift away from silently. Added beyond the task: the guard also runs **in reverse**,
      failing on a row whose client type no longer exists in `src/`, since a row nobody is forced to
      update reads as coverage of a provider that shipped away
- [x] 6.3 Liveness self-tests for both, with a conservative `MinimumScannedFiles` floor below the real
      count and the real count named in the comment — the established shape, so a broken locator fails
      instead of reporting a clean scan of nothing
      **Done — three liveness self-tests, not two.** Both guards assert `MinimumScannedFiles = 500`
      against a real count of **864** (`src/`, obj/bin and generated files excluded), with the real
      count named in the comment. The conformance guard gets a **second** one the task did not ask for
      and needs: it asserts the record file resolves and exceeds 5 000 characters, because a moved or
      renamed record would otherwise turn the whole guard into an assertion about an empty string
- [x] 6.4 Detector unit tests: true positive with exact file and 1-based line; immunity for the same
      text in a comment, an XML doc and a plain string literal. `Verbara.Sdk.Governance.Tests` has
      **zero** `ProjectReference`s by design — neither scanner may add one
      **Done — 35 detector unit tests across the two scanners, zero `ProjectReference`s added.** True
      positives pin exact path and 1-based line. Immunity is pinned for the three shapes the task names
      — comment, XML doc, plain string literal — plus the two this repo actually produces: vendor
      documentation links inside `<see href="…"/>` (which the record and the options types are full of)
      and the `[RegularExpression]` `ErrorMessage` prose that the first run flagged eight times. Marker
      validity is pinned in both directions: a valid marker excuses, while a bare marker, an unknown
      category, an empty reason, and a marker separated from its site by code all still fail
- [x] 6.5 Negative-test both guards end to end: introduce the violation, watch the guard fail naming
      file and line, remove it, watch the suite return to green
      **Done — three end-to-end mutations, each introduced into `src/`, observed, then removed.**
      (1) An inlined production endpoint added to `GoogleSpeechRecognizer` — guard failed naming
      `src/Verbara.Sdk.VoiceAi.Stt/Google/GoogleSpeechRecognizer.cs:20` and the endpoint text; removed,
      green. (2) A new `MutationProbeSpeechRecognizer` with no row — conformance guard failed naming the
      type and `src/Verbara.Sdk.VoiceAi.Stt/MutationProbeSpeechRecognizer.cs:3`; removed, green.
      (3) A record row retyped to a client that does not exist — the reverse-direction test failed
      naming `RetiredVendorSpeechRecognizer`; restored, green. Full project green at **99 tests**
      (from 64) after each restore
- [x] 6.6 `docs/decisions/0048-wire-conformance-by-live-probe-with-negative-control.md` — the file is
      already on disk, `Status: Accepted`, dated 2026-08-15; use that exact filename. 0045 / 0046 / 0047
      are claimed by the open changes, so 0048 was the next free number. Content: the live probe with a
      negative control as an evidence class; the **probe-depth rule from §5.11**, which is what
      separates a sufficient handshake check from an insufficient one; the four defect classes and the
      single root cause (no test in this repository has ever compared the SDK's wire behaviour against a
      real vendor endpoint); the public-API decision from §3.2 and the authentication decision from
      §4.2; and why none of the open changes could host this work — a route fix
      is production behaviour, which the test substrate explicitly cannot carry; these are not parse
      defects, because the bytes never arrive or arrive on the wrong frame type; and they are not drift,
      because they are static, present-day, and were wrong on the day the code was written. Related:
      ADR-0041 (recordings as the provider evidence class), ADR-0043 (evidence produced off the PR path)
      **Done — as a dated `## Addendum`, because the ADR was already `Accepted` and shipped.** The task
      assumed the file was a stub to be filled; it is not. ADR-0048 landed in **#174** on 2026-08-15
      with 401 lines, and the house rule is that an `Accepted` ADR is never edited. Checked for
      precedent rather than improvised: ADR-0038 and ADR-0039 both carry `## Addendum (YYYY-MM-DD) — …`
      sections appended after acceptance, one of them a correction to a numbered decision. That is the
      form used here, and the original text is untouched.
      **Most of what §6.6 asked for was already in the ADR** and an earlier keyword search of mine
      missed it: the probe-depth rule from §5.11 is **D9** (verbatim, including the Speechmatics
      counterexample as its strongest argument), the public-API principle is **D7**, the four defect
      classes and the single root cause are the Context and Classes A–D, and "why none of the open
      changes could host this work" is **Option C** under *Alternatives considered*. The addendum adds
      only what was genuinely absent: **A1** D8's example is stale (Cartesia and AssemblyAI were
      unprobed when it was written and are not now) while D8's rule stands; **A2** the §3.19 finding as
      a rule — a control that cannot fail discards the route claim, not the probe, and is a property of
      the host on the day measured rather than a permanent verdict; **A3** D7's concrete outcome for
      `SpeechmaticsOptions.BaseUri` with both alternatives rejected by name, the consequence for
      existing callers, and an explicit label that the rejection reasoning is **design reasoning, not
      measurement**; **A4** header auth as the chosen remedy with the measured/inferred split restated;
      **A5** the two guards that turn D1 and D8 from prose into build failures
- [x] 6.7 Add the ADR-0048 **and ADR-0049** rows to `docs/decisions/README.md` in numeric order, matching the existing row
      format (link, one-sentence summary, status and date).
      **Closed 2026-08-17 with no edit — both rows were already there**, added by `926fd413` (#174), the
      PR that proposed this change. The task was written expecting a later close-out to add them, so
      this is the §2.8 "verify before scoping" case: the gap was assumed from the task list rather than
      from the tree. Both rows carry link, one-sentence summary, status and date, and sit in numeric
      order after ADR-0044. Verified beyond what was asked, because a glance at the tail proves nothing
      about the middle: **every one of the 46 ADR files on disk has a README row** (`0001`–`0044`,
      `0048`, `0049`). The apparent gap at `0045`–`0047` is correct and must not be "fixed" — those
      numbers are **reserved** by open openspec changes via their `decision_ref` (`Sdk/ADR-0045`,
      `Sdk/ADR-0046`, `Sdk/ADR-0047`) and their ADR files do not exist yet; ADR-0048 and ADR-0049 both
      cite 0046 and 0047 as the neighbouring layers by number
- [x] 6.8 `docs/guides/provider-recording-protocol.md` — add the probe method as a named section: the
      controlled comparison, the mandatory negative control, and the governing epistemic rule *"a vendor
      asserting X is evidence; a vendor not mentioning Y is not."* Section 4's redaction rules already
      cover the probe and are referenced rather than restated.
      **Closed 2026-08-17** as **§11**, appended rather than inserted: §4 (redaction), §5 (provenance)
      and §7 (terms) are referenced by number from other guides and from ADR-0048, so renumbering to
      place the probe next to §3 would have broken live cross-references to buy adjacency. Five
      subsections — the controlled comparison, the mandatory negative control, the epistemic rule, the
      four evidence classes, and handling. Two things the task did not ask for but the train earned and
      would otherwise be lost with the scratch probe scripts: the corollary that **a measured tolerance
      is weaker ground than a stated contract** (the §3.6f trade, and §3.18's padding decision in the
      other direction), and that **a control refuting its own hypothesis is a finding** — §3.18's arm I
      predicted a damaged transcript and returned 10/10, which narrowed the shipped claim to duration
      arithmetic. §4 is referenced, not restated, and the identifier-value rule is named as the probe's
      instance of it rather than duplicated
- [x] 6.9 `docs/guides/provider-test-substrate.md` — state plainly that a green provider suite is not
      evidence of route, authentication or frame-type conformance, with these six defects as the
      demonstration, and point at the §5.5 record for what has actually been checked.
      **Closed 2026-08-17** as two named subsections under the existing §5 *Where the substrate does not
      reach* — the honest home, since this is a limit of the substrate and not a new topic. The
      pre-existing drift paragraph became *Recordings age*; the new *A green suite is not evidence of
      conformance* leads, because it is the graver limit: drift is about a fixture aging, this is about
      the suite never having tested the thing at all. Carries the closed-loop mechanism (fake and client
      written by the same author from the same reading, so the suite compares the client to the author's
      belief), the six defects as a table with what was green while each shipped, and the §5.5 pointer
      with *not characterised* named as distinct from correct. Class D is called out specifically: the
      handshake **succeeded**, so a test asserting the connection opened has asserted nothing about the
      credential. Shipped with §6.8 rather than separately — the new text cites §11 of the recording
      protocol, and splitting them would have merged a dangling cross-reference
- [x] 6.10 `CHANGELOG.md` — one `[Unreleased]` entry under `### Fixed`. This changes **shipped**
      behaviour in `Verbara.Sdk.VoiceAi.Tts` and `Verbara.Sdk.VoiceAi.Stt`, not test behaviour. State
      the blast radius per provider without inflating it: Speechmatics **STT** could never authenticate,
      so every caller of `SpeechmaticsSpeechRecognizer` is affected and no option contained it; Cartesia
      and ElevenLabs affect every caller of those synthesizers and previously completed successfully
      with zero audio; Speechmatics TTS has never reached the vendor; LMNT affects only callers who set
      `Transport = Http`
      **Done — and the per-provider blast radius was already there, entry by entry.** Each behavioural
      fix in this change carried its own `[Unreleased]` entry as it shipped, so what §6.10 describes is
      satisfied across the entries already in `CHANGELOG.md` — Speechmatics STT, Cartesia, ElevenLabs,
      Speechmatics TTS and LMNT each state their own radius, and the `Changed — BREAKING` entry for
      ADR-0050 names all eight affected clients. What this task adds is the entry this final block
      owes: `### Added — two governance guards so provider conformance stops depending on memory`,
      which states plainly that **no shipped behaviour changes**, describes both guards, and records
      the stale-inventory finding (three of four exemption sites already remediated) and the AssemblyAI
      no-failing-control result
- [x] 6.11 State the residue explicitly so no omission reads as an oversight, and state it at the
      resolution §1 now supports: Cartesia STT — route and auth verified with two controls, **frames not
      exercised**; Cartesia **TTS** — route and auth verified, **frame inventory still not characterised**
      because the probe's synthesis request was malformed, so its Class B finding still rests on the
      2026-08-14 documentation read; AssemblyAI STT — route verified, swallow defect §4.15 confirmed;
      Deepgram STT — route verified, **frames not exercised**; Speechmatics STT — authentication and the
      first two frame types now measured, the rest of the frame inventory **not characterised**; the
      two remaining HTTP batch recognizers — a live capture without a negative control, its own weaker class;
      the LMNT WebSocket path; the Speechmatics TTS body fields; and Azure TTS's weaker evidence class.
      Each is a row in §5.5 with its own evidence class, not a silent gap and not a shared verdict

      **Done — by correcting the record's existing residue section rather than writing a second one.**
      `docs/guides/provider-wire-conformance.md` already named the residue surface by surface; three
      bullets had gone stale against measurements taken after they were written, and a stale residue
      statement is worse than none because it reads as current. Corrected: **Speechmatics STT**
      narrowed twice — the 2026-08-18 session measured `word_delimiter` inside
      `RecognitionStarted.language_pack_info`, `attaches_to`, `metadata.transcript` on all three finals
      and all eight partials, and the inter-segment glue whitespace finals carry — and it now names the
      two unmodelled frame kinds seen in the same run (`AudioAdded` ×29, `Info` ×2). **Speechmatics
      TTS** is down to the `voice`-in-body question alone: the 2026-08-17 capture answered the
      `language`/`sample_rate` half by observation, and the note records that the route fix made the
      remaining half *harder* to reach, since the client no longer sends `voice` at all. **AssemblyAI
      STT** gains its own bullet — route *not controllable*, which is different from unprobed, with a
      pointer to ADR-0048 A2. §6.11's own text called AssemblyAI's route "verified"; §3.19 measured
      otherwise, and the record follows the measurement
## 7. Verification

- [ ] 7.1 `dotnet build Verbara.Sdk.slnx` — 0 warnings, 0 errors (`TreatWarningsAsErrors`)
- [ ] 7.2 `dotnet test Verbara.Sdk.slnx --filter "Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike"`
      green — the four-exclusion filter `ci.yml` actually uses
- [ ] 7.3 `aot-validate.yml` green. The new DTOs are source-generated and registered in their contexts;
      no reflection is introduced. The workflow is the proof, not the assumption
- [ ] 7.4 `PublicAPI.Unshipped.txt` for `src/Verbara.Sdk.VoiceAi.Tts/` and
      `src/Verbara.Sdk.VoiceAi.Stt/` — unlike the sibling DTO change, this one **does** move public API
      (§3.2, §4.2, and possibly §2.10 and §3.10). Review the diff line by line and name every entry in
      the CHANGELOG; a surprise entry means the scope was wrong
- [ ] 7.5 **Re-probe every fixed surface against the live endpoint**, with the negative control still
      returning its known-wrong status on the same host: each corrected route returns a success status,
      each corrected frame path yields non-zero audio bytes, and the Speechmatics STT session opened by
      the shipped code path reaches `RecognitionStarted` instead of closing `4001` — the probe reads
      past the upgrade or it has verified nothing (§5.11). This — not the suite — is the close-out
      evidence for §2, §3 and §4.1
- [ ] 7.6 The silent-success assertion: a Cartesia synthesis that reaches `done` having produced zero
      audio must fail the suite. Negative-test it by reverting the frame-type fix locally, watching the
      test go red, and restoring
- [ ] 7.7 Where a surface could not be re-probed for want of a credential, the close-out record says so
      and the surface stays *not characterised*. A task is not closed by a green fake, and a task closed
      on a pinned vendor document (§2.1) is a **closed task with an unverified surface** — the task
      ledger and the §5.5 record say different things about it, deliberately
- [ ] 7.8 `openspec validate provider-wire-protocol-conformance --type change --strict` clean
- [ ] 7.9 CI green on the PR, zero warnings; enqueue with `gh pr merge <pr> --auto` (merge queue —
      never `--squash` / `--delete-branch`)
