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

- [ ] 1.1 Commit the current scoreboard into this change directory as working evidence, one row per
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
      got one, which is exactly the conflation §1.9 and the spec's evidence-class rule exist to prevent
- [ ] 1.2 The four **WebSocket streaming recognizers** are no longer unknown at all: all four were
      probed on 2026-08-15 with the §5 method, and §1.3–§1.5a record what each returned. Their classes
      still differ — Cartesia STT and AssemblyAI STT carry both controls, Deepgram STT carries a
      wrong-path control but **no invalid-credential control**, so its validation point is *not
      established* — and the table MUST keep that difference visible rather than giving the four one
      shared verdict on the strength of having all been touched
- [ ] 1.3 **Deepgram STT — route verified 2026-08-15 with a negative control; frames not exercised.**
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
- [ ] 1.3a **Run the missing invalid-credential control against Deepgram** — TTS and STT, same host,
      deliberately malformed key, alongside the wrong-path control already taken. It is the one surface
      in the scoreboard whose validation point rests on inference, and it is the surface every other
      row's "handshake vs in-band" framing was originally reasoned from, so leaving it uncontrolled
      leaves the weakest evidence under the most load. Two outcomes, both useful: a `401` at the
      handshake confirms what was assumed and costs one probe, or a `101` followed by an error frame
      makes it **four** in-band surfaces and puts `DeepgramSpeechRecognizer.cs:120` from §4.16 in the
      live-symptom set rather than the latent one. Record whichever, with its date
- [ ] 1.4 **Speechmatics STT — probed 2026-08-15 to the first protocol exchange, and it does not
      authenticate.** The route resolves and the upgrade completes; the credential is then rejected
      in-band with close code `4001 not_authorised`. That is the defect fixed in §4.1–§4.4 — in this
      change, not a follow-on. Observed live: the `Info` frame (§4.5) and `RecognitionStarted` (§4.6).
      **Not** observed: any `AddTranscript` frame — the sessions that authenticated were opened to
      establish the remedy and no audio was streamed — so the frame inventory beyond those two message
      types stays **not characterised**, and the assembly finding from §4.7 onwards remains derived from
      the vendor's message set and the committed fixtures rather than from live transcript frames
- [ ] 1.5 **Cartesia STT and AssemblyAI STT — credentials obtained 2026-08-15; both now probed with
      two controls.** This supersedes the original *not characterised, no credential* entry, which was
      true when written. `src/Verbara.Sdk.VoiceAi.Stt/Cartesia/CartesiaSpeechRecognizer.cs`: wrong path
      `404`, invalid credential `401`, real `101` — route and auth OK, **frames not exercised**.
      `.../AssemblyAi/AssemblyAiSpeechRecognizer.cs`: wrong path `404`, invalid credential **`101`
      followed by an error frame** (in-band auth), real `101` with first frame `Begin`
      `{configuration, expires_at, id, type}` — route OK, and the invalid-credential control is what
      exposed §4.15. Record both with their controls; do not carry the frame halves further than the
      evidence goes
- [ ] 1.5a **Google STT — promoted from `uncontrolled` to a controlled probe, 2026-08-15.** Wrong path
      `404`, invalid credential `400 API_KEY_INVALID`, real key `400 RecognitionAudio not set` — the
      last of which is the vendor accepting the credential and rejecting the empty payload, i.e. past
      auth into argument validation. Its row moves out of the shared HTTP-batch line in §1.6, which
      now covers only the two Whisper recognizers. Note for the record that the SDK's `?key=` query
      parameter **is** a supported mechanism on `speech:recognize`: Google's own auth page does not
      list API keys, and reading that silence as a defect would have been wrong — the probe settled it
- [ ] 1.6 The **two remaining HTTP batch recognizers** — `.../Whisper/WhisperSpeechRecognizer.cs` and
      `.../Whisper/AzureWhisperSpeechRecognizer.cs` — are a
      different shape (request/response, no frame protocol), and each already carries a committed
      recording under `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Recordings/` whose provenance sidecar
      declares `"class": "recorded"` — a **live capture**, taken **without a negative control**. That is
      real route evidence of its own weaker class: do not call it unverified and do not put it in the
      same column as §1.3. Either re-probe with a control or record the class as it stands.
      `.../Google/GoogleSpeechRecognizer.cs` **left this group on 2026-08-15** — see §1.5a, where it was
      re-probed with both controls — so this task covers two recognizers, not three, and the third is
      the worked example of what "re-probe with a control" produces
