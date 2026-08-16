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
- [ ] 2.3b **The Class B fix converts an ignored margin into a live defect, and fixing §2.3/§2.1
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
- [ ] 2.3c **The fake seam bypasses the credential entirely, at six sites — so no fake can catch an
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
      **Closes when** all six are reshaped and each fake carries a credential assertion
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
- [ ] 2.10 The silent-completion **signal**, which does not fall out of the frame-type fix in 2.1 and is
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
      has no code behind it
- [ ] 2.10a The signal from 2.10 is **not Cartesia-only — measured, not assumed.** The open question
      this task originally carried was answered by the 2026-08-15 probe: ElevenLabs emits only text
      frames (`{alignment, audio, isFinal, normalizedAlignment}`, audio base64) and then closes
      **`1000` normal**. `src/Verbara.Sdk.VoiceAi.Tts/ElevenLabs/ElevenLabsSpeechSynthesizer.cs` reads
      only `WebSocketMessageType.Binary`, so it completes **successfully with zero bytes**, exactly as
      Cartesia does. Both synthesizers get the same observable form and the same counter — there is no
      longer a provider for which the spec's zero-audio requirement is satisfied by luck.
      Additionally, and worse than first recorded: ElevenLabs sends its **auth error** as text too
      (`{code, error, message}`), so a bad credential loses the audio and the reason in the same
      branch. Fixing the frame type in §2.1 fixes both, but assert them as two separate tests — a
      normal synthesis yields audio, and a rejected credential surfaces an error
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
- [ ] 2.15 **Drift caused into a neighbouring open change — recorded here, not fixed here.**
      `openspec/changes/provider-dto-robustness-fences/proposal.md` counts response members by name
      and states *"Tts contributes exactly one response member (`CartesiaTtsControlMessage.Type`)"*.
      That type no longer exists: §2.2 replaced it with `CartesiaTtsServerMessage`, which contributes
      one non-nullable response member and four nullable ones, and §2.4 added
      `ElevenLabsAudioOutput` with two more. Its **24** is now wrong in both the name and the
      number. Editing another change's proposal from this one is the scope-widening §2.8 forbids, so
      the correction belongs to whoever next picks that change up — it must **re-run its inventory**
      rather than adjust the figure by hand, because this commit is unlikely to be the only source
      of drift since it was counted

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
- [ ] 3.3 Everything else the client sends is already correct — bearer auth, content type, sample rate.
      Whether `voice` should *also* remain in the body, and whether the `language` and `sample_rate`
      body fields are accepted as sent, are **not verified**: only the route was isolated. Record them as
      not verified; do not resolve them by inference
- [ ] 3.4 The competing hypothesis is closed and must be recorded as closed so it is not reopened: the
      shipped default voice `eleanor` is absent from the vendor's published four-voice list **but
      returns 200**, so the published list is incomplete and `SpeechmaticsOptions.Voice` is fine. One
      delta, not three. Do not change the default
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
- [ ] 3.6b **The error frame that says why is thrown away — not fixed here, and it is `Sdk/ADR-0049`
      D1 measured on a sixth surface.** `LmntSpeechSynthesizer.ReceiveWsFramesAsync` terminates on
      `notification.Error == "error"`, comparing an error *message* against the literal string
      `"error"`, which no real message equals. Both live failures — `{"error":"model: Input should be
      'aurora', …"}` from §3.6a and `{"error":"Invalid API key"}` from the invalid-credential control —
      therefore fall through, the socket then closes `1002`, `catch (WebSocketException) { break; }`
      swallows it, and the caller gets an **empty stream and no exception**. That is D1 (silent discard
      of a failure frame) and D2 (zero output as success) together, with a D4 control, on the LMNT WS
      surface. Fixing it changes behaviour — synthesis that silently yields nothing would start
      throwing — so it belongs with the ADR-0049 train and its D1 remedy, not inside a route fix.
      **Closes when** ADR-0049's D1 remedy covers this site, or a decision records why it does not
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
- [ ] 3.6d **The half-close is a class, not an LMNT bug, and six sites are unmeasured.**
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
      transfer** — it must be measured, not carried over
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
- [ ] 3.16 Neither §3.14 nor §3.15 was in this change when it was written — both were found by auditing
      `wiremock-http-provider-substrate`'s three blocked tasks on 2026-08-15, after this change had
      already been merged. Sweep `scripts/capture-provider-recording.py` for the **remaining** plans and
      record, per plan, whether its request matches what the shipped client sends. Two were wrong out of
      the two that were checked; the rest are unexamined, which is not the same as correct

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
- [ ] 4.6a Record the **swallowed `Error` frame — this one *is* a defect, and it is what makes §4.1
      silent.** The same `continue` that correctly skips `Info` also skips `Error`. Speechmatics signals
      in-band failure as a message, so a session the vendor rejects yields no exception, no log and no
      transcript: the caller observes an `IAsyncEnumerable` that completes normally and empty. That is
      why the `4001 not_authorised` defect in §4.1 presents to a consumer as "STT returns nothing"
      rather than as an error, and it is why a green suite never caught it. Surface `Error` (and decide
      the same question for `Warning`) so an in-band rejection reaches the caller. This is the STT
      counterpart of the TTS silent-completion signal in §2.10, and it binds to the same spec
      requirement — a provider that produced nothing does not report success