- [ ] 1.7 Azure TTS is recorded as previously proven working. That is a **weaker evidence class** than
      the 2026-08-15 Deepgram probe: it was not re-probed with a negative control. Either re-probe it
      or record the weaker class explicitly — do not promote it by placing it in the same column
- [ ] 1.8 The LMNT **WebSocket** path is untouched by the HTTP finding and is **not verified**.
      `LmntSpeechSynthesizer.cs` builds `wss://api.lmnt.com/v1/ai/speech/stream` at line 265 with no
      option to override it. "Not affected by this finding" is not "checked"; say the second thing only
      if it was done
- [ ] 1.9 Where no capture credential exists for a surface, the answer is **not characterised — no
      credential**, recorded as such. `docs/guides/provider-recording-protocol.md` section 7 already
      carries the per-provider terms verdicts; a `not-cleared` terms verdict blocks storing Output but
      does **not** block a route probe that stores nothing, and the two must not be conflated
- [ ] 1.10 Every probe run in this section obeys `docs/guides/provider-recording-protocol.md` section 4:
      no Output stored or printed, correlating identifiers never echoed. This is how the 2026-08-15
      Deepgram and Speechmatics runs were conducted and the instrument in §5 inherits it
- [ ] 1.11 The output of this section is one table, committed, that §5.5 promotes into `docs/`. Every
      TTS and STT surface gets a row with route status, frame status, evidence class and date. A surface
      with no row is the failure mode this section exists to prevent

## 2. Class B — audio arrives on a text frame (Cartesia, ElevenLabs)

- [ ] 2.1 `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs` — the receive loop yields
      only `WebSocketMessageType.Binary` frames and treats every text frame as a control message,
      breaking only on `done` / `error`. Decode the vendor's base64 audio field from the JSON text frame
      and write those bytes to the channel. **Closes when** a synthesis over the documented text-frame
      shape yields non-zero audio, and when the frame inventory from a live probe matches what the loop
      now consumes. Absent a credential, the pinned vendor document named in the fixture's provenance
      sidecar closes **the task and not the verification**: documentation is what produced these defects
      and would not have caught any of the frame-type ones, so the surface stays *not characterised* in
      the §5.5 record and §7.7 applies
- [ ] 2.2 `src/Verbara.Sdk.VoiceAi.Tts/Internal/VoiceAiTtsJsonContext.cs` — `CartesiaTtsControlMessage`
      models only `type`. Add the audio-carrying member (or a separate chunk DTO), register it in the
      context, and keep the discriminator branch that already recognises `done` / `error`
- [ ] 2.3 `src/Verbara.Sdk.VoiceAi.Tts/ElevenLabs/ElevenLabsSpeechSynthesizer.cs` — the loop carries the
      defect as a comment: *"Only yield binary frames; skip text messages (alignment, metadata)."*
      Decode `AudioOutput.audio` from the text frame instead. **Closes when** the committed fixture in
      2.6 round-trips through the synthesizer to the audio bytes it encodes
- [ ] 2.4 ElevenLabs has **no server-message DTO at all** — `VoiceAiTtsJsonContext.cs` declares only the
      outbound `ElevenLabsTextChunk` / `ElevenLabsVoiceSettings`. Add a server DTO for the audio field
      and register it. Alignment members are optional: model them or ignore them, but tolerate them —
      the unmapped-member tolerance rule belongs to `provider-dto-robustness-fences` and must not be
      contradicted here
- [ ] 2.5 Decide, and record, whether the binary branch stays. Neither vendor documents a raw-binary
      mode (both read first-hand 2026-08-14), but a vendor not mentioning a mode is not evidence the
      mode does not exist — so keeping the branch as *tolerated without evidence* costs nothing and
      removing it could break an undocumented path. State which was chosen and on what basis
- [ ] 2.6 `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/elevenlabs-tts/audio-output-frame.json`
      **already carries** the base64 `audio` field plus the `alignment` / `normalizedAlignment`
      structure. It is committed evidence of a shape the shipped client cannot consume. Wire it into the
      test rather than authoring a new fixture — a fixture that already exists and is already unusable
      is the strongest available proof of the defect
- [ ] 2.7 `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/cartesia-tts/audio-chunk-pcm-s16le-8khz.provenance.json`
      records the divergence in its own notes and defers it: *"this fixture is seeded as binary frames
      because that is what the client under test consumes … the divergence needs its own change, not a
      silent fixture edit."* **This is that change.** Re-seed the fixture to the documented text-frame
      shape and update the sidecar's `notes` and `source_schema.method` to say so; the `.raw` bytes
      stay `SyntheticPcm.Triangle`-generated and byte-asserted
- [ ] 2.8 `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Cartesia/CartesiaFakeServer.cs` and
      `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/ElevenLabs/ElevenLabsFakeServer.cs` must send what the vendor
      documents, not what the client currently reads. Scope discipline: `websocket-fake-protocol-contract`
      owns the fake-protocol contract in general and forbids production changes — the fake edits here
      are only those the production fix in 2.1 / 2.3 requires. Do not widen into that change's scope
- [ ] 2.9 A regression test per provider, `Method_ShouldExpected_WhenCondition`: a normal synthesis
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
- [ ] 2.11 Cartesia and ElevenLabs land as **two separate commits**. Cartesia additionally reaches its
      `done` terminator and completes successfully with zero audio — call that out in its commit body,
      because it is the silent-failure case and it is the reason this section is first

## 3. Class A — the request never reaches the vendor (LMNT HTTP, Speechmatics TTS)

- [ ] 3.1 `src/Verbara.Sdk.VoiceAi.Tts/Speechmatics/SpeechmaticsSpeechSynthesizer.cs` — the request is
      built from `_options.BaseUri` with the voice carried in the JSON body
      (`SpeechmaticsTtsRequest.Voice`). The vendor selects the voice by **path segment**:
      `/generate/{voice}` returns `200 audio/wav`, `/generate` returns `404`. **Closes when** the
      corrected route returns a success status against the live endpoint with the negative control still
      `404` on the same host
- [ ] 3.2 The public-API decision, taken explicitly and not smuggled in under a route fix:
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
- [ ] 3.5 `src/Verbara.Sdk.VoiceAi.Tts/Speechmatics/SpeechmaticsOptions.cs` line 23 — the
      `<see href="https://docs.speechmatics.com/tts-api-ref"/>` is a **dead link (404)**. Replace it
      with a live URL or remove the `href`; XML docs ship to consumers of a public MIT package, so a
      dead reference is a shipped defect, not a cosmetic one
- [ ] 3.6 `src/Verbara.Sdk.VoiceAi.Tts/Lmnt/LmntSpeechSynthesizer.cs` line 294 hardcodes
      `https://api.lmnt.com/v1/ai/speech/generate` — there is no option for it — and posts
      `FormUrlEncodedContent`. That returns `404`. A controlled comparison with the same credential
      seconds apart got `200 audio/mpeg` from the documented `/v1/ai/speech/bytes` with a **JSON** body.
      **Three deltas: path, body encoding, response media type.** Fix all three or state which was
      deferred and why
- [ ] 3.7 The media-type delta is the one with consumer-visible consequence: `SynthesizeHttpAsync`
      chunks the response body straight out as if it were raw PCM, and MP3 is not chunkable that way.
      `LmntTtsOptions.Format` defaults to `raw`, but whether sending `format: "raw"` on the JSON body
      yields L16 rather than MP3 is **not verified**. Resolve that by probe before choosing between
      decoding, rejecting, or documenting the format — it decides the shape of the fix
- [ ] 3.8 A JSON body needs a request DTO: add it to
      `src/Verbara.Sdk.VoiceAi.Tts/Internal/VoiceAiTtsJsonContext.cs` and register it;
      `FormUrlEncodedContent` and its `Dictionary<string, string>` go away. The DTO is AOT-source-gen
      only — no reflection, no anonymous objects