- [x] 4.6 Record that the live `RecognitionStarted` field set **confirms** the committed fixture: the
      live top-level set is `{message, orchestrator_version, id, language_pack_info}` with
      `language_pack_info` an object, and
      `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Recordings/speechmatics-stt/recognition-started-frame.json`
      matches it exactly, correctly nesting `word_delimiter` **inside** `language_pack_info`. The
      fixture was right; nothing in it changes. Upgrade its provenance sidecar's evidence class the way
      §5.9 does for the Deepgram TTS sidecars — from documentation-derived to "conforms to what the
      service actually sends"
- [ ] 4.7 `src/Verbara.Sdk.VoiceAi.Stt/Speechmatics/SpeechmaticsSpeechRecognizer.cs` line 170 —
      `if (sb.Length > 0 && !string.IsNullOrEmpty(alt.Content)) sb.Append(' ');` space-joins every token
      unconditionally. Three vendor-supplied signals are ignored; each is a separate sub-task below.
      **Closes when** the committed fixtures assemble to text with no spurious separator
- [ ] 4.8 `word_delimiter` — sent on `RecognitionStarted` inside `language_pack_info`, and discarded:
      the recognizer drops every non-transcript message and `VoiceAiSttJsonContext.cs` has no DTO for
      the start message at all. Add the DTO, capture the delimiter at session start, and join with it.
      `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Recordings/speechmatics-stt/recognition-started-frame.json`
      already carries `"word_delimiter": " "` nested inside `language_pack_info`, and §4.6 confirms that
      shape against the live session
- [ ] 4.9 `attaches_to` — `SpeechmaticsResult` in
      `src/Verbara.Sdk.VoiceAi.Stt/Internal/VoiceAiSttJsonContext.cs` models only `alternatives`. Add
      `type` and `attaches_to`; a result marked as attaching to its predecessor gets no leading
      delimiter. `.../Recordings/speechmatics-stt/add-transcript-frame.json` already carries a
      `"type": "punctuation"` result with `"attaches_to": "previous"`
- [ ] 4.10 `metadata.transcript` — `SpeechmaticsTranscriptMessage` models only `message` and `results`,
      while the vendor publishes the assembled segment. Decide and record: the vendor's assembled text
      is the authority for the transcript, and local assembly survives only for what that text does not
      carry. The same fixture carries `"transcript": "El equipo revisó el informe esta mañana."`
- [ ] 4.11 Confidence today is the mean of `alternatives[0].confidence` across results. If the transcript
      text stops coming from local assembly, confidence still must — say so in the code and in the
      change record rather than letting it quietly change meaning
- [ ] 4.12 Regression test for the reported shape, asserted against the committed fixture and its actual
      text: `.../Recordings/speechmatics-stt/add-transcript-frame.json` carries
      `"transcript": "El equipo revisó el informe esta mañana."`, so the assembled segment must end
      `… mañana.` and not `… mañana .`. Use that sentence, or author a new fixture and say so — do not
      assert a sentence no committed fixture contains
- [ ] 4.13 A second test with a non-space delimiter — a language pack declaring an empty
      `word_delimiter` assembles with no separators — so the fix is *use the vendor's delimiter* and not
      *special-case punctuation*
- [ ] 4.14 The new and widened DTOs from §2.2, §2.4, §3.8 and §4.8–§4.10 — plus the unmodelled `Info`
      frame observed in §4.5, which is routed to that change and not modelled here — land inside the
      reachability
      closure `provider-dto-robustness-fences` counts (its §1.2 figures) and inside its coverage guard
      (its §8.3). Flag it in this change's record so those numbers are re-derived; **do not edit that
      change's artifacts from here**
- [ ] 4.15 **AssemblyAI STT — the seventh defect, and the one that makes the swallow a class.**
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
      allow-list (`Sdk/ADR-0049` D1)
- [ ] 4.16 Audit **every** provider receive loop in `src/Verbara.Sdk.VoiceAi.Stt/` and
      `src/Verbara.Sdk.VoiceAi.Tts/` for the allow-list filtering shape — a `continue` or a
      message-type equality test that lets unanticipated frames fall into a discard branch. A first
      pass already found **five** sites, not the three with a live symptom: beyond Speechmatics,
      AssemblyAI and ElevenLabs-by-frame-type, `CartesiaSpeechRecognizer.cs:165` (`Type !=
      "transcript"`) and `DeepgramSpeechRecognizer.cs:120` (`Type != "Results"`) are the same
      construction. Those two are **latent, not clean** — their vendors validate credentials at the
      handshake so no auth frame reaches the branch today, but every other error either vendor defines
      does, and a vendor moving validation in-band converts them with no line changing. Finish the
      sweep across the remaining surfaces and record the result per surface even where the answer is
      "no such branch", because a clean loop is evidence and an unexamined one is not
- [ ] 4.17 Remediate the two **latent** sites from §4.16 (`CartesiaSpeechRecognizer.cs:165`,
      `DeepgramSpeechRecognizer.cs:120`) under the same D1 shape as §4.6a and §4.15. No measured defect
      forces these — that is precisely the argument for doing them here rather than after one bites,
      and `Sdk/ADR-0049` binds all five sites, not the three with symptoms. If they are deferred
      instead, the deferral is recorded with that reasoning rather than left as silence

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

- [ ] 6.1 A Governance scanner in `Tests/Verbara.Sdk.Governance.Tests/`: a provider's production
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
- [ ] 6.2 A second scanner: every provider client type has a row in the §5.5 conformance record. Fails
      naming the client and the file that declares it, so a new provider ships with a status — including
      the status *not characterised*, which is a legal value
- [ ] 6.3 Liveness self-tests for both, with a conservative `MinimumScannedFiles` floor below the real
      count and the real count named in the comment — the established shape, so a broken locator fails
      instead of reporting a clean scan of nothing
- [ ] 6.4 Detector unit tests: true positive with exact file and 1-based line; immunity for the same
      text in a comment, an XML doc and a plain string literal. `Verbara.Sdk.Governance.Tests` has
      **zero** `ProjectReference`s by design — neither scanner may add one
- [ ] 6.5 Negative-test both guards end to end: introduce the violation, watch the guard fail naming
      file and line, remove it, watch the suite return to green
- [ ] 6.6 `docs/decisions/0048-wire-conformance-by-live-probe-with-negative-control.md` — the file is
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
- [ ] 6.7 Add the ADR-0048 **and ADR-0049** rows to `docs/decisions/README.md` in numeric order, matching the existing row
      format (link, one-sentence summary, status and date)
- [ ] 6.8 `docs/guides/provider-recording-protocol.md` — add the probe method as a named section: the
      controlled comparison, the mandatory negative control, and the governing epistemic rule *"a vendor
      asserting X is evidence; a vendor not mentioning Y is not."* Section 4's redaction rules already
      cover the probe and are referenced rather than restated
- [ ] 6.9 `docs/guides/provider-test-substrate.md` — state plainly that a green provider suite is not
      evidence of route, authentication or frame-type conformance, with these six defects as the
      demonstration, and point at the §5.5 record for what has actually been checked
- [ ] 6.10 `CHANGELOG.md` — one `[Unreleased]` entry under `### Fixed`. This changes **shipped**
      behaviour in `Verbara.Sdk.VoiceAi.Tts` and `Verbara.Sdk.VoiceAi.Stt`, not test behaviour. State
      the blast radius per provider without inflating it: Speechmatics **STT** could never authenticate,
      so every caller of `SpeechmaticsSpeechRecognizer` is affected and no option contained it; Cartesia
      and ElevenLabs affect every caller of those synthesizers and previously completed successfully
      with zero audio; Speechmatics TTS has never reached the vendor; LMNT affects only callers who set
      `Transport = Http`
- [ ] 6.11 State the residue explicitly so no omission reads as an oversight, and state it at the
      resolution §1 now supports: Cartesia STT — route and auth verified with two controls, **frames not
      exercised**; Cartesia **TTS** — route and auth verified, **frame inventory still not characterised**
      because the probe's synthesis request was malformed, so its Class B finding still rests on the
      2026-08-14 documentation read; AssemblyAI STT — route verified, swallow defect §4.15 confirmed;
      Deepgram STT — route verified, **frames not exercised**; Speechmatics STT — authentication and the
      first two frame types now measured, the rest of the frame inventory **not characterised**; the
      two remaining HTTP batch recognizers — a live capture without a negative control, its own weaker class;
      the LMNT WebSocket path; the Speechmatics TTS body fields; and Azure TTS's weaker evidence class.
      Each is a row in §5.5 with its own evidence class, not a silent gap and not a shared verdict

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