- [ ] 3.9 The 3.5 rule — XML docs ship to consumers of a public MIT package, so a wrong reference in
      them is a shipped defect — applied to LMNT, where it is the `404` route itself that is documented:
      `src/Verbara.Sdk.VoiceAi.Tts/Lmnt/LmntTtsOptions.cs` line 20 tells every consumer the HTTP
      transport "Uses `https://api.lmnt.com/v1/ai/speech/generate`", and
      `src/Verbara.Sdk.VoiceAi.Tts/Lmnt/LmntSpeechSynthesizer.cs` line 26 repeats it in the class-level
      `<remarks>`, adding "with form-encoded body fields". Correct both to `/v1/ai/speech/bytes` and a
      JSON body inside the 3.6 commit. A corrected route still documented as the broken one ships the
      defect to every reader of the package
- [ ] 3.10 Decide whether the HTTP base URI becomes an option — this change decides it, it is not
      pre-decided here. `LmntTtsOptions` has none today, and the WebSocket URI at line 265 is hardcoded
      for the same reason. Note the true baseline before arguing from consistency: only three TTS
      providers expose a `BaseUri` option at all — Cartesia, Deepgram TTS and Speechmatics TTS —
      so "like every other provider" is not an available argument. If the decision covers both LMNT
      URIs, say so and change both; otherwise change only the HTTP one and leave the WS path untouched,
      since §1.8 records it as unverified rather than known-good
- [ ] 3.11 Blast radius, stated honestly in the commit and the CHANGELOG: `LmntTtsOptions.Transport`
      defaults to `WebSocket`, so only callers who opt into HTTP are affected. Speechmatics TTS, by
      contrast, has never worked for anyone
- [ ] 3.12 `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Lmnt/LmntFakeServer.cs` and
      `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Speechmatics/SpeechmaticsFakeServer.cs` — the
      fakes must **match on method and path** so a misrouted request fails to match instead of being
      served anyway. Without that, this entire defect class stays invisible to the suite no matter how
      much coverage is added. This is the same property `wiremock-http-provider-substrate` requires of
      its HTTP substrate; reuse it rather than reimplementing it
- [ ] 3.13 LMNT and Speechmatics TTS land as **two separate commits**

## 4. Speechmatics STT — the session never authenticates, and the assembly ignores vendor fields

Two defects in one file. §4.1–§4.6 are Class D — **the credential is rejected in-band, so no session
ever opens**; §4.7–§4.14 are Class C — the frame is read but assembly-governing fields are ignored.
The second is only reachable once the first is fixed, which is why they share a section and not a
commit.

- [ ] 4.1 `src/Verbara.Sdk.VoiceAi.Stt/Speechmatics/SpeechmaticsSpeechRecognizer.cs` line 195 —
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
- [ ] 4.2 Two remedies are **measured** (rows B and C), so the fix is an API-design choice with a
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
- [ ] 4.3 The competing hypothesis is **closed** and must be recorded as closed so it is not reopened:
      the failure is not a credential lacking realtime-STT entitlement, because the same credential
      opened a session through two different channels. Row B exists precisely to kill that explanation —
      the same role `eleanor` plays for Speechmatics TTS in §3.4. The defect is the SDK's auth scheme,
      not the key
- [ ] 4.4 `src/Verbara.Sdk.VoiceAi.Stt/Speechmatics/SpeechmaticsOptions.cs` line 16 documents `ApiKey`
      as "Passed as the `jwt` query parameter" — the broken scheme, shipped to consumers in XML docs,
      the same shipped-defect rule as §3.5 and §3.9. Correct it to whatever §4.2 chooses, in the same
      commit. While there, check the `<see href="https://docs.speechmatics.com/rt-api-ref"/>` on line 12
      the way §3.5's TTS link was checked; it is **not** asserted dead here, only unchecked
- [ ] 4.5 Record the **`Info` frame** — unmodelled, and first in every session. Every Speechmatics
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
- [ ] 4.6 Record that the live `RecognitionStarted` field set **confirms** the committed fixture: the
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

## 5. The conformance probe as a committed instrument

- [ ] 5.1 Codify the method that produced every finding in this change: controlled comparison against
      the live endpoint — same credential, same host, seconds apart — with a **negative control that is
      known wrong**, so a pass is distinguishable from a probe that cannot fail. Nothing is stored
- [ ] 5.2 Decide where it lives and record why. A probe needs live credentials and network egress, so it
      cannot be a required PR check; ADR-0043 is the precedent for evidence produced off the PR path and
      read by a human. Candidates: a script under `tools/` plus a `Category`-gated test excluded by the
      unit-lane filter, or a scheduled workflow. Name the rejected option
- [ ] 5.3 The negative control is mandatory and is part of the recorded output, not a step someone
      remembers to run. Worked example to encode: `wss://api.deepgram.com/v1/speak` with the SDK's
      shipped defaults (`model=aura-2-thalia-en`, `encoding=linear16`, `sample_rate=24000`) returned
      `101 Switching Protocols`; `/v1/speak-does-not-exist` on the same host returned `404 Not Found`
- [ ] 5.4 The probe inherits `docs/guides/provider-recording-protocol.md` section 4 verbatim: no Output
      stored or printed, correlating identifiers (`request_id`, `model_uuid`) never echoed. This is how
      the 2026-08-15 run was conducted; the instrument must not be able to do otherwise
- [ ] 5.5 Promote §1's table into `docs/` as the per-surface conformance record: route status, frame
      status, evidence class, date, negative control present. This is the artifact that makes *not
      characterised* a visible state rather than a gap between rows
- [ ] 5.6 Record the Deepgram TTS measurements as the instrument's worked example: `Metadata` text
      frame, then **37 binary frames of 1920 bytes** (71040 bytes, 1.48 s of linear16 @ 24 kHz), then a
      `Flushed` text frame — exactly what `DeepgramSpeechSynthesizer` expects, and explicitly **not**
      the Class B shape: no text frame carried a long string field, so there is no base64 audio hidden
      in JSON on this surface
- [ ] 5.7 Record one margin as a **margin, not a defect**: the receive loops ignore
      `result.EndOfMessage`, so a text frame exceeding the 65536-byte receive buffer would throw an
      uncaught `JsonException` in the text handler. Largest binary frame measured 1920 bytes (34x
      headroom) and the `Metadata` frame 291 bytes — the vendor would have to grow that frame 225x to
      reach the buffer. State the numbers; do not file it as a bug and do not let a future reader mistake
      the note for one
- [ ] 5.8 Record that synthesis is **non-deterministic**: two runs with identical input produced 1.48 s
      and 1.20 s of audio. This retroactively justifies generating the `.raw` fixtures with
      `SyntheticPcm.Triangle` rather than capturing them — a captured audio fixture could not have been
      asserted byte-for-byte, which is exactly what those fixtures do today
- [ ] 5.9 Upgrade the evidence class of the Deepgram TTS sidecars —
      `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Recordings/deepgram-tts/metadata-frame.provenance.json` and
      `flushed-frame.provenance.json`. The live field sets match the committed synthetic
      (documentation-derived) fixtures **exactly**: `Metadata` = `{type, request_id, model_name,
      model_version, model_uuid, additional_model_uuids[]}`, `Flushed` = `{type, sequence_id}`. Edit the
      sidecars' `source_schema` / `notes` to record "conforms to what the service actually sends" in
      place of "conforms to the docs"; the frame JSON and the `.raw` bytes are unchanged. Upgrade
      **those two sidecars only**: the probe observed exactly two frame types, so
      `warning-frame.provenance.json` and `audio-linear16-16khz.provenance.json` keep their current
      class — the Warning frame and the error paths were never exercised
- [ ] 5.10 The same measurement confirms `model_uuid` and `additional_model_uuids` are really sent and
      really unmodelled by `DeepgramTtsServerMessage` — so the unmodelled-sibling test asserts a real
      condition rather than a hypothetical one. Note that where the test lives. Do **not** add
      `[JsonRequired]`: that instrument belongs to `provider-dto-robustness-fences` and its arity
      condition is not met on a union DTO
- [ ] 5.11 Encode the **depth** rule the Speechmatics run produced — it governs what a probe must *do*,
      not merely what it must compare against. A handshake-only probe is **sufficient** for a vendor
      that authenticates in the HTTP upgrade headers (Deepgram: the `101` proves the credential was
      accepted) and **insufficient** for a vendor that authenticates **in-band** (Speechmatics: the
      `101` proves nothing and the rejection arrives afterwards as close code `4001`). A conformance
      probe must therefore reach the vendor's first protocol exchange, not stop at the upgrade. Had this
      programme stopped at the handshake, Speechmatics STT would have been recorded as verified good
      while being entirely unusable — state that consequence in the instrument and in §6.6; it is the
      strongest argument either has

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
