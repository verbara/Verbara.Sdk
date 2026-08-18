# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]

### Changed — BREAKING: a provider failure now reaches the caller instead of an empty stream

Eight WebSocket speech clients — Cartesia TTS, ElevenLabs TTS, LMNT TTS (WebSocket), Deepgram TTS,
and Deepgram, Speechmatics, AssemblyAI and Cartesia STT — ended a failed session by completing
normally with nothing in it. **This ships in a minor and never in a patch**: code that compiles
unchanged behaves differently, because a call that used to go quiet now throws. That is the point of
the change (`Sdk/ADR-0050`).

- **Three separate doors let a failure out silently, and every one of the eight had all three open.**
  (1) The frame allow-list: a receive loop keeping only the message types it wanted dropped the
  vendor's error frame into the same discard branch as lifecycle noise. (2) **The close code was read
  and thrown away** at all eight — a session the vendor ended `1002`, `1008` or `4001` was
  indistinguishable from one that finished. (3) `catch (WebSocketException) { break; }` turned a
  socket dying mid-stream into normal completion. A surface with a clean allow-list was still silent
  through the other two, which is why the audit widened from the five sites first found to eight.

- **Two new exception types, both rooted at `System.Exception`.** `SpeechProviderFailureException`
  means *the vendor reported a failure*, and carries a `Signal` — `ErrorFrame`, `CloseCode`,
  `Handshake` or `Transport` — plus the vendor's own code and text where the wire supplied them.
  `SpeechProviderEmptyResultException` means *the session ended clean and empty*. Both derive from
  `SpeechProviderException`, which exposes `Provider`. The two types are **evidence about what
  happened, not a retry policy**: nothing in this SDK decides for a caller which of them is worth
  retrying.

- **Cancellation is never a failure.** A caller cancelling its own token, or a barge-in cancelling
  synthesis, ends the stream as it always did. Neither type is raised.

- **Recognition and synthesis are deliberately asymmetric on "empty".** A synthesis that yields no
  audio has failed. A *recognition* that yields no transcript has not — turn detection flushes on any
  trigger, so noise with no speech is a session that correctly produced nothing. The STT rule
  therefore fires only when the vendor sent **no message of any kind**, not when it sent lifecycle
  messages and no words. Each STT surface carries both tests, so the distinction cannot rot.

- **New counter, additive: `tts.syntheses.silent`** on the existing `Verbara.Sdk.VoiceAi.Tts` meter,
  tagged `voiceai.provider`. It counts a synthesis that completed with zero audio chunks, was not
  cancelled and raised nothing — which the eight clients above can no longer do, so what it actually
  reports is the residual they cannot reach: an HTTP-backed synthesizer, or any third-party subclass
  of the public `SpeechSynthesizer` base, returning silence in silence. **`tts.syntheses.failed`
  changes meaning** for anyone already listening: it now absorbs provider failures that used to be
  counted as completed syntheses. That is a correction, not a regression — those sessions had failed.

- **Two branches are closed without a measured frame behind them, and say so in the code.** Deepgram
  validates a credential with `HTTP 401` at the WebSocket upgrade on both its surfaces, so no live
  session has ever produced an in-band failure frame there; those two error-frame branches are written
  from the vendor's published schema and are labelled as such rather than left looking like captures.
  Every other frame and close code under test is one a live probe recorded.

- **Unchanged on purpose: LMNT's HTTP transport still surfaces a non-2xx status as
  `HttpRequestException`.** No measured defect stands behind retyping it, and doing so would be a
  second behavioural break riding along with this one.

- **Two test fakes were rebuilt to make the socket-death case testable at all.** `HttpListener` plus
  `ws.Abort()` hangs on Linux — measured here as a single ElevenLabs test taking **9 m 49 s** against
  753 ms for an already-migrated class — so the ElevenLabs TTS and Deepgram STT fakes moved onto the
  shared `TcpListener`-based test server. The ElevenLabs suite now runs in 673 ms. Deepgram STT had no
  abort test before this: not one asserting the wrong thing, none at all, because its fake could not
  abort without hanging.

### Fixed — BREAKING: Speechmatics TTS was the sixth synthesizer, and it kept neither half of the empty-result contract

`SpeechSynthesizer.SynthesizeAsync` declares two promises in XML docs that ship to consumers of this
package: a session that ends cleanly having produced no audio raises `SpeechProviderEmptyResultException`,
and `text` that is empty or whitespace yields nothing *without asking any provider for anything*. Five
synthesizers kept both. `SpeechmaticsSpeechSynthesizer` kept neither — its read loop was
`if (read == 0) yield break;` with no accounting, and the file contained no whitespace guard at all. Its
closest analogue is LMNT's own HTTP loop, which does both, so this was never a transport limitation; the
ADR-0050 sweep closed eight WebSocket surfaces and this HTTP-only one was never in that scope. Same
breakage class as the entry above: a call that used to go quiet can now throw.

- **The whitespace half fixes a defect the live route reproduces.** Probed on 2026-08-18,
  `POST /generate/{voice}` answers an empty or whitespace `text` with `200 audio/wav` and **7 724 bytes**
  — and those bytes are not silence: 3 817 of the 3 840 samples are non-zero, 0.24 s of audible audio.
  A caller the contract promised silence was therefore billed for a request and handed speech. The guard
  stops at "empty or whitespace" and deliberately does not widen to punctuation-only text, which the same
  probe showed returns the same body: the contract's words are what they are, and "text a human would not
  read aloud" is a judgement no measurement supports.

- **The empty-result half closes a published-contract gap, not a reproducible vendor failure, and the
  code says so.** The same probe never saw an empty body — the smallest response was a 44-byte RIFF header
  plus 7 680 bytes of data. The guard counts **bytes, not samples**: a header-only response carrying zero
  samples would still pass it, because catching that means parsing the container, which this provider
  deliberately does not do, and nothing measured says the vendor emits one.

- **Both guards are negative-controlled.** Removing them fails exactly the two new tests and nothing else
  — 2 red, 9 green — so neither is passing on an assertion some other test already made.

### Fixed — BREAKING: AssemblyAI realtime STT rejected every session a telephony caller could start

`AssemblyAiSpeechRecognizer` sent **one WebSocket message per frame the caller yielded**. AssemblyAI
enforces a message-duration window and answers anything outside it with
`{"type":"Error","error_code":3007,"error":"Input Duration Error: Input Duration Violation: 20.0 ms.
Expected between 50 and 1000 ms"}`, then closes the session. An Asterisk AudioSocket source yields
20 ms frames, so every such session died — and died silently, because the receive loop keeps only
transcript messages and drops the error frame on the floor. Measured end-to-end through the shipped
client on 2026-08-17: 3.2 seconds of speech in, an empty transcript out, **no exception**.

- **The client now coalesces caller frames into the vendor's stated 50–1000 ms window**, emitting as
  soon as the floor is reached so the added latency is bounded by one message. It **splits at the
  ceiling** as well: a single 2000 ms message draws the same `3007`, so both ends are enforced, and the
  ceiling is reachable from one caller frame rather than only from accumulation.

- **BREAKING for anyone who set `AssemblyAiOptions.SampleRate`: the caller's `AudioFormat` now wins.**
  The option is read only when the format carries no rate, matching what `SpeechmaticsSpeechRecognizer`
  already did and what Deepgram and Cartesia do outright. This client was the only one of the four that
  ignored the format it was handed, and it matters beyond consistency: the service computes each
  message's duration from the rate the client **declared**. The same 800-byte messages of the same
  8 kHz audio transcribe 10/10 declared as `8000` and die `3007 Input Duration Violation: 25.0 ms`
  declared as `16000`. The declared rate and the coalescing thresholds now come from one value, so that
  divergence is not expressible rather than merely fixed.

- **`AssemblyAiOptions.SampleRate`'s summary said the service "expects 16000"** — stronger than the
  evidence. A session declaring `8000` over 8 kHz audio recovered all ten digits of a ten-digit
  utterance. The summary now says what was measured, and marks the option as the fallback it is.

- **The trailing remainder is padded with silence, and sending it as-is was measured working first.**
  A lone sub-floor message at the end of a stream is tolerated, three runs of three. It is rejected
  anyway: three consecutive sub-floor messages drew `3007` with zero finals, so the tolerance is real,
  thin and nowhere stated, and when it breaks it costs the whole transcript rather than the tail.
  Padding keeps every message inside the window the vendor *states*; zeros are silence in signed 16-bit
  PCM, so it cannot invent a word where dropping the remainder could clip one.

- **Verified through the shipped client with the before arm in the same session.** Fed 20 ms frames of
  8 kHz audio with `SampleRate` left at its `16000` default on purpose, the remediated client returns
  **10/10** digits in one final; the pre-fix client through the same harness minutes later returns
  **0/10** with zero finals and zero partials.

- **The suite now asserts on the bytes the client sends.** The fake discarded `result.Count` on its
  binary branch and kept only a frame counter, and no test in this repo asserted on the size of audio
  sent to any provider — a fake that cannot fail a client sending 20 ms messages is what let this ship.
  It now records every complete binary message's length, accumulating until `EndOfMessage` so a
  fragmented message is not read as several short ones. Reverting the declared rate fails exactly one
  test and nothing else; removing the coalescing fails four while that rate assertion still passes; and
  sending the tail short fails three of those four.
### Fixed — Tests: six provider fakes never saw a credential, so no test could catch an auth defect

Every WebSocket provider client carried a second, test-only constructor taking a `fakeServerPort`, and
it did more than redirect the origin: behind `if (_fakeServerPort is null)` it also **suppressed the
auth header** and rebuilt the **route and query**. The production expression was therefore executed by
production alone and by no test at all — which is the structural reason a credential defect could ship
past a green suite in this layer.

- **The blindness was measured before it was removed.** With every auth header in the six clients
  renamed to a header no vendor reads, the two provider suites returned **187 passed, 0 failed**.
  Renaming `Authorization` was invisible. After the change the same mutation fails **six tests, one per
  client** — that delta is the evidence, not the green suite.

- **The fix is this repo's own precedent, not a new invention.** `SpeechmaticsSpeechRecognizer` already
  had no test seam: its tests reach a fake by setting `BaseUri`, the same knob an operator uses for a
  regional endpoint. All six clients now have that shape, the `fakeServerPort` overloads are gone, and
  the credential headers are set unconditionally — so every test executes the line production executes,
  header name, scheme and value included.

- **Two query copies had already drifted, silently.** Deepgram STT's under-test URI omitted `model` and
  `language` entirely, so the suite was watching a request production never sends; ElevenLabs put a
  hard-coded `test-voice` in the route where production puts `VoiceId`. Each client now builds its URI
  in one expression, and both parameters are asserted from non-default values.

- **`ElevenLabsOptions` and STT `DeepgramOptions` gained `BaseUri`**, validated `^wss?://` like the
  other providers'. Required for the route to flow through shipped code, and an operator gap in its own
  right: neither client could be pointed at a regional or self-hosted endpoint.

- **What is deliberately still unasserted:** each vendor's *rejection* shape for a bad key. Speechmatics
  answers 101 then `4001 not_authorised`; the other five have not been observed, and inventing five
  guesses would be worse than asserting none.

Unit lane after the change: **3 094 tests** across 30 projects, 0 failures, 0 warnings.

### Fixed — Tests: a state transition awaited on a clock, and two suites competing for one port

The sweep behind the two-fakes entry below continued into the rest of the class. Two more defects, both
confirmed by forcing the interleaving, plus a correction to what that sweep concluded about a third fake.

- **A test waited 100 ms for the state it is named after.**
  `WebSocketAudioSessionTests.ReadPump_ShouldTransitionToDisconnected_WhenCloseFrameReceived` slept and
  then asserted; at 0 ms it fails. It now waits on `StateChanges`, the observable it was already
  subscribed to — the same file had been waiting that way elsewhere, so this was an inconsistency inside
  one file. Checked in the other direction too: with the close frame removed the wait now throws a
  `TimeoutException` in 5 s instead of asserting on an empty list. Two sibling barriers were controlled,
  passed at 0, and left alone.

- **Two test assemblies competed for TCP 4573, and only one of them said so.**
  `FastAgiIntegrationTests` must use that port — the Asterisk dialplan dials back to it. The other
  holder was invisible: `AddVerbara` registers the AGI hosted service unconditionally, so any started
  host binds `AgiPort`, and two `GracefulShutdownTests` tests that never speak AGI left it at its 4573
  default. No file in the test tree contains the string. Both now ask for an ephemeral port; a
  hard-coded 14573 elsewhere in the same class, sitting under a comment that called it ephemeral, went
  with them.

- **The overlap is measured, and the CI comment that denies it is wrong.** In the failing run
  `Verbara.Sdk.IntegrationTests` and `Verbara.Sdk.FunctionalTests` overlapped for 36 s;
  `RunConfiguration.MaxCpuCount=1` does not serialise the projects the way `ci.yml` claims, and four
  other assemblies interleave in the same log. Two mechanisms were refuted before the right one was
  accepted — neither a socket in `TIME_WAIT` nor an accepted leg still open blocks the re-bind; only two
  live listeners conflict. Verified as a property, not a green test: polling `ss` while those tests run
  shows 4573 listening on unfixed `main` and never with the fix.

- **A per-timer control does not clear a fake.** `RealtimeFakeServer`'s three timers each pass 59/59
  alone; zeroed together the suite fails — 3 tests in one run, 1 in the next, which is what a race looks
  like. The load-bearing one is the pre-close wait, and what holds the suite up is the sum of the slack,
  not any single delay. Left to ADR-0045 §3.2, which already owns replacing it: the causal close
  condition is "the client has sent everything it will", which the fake cannot derive.

- **The two fakes' replacement ceilings no longer carry an allowance.** Both were unmarked `Task.Delay`
  calls, so the sync-fence baseline still grandfathered those files at 1 — the barriers were gone but
  the room to add new ones was not. They now carry `// fence-allow: GUARD-TIMEOUT` and both entries are
  0. Unit lane after the change: 3 086 tests / 30 assemblies, 0 warnings.

### Fixed — Tests: two fakes answered on a timer, and one of them ejected a PR from the merge queue

`CartesiaFakeServer` and `ElevenLabsFakeServer` replied after a fixed `Task.Delay(30)` instead of
waiting for the client's request. Nothing ordered the receive loop against the answer path, so on a
loaded runner the fake sent its audio, its terminator and its close before the loop had recorded what
the client sent — the client's stream completed, and an assertion on the request read a prefix of it.

- **The cost is measured, not hypothetical.** CI failed
  `CartesiaSpeechSynthesizerTests.SynthesizeAsync_ShouldSendADistinctContextId_PerRequest` on the
  merge-queue ref and the PR was ejected from the queue. The same commit had passed the same test on
  the PR ref minutes earlier: runner load was the only variable.

- **Both fakes now wait on the protocol.** Cartesia waits for the request, since its client opens one
  `ClientWebSocket` per `SynthesizeAsync` and one request per session is the whole signal. ElevenLabs
  waits for the empty-`text` end-of-input, because that client sends three messages and the assertions
  are on the ones after the first. Each keeps a receive-loop-ended arm so a client that sends nothing
  is still answered, and a generous ceiling whose only job is to keep a fake from hanging a suite.

- **Confirmed by forcing the interleaving both ways.** Setting the delay to 0 fails 10/10 on Cartesia
  and 5/5 on ElevenLabs; with the waits in place the delay is gone entirely and the TTS suite is green
  20/20 idle and 15/15 with twice as many spinners as cores, unit lane 3 081 tests / 30 assemblies.

- **The same control refused a third fake — per timer.** `RealtimeFakeServer`'s equivalent timer
  passes 5/5 at delay 0, so it was left untouched. That verdict is narrower than it first read: the
  fake has three timers and zeroing all of them together does fail. See the continuation entry above.

- **Why an earlier sweep for this exact class missed them, and it is two different reasons.** The
  Cartesia refutation was correct when it was made and expired a week later, when the first test to
  issue two requests against one fake instance was added. The ElevenLabs one was simply wrong: its
  observing test predates the sweep by three months, with the same assertion it has today. A
  refutation resting on "no test observes it today" has a shelf life, and nothing re-runs it.

### Fixed — BREAKING: Cartesia realtime STT could not open a session at all

`CartesiaSpeechRecognizer` connected to `wss://api.cartesia.ai/stt/websocket` with **no query string**
and sent its configuration as an opening JSON frame. Both halves were wrong. The service reads session
parameters from the query and closes `1008 Missing sample_rate` without one — twelve runs on
2026-08-16, twelve rejections — and it has no opening message at all, answering JSON on that socket
with `Invalid client message: Unrecognized text message "{…}". Expected one of: "finalize", "done",
"close"`. The rejection is in band, behind a successful `101`, which is why a probe that stopped at the
handshake recorded this surface as healthy while no session had ever opened.

- **`model`, `language`, `encoding` and `sample_rate` now travel in the query string**, where this
  service reads them. `sample_rate` comes from the `AudioFormat` passed to `StreamAsync` rather than
  from options, because it describes the audio actually being sent.

- **`CartesiaSttInitMessage` is deleted, not left unsent** — the DTO and its `[JsonSerializable]`
  registration are both gone. A type that only ever produced a message the vendor rejects is not
  dead weight to keep for symmetry with the other clients.

- **Measured through the shipped client with a control in the same run.** The URI as previously
  shipped was rejected `1008` seconds before the fixed client recovered **10/10** spoken digits in one
  final transcript, same key, same host — so the difference is the query string and cannot be the
  account or the day. Repeated five consecutive times at the shipped 5-second connect default: 10/10
  each time.

- **The test asserted the wrong channel, which is how this survived a green suite.** The fake checked
  the *body* of an opening frame the vendor never reads; it now records the upgrade's request-target
  and asserts the four parameters there, plus that no configuration frame is sent at all. Both new
  assertions were verified by reverting each half of the fix in turn — each failure lands on exactly
  one test.

### Fixed — two of the four streaming STT clients returned no final transcript at all

Every streaming STT client ended its input the same way: stream the audio, then `CloseOutputAsync`.
The comment above one of them stated the belief the other three shared — *"signal end-of-audio
(half-close) so the server flushes any pending transcript"*. Nobody had measured it. Measured on all
four surfaces on 2026-08-16, against one utterance of ten spoken digits replayed byte-identically
into every arm, it is false on three and actively destructive on two: **Speechmatics and AssemblyAI
emit partials all session and then end with zero finals.** A caller consuming only finals — the normal
way to consume this API — got nothing from either provider.

- **All four clients now send the vendor's in-band terminator as a text frame and leave the output
  side open**, letting the vendor end the session: `{"type":"CloseStream"}` (Deepgram),
  `{"message":"EndOfStream","last_seq_no":N}` (Speechmatics — the send loop counts audio chunks
  because the terminator has to name the last one), `{"type":"Terminate"}` (AssemblyAI), and the bare
  word `done` (Cartesia, which answers any JSON on that socket with
  `Expected one of: "finalize", "done", "close"` — that rejection is how the accepted commands were
  established, not a documentation page).

- **The half-close is removed, not supplemented, and that is a measured distinction.** The obvious
  remedy — keep the half-close, add the terminator — was run as its own arm and is exactly as bad as
  the half-close alone on both broken surfaces. Adding the terminator without removing the close
  would have looked like a fix and shipped the defect.

- **Deepgram is remediated too, although it was measured unaffected** (10/10 digits with the
  half-close, without it, and with both). Leaving one site different for no behavioural reason costs
  the next reader a re-derivation before they dare touch it. The arm that makes that a result rather
  than an untested assumption is the known-wrong control: a torn-down transport scored 8/10 there, so
  the instrument does detect a lost tail.

- **Verified against the shipped clients, not a probe reproducing them.** The remediated
  `SpeechmaticsSpeechRecognizer` and `AssemblyAiSpeechRecognizer` each recovered **10/10** digits with
  one final; the half-close restored in the same source files, run through the same harness minutes
  later, returned **0/10** and zero finals on both. Running only the fixed build would have measured
  the day rather than the change.

- **Cartesia's `CloseOutputAsync` needed a timeout, because it can hang on a socket the peer has
  abandoned.** That timeout is gone with the call it guarded — a text frame on a dead socket fails
  rather than blocks.

- **The receive loops were left alone on purpose.** All four already read while the socket is `Open`
  *or* `CloseSent`, so they were never why a final arrived too late. One unchanged line changed
  meaning instead: the close frame that ends the loop is now the vendor deciding the session is over,
  not the vendor answering a close we sent before it had finished transcribing.

- **The first version of the tests for this was blind, and the fix to that shipped with it.** Each
  fake stopped reading at the terminator — so a client that half-closed immediately behind it looked
  clean, and the new assertions passed against exactly the defect they exist to catch. The fakes now
  keep reading (`or CloseSent`), and `WebSocketTestServer.SessionCompleted` gives the tests a
  deterministic join point instead of a race: `StreamAsync` returns as soon as the server closes,
  which can be before the server has read what the client sent just before that. Re-verified by
  injecting both destructive arms into all four clients — eight arms, eight detections.

Named so it is not mistaken for done: **Cartesia's fix was asserted by its fake and unmeasured on the
wire**, because that client could not open a session at all — it connected with no query string and the
vendor closed `1008 Missing sample_rate`. Its live verification was deferred to that fix. **That caveat
is now retired**: the query string is fixed (entry above) and the same shipped client returns 10/10 on
the wire, so all four surfaces in this entry are measured rather than three. The way it failed is still
its own finding: zero results, dead in half a second, and no error reaching the caller
(`Sdk/ADR-0049` D1, observed end-to-end through a shipped client for the first time). Also unchanged:
AssemblyAI still sends one message per caller frame and so still fails any caller feeding frames
shorter than the vendor's 50 ms floor.

And the trade this makes, stated rather than left to be found: no client here sends a close frame any
more, so a session now ends only when the **vendor** closes it. The unbounded wait is not new — the old
code also sat in `ReceiveAsync` waiting on a peer that might never answer — but its backing is weaker,
because RFC 6455 obliges a peer to echo a close frame and nothing obliges a vendor to end a session.
**All four are now measured ending it**, Cartesia included once its query string was fixed: it answers
the terminator with a `{"type":"done"}` frame and closes `1000` about 158 ms later, and through the
shipped client the session ends 172 ms after the last audio frame. A drain deadline is still
deliberately **not** shipped, but the reason has changed from a missing measurement to a stated one:
no surface in this package acknowledges a terminator and then holds the session open, so there is
nothing to calibrate a timeout against. Cartesia remains the one surface with a sibling command
(`finalize`) whose purpose is to flush *without* ending the session; the client does not send it, and
the first surface measured acknowledging-without-closing is what should trigger building the bound.

### Fixed — BREAKING: Speechmatics realtime STT could not open a session at all

The long-lived API key travelled as a `?jwt=` query parameter, which the service does not accept.
The rejection is **in-band** and that is what made it invisible: the upgrade succeeds with `101` and
the socket is then closed `4001 not_authorised`, so nothing about the handshake looked wrong, the
client surfaced no exception, and the caller received an `IAsyncEnumerable` that completed normally
and empty. The provider was unusable as shipped, with no containment — and its suite was green,
because `SpeechmaticsFakeServer` had no way to look at the credential.

- **`SpeechmaticsSpeechRecognizer` now authenticates with `Authorization: Bearer <ApiKey>`** on the
  upgrade request, and the credential is gone from the URL. Measured live 2026-08-15, three arms,
  same credential, same host, seconds apart: `?jwt=<API key>` (what shipped) → closed
  `4001 not_authorised`; `Authorization: Bearer <same key>` → accepted, reached
  `RecognitionStarted`; `?jwt=<temporary key minted at the vendor's management endpoint>` → also
  accepted. The third arm was measured and **not** taken — it adds a request before every session, a
  key lifetime to manage, and an HTTP dependency to a type that has none — but it is what **refutes**
  the competing explanation: the same credential opened a session through two channels, so the key
  was never missing a realtime entitlement. The defect was the scheme.

- **`SpeechmaticsOptions.ApiKey`'s XML doc shipped the broken scheme to consumers** — "Passed as the
  `jwt` query parameter". Corrected in the same commit, along with a note that this is a long-lived
  key and nothing here needs refreshing.

- **The test-only seam is gone, not just ungated.** `SpeechmaticsSpeechRecognizer` had an
  `internal` constructor taking a fake port and branched on it when building the session URI, so the
  URI expression that ships was executed by no test at all — every assertion about the session URL,
  "the credential is not in it" included, was made against a line that only ran under test. The
  branch and the constructor are deleted; the suite reaches its fake through `BaseUri`, the same
  option an operator sets to pick a region, and now drives the shipped expression. Gating anything
  behind a "is this a test?" check is precisely what leaves a fake unable to see what it exists to
  check, and the cheapest fix for that shape is usually to remove the check rather than widen it.

- **The shared WebSocket test substrate could not assert on a credential at all.**
  `WebSocketTestServer.ReadUpgradeRequestAsync` parsed the upgrade headers for `Sec-WebSocket-Key`
  and discarded the rest, so no fake built on it could tell an authenticated connection from an
  anonymous one. It now returns the full header set and `WebSocketTestSession` exposes it.

- **Confirmed rather than assumed:** the live `RecognitionStarted` top-level field set is exactly
  `{message, orchestrator_version, id, language_pack_info}`, with `word_delimiter` nested *inside*
  `language_pack_info` — which is what the committed fixture already held. The fixture was right and
  nothing in it changes; its provenance sidecar moves from documentation-derived to confirmed, for
  those names and that nesting and for nothing else. Also recorded, and **not** a defect: every
  session opens with an `Info` frame carrying sixteen fields against a DTO that declares two. The
  receive loop skips it by design; modelling it is `provider-dto-robustness-fences`' question.

Named so it is not mistaken for done: the swallowed `Error` frame on this same loop is untouched
(`Sdk/ADR-0049` D1 — it is what makes an in-band rejection silent), as are the three assembly signals
the client ignores (`word_delimiter`, `attaches_to`, `metadata.transcript`). The fixed client had not
itself been run live when this was written — the three arms were a probe reproducing what it now
sends, a reconstruction rather than the artifact. **That has since been closed** by the half-close
re-probe in the entry above: the shipped `SpeechmaticsSpeechRecognizer` reached a full transcript live
on 2026-08-16, which is not possible unless its `Authorization: Bearer` channel authenticated.

### Fixed — BREAKING: ElevenLabs and Cartesia TTS never returned a byte of audio

Two providers, five defects, and every one of them a total failure that a green test suite had been
certifying as correct. Both clients connected, sent a request, completed without error and handed
the caller an empty stream. Measured live 2026-08-16, each defect isolated as the only variable in
its own arm.

- **Neither client ever read the frame the audio arrives on.** ElevenLabs delivers base64 in
  `audio` on a JSON text frame; Cartesia delivers base64 in `data` on a `type="chunk"` text frame.
  Both receive loops yielded only `WebSocketMessageType.Binary` and skipped or barely parsed text.
  **Zero binary bytes arrive on either surface** — so this was not a client preferring the wrong
  branch, it was a client reading a branch that receives nothing. ElevenLabs now decodes through the
  new `ElevenLabsAudioOutput`; Cartesia through `CartesiaTtsServerMessage`, which replaces
  `CartesiaTtsControlMessage` (that type modelled `type` alone — enough to recognise the terminator,
  blind to the frames carrying the audio, so the client that stopped correctly had never started).
  Unmodelled siblings — the two alignment structures, `flush_id`, `step_time`, the echoed
  `context_id` — are tolerated rather than modelled, because nothing here consumes them.

- **Both clients half-closed the socket immediately after the request**, and either vendor reads
  that Close frame as "abandon the request". With the half-close as the only variable: ElevenLabs
  **0 B and close `1006`** against **86 193 B and close `1000`**; Cartesia **0 frames** against
  **7 chunks, 32 694 B, 1.022 s**. In both cases the request itself was already the end-of-input
  signal, so the half-close was a second, contradictory one. Third and second confirmed instances of
  the class LMNT opened — see the entry below.

- **Cartesia's request omitted `context_id`, which the endpoint requires.** It answers
  `{"type":"error","status_code":400,"done":true,"error":"context_id is invalid: …"}` and sends no
  audio. One fresh id per request; a constant would defeat the only thing the field does. A prior
  hypothesis that `"continue": null` caused the rejection was **refuted** by an A/B — both forms
  produced the identical error — so `CartesiaTtsRequest.Continue` is deliberately left as it was
  rather than changed on a guess.

- **Both loops now assemble until `EndOfMessage` before parsing, and without that the fix above
  would have introduced a new defect.** No receive loop in `Verbara.Sdk.VoiceAi.Tts` or
  `Verbara.Sdk.VoiceAi.Stt` read `result.EndOfMessage` at all. That was a harmless margin while
  audio arrived as binary frames the client sized (Deepgram: 1920 B against a 64 KiB buffer). It
  does not transfer to text frames the **vendor** sizes: ElevenLabs averaged ~29 KB of base64 per
  frame, Cartesia carried 32 694 B across seven, and one frame past the 65 536-byte buffer arrives
  fragmented — at which point a per-read parse hands JSON a truncated document. The failure is
  length-dependent, which is why no short probe and no fixture in either suite had ever reached it.

- **Both fakes were certifying the defects.** `CartesiaFakeServer` sent binary frames because that
  is what the client read; `ElevenLabsFakeServer`'s alignment flag existed to prove the client
  *skipped* the very message that carries the audio. Neither matched the endpoint, so a green suite
  proved only that client and fake agreed with each other. Both now answer the measured way by
  default, keep the binary path behind a `Transport` knob, gained a `TextFrameFragmentBytes` knob so
  fragmentation is reachable without a 64 KB fixture, and record the client's Close frame instead of
  tolerating it. New fixture `cartesia-tts/chunk-frame.json` holds the measured key set verbatim;
  no vendor value was stored, so it stays `synthetic`.

  Non-vacuity was checked by mutation, not by inspection: restoring either half-close fails a test,
  ignoring `EndOfMessage` fails a test, reverting either loop to binary-only fails five, and an
  empty `context_id` fails two.

Controls, both surfaces, same run: Cartesia — wrong path `HTTP 404`, invalid credential `HTTP 401`,
both at the handshake. ElevenLabs — wrong path `HTTP 403` (not the `404` every other surface
answers; a wrong-path control has to be read, not pattern-matched), invalid credential in-band, then
close `1008`.

**Not fixed here, and named so it is not mistaken for done:** on both providers a vendor error frame
still ends the stream silently, leaving the caller an empty result and no exception. That is
`Sdk/ADR-0049` D1, it changes observable behaviour, and it belongs to the D1 remedy rather than to a
frame-format fix.

**BREAKING:** callers of `ElevenLabsSpeechSynthesizer.SynthesizeAsync` and
`CartesiaSpeechSynthesizer.SynthesizeAsync` now receive audio. No API signature changed.

### Fixed — BREAKING: LMNT TTS has never worked either, on either transport

- **`LmntSpeechSynthesizer` no longer sends `"model": null`, which the WebSocket endpoint rejects
  outright.** `LmntTtsOptions.Model` defaults to `null` and the init message serialized it
  explicitly; the API validates that field against a literal set, refuses an explicit null, and
  closes `1002 protocol error` after sending **zero audio frames**. WebSocket is the default
  transport, so this was every LMNT caller at stock configuration, since the provider shipped.
  `LmntInitMessage.Model` now carries `[JsonIgnore(Condition = WhenWritingNull)]` and the field is
  absent unless configured.

  **This corrects an earlier, narrower reading.** The HTTP route defect below was scoped on the
  premise that "only callers who opt into HTTP are affected, because `Transport` defaults to
  `WebSocket`". Probing the WebSocket surface showed the default path was independently broken.
  Both transports failed, for unrelated reasons, for everyone.

- **`SendWsRequestAsync` no longer half-closes the socket after `eof`, which cost all the audio.**
  The client sent its four request frames and then called `CloseOutputAsync(NormalClosure)`. The
  endpoint reads that Close frame as "abandon the request": measured A/B on 2026-08-15 with the
  half-close as the only variable, it returns **0 bytes** and the receive loop ends
  `ConnectionClosedPrematurely`, while the identical sequence without it returns 30 688 B — 0.959 s
  of 16 kHz PCM — and the server closes `NormalClosure` on its own. `eof` is already the
  end-of-input signal; the half-close was a second, contradictory one.

  **This is a third independent blocker on the default transport, and it was nearly missed.** The
  `model: null` fix above was verified with a probe that reproduced the init message but not the
  client's close sequence — so it proved the message was acceptable, and nothing more. Fixing only
  those two would have shipped a WebSocket path that still produced silence. The rule that follows:
  a probe that reproduces the *message* is not a probe of the *client*.

- **The HTTP transport POSTs to `/v1/ai/speech/bytes`, not `/v1/ai/speech/generate`.** The shipped
  route answers `404 {"detail":"Not Found"}` — byte-identically to a path that does not exist,
  confirmed against a wrong-path control on the same host, with an invalid-credential control
  returning `403 {"error":"Invalid API key"}` (`Sdk/ADR-0048`, `Sdk/ADR-0049` D4). Probed live
  2026-08-15.

  The form-encoded body is **kept**. The vendor documents JSON, and this fix was planned to switch,
  but a form body posted to the corrected route returns `200` with a byte-identical payload — so the
  encoding was never part of the defect, and changing it would have been an unmeasured edit riding
  along with a measured one.

### Changed — BREAKING: `LmntTtsOptions.Format` now defaults to `pcm_s16le`

- **The default moves from `raw`, which does not mean raw PCM on every transport.** Measured
  2026-08-15: over WebSocket, `format=raw` is 16-bit PCM as assumed; over
  `POST /v1/ai/speech/bytes` the same value returns an **MP3 frame stream** (MPEG-2 Layer III,
  16 kHz, 96 kbps, mono) under a `Content-Type: application/vnd.lmnt.audio-fp32` header that
  describes neither. `SynthesizeHttpAsync` streams the body through unchanged, so HTTP callers were
  handed MP3 bytes labelled `Slin16`. `pcm_s16le` returns headerless int16 PCM on **both**
  transports — one value, correct everywhere, and no decoder added to the SDK.

  Callers who set `Format` explicitly are unaffected. Callers relying on the default get working
  PCM on HTTP and byte-equivalent audio on WebSocket. Also worth knowing before you reach for it:
  `format=ulaw` arrives wrapped in a RIFF/WAV container, not as bare G.711.

### Known — LMNT WebSocket discards the error frame that explains a failure

- **A failed LMNT WebSocket synthesis still yields an empty stream and no exception.**
  `ReceiveWsFramesAsync` terminates on `notification.Error == "error"`, comparing an error *message*
  against the literal string `"error"` — which no real message equals. Both live failures observed
  (`{"error":"model: Input should be …"}` and `{"error":"Invalid API key"}`) fall through, the
  socket closes, and the transport exception is swallowed. Not fixed here: making it throw is a
  behavioural change that belongs with the `Sdk/ADR-0049` D1 remedy rather than a route fix. The two
  fixes above remove the failures that were reaching it; they do not make the next one visible.
  **Now fixed in this same unreleased version** — see the typed-provider-failure entry at the top,
  which is that remedy. This entry stays because it records how the defect was found and why the fix
  waited for a decision.

### Known — the half-close is a class: three of three TTS sites measured, all total failures, all now fixed

- **The LMNT half-close above is a pattern, not a one-off.** `CloseOutputAsync` immediately after
  the request appears in `ElevenLabs` TTS, `Cartesia` TTS, and the Deepgram, Speechmatics, AssemblyAI
  and Cartesia speech recognizers. **Every TTS site measured so far returns zero bytes with it and
  audio without it** — LMNT (0 B → 30 688 B), Cartesia TTS (0 frames → 7 chunks, 32 694 B), and
  ElevenLabs (0 B, close `1006` → 86 193 B, close `1000`, measured 2026-08-16). Three of three, and
  all three are fixed — LMNT above, the other two in the entry at the top of this release.

  **This supersedes the previous entry's ElevenLabs note, which said it was "probed on 2026-08-15 and
  found working".** That probe never reproduced the client's close sequence, so it certified the
  request and not the client — the same gap that nearly let the LMNT defect ship. Re-probed with the
  close as the only variable, ElevenLabs fails exactly like the other two. The lesson is the entry
  itself: *"the one to watch"* was the right instinct and an insufficient one, because the reason it
  was worth watching was a known weakness in the evidence, and a known-weak measurement is not a
  measurement.

- **The four speech recognizers are still *not characterised*, and are a different experiment.** In
  all four, the bare `CloseOutputAsync` is the **only** end-of-input signal the client sends — there
  is no `eof` or terminator message beside it — so removing it does not reproduce the TTS A/B, it
  produces a hang. Deciding these needs a three-arm design, not the two-arm one that settled TTS.
  Nothing here should be read as a claim that they are broken.

### Fixed — the LMNT test fakes certified every one of these defects as correct behaviour

- **`LmntHttpFakeServer` now matches on method and path**, serving only `POST /v1/ai/speech/bytes`
  and answering the live `404 {"detail":"Not Found"}` otherwise, with an unmatched-request counter
  so a route assertion cannot pass on a stale body. It previously never inspected the path. A
  mutation test measured the cost: restoring the old route fails **five** HTTP tests, so the entire
  suite had been green against an endpoint that returns 404 — one test even named
  `ShouldPostToGenerateEndpoint` while asserting nothing but a header.

- **`LmntWsFakeServer`'s blind spot is now covered by tests rather than the fake.** It records the
  init message and then replies with audio regardless of what that message says, so the suite
  asserted the init was *sent*, never that it was *acceptable*. Two regression tests now pin the
  `model` field's absence when unset and its presence when set.

  It also answered the client's Close frame with a full audio stream, where the live endpoint
  answers with nothing. Reproducing that reaction faithfully would mean racing the fake's own send,
  so the fake records `ClientSentCloseFrame` and the test asserts on what the client *sent* instead
  — a check whose ordering is fixed by causality rather than timing. A mutation check confirms it:
  restoring `CloseOutputAsync` fails the new guard.

- **`scripts/capture-provider-recording.py` no longer reproduces the LMNT defect.** `lmnt_http_plan`
  hardcoded the same `404` route and the `raw` format, so a capture run would have recorded a 404
  envelope as though it were the surface — the defect one level up from the client.

  **And its own test suite had pinned both broken plans.** `scripts/tests/` was green against the
  `/generate` route, the `voice` body field and `format=raw`; LMNT's route was not asserted by any
  test at all, which is how it survived every run. The measured values are pinned now and the two
  missing route assertions exist.

### Fixed — Speechmatics TTS has never worked

- **`SpeechmaticsSpeechSynthesizer` now selects the voice by path segment, so the request succeeds.**
  The shipped client POSTed to `/generate` with the voice as a JSON body field. The API has no
  `/generate` route: it answers `404 Not Found`, identically to a route that does not exist —
  because that is what it is. Every synthesis this provider ever attempted failed, for every caller,
  since the surface shipped. Probed live 2026-08-16: `POST /generate/{voice}` returns
  `200 audio/wav` (33 836 B, valid `RIFF`/`WAVE`), with a wrong-path control still `404` and an
  invalid-credential control `401` on the same host (`Sdk/ADR-0048`, `Sdk/ADR-0049` D4).

  The `voice` field is **removed from the request body** rather than left alongside the path
  segment. Sending both returned identical output when the two agree, but which one wins when they
  disagree was not measured, and an unmeasured precedence is not a thing to depend on.

### Changed — BREAKING: `SpeechmaticsOptions.BaseUri` is now an origin

- **`BaseUri` no longer carries a path.** Its default moves from
  `https://preview.tts.speechmatics.com/generate` to `https://preview.tts.speechmatics.com`, and the
  synthesizer appends `/generate/{Voice}` itself. Callers who never set the property are unaffected
  and go from a guaranteed `404` to working audio.

  **Callers who do set it must drop the `/generate` suffix**; a value that still carries it now
  produces `/generate/generate/{voice}`. The property's signature is unchanged, so this is a
  behavioural break the compiler cannot catch — hence this entry. Alternatives rejected: appending
  the segment to whatever the caller supplies (leaves `BaseUri` meaning "the URL minus its last
  segment", a rule nothing in the type communicates), and introducing a replacement option with
  `[Obsolete]` on `BaseUri` (downstream repos run `TreatWarningsAsErrors`, so the warning breaks
  their builds).

### Fixed — the test fake certified the broken route

- **`SpeechmaticsFakeServer` now matches on method and path**, returning `404` for anything but
  `POST /generate/{voice}`. It previously never inspected `Request.Url` and answered any route, so
  three fully green tests certified a client whose every production request `404`ed. A fake more
  permissive than the vendor cannot fail on a wrong route — it can only bless one. Three regression
  tests now pin the path segment, the absence of the body field, and URI escaping of the voice.

- **`scripts/capture-provider-recording.py` no longer reproduces the defect.** The Speechmatics
  capture plan built the same `/generate` request with the voice in the body, so the instrument
  meant to establish what the vendor does could only ever have recorded the `404` — the defect one
  level up from the client.

### Added — the conformance method is committed code now, not a procedure

- **`scripts/probe-provider-conformance.py`** — the instrument that produced every finding above.
  Every one of them came from the same method: send what the shipped client sends, to the real
  endpoint, beside a control that is *known wrong*, and compare. Every one was also hidden the same
  way — by a green suite whose fake was written by the same author as the client, so one misreading
  of the vendor's contract passed on both sides.

  It enforces three rules structurally, and each is there because it was broken by hand first.
  **(1) Redaction is by field name, whatever the value's type.** The ad-hoc redactor used during the
  live runs tested the value's type first, so an array-valued identifier field walked straight past
  it and a raw identifier reached the operator's screen — the rule said "never echoed" and the code
  said otherwise. **(2) A probe cannot be constructed without both controls.** A wrong-path control
  proves it distinguishes routes; only an invalid-credential control proves it distinguishes
  credentials. A run carrying one is not a weaker measurement — it is silent about the question it
  did not ask. **(3) A handshake is not a measurement.** `101 Switching Protocols` proves the
  credential for a vendor that authenticates in the upgrade headers and proves nothing for one that
  authenticates in-band; Speechmatics STT answers `101` to a rejected key and closes `4001`
  afterwards. Had this programme stopped at the handshake, that provider would have been recorded as
  verified-good while being entirely unusable.

  The parts that can be wrong **without a network** are the parts that actually failed, so they are
  ordinary unit-tested code gated on every PR (`scripts/tests/`), with a `--self-check` liveness
  fence for the rest. All three rules were mutation-checked: breaking each one fails tests, rather
  than quietly producing a plausible report.

- **`docs/guides/provider-wire-conformance.md`** — the per-surface record: fourteen surfaces, each
  with route status, frame status, where the vendor validates the credential, the evidence class
  behind the claim, and **its own date**. A single header date would have asserted a live measurement
  for surfaces that never got one. It includes a named *Still not characterised* section, because a
  gap between rows reads as coverage, and it keeps `live + route control` and `live + both controls`
  as different rows rather than one row with a footnote.

### Security

- **`SSH.NET` pinned to `2026.0.0` to clear [GHSA-q939-rpr3-3284](https://github.com/advisories/GHSA-q939-rpr3-3284)**
  — HIGH, CVSS 7.1, arbitrary file write via server-controlled filenames in `ScpClient`'s recursive
  download (#167). **Advisory drift, not a change of ours**: it was published 2026-08-12, after this
  repo's last green run, and under `TreatWarningsAsErrors` NuGet audit's `NU1903` is a build error —
  so every restore failed across 11 projects here, with `Verbara.Sdk.Pro` and `Verbara.Platform`
  going red on the same advisory the same night. **Real exposure is nil**: `SSH.NET` arrives
  transitively via `Testcontainers` into 8 container-backed test projects and is loaded only for
  host-port forwarding — `ExposeHostPorts` / `SshdContainer` / `PortForward` appear nowhere in this
  repo, so the vulnerable path is never reached. **Upgrading `Testcontainers` does not fix it**:
  `4.13.0`, the latest, still depends on `SSH.NET 2025.1.0`, so the transitive pin is the only
  available remedy and `2026.0.0` is the first patched release.

### Changed — Dependencies

- **`CentralPackageTransitivePinningEnabled` enabled** to carry the pin above without adding a fake
  direct dependency to every affected project (#167). Two measured consequences:
  - **`Microsoft.Extensions.Hosting` aligned to `10.0.10`.** Enabling the flag raised `NU1109` across
    35 projects: `Microsoft.Extensions.Hosting 10.0.10` demands `>= 10.0.10`, and this pin was the
    **sole straggler** among 12 `Microsoft.Extensions.*` siblings already there. Aligning it is the
    fix — this is not a cascade in the ADR-0040 sense.
  - **28 lower duplicate resolutions collapse upward** into versions this file already declares
    (`OpenTelemetry` ×5 `1.15.3` → `1.17.0`; `Microsoft.CodeAnalysis.*`
    `3.3.4`/`3.11.0`/`4.8.0`/`4.14.0`/`5.3.0` → `5.6.0`; `Microsoft.Extensions.*`
    `6.0.0`/`8.0.x`/`10.0.0` → `10.0.10`; `BouncyCastle.Cryptography` `2.6.2` → `2.7.0`, which comes
    with `SSH.NET`). Every project's `project.assets.json` was diffed before and after: **309 → 281
    resolved pairs, and no package disappears from the graph** — every removal is a lower duplicate
    of a package still present at a higher version. The practical effect is that test and example
    projects stop silently running against older assemblies than the ones declared centrally.
  - Verified: `dotnet restore` exit 0 with **0** `NU1903` / `NU1109` / `NU1605`, `dotnet build -c
    Release` **0 warnings 0 errors**, `dotnet test` under the CI filter **3027 passed, 0 failed**.

### Changed — Tests & tooling

- **Speech-provider HTTP suites now run against a real loopback server, driven by recorded vendor
  responses** ([ADR-0041](docs/decisions/0041-wiremock-as-http-provider-test-substrate.md), Accepted
  2026-08-09; Phase A in [#149](https://github.com/verbara/Verbara.Sdk/pull/149)). **No public API
  changes and no behaviour change on any production path** — this is test infrastructure. **All six
  HTTP surfaces have now migrated**: Azure TTS, OpenAI Whisper, Azure OpenAI Whisper, Google
  Speech-to-Text, and — once the route defects below were fixed under
  [ADR-0048](docs/decisions/0048-wire-conformance-by-live-probe-with-negative-control.md) —
  Speechmatics TTS and the LMNT HTTP fallback. The last two are worth reading about: they were blocked on defects in shipped
  code rather than on effort, which is why they landed last rather than being faked green.
  - **`WireMock.NET` replaces `MockHttpMessageHandler` on the HTTP side.** The old handler returned
    one canned response to *every* call, so a request sent to the wrong route or without the
    provider's credential passed silently. Matching is now strict (method + exact path + exhaustive
    query + auth header, `AllowPartialMapping = false`), so those requests 404 instead — a shape the
    suites can now assert, and do. The substrate lives in its own
    `Tests/Verbara.Sdk.TestInfrastructure.Http` project: referencing WireMock adds a
    `FrameworkReference` to `Microsoft.AspNetCore.App`, which stops ~30 `Microsoft.Extensions.*`
    assemblies reaching the output directory and makes coverlet **silently skip instrumentation**
    (measured 80.42% → 61.96% line coverage with every test still green, caught by the ratchet).
  - **Fixtures are recorded, not invented** (D4). Each migrated suite replays a real captured
    response committed with a provenance sidecar (provider, endpoint, capture date, source-audio
    origin, redaction applied, terms verdict), governed by a new
    [provider recording protocol](docs/guides/provider-recording-protocol.md) with a per-provider
    terms-of-service review, a redaction rule enforced by `scripts/check-recording-redaction.py`, a
    source-audio rule (synthetic or public-domain only, never an identifiable person's voice) and a
    256 KiB binary cap. The captures immediately earned their keep: OpenAI's transcription response
    carries a `usage` object the SDK does not model, while Azure OpenAI — running the same Whisper
    model — returns a bare `{"text": …}`. Both hand-authored fixtures had claimed the same shape.
  - **The Azure TTS suite exercises real codec bytes** for the first time, replacing a `new byte[320]`
    of zeros and putting a genuinely non-chunk-aligned payload through the frame-chunking path.
  - **WebSocket providers are deliberately not migrated** (D2). WireMock.NET matches HTTP/1.1
    requests and cannot hold a duplex session, so the eight WebSocket surfaces keep
    `WebSocketTestServer` and their protocol fakes; each suite now carries a one-line comment naming
    its transport so the omission reads as a decision. Which substrate a suite uses is documented in
    the new [provider test substrate guide](docs/guides/provider-test-substrate.md).
  - **One `internal`, test-only change under `src/`:** `AzureTtsSpeechSynthesizer` composed its URL
    from `Region` and therefore ignored `HttpClient.BaseAddress`, so it takes an `internal` optional
    origin parameter following the existing `SpeechmaticsSpeechSynthesizer` / `LmntSpeechSynthesizer`
    precedent (D12). It substitutes scheme/host/port only — the route stays in production code so the
    strict matcher asserts the path the provider really builds. Nothing becomes public API; the
    production constructor and its behaviour are unchanged.
  - **`scripts/capture-provider-recording.py`** (stdlib-only, **150 unit tests**) automates capture
    steps 4–8 of the protocol for **five of the six HTTP surfaces**, issuing the same request each
    SDK client issues so the fixture matches production traffic. Credentials are read from the
    environment and never written or echoed. It now covers both directions and three commit shapes:
    the vendor's JSON (Whisper ×2, Google), the vendor's audio bytes under a hard 256 KiB cap
    (Speechmatics TTS), and — where the terms do not clear committing the payload — a **response
    envelope** recording status, headers, media type, content length and observed chunk boundaries
    (LMNT). The envelope route keeps its promise structurally rather than by discipline: the reader
    is told not to retain the body, so the audio is counted and dropped one read at a time and no
    whole payload ever exists in memory for a later line to write out.
  - **The Google Speech-to-Text suite replays a real captured response**, and the capture paid for
    itself the same way the Whisper pair did: Google's body carries four fields the SDK's DTOs do
    not model (`results[].resultEndTime`, `results[].languageCode`, `totalBilledTime`, `requestId`),
    now asserted as present so shrinking the fixture back to the hand-authored shape fails the
    suite. `GoogleSpeechRecognizer` needed the same `internal` origin-only seam Azure TTS took (D12)
    — it built one absolute URL and so ignored `HttpClient.BaseAddress`. Nothing becomes public API.
  - **Two more shipped-code defects surfaced, both found by trying to capture and both confirmed
    against the live vendor. Neither is fixed here** — this change's contract is a test substrate,
    and a route fix is production behaviour.
    - **`LmntSpeechSynthesizer`'s HTTP path cannot reach LMNT.** It POSTs form-encoded to
      `/v1/ai/speech/generate`, which returns **404**. A controlled comparison with the same
      credential seconds apart got **200 `audio/mpeg`** from the documented `/v1/ai/speech/bytes`
      with a JSON body. Three deltas — path, body encoding, and response media type, since the
      client assumes raw PCM it can chunk while LMNT returns MP3. Contained in practice:
      `LmntTtsOptions.Transport` defaults to `WebSocket`, so only callers who opt into HTTP are
      affected, and the WebSocket path is untouched by this finding.
    - **`SpeechmaticsSpeechSynthesizer` cannot reach Speechmatics.** It POSTs to `/generate` with
      the voice as a JSON body field; the API selects the voice by **path segment**, so
      `/generate/{voice}` returns 200 `audio/wav` and `/generate` returns 404. Everything else the
      client sends — bearer auth, content type, sample rate — is already right. A plausible second
      hypothesis was checked and disproved rather than assumed: the shipped default voice
      `eleanor` is absent from the vendor's published four-voice list, but it returns 200, so the
      list is incomplete and the option default is fine.
  - **Both of those routes are now fixed (ADR-0048) and both suites have migrated — and two of the
    deltas reported above turned out not to exist.** Each correction came from a measurement, not a
    re-reading.
    - **LMNT's body encoding was never a delta.** A form-encoded body posted to the corrected
      `/v1/ai/speech/bytes` returns 200 with a payload byte-identical to the JSON one, so the form
      encoding is deliberately kept rather than swapped: changing it would have been an unmeasured
      change riding along with a measured fix. **And the response is not MP3** at the format this SDK
      ships. `audio/mpeg` is what the route returns at the *vendor's* default; `LmntTtsOptions.Format`
      defaults to `pcm_s16le`, which is headerless int16 on both transports and arrives declared as
      `application/vnd.lmnt.audio-int16`. The one real delta was the route.
    - **Speechmatics `/generate/{voice}` accepts the rest of the body as sent.** The capture posts
      `text`, `language` and `sample_rate` together and returns 200 `audio/wav`, which the earlier
      note had correctly declined to assume in either direction.
  - **The LMNT fixture is a pair, and only one half is the vendor's.** LMNT's terms do not clear
    committing generated audio, so the recorded artifact is the response **envelope** — status,
    header names, declared media type, content length and observed read boundaries — and the body
    served under it is a locally computed tone in the same codec. The envelope is load-bearing rather
    than decorative: every success-path stub takes its status and media type from the capture, and a
    fence requires the recorded length to equal the sum of the recorded read boundaries, so a
    hand-edited envelope fails the suite. What the pair cannot prove is anything about the content of
    LMNT's speech, and its provenance sidecar says so.
  - **One fidelity loss, recorded rather than papered over.** The retired `HttpListener` fakes read
    `Uri.AbsolutePath`, which keeps `%2F` escaped, so they could prove an escaped slash stayed inside
    a single path segment. This substrate cannot, and measuring the limit rather than assuming it
    made it larger than one reserved character: the request target is decoded **twice** before the
    matcher sees it, so the escaped `/generate/a%20b%2Fc`, the unescaped `/generate/a%20b/c` and the
    double-escaped `/generate/a%2520b%252Fc` all arrive as `/generate/a b/c` and all three match.
    Escaping is not observable here at any level. The affected test was renamed to what it can still
    prove — the voice's characters reach the route intact, so truncating or substituting them fails —
    and its remarks now carry the measurement; the segment-boundary property belongs with
    wire-conformance work against the live vendor, not with a fake taught to agree.
  - **The two retired `HttpListener` fakes are deleted**, which is what the migrations were for. The
    LMNT WebSocket fake shared a file with the HTTP one and is now in `LmntWsFakeServer.cs`, a name
    that says which transport it serves; its behaviour is unchanged.
  - Every `*_ShouldAbort_WhenCancelled` test carried over **verbatim** as the `test-determinism`
    tripwire for the swap, and re-verified under the 30× repeat-run protocol (0 failures).

- **All eight WebSocket provider fakes now send frames authored to each vendor's published protocol
  documentation, instead of hand-authored minimal JSON** (ADR-0041 D4). **Test-only — no shipped code
  changed.** The eight WebSocket surfaces stay on their in-process fakes, because WireMock.NET cannot
  hold a duplex session (D2); only their payloads change. Five of the eight vendors are `not-cleared`
  for committing captured output and no capture credential exists for any of the eight, so the
  fixtures take a second route the recording protocol now defines: **conform to the vendor's
  documented field set, nesting and frame ordering, with our own fictional values** — `class:
  "synthetic"`, a new `terms.verdict: "not-applicable"` (no vendor output is present, so the
  redistribution question does not arise), and a new required `source_schema` block naming the page,
  its revision marker and the date it was read. The boundary is explicit: conforming to a schema is
  not copying a vendor's example payloads, and none are in the tree. **All eight vendor pages publish
  no revision marker**, recorded as `"undated"` — which the guide now treats as a finding, since a
  silent breaking edit there is indistinguishable from no edit at all.
  - **Three shipped-code defects surfaced, none of them fixable inside a test-substrate change; each
    is recorded with the assertion that pins it.** This is what ADR-0041 D4 was adopted to do —
    `proposal.md` argued that a fixture written by the same person who wrote the parser cannot expose
    a shared misreading of a vendor's schema, and no amount of coverage substitutes, because every
    test passes against a fixture that shares the defect.
    - **`CartesiaSpeechSynthesizer` and `ElevenLabsSpeechSynthesizer` cannot receive audio from
      their vendors.** Both yield only `WebSocketMessageType.Binary` frames; both vendors deliver
      audio as base64 inside JSON text frames (ElevenLabs `AudioOutput.audio`, Cartesia
      `chunk.data`) and **neither documents a raw-binary mode at all** (read first-hand 2026-08-14).
      Cartesia additionally reaches its `done` terminator, so it completes successfully having
      produced zero audio.
    - **`SpeechmaticsSpeechRecognizer` space-joins transcript tokens unconditionally**
      (`SpeechmaticsSpeechRecognizer.cs:170`), ignoring the `word_delimiter` sent on
      `RecognitionStarted`, the per-result `attaches_to` marker, and the already-assembled segment
      the vendor publishes at `metadata.transcript`. A segment ending in punctuation comes out as
      `"… correctamente ."`.
  - **Fixture integrity is fenced, and the fences were mutation-tested rather than asserted.** Each
    suite gains a test asserting its fixture still carries the unmodelled field names and its exact
    byte length; shrinking a fixture back to the shape this work retires makes them fail. Binary TTS
    payloads are locally generated by `SyntheticPcm.Triangle`, integer-only because `Math.Sin` is not
    guaranteed bit-identical across platforms and the files are asserted byte-for-byte, with lengths
    deliberately not chunk-aligned so a partial final frame is always exercised. Every audio fixture
    is under 1.2% of the 256 KiB cap.

- **The STT cancellation frame generator is now one shared helper instead of four copies** (#144).
  **Test-only — no shipped code changed.** `EndlessFrames`, which keeps an STT stream open until a
  pre-cancelled token is observed at the iteration boundary, was duplicated verbatim across the four
  WebSocket recognizer suites (Deepgram, AssemblyAI, Cartesia, Speechmatics); it moves to
  `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/Helpers/SttFrameGenerators.cs`, alongside the existing
  `MockHttpMessageHandler`, so the seven-provider cancellation contract asserted by
  `StreamAsync_ShouldAbort_WhenCancelled` rests on one non-duplicated generator. The `Task.Delay(10,
  ct)` pacer keeps its `fence-allow: LOOP-DRIVER` annotation at the single shared site — one
  annotated pacer instead of three copies plus one unannotated. **Discovered during apply:**
  Deepgram's copy was that unannotated one, grandfathered in `sync-fence-baseline.json` at count 1;
  deleting it takes that file's real unmarked-barrier count to **0**, so the entry is removed and the
  ratchet moves down, which is the only direction its own rule permits. Per-class `SingleFrame` /
  `ThreeFrames` are deliberately left as-is.

### Fixed — Tests

- **Fake-server seams dial the IPv4 loopback literal `127.0.0.1`, never `localhost`**
  ([ADR-0044](docs/decisions/0044-ipv4-loopback-literal-for-test-servers.md), #146). **Test-only —
  the eight touched `src/` lines are the fake-server URL branch of the recognizers and synthesizers,
  reached only when a test sets the fake port; no production endpoint or public API changes.** Two
  tests flaked under parallel load (`DeepgramSpeechSynthesizerTests.SynthesizeAsync_ShouldSendRequestToCorrectPath`,
  `LmntSpeechSynthesizerWsTests.SynthesizeAsync_ShouldSendTextMessage_WithCorrectText`) and **the
  recorded diagnosis was wrong** — both had been filed as fake-server synchronization races, an
  assertion reaching the capture state before the server wrote it. The real cause is an
  address-family ambiguity, established by direct experiment: `localhost` resolves to `::1` **first**,
  then `127.0.0.1`; `WebSocketTestServer` binds `TcpListener(IPAddress.Loopback, 0)` — IPv4 only, so
  it does **not** own `::1` on its port; an `HttpListener` with prefix `http://localhost:{port}/`
  therefore binds the **same port number** successfully while that listener holds it (different
  address family, no `EADDRINUSE`); and a client dialling `ws://localhost:{port}` resolves `::1`
  first, reaches the `HttpListener`, and gets HTTP **200** where the WebSocket handshake requires
  **101**. Reproduced under CPU saturation (32 spinners on 24 cores). The convention is **enforced,
  not remembered**: a `LoopbackSeamScanner` + `LoopbackSeamGuardTests` pair in
  `Verbara.Sdk.Governance.Tests` Roslyn-parses every `src/` and `Tests/` source file and fails the
  build on any reintroduced `localhost` fake-server seam — zero-tolerance like the reflection ban
  rather than ratcheted, since the repo carries none today. It ships with a liveness self-test (the
  scan must walk >700 files, defeating the found-zero-files false green) and detector unit tests
  pinning both the true positives and the immunity of the real product defaults that legitimately
  name `localhost` on a fixed port (ARI 8088, OTLP 4317, Toxiproxy 8474).
- **Three timing and thread-safety defects in the WebSocket provider fakes.** All test-only; no
  shipped code changed. They surfaced on this change's first CI run and predate it — the fakes were
  byte-identical to `main` — but they matter beyond the one red build, because 30 green local
  repeat-runs had not found them. The repeat-run protocol multiplies *runs*, not *machines*, and
  each of these races was decided by a fixed 30 ms server timer against client-side work: a fast dev
  box wins every round, a loaded CI runner does not.
  - **`LmntWsFakeServer` answered on a timer instead of on the client's request.** It slept 30 ms,
    sent audio, then called `CloseAsync` — which drains and discards peer frames to complete the
    close handshake, so a request frame could vanish between `text` and `eof`. The session now waits
    for the client's terminal `eof` frame (bounded at 2 s, so cancelled or aborted clients are still
    answered), which is also what the real LMNT server does.
  - **`HoldOpenUntilDisposed` did not hold the socket open.** It awaited the receive loop, and that
    loop ends the instant the client half-closes (`CloseOutputAsync` after EOF) — so the session tore
    down and completed the client's stream, the one thing a cancellation test must never observe. It
    now holds until the fake is disposed, and `SynthesizeAsync_ShouldAbort_WhenCancelled`, which had
    never set the flag despite documenting the strategy, sets it: the test had been passing on the
    30 ms delay outlasting its own 5 ms cancel poll.
  - **Five fakes handed tests the live `List<string>` their receive loop was still appending to** —
    a torn read of a collection under concurrent mutation. LMNT, ElevenLabs, Deepgram TTS, Cartesia
    TTS and Cartesia STT now expose an `IReadOnlyList<string>` snapshot taken under the same lock the
    writer holds.
  - **`DeepgramTtsFakeServer` carried the same timer defect and is fixed the same way.** Found by
    sweeping every fake for the pattern once LMNT was understood, rather than waiting for it to go
    red. It now answers on the client's `Flush` frame — the last request frame the synthesizer sends
    unconditionally (`Close` is guarded by `ws.State == Open`), and the one a real Deepgram server
    acts on. Its exposure was **wider** than LMNT's: the synthesizer never sends a WebSocket close
    frame, so the fake's `CloseAsync` stayed pending — and draining — for the rest of the session.
    Forcing the interleaving (delay → 0) failed `SynthesizeAsync_ShouldSendSpeakMessageWithText` and
    `SynthesizeAsync_ShouldComplete_WhenServerAbortsAfterSend`. Its orphaned `HangForever` flag, which
    had the hold-open defect too but no consumers, was corrected rather than left as a trap.

### Changed — CI

- **The patch-coverage mis-wiring trip now arms from the report, not from the line text** (verbatim
  replication of `Verbara.Sdk.Pro` PR #95; the script is byte-identical across the four ADR-0013
  repos). `check-patch-coverage.py`'s second liveness self-test asks *"diff-cover measured 0 lines —
  did this diff add something it was supposed to measure?"* and answered from a prefix heuristic that
  models only comment and TypeScript shapes. It therefore reads a `[LoggerMessage(…)]` attribute line
  and a `Message = "…"` string continuation as executable C#, and **neither is ever a Cobertura
  sequence point** — so a documentation/attribute-only PR failed as `mis-wired` while the report it
  accused was correct (measured on the Pro PR that exposed it: 85 added `src/**/*.cs` lines, 5 of
  them arming the trip, **0** instrumented). The trip now asks the report: for a file the report
  carries, an added line arms it only when that line's **number** is in the file's instrumented set.
  The two coordinate systems are one — the report is built from HEAD, and `@@ -a,b +c,d @@` seeds a
  HEAD-side counter that `+` lines advance and `-` lines do not. A file the report does **not** carry
  and that did not exist at the merge base keeps the text heuristic, so the failure this trip was
  built for — a path-normalization break that puts *every* file outside the report — still fires on
  the first added file. It is sharper in the other direction too: an lcov `DA:` on an `import` line
  now arms where the heuristic waved it through. Suite 7 → 10 cases in
  `scripts/tests/test_check_patch_coverage.py`; no version bump, no package delta — CI machinery
  only. Rationale, alternatives and the rule as a spec: Pro `openspec/specs/patch-coverage-liveness/`.

- **`publish.yml` now creates the GitHub Release object itself.** The workflow packed and pushed
  the packages to nuget.org on every `v*` tag but never created the Release, so tags accumulated
  published-but-release-less — `v2.3.0` shipped that way and was backfilled by hand on 2026-07-12
  after an `/xr:pending` fact-check (10 Release objects over 41 tags today). The new final step runs
  **after** the nuget push, so a Release never advertises packages that failed to publish; it is
  **idempotent** (an existing themed Release created by `/xr:release` §H is left untouched); it is
  gated on `github.ref_type == 'tag'` because this workflow also answers `workflow_dispatch`, where
  there is no version to release; its body is this repo's own `CHANGELOG.md` section for the version
  plus the list of packages actually packed; and it claims the `Latest` badge **only when the tag is
  the highest version**. Workflow `permissions` widened `contents: read` → `write` for this. Matches
  Verbara.Platform.Web `#246`, Verbara.Platform, and Sdk.Pro's in-workflow `gh release create`.

- **Docs/data-only CI fast-path (gate job, verbara-meta/ADR-0016 wave 2).** `ci.yml` and `codeql.yml` each gain a lightweight `gate` job that classifies the PR / `merge_group` diff against the event's own base (`scripts/ci/classify-docs-only.sh`; fail-closed allowlist `docs/**`, `openspec/**`, `CHANGELOG.md`, top-level `*.md`, `**/README.md`, **minus** the six Markdown files `Verbara.Sdk.DocSnippets.Tests` Roslyn-compiles, whose only guard is the `Unit Tests` job). Six plain-named heavy required contexts — `Unit Tests`, `Coverage Ratchet`, `AOT Trim Check`, `Pack Warnings Gate`, `Audit Test Asserts`, `Analyze (C#)` — take `needs: gate` and a fail-closed **job-level** `if:`, reporting a satisfying `skipped` on a docs-only diff. The matrix-suffixed `Functional Tests (Testcontainers) (23)` keeps its job (and matrix) always-run and takes the guard at **step** level, so the suffixed check-run still materializes and reports green (ADR-0039 addendum, PRs #104/#105). `OpenSpec Validate` and `Coverage Script Tests` stay always-run; **`Coverage Script Tests` was promoted to a required context** so a mis-widened allowlist is merge-blocking rather than advisory. `codeql.yml` gets a gate rather than a `paths-ignore` because it is `merge_group`-wired and emits a required context; its classify step does not run on `push`/`schedule`, so the default-branch and weekly security baselines are never path-skipped. `aot-validate.yml` (non-required `aot-check`, no queue trigger) takes the §2 `paths-ignore` instead. The classifier ships with 31 bash unit tests plus a 6-assertion drift guard asserting its DocSnippets carve-out stays a superset of `DocSnippetCompilationTests.cs`. Measured cadence: 16 of the last 70 merged PRs are docs-only; 14 of them take the fast path.

## [2.4.0] - 2026-07-26

### Changed — Dependency bumps

- Bumped **`NATS.Client.Core` from 2.8.2 to 3.0.0** ([#126](https://github.com/verbara/Verbara.Sdk/pull/126)) — a **major** upgrade of a runtime dependency of the published packages. Consumers that resolve `NATS.Client.Core` transitively should review the NATS.Net 3.x breaking changes before upgrading. Verbara's own NATS usage is unaffected by the upgrade.
- Aligned the OpenTelemetry package family to **1.17.0** ([#128](https://github.com/verbara/Verbara.Sdk/pull/128), [#131](https://github.com/verbara/Verbara.Sdk/pull/131)): `OpenTelemetry` (core) and `OpenTelemetry.Exporter.Console` were raised to 1.17.0, and the lagging `OpenTelemetry.Extensions.Hosting` + `OpenTelemetry.Exporter.OpenTelemetryProtocol` (1.16.0) were aligned to match. OTel .NET ships the family in lockstep, so this removes the intra-family version drift. `OpenTelemetry.Exporter.Prometheus.AspNetCore` stays on its own prerelease cadence (`1.15.2-beta.1`).
- Bumped the **`Microsoft.Extensions.*` package group** (11 packages, [#115](https://github.com/verbara/Verbara.Sdk/pull/115)).

## [2.3.2] - 2026-07-20

### Changed — TTS cancellation contract (behavioral clarification)

- TTS speech synthesizers (`Deepgram`, `ElevenLabs`, `Lmnt`) now observe the `CancellationToken` at `SynthesizeAsync` iterator entry (`ct.ThrowIfCancellationRequested()` before any provider request is issued). A pre-cancelled token now deterministically throws `OperationCanceledException` before the first WebSocket/HTTP call, instead of racing scheduling/mock latency. No behavior change for non-cancelled tokens. Mirrors the STT fence shipped in v2.3.0 and de-flakes the queue-blocking cancellation tests (`DeepgramSpeechSynthesizerTests`/`ElevenLabsSpeechSynthesizerTests.SynthesizeAsync_ShouldAbort_WhenCancelled`), which switch from wall-clock timer races to the deterministic pre-cancelled-token pattern; verified with the 30× repeat-run protocol (zero flakes). Lmnt's causal-trigger test was audited and left as-is. (ADR-0038, verbara-meta/ADR-0004 adopt-on-touch)

### Changed — CI

- **Coverage is collected once per validation run.** The `Unit Tests` job now runs with coverage collection and uploads the raw cobertura results as an artifact; the `Coverage Ratchet` job (`needs: unit-tests`) consumes that artifact — `reportgenerator` merge + `check-coverage-floor.py` — instead of re-building and re-running the whole unit suite (~11 min removed). The committed floor and manual-ratchet semantics are unchanged. (ADR-0038 D2, verbara-meta/ADR-0003) ([#101](https://github.com/verbara/Verbara.Sdk/pull/101))
- **Representative functional matrix on PRs, full matrix in the merge queue.** `functional-tests` now runs Asterisk `[23]` only on `pull_request` (fast feedback) and the full `[22, 23]` matrix on `merge_group`, so nothing lands on `main` without full-matrix validation. (ADR-0038 D3, verbara-meta/ADR-0003) ([#101](https://github.com/verbara/Verbara.Sdk/pull/101))
- **Dependabot CI-load reduction (ADR-0039).** The representative functional matrix is skipped on Dependabot PRs and dependabot version bumps are consolidated into groups ([#103](https://github.com/verbara/Verbara.Sdk/pull/103)); a step-level dependabot guard keeps the required (23) status context reporting on bot PRs ([#106](https://github.com/verbara/Verbara.Sdk/pull/106), ADR-0039 addendum).

## [2.3.1] - 2026-07-14

### Docs

- Purged post-rebrand `Asterisk*` residue from 13 living package/example READMEs (`AddAsterisk*` DI-call identifiers → `AddVerbara*`, plus the bare `AsteriskOptions`/`AsteriskServer`/`AsteriskServerPool` types in `Verbara.Sdk.Hosting/README.md`), and rewrote the fictional multi-server snippet in `Verbara.Sdk.Hosting/README.md` against the real `AddVerbaraMultiServer()` + `VerbaraServerPool.AddServerAsync()` API. Runtime data values preserved byte-for-byte: the `asterisk.sdk.calls…` NATS subjects and the `"Asterisk"` config-section key. (verbara-meta/ADR-0007)

### Fixed

- **LMNT TTS: `SynthesizeAsync` no longer throws when the server aborts mid-send.** The LMNT WebSocket path sent its four request frames (init/text/flush/EOF) unguarded, so a transport abort during send surfaced a `WebSocketException` (`Broken pipe`) that propagated out of the async enumerator and threw instead of completing gracefully — violating the documented contract (LMNT's 4-frame handshake had 4× the exposure of single-frame Cartesia/Deepgram). Request sends are now wrapped in the same `OperationCanceledException` + `WebSocketException` catch the receive loop and half-close already use; the receive loop owns teardown, so a mid-send abort ends the stream cleanly. Deterministic regression test added (`SynthesizeAsync_ShouldComplete_WhenServerAbortsMidSend`). ([#84](https://github.com/verbara/Verbara.Sdk/pull/84))

## [2.3.0] - 2026-07-05

### Changed

- **OpenTelemetry default `service.name` rebrand:** `VerbaraOpenTelemetryBuilder.ServiceName` now defaults to `"verbara-sdk"` instead of the pre-rebrand `"asterisk-sdk"`. Consumers who rely on the old default in dashboards, alerts, or exporter resource-matching rules should set `ServiceName = "asterisk-sdk"` explicitly via `AddVerbaraOpenTelemetry(o => o.ServiceName = "asterisk-sdk")` to keep the old value. ([#82](https://github.com/verbara/Verbara.Sdk/pull/82))

### Changed — STT cancellation contract (behavioral clarification)

- STT streaming recognizers (`Deepgram`, `Whisper`, `AzureWhisper`, `Google`, `Speechmatics`, `AssemblyAI`, `Cartesia`) now observe the `CancellationToken` at `StreamAsync` iterator entry (`ct.ThrowIfCancellationRequested()` before any provider request is issued). A pre-cancelled token now deterministically throws `OperationCanceledException` before the first WebSocket/HTTP call, instead of racing scheduling/mock latency. No behavior change for non-cancelled tokens. Fixes a CI flake in `DeepgramSpeechRecognizerTests.StreamAsync_ShouldAbort_WhenCancelled` (verbara-meta/ADR-0004 adopt-on-touch — deterministic-test-fences program). ([#77](https://github.com/verbara/Verbara.Sdk/pull/77))

### Fixed

- Repaired 13 dead relative doc links: a moved architecture-review doc, three ADRs (0013–0015) still pointing at pre-rebrand `src/Asterisk.Sdk.*` paths, and a false-positive markdown-link capture in `Verbara.Sdk.Config`'s README. ([#81](https://github.com/verbara/Verbara.Sdk/pull/81))

### Changed — CI / OpenSpec

- Added an OpenSpec strict-validate CI gate (`openspec validate --all --strict`). ([#76](https://github.com/verbara/Verbara.Sdk/pull/76))
- `openspec/config.yaml` gained public-content and release-bump authoring rules, later extended to cover cross-repo ADR citations in any doc. ([#79](https://github.com/verbara/Verbara.Sdk/pull/79), [#82](https://github.com/verbara/Verbara.Sdk/pull/82))

### Docs

- Fixed a stale package count and hardened OpenSpec authoring rules. ([#74](https://github.com/verbara/Verbara.Sdk/pull/74))
- Opened, then archived on merge, the `stt-cancellation-test-fence` OpenSpec change tracking the fix above. ([#75](https://github.com/verbara/Verbara.Sdk/pull/75), [#78](https://github.com/verbara/Verbara.Sdk/pull/78))
- Fixed stale API names (`AddAsterisk*` → `AddVerbara*`, `AsteriskTelemetry` → `VerbaraTelemetry`, `AsteriskSemanticConventions` → `VerbaraSemanticConventions`), package/meter counts (29 packages, 9 ActivitySources, 15 Meters), and dead links across READMEs and operations docs. ([#80](https://github.com/verbara/Verbara.Sdk/pull/80))

## [2.2.1] - 2026-05-23

**ADR-0022 Phase A.5 — `Verbara.Sdk.Cluster.Postgres`.** New Postgres-backed implementation of the cluster primitives shipped in v2.2.0 (`Verbara.Sdk.Cluster.Primitives`), built on `Verbara.Sdk.Data.Npgsql` (zero Dapper, AOT-clean).

### Added — Cluster package

- **`Verbara.Sdk.Cluster.Postgres`** (new) — Postgres-backed cluster primitives.
  - `PostgresDistributedLock` — advisory-lock-backed distributed lock implementing `IDistributedLock`. Full unit coverage; integration suite uses Testcontainers (`Category=Integration`).
  - `MigrationRunner` + embedded `V001__DistributedLockSchema.sql` for one-shot schema setup.
  - `AddPostgresClusterPrimitives(...)` DI helper.

### Changed — CI hardening

- **Merge queue activated** on `main` (SQUASH, ALLGREEN, batch ≤ 5, min wait 5 min).
- **Dependabot auto-merge** for analyzers + github-actions groups (non-major). Cooldown + grouped security updates configured.
- LFS pulled in CI so the ONNX turn-detection model arrives as the real binary, not a pointer.
- Fixed merge-queue recursion: dependabot auto-merge uses `AUTOMERGE_PAT` (not `GITHUB_TOKEN`) to clear GitHub's anti-recursion block on `merge_group` workflows; dropped `--squash` from the auto-merge step (queue owns the merge method).
- Removed redundant `push:[main]` CI run; `merge_group` triggers active.

### Changed — Dependency bumps

- `Microsoft.ML.OnnxRuntime` 1.22.0 → 1.26.0
- `NATS.Client.Core` 2.7.3 → 2.8.0
- `microsoft-extensions` group — 11 packages
- `Meziantou.Analyzer` 3.0.60 → 3.0.85
- `Microsoft.SourceLink.GitHub` 10.0.203 → 10.0.300
- `coverlet.collector` 10.0.0 → 10.0.1
- `dotnet-stryker` 4.14.1 → 4.14.2
- `dotnet-reportgenerator-globaltool` 5.5.9 → 5.5.10
- `github/codeql-action` 4 → 4.35.5
- `actions/dependency-review-action` 4 → 5
- `dependabot/fetch-metadata` 2 → 3

### Documentation

- ADR-0035 — canonical HEAD reference correction.

## [2.2.0] - 2026-05-20

**ADR-0022 Phase D — Dapper removed from the SDK.** Verbara.Sdk + every consumer ships Native-AOT-clean with zero Dapper code paths.

### Added — Data-access package

- **`Verbara.Sdk.Data.Npgsql`** (new) — reflection-free Postgres data-access facade.
  - `NpgsqlExecutor` (Dapper-parity surface: `ExecuteAsync`, `QueryAsync<T>`, `QuerySingleAsync`, `QueryFirstOrDefaultAsync`, `QuerySingleOrDefaultAsync`, `ExecuteScalarAsync<T>`).
  - Name-based `NpgsqlDataReader` getters, hand-written `static Map(NpgsqlDataReader)` row mapping.
  - No `DynamicMethod`, no `MakeGenericType` — clean Native AOT.

### Changed — Migrations

- `Verbara.Sdk.Sessions.Postgres` refactored to use `Verbara.Sdk.Data.Npgsql` (Dapper-free).
- `Dapper` + `Dapper.AOT` dropped from `Directory.Packages.props` — no remaining SDK consumer.
- Dead `Verbara.Sdk.Dapper.Stubs` canary project removed.

### Added — Build guards

- Permanent `BanDapperPackageReferences` MSBuild guard — any future reference to `Dapper`, `Dapper.AOT`, or `Verbara.Sdk.Dapper.Stubs` fails the build.

### Fixed — CI

- `NU1301` fixed: siblings reference `Verbara.Sdk.Data.Npgsql` via `ProjectReference` (matching every other Verbara.Sdk.* sibling), dropped the maintainer-only local NuGet feed from `nuget.config`. `dotnet pack` still emits the correct `Verbara.Sdk.Data.Npgsql 2.2.0` nuspec dependency.

### Documentation

- New `docs/research/` gitleaks audit baseline.
- README stack table aligned to canonical role wording.

## [2.1.2] - 2026-05-08

**SmartTurn polish + observability hardening.**

### Added

- `[OptionsValidator]` source generator on `SmartTurnDetectorOptions` — AOT-safe validation with `[Range]` on `TurnConfidenceThreshold` [0,1], `SilenceThresholdDb` [-100,0], `IntraOpThreads` [1,64]. `ValidateOnStart()` wired in DI.
- 8 dedicated `MelFilterBank` tests + 7 options-validation tests.

### Fixed

- Hann window aligned to the periodic formula (`2πi/N`) matching HuggingFace WhisperFeatureExtractor and librosa convention used during smart-turn-v3 model training. Improves mel spectrogram accuracy.

### Infrastructure

- ONNX model (8.3 MB) migrated to Git LFS.

## [2.1.1] - 2026-05-07

### Fixed

- Corrected `RepositoryUrl` and `PackageProjectUrl` in package metadata — was pointing to `verbara/verbara-sdk` (wrong) instead of `verbara/Verbara.Sdk`.

## [2.1.0] - 2026-05-07

**Smart Turn Detection — ML-based pluggable turn detector.**

### Added — VoiceAi package

- **`Verbara.Sdk.VoiceAi.TurnDetection`** (new) — ML-based turn detector using the [Pipecat smart-turn-v3.2](https://huggingface.co/pipecat-ai/smart-turn-v3) ONNX model.
  - Detects semantic end-of-turn boundaries, not just silence pauses (94.3% English accuracy, ~12 ms CPU inference).
  - Drop-in replacement for `SilenceTurnDetector` via `services.AddSmartTurnDetection(...)`.
  - Pipeline: PCM16 8 kHz → 16 kHz resampling → Whisper-compatible Mel spectrogram → ONNX inference.
  - Configurable: confidence threshold, silence trigger duration, execution provider (CPU/CUDA), max utterance duration.
  - `IsAotCompatible=false` (ONNX Runtime uses reflection) — the rest of the SDK remains fully AOT-compatible.

### Added — Package validation

- API compatibility validation enabled against v2.0.0 baseline for all 26 existing packages.
- Removed stale Asterisk-era `CompatibilitySuppressions.xml` files.

## [2.0.0] - 2026-05-06

**Full rebrand from `Asterisk.Sdk.*` → `Verbara.Sdk.*` ([ADR-0036](docs/decisions/0036-rebrand-to-verbara.md)).** Breaking change: every namespace, assembly, and NuGet package renamed. The legacy `Asterisk.Sdk.*` packages on nuget.org are deprecated and each points to its `Verbara.Sdk.*` replacement.

### Breaking — Rebrand

- All namespaces, assemblies, and NuGet packages renamed from `Asterisk.Sdk.*` to `Verbara.Sdk.*`.
- DI methods renamed: `AddAsterisk*()` → `AddVerbara*()`.
- Types renamed: `AsteriskServer` → `VerbaraServer`, `AsteriskOptions` → `VerbaraOptions`, `AsteriskTelemetry` → `VerbaraTelemetry`, etc.

#### Migration from v1.x

1. Update all NuGet package references from `Asterisk.Sdk.*` to `Verbara.Sdk.*`.
2. Replace `using Asterisk.Sdk.*` with `using Verbara.Sdk.*` in all source files.
3. Rename DI methods: `AddAsterisk*()` → `AddVerbara*()`.
4. Rename types: `AsteriskServer` → `VerbaraServer`, `AsteriskOptions` → `VerbaraOptions`, etc.

### Added — Turn detection

- **`ITurnDetector`** interface — pluggable turn detection for the VoiceAi pipeline. Replace the default silence-based detector with custom implementations (e.g., ML-based turn detection).
- **`SilenceTurnDetector`** — default implementation refactored out of the hardcoded `AudioMonitorLoop` logic. Registered automatically via DI; override by registering your own `ITurnDetector` before calling `AddVoiceAiPipeline<T>()`.
- **`FakeTurnDetector`** — test fake in `Verbara.Sdk.VoiceAi.Testing` for deterministic pipeline testing.

### Documentation

- `Examples/README.md` — full 26-example catalog.
- ADR catalog — entries 0025–0036 added.
- `docs/guides/README.md` — 8-guide index created.
- Open-core stack table + trademark note added to root `README.md` License section.

### Stats

- 26 NuGet packages published.
- 2,868 unit tests passing.
- 0 build warnings, 0 trim warnings.

## [1.15.3] - 2026-05-03

**R1.5 "VoiceAi Refresh" — three new TTS providers + TTFA metric + housekeeping.** Strictly additive minor patch — zero breaking changes, all existing test suites pass without modification. Ships ElevenLabs Flash 2.5 polish, Deepgram Aura 2 TTS WebSocket as a new provider, LMNT TTS as a new provider, and the `tts.synthesis.ttfa_ms` histogram so the latency claims of the new providers are verifiable in production. Also rolls in tooling housekeeping (coverlet 10, CI dependency-review, xunit migration tracking).

### Added — VoiceAi providers

- **`Verbara.Sdk.VoiceAi.Tts.Deepgram`** — new TTS provider using Deepgram's WebSocket streaming endpoint (`wss://api.deepgram.com/v1/speak`). NOT the older REST `/v1/speak` (which had ~70% higher LLM→TTS latency per Deepgram's published benchmarks). Mirrors the Cartesia WebSocket pattern (`Channel<ReadOnlyMemory<byte>>` + dedicated receive loop, half-close socket post-request). 12-voice catalog: 8 Aura 2 EN voices (Thalia default, Andromeda, Zeus, Orpheus, Helios, Apollo, Luna, Arcas) + 1 Aura 2 ES (Sirio) + 3 legacy Aura 1 voices (Asteria, Orion, Stella) for migration paths. New types under `Verbara.Sdk.VoiceAi.Tts.Deepgram` namespace: `DeepgramTtsOptions`, `DeepgramSpeechSynthesizer`, `DeepgramVoices`. Register via `services.AddDeepgramSpeechSynthesizer(opts => { opts.ApiKey = "…"; opts.Model = DeepgramVoices.Thalia; })`. Auto-registers `TtsHealthCheck`. Multilingual Aura 2 voices (NL/FR/DE/IT/JA) intentionally not in the catalog yet — voice ids unconfirmed in public Deepgram docs at impl time; tracked as a TODO in `DeepgramVoices.cs`.

- **`Verbara.Sdk.VoiceAi.Tts.Lmnt`** — new TTS provider for LMNT (sub-200 ms TTFA per third-party 2026 benchmarks). Supports both transports via `LmntTtsOptions.Transport` enum: `WebSocket` (default, low-latency, `wss://api.lmnt.com/v1/ai/speech/stream`) and `Http` (fallback for environments blocking outbound WS, `POST https://api.lmnt.com/v1/ai/speech/generate`). Auth via `X-API-Key` (header for HTTP; first-message JSON field for WS) + `lmnt-version: 1.0`. 4-voice catalog (`Leah` default, `Amy`, `Ansel`, `Elowen`). New types under `Verbara.Sdk.VoiceAi.Tts.Lmnt` namespace: `LmntTtsOptions`, `LmntSpeechSynthesizer`, `LmntVoices`. Register via `services.AddLmntSpeechSynthesizer(opts => { opts.ApiKey = "…"; opts.Voice = LmntVoices.Leah; })`. Auto-registers `TtsHealthCheck`. A few contract details in the LMNT public docs were ambiguous; `TODO(R1.5)` comments in the source flag specific lines to verify against the live API at integration-test time.

### Added — ElevenLabs Flash 2.5

- **`ElevenLabsModels`** — public static class with const strings: `Flash25 = "eleven_flash_v2_5"`, `Turbo2 = "eleven_turbo_v2"`, `Multilingual2 = "eleven_multilingual_v2"`. Use these instead of magic strings in `ElevenLabsOptions.ModelId`.
- **`ElevenLabsLatencyOptimization`** — public enum (`Off`/`Low`/`Mid`/`High`/`Max`, mapped to ElevenLabs' `optimize_streaming_latency` URL param 0-4 scale).
- **`ElevenLabsOutputFormat`** — public enum (`Pcm16k` / `Pcm22050` / `Pcm24k`, mapped to provider's `output_format` URL param).
- **`ElevenLabsOptions.LatencyOptimization` and `.OutputFormat`** — additive properties. The synthesizer surfaces these as query parameters on the WebSocket endpoint URL.

### Added — Observability

- **`SpeechSynthesisMetrics.SynthesisTtfaMs`** — new public `Histogram<double>` exposed on the existing `Verbara.Sdk.VoiceAi.Tts` `Meter`. Records **Time-To-First-Audio**: elapsed milliseconds from synthesis start until the first audio chunk is yielded to the caller. Tagged with `voiceai.provider`. Recommended histogram buckets: 5/10/25/50/100/250/500/1000/2500/5000 ms. The existing `tts.synthesis.latency_ms` (total synthesis duration) is preserved unchanged.
- **`VoiceAiPipeline`** records TTFA inline at the existing metric site — gated by a single boolean so only the first chunk emits the measurement; subsequent chunks pass through without extra cost. Behavior validated by 5 new pipeline tests covering: recording on first yield, no recording on empty enumerable, TTFA ≤ total latency, exactly-once on many chunks, no recording when synthesizer throws.

### Added — CI / tooling

- **`.github/workflows/dependency-review.yml`** — preventive scanning on every PR. Blocks merges that introduce a package with High/Critical CVE or a copyleft license incompatible with MIT (AGPL, GPL-2.0, GPL-3.0, SSPL). Complements the existing reactive Dependabot configuration.

### Changed

- **ElevenLabs default model** flips from `eleven_turbo_v2` → `eleven_flash_v2_5`. **Non-breaking default change**: callers who explicitly set `ElevenLabsOptions.ModelId` see no change; callers using the default see the new model. Flash 2.5 targets <150 ms TTFA per ElevenLabs' published latency guidance and is the correct choice for real-time telephony. Eleven v3 (GA 2026-03-14) is intentionally NOT a candidate for this SDK — v3 is the expressive flagship for non-realtime use; Flash 2.5 remains the streaming/telephony target.
- **`coverlet.collector` 6.0.4 → 10.0.0** — drop-in replacement for code coverage collection. Skips 8.x (no value sitting there). Real fixes that benefit this SDK: IAsyncEnumerable branch math (#1836) used in ARI/Live/Sessions stream code, `LibraryImport`/`DllImport` instrumentation crashes (#1762), `Mediator.SourceGenerator` empty reports (#1718). `nuspec` deps empty + `coverlet.collector.targets` and `VSTestIntegration.md` shipped surface idéntico across versions verified at audit time. Validated locally on `Tests/Verbara.Sdk.Ami.Tests` with a `VersionOverride` spike — zero delta in coverage metrics (line/branch counts byte-identical between 6.0.4 and 10.0.0 baseline). VSTest collector hook works on .NET 10 SDK + xunit 2.9 without `TestingPlatformDotnetTestSupport=false` guard.
- **`.github/dependabot.yml`** — removed the obsolete `coverlet.collector` major-version ignore rule that mischaracterized 10.x as breaking. Only the MTP/VSTest split matters for the upgrade and the repo stays VSTest.

### Documentation

- **R1.5 spec + plan rewritten in place (v2)** — scope correction based on a deep state-of-the-art audit (May 2026): (a) **dropped** Whisper V3 local STT (quality unfit for telephony 8 kHz audio per third-party benchmarks — ~30-40% WER regression vs cloud STT options already in the SDK; Whisper.net AOT support unconfirmed in any release notes; deferred to a future on-prem privacy track); (b) **upgraded** Deepgram Aura 2 integration from REST to WebSocket; (c) **added** LMNT TTS as a new provider. Same total ~1 week of work, no Phase 0 AOT spike, lower risk, more product value. Original v1 spec retained in git history at commit `565a1bb`.
- **`docs/research/2026-05-03-xunit-v3-v4-migration-readiness.md`** — watch list documenting the four readiness gates that must flip before re-evaluating the migration from xunit 2.9.x: FluentAssertions #2935 detection bug fix shipped in FA 7.x, xunit #3167 NSubstitute false-positive resolved, xunit.v3 v4.0 stable released with full Native AOT, and a canary migration in dotnet/runtime or dotnet/aspnetcore. The `dependabot.yml` ignore rules for `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, and `FluentAssertions` remain tied to these gates.
- **`src/Verbara.Sdk.VoiceAi.Tts/README.md`** — provider table updated to 6 providers (added Deepgram, LMNT). New "Metric catalog" section documents `tts.synthesis.ttfa_ms` and `tts.synthesis.latency_ms` with recommended histogram buckets.

### Notes

- 0 build warnings, 0 trim warnings, 0 IL3050/IL3053 across all 26 NuGet packages. Native AOT clean.
- Test totals: ~2,837 unit tests pass / 0 fail / 0 skip (was ~2,799 in v1.15.2). New tests: +9 ElevenLabs (Track 1.A), +12 Deepgram (Track 1.B), +12 LMNT (Track 1.C), +5 TTFA pipeline (Phase 2). 154/154 functional + 65/65 integration unchanged.
- **Deferred**: per-provider streaming-not-buffering quality gate (one test per TTS provider asserting the synthesizer yields its first frame before the upstream finishes sending) was scoped into Phase 3 but deferred to a follow-up patch — the TTFA metric works correctly today; this gate would catch *future* provider regressions where a provider buffers the full clip before yielding. Tracked as a follow-up issue.
- 26 packages pack clean with `TreatWarningsAsErrors=true`.
- Whisper V3 local STT is **deferred**, not cancelled. The original v1 R1.5 plan included it; the v2 scope-correction moved it to a future "on-prem privacy mode" track where it will deliver actual value (air-gapped privacy-sensitive deploys), not as a marginal STT option for telephony where Deepgram cloud already wins on quality and latency.

## [1.15.2] - 2026-04-27

**Documentation refresh + CI portability fix.** Zero public API surface delta (`PublicAPI.Shipped.txt` unchanged). Zero functional changes. Ships a doc-audit sprint that addresses the highest-impact P0+P1 findings on nuget.org / repo-landing pages, plus drops a machine-specific path from `nuget.config` so GitHub Actions runners can restore the project portably.

### Changed (documentation — root README + ops docs)

- **Root [`README.md`](README.md)** — "Status" paragraph rewritten end-to-end. The previous version still described **v1.12.0 / 24 packages** despite v1.13/v1.14/v1.15 having shipped since. Now describes v1.15.1 cumulative state (26 pkgs, 4-release rollup highlighting `Verbara.Sdk.Resilience`, `Verbara.Sdk.Cluster.Primitives`, per-URL circuit breaker on `Push.Webhooks`, `AsteriskSemanticConventions` catalog, multi-RID AOT matrix, dual Asterisk 22 LTS / 23 Standard support, 35 ADRs).
- **Root README Observability section** — Meter count corrected `14` → `15` (the `Verbara.Sdk.Resilience` meter shipped in v1.14.0 but the doc still claimed the v1.13 count). Added explicit reference to the `AsteriskSemanticConventions` const-string catalog so consumers know it exists.
- **[`docs/operations/README.md`](docs/operations/README.md)** — Same meter-count correction (`12` → `15`) in two places. Added pointer to `AsteriskTelemetry.MeterNames` as the canonical source-of-truth list.

### Changed (per-package READMEs visible on nuget.org)

Five package READMEs were either 2-line stubs or inadequate-but-better. Each now follows the same template used by the well-documented packages (`Verbara.Sdk.Resilience` v1.14, `Verbara.Sdk.Cluster.Primitives` v1.15) — title + 1-line tagline, "What it does" with public surface, install instructions, working quickstart code, ADR cross-references where relevant, and a license note.

- **[`src/Verbara.Sdk/README.md`](src/Verbara.Sdk/README.md)** — was 10 lines. Now ~60 lines covering the actual public surface consumers reach for: `AsteriskSemanticConventions` catalog (60 const strings / 14 nested classes), `AsteriskTelemetry` runtime-discoverable lists (9 ActivitySources / 15 Meters), source-generator attribute markers, OTel one-liner registration snippet.
- **[`src/Verbara.Sdk.Hosting/README.md`](src/Verbara.Sdk.Hosting/README.md)** — was 20 lines. Now ~90 lines positioning the package as the recommended SDK entry point with `AddAsterisk` variants (`IConfiguration` and inline `Action<AsteriskOptions>`), `appsettings.json` binding example, multi-server pool pointer, health-endpoint wiring, hosted-lifecycle and AOT notes.
- **[`src/Verbara.Sdk.VoiceAi.Stt/README.md`](src/Verbara.Sdk.VoiceAi.Stt/README.md)** — was 2 lines and listed only 4 of 7 providers. Now ~65 lines with full provider table (Deepgram, Whisper local, Azure Whisper, Google Speech, Cartesia Ink-Whisper, AssemblyAI Universal-2, Speechmatics) including mode + latency notes, per-provider DI registration snippets, example pointers, and an ADR-0014 cross-reference for the no-vendor-SDK design rationale.
- **[`src/Verbara.Sdk.VoiceAi.Tts/README.md`](src/Verbara.Sdk.VoiceAi.Tts/README.md)** — was 2 lines and listed only 2 of 4 providers (missing Cartesia and Speechmatics). Now ~70 lines with full provider table (ElevenLabs, Cartesia Sonic-3, Speechmatics, Azure) including TTFA targets and a "choosing a provider" decision guide.
- **[`src/Verbara.Sdk.VoiceAi.Testing/README.md`](src/Verbara.Sdk.VoiceAi.Testing/README.md)** — was 2 lines. Now ~65 lines with the three fakes table (`FakeSpeechRecognizer`, `FakeSpeechSynthesizer`, `FakeConversationHandler`), quickstart code for stubbing recognizers + synthesizers in unit tests, and a "why use it" section (no API keys in CI, deterministic timing, failure injection).

### Fixed (build portability)

- **[`nuget.config`](nuget.config)** — drop the machine-specific local feed entry. The previous commit (`4393dfc`) added `<add key="local" value="/media/Data/Source/Verbara/local-nuget-feed/" />` to mirror the Pro/Platform pattern, but the MIT SDK is the **producer** of that cross-repo local feed (Pro and Platform are the consumers). The hard-coded absolute path broke `aot-check` on GitHub runners with `NU1301: The local source ... doesn't exist`. Comment expanded to document why no local source belongs in this repo's `nuget.config`.

### Documentation

- **R1.5 "VoiceAi Refresh" plan + spec** — re-targeted from v1.15.2 → **v1.15.3** to make room for this docs-only patch. R1.5 itself is unchanged; it remains the next non-trivial release with Phase 0 AOT spike for Whisper.net pending.

### Notes

- 0 build warnings, 0 trim warnings across all 26 NuGet packages. Native AOT clean.
- 35 ADRs in repo (0001–0035, no holes). Resolved 0031 collision in v1.15.1 stays resolved.
- Test totals unchanged: ~2,799 unit tests / 154 functional / 65 integration. Same numbers as v1.15.1.
- 4 commits on `main` since `v1.15.1` tag (`41ca790`): `4393dfc` nuget.config, `205125b` docs D1+D2, `42e4081` nuget.config fix, plus the `1.15.2` bump itself.
- README content is embedded into each `.nupkg` at pack time, so the new READMEs become visible on nuget.org as soon as `publish.yml` succeeds.

## [1.15.1] - 2026-04-26

**Housekeeping patch.** Zero public API surface delta (`PublicAPI.Shipped.txt` unchanged across all 26 packages). Zero functional changes in shipped binaries — the only production-code touch is an `internal` accessor used exclusively by the test assembly via `InternalsVisibleTo`. Ships accumulated dependency maintenance, a CI test-stability fix, and post-v1.15.0 ADR/spec documentation.

### Fixed

- **`Verbara.Sdk.Cluster.Primitives.Tests.InMemoryClusterTransportTests`** — eliminated CI flakiness on 5 tests that used `Task.Delay(50)` to "wait for the subscriber's `await foreach` to register the channel". Replaced with deterministic polling on a new `internal int SubscriberCount` accessor (visible only via existing `InternalsVisibleTo` to the test assembly). 20/20 stability runs verified locally; CI verde on `f5a1bd9` and `e2f5e82`.

### Changed

- **`Microsoft.Extensions.*` 10.0.6 → 10.0.7** — patch bump on 11 packages (`Logging`, `Logging.Abstractions`, `Logging.Console`, `DependencyInjection`, `DependencyInjection.Abstractions`, `Hosting`, `Hosting.Abstractions`, `Configuration`, `Configuration.Abstractions`, `Diagnostics.HealthChecks`, `Http`, `Options`). Transitively visible to consumers of `Verbara.Sdk.Hosting`, `Sessions`, `OpenTelemetry`, etc.
- **`OpenTelemetry` 1.15.2 → 1.15.3** — patch bump on 4 packages (`OpenTelemetry`, `Extensions.Hosting`, `Exporter.Console`, `Exporter.OpenTelemetryProtocol`). Visible to consumers of `Verbara.Sdk.OpenTelemetry`.
- **`NATS.Client.Core` / `NATS.Client.Hosting` 2.5.10 → 2.7.3** — minor bump on the upstream client used by `Verbara.Sdk.Push.Nats`. **Forward-compat verified** end-to-end: 6/6 NATS integration tests (Testcontainers + real `nats:latest`) pass; none of the 2.6.x/2.7.x breaking changes affect our usage (no JetStream APIs, ASCII-only subjects, internal timeouts wrapped in our own `CancellationTokenSource.CreateLinkedTokenSource` so the `OperationCanceledException` → `NatsTimeoutException` rename is irrelevant; OTel tag rename `network.protocol.version` → `network.transport` not referenced in our docs/dashboards).
- **`Microsoft.SourceLink.GitHub` 10.0.202 → 10.0.203** — patch bump (build-time, not user-facing).
- **`Meziantou.Analyzer` 3.0.50 → 3.0.52** — patch bump (build-time analyzer).
- **`dotnet-reportgenerator-globaltool` 5.5.5 → 5.5.6** — patch bump (CI tool, not shipped).

### Documentation

- **ADR-0035 "COS (Calling Permissions System) deferred — customer-driven trigger only"** (Accepted 2026-04-25) — locks the deferral of the `feat/calling-permissions` branch until a customer-driven trigger is met. Originally numbered ADR-0031; **renumbered to 0035 on 2026-04-26** to fix an accidental collision with the prior Proposed ADR-0031 "Domain vs Integration events" (part of the v1.15.0 Event Model v2 batch). Decision content unchanged.
- **R1.5 "VoiceAi Refresh" design spec + execution plan** — `docs/specs/2026-04-25-r1.5-voiceai-refresh-design.md` + `docs/plans/active/2026-04-25-r1.5-voiceai-refresh.md`. Pending Phase 0 AOT spike (Whisper.net AOT compatibility probe) before implementation. Targets v1.15.2 (re-targeted from v1.15.1 after this housekeeping cut).

### Notes

- 0 build warnings, 0 trim warnings across all 26 NuGet packages. Native AOT clean.
- 35 ADRs in repo (0001–0035 — no missing numbers; 0031 collision resolved by renumbering COS to 0035, original 0031 "Domain vs Integration events" remains Proposed).
- Test totals unchanged: ~2,799 unit tests / 154 functional / 65 integration. `Verbara.Sdk.Cluster.Primitives.Tests` stays at 20 tests (the new helper is not a test).
- 13 commits on `main` since `v1.15.0` tag, all CI-verified before tag cut.

## [1.15.0] - 2026-04-20

**Pre-v2 Foundation.** No breaking changes. New MIT package `Verbara.Sdk.Cluster.Primitives` (26th on nuget.org) ships domain-agnostic cluster abstractions that Pro.Cluster and future consumers can build on. `AsteriskSemanticConventions` catalog grows with `Tenant`/`Event`/`Node` nested classes (6 new const strings). `Verbara.Sdk.Push.Webhooks` gains per-URL circuit breaker. ADR-0028 "Cadence commitment (v1 preview → v2 stable)" moves to `Accepted`. Operations starter kit (3 Grafana dashboards + Jaeger query catalog) lands in `docs/operations/`. Dual Asterisk support matrix (22 LTS + 23 Standard) added. AOT validation workflow expands to multi-RID matrix.

### Added

- **`Verbara.Sdk.Cluster.Primitives`** — new MIT package with domain-agnostic cluster abstractions: `ClusterEvent` (abstract record canónico), `NodeInfo`, `NodeState`, `IClusterTransport` (pub/sub), `IDistributedLock`, `IMembershipProvider`. Ships 3 in-memory reference implementations for tests. 20 unit tests. Addresses PSD v2 §9 Mes 3 foundation item. Pro.Cluster consumes this in Pro v1.10.0-pro (R1-B bundled, not included in this release).
- **`AsteriskSemanticConventions.Tenant`** — new nested class with `Id` constant (`"tenant.id"`). Aligns tenant-context tag name across SDK + Pro telemetry.
- **`AsteriskSemanticConventions.Event`** — new nested class with `Type`, `Id`, `Count` constants. Standardizes event-attribution tag names for Push/EventStore/Analytics consumers.
- **`AsteriskSemanticConventions.Node`** — new nested class with `OriginId`, `ReceiverId` constants. Standardizes cluster node-identification tag names.
- **`Verbara.Sdk.Push.Webhooks` per-URL circuit breaker** — `WebhookDeliveryService` now keys a `CircuitBreakerState` dictionary by `TargetUrl.AbsoluteUri`. Defaults: 5 failures → 30s open. New counters `CircuitOpened{url}` / `CircuitSkipped{url}` on meter `Verbara.Sdk.Push.Webhooks`. `TimeProvider` injection for deterministic tests. 5 new unit tests.
- **`docs/operations/` starter kit** — 3 Grafana dashboards (JSON-validated): `grafana-overall.json`, `grafana-webhooks.json`, `grafana-resilience.json`. `jaeger-queries.md` with 9 query patterns for distributed tracing. `README.md` with import instructions.
- **`docs/guides/asterisk-version-matrix.md`** — dual Asterisk support guide (22 LTS + 23 Standard lifecycle, break-change risk areas, migration notes).
- **`docker/docker-compose.test-23.yml`** + parameterized `docker/Dockerfile.asterisk` (`ASTERISK_VERSION`, `CODEC_OPUS_VERSION` build args) — run Functional + Integration test matrix against Asterisk 22 and 23 in parallel.
- **`.github/workflows/aot-validate.yml`** (renamed from `aot-trim-check.yml`) — multi-RID AOT validation matrix (`linux-x64`, `win-x64`, `osx-arm64`). `verify-aot.sh` accepts RID arg + host-match smoke run. `AotCanary` app extended to cover `Webhooks` / `Resilience` / `Cluster.Primitives`.

### Changed

- **ADR-0028 "Cadence commitment (v1 preview → v2 stable)"** — status `Proposed` → `Accepted`. v2.0.0 target Q4 2026 formalizado. Cadencia minor releases cada 2-4 semanas durante v1.x; v2 preview → stable window documented.
- **`MeterNames_ShouldContainAllPackages` pin test** — expected count 14 → 15 (corrects v1.14 drift where Resilience meter shipped without test update).

### Documentation

- **Post-ADR-0029 roadmap** — `docs/plans/active/2026-04-20-post-adr-0029-roadmap.md` (sanitized SDK scope, full cross-repo mirror lives in private Pro repo). Covers R1/R1.5/R2/R3/R4 ~8-10 semanas plan.
- **ADR-0026..0034 batch** — PSD v2 foundation (Event Model v2 prerequisites, CloudEvents preview, IEventLog split, ISessionInterceptor, ClusterEvent contract, cadence commitment, AOT multi-RID policy). 10 ADRs total this release.
- **`docs/plans/archived/2026-04-21-v1.14-candidates-absorbed.md`** — historical record of v1.14 candidates absorbed into post-ADR-0029 plan.

### Notes

- 0 build warnings, 0 trim warnings across all 26 NuGet packages. Native AOT clean (multi-RID matrix).
- 25 ADRs (post-v1.14.0) → 34 ADRs (post-v1.15.0). ADR-0026..0034 batch covers PSD v2 foundation; ADR-0028 advances to `Accepted`; ADR-0029 remains `Accepted` (v1.14 shipped).
- 13 commits on `main` since `v1.14.0` tag, all CI-verified.
- Pro v1.10.0-pro coordinates adoption (consume `Cluster.Primitives` + adopt `SemanticConventions.Tenant/Event/Node` in 23 call-sites across 7 Pro packages).

## [1.14.0] - 2026-04-20

**Resilience primitives added to SDK (MIT).** No breaking changes. New `Verbara.Sdk.Resilience` package (25th on nuget.org) ships composable `CircuitBreakerState`, `ResiliencePolicy`, `ResiliencePolicyBuilder`, `CircuitBreakerOpenException`, `ResilienceMetrics`, `BackoffSchedule`, and `AddAsteriskResilience` DI extension. Migrated from `Verbara.Sdk.Pro.Resilience` v1.8.1-pro per [ADR-0029](docs/decisions/0029-resilience-primitives-mit.md) (stewardship pledge — generic primitives belong in MIT). Internal hot paths (AMI/ARI reconnect, Webhook delivery) now share a single backoff primitive instead of three duplicated open-coded loops.

### Added

- **`Verbara.Sdk.Resilience`** — new MIT package with composable resilience primitives. AOT-safe, zero reflection, `TimeProvider`-based for testability. 38 migrated unit tests + 12 new `BackoffSchedule` tests (50 total). Meter `Verbara.Sdk.Resilience` enrolled automatically by `AddAsteriskOpenTelemetry().WithAllSources()` via `AsteriskTelemetry.MeterNames` catalog.
- **`BackoffSchedule.Compute(attempt, baseDelay, multiplier, maxDelay)`** — stateless helper for reconnect loops and iterative retry schedules that don't fit the bounded `ResiliencePolicy.ExecuteAsync` model. Preserves configurable multiplier + max delay cap (critical for reconnect loops with specific timing requirements).
- **`BackoffSchedule.ComputeWithJitter`** — same with deterministic ±jitter via caller-provided `Random` source.

### Changed

- **`AmiConnection.ReconnectLoopAsync`** — internal refactor. Delegates backoff calculation to `BackoffSchedule.Compute` (preserves `ReconnectInitialDelay` + `ReconnectMultiplier` + `ReconnectMaxDelay` semantics exactly). Zero observable behavior change; 633/633 AMI tests green.
- **`AriClient.ReconnectLoopAsync`** — same refactor. 423/423 ARI tests green.
- **`WebhookDeliveryService.DeliverAsync`** — same refactor. 13/13 Webhook tests green.

### Migration

Consumers of `Verbara.Sdk.Pro.Resilience` v1.8.x-pro migrate by renaming `using` + swapping `<PackageReference>`. See [ADR-0029 Migration guide](docs/decisions/0029-resilience-primitives-mit.md#migration-guide). Meter name changes from `Verbara.Sdk.Pro.Resilience` to `Verbara.Sdk.Resilience` (dashboards need one-time update; no dual-emit window).

## [1.13.0] - 2026-04-20

**Telemetry + multi-node Push.** No breaking changes. Public API grows with `AsteriskSemanticConventions` catalog (OpenTelemetry attribute names for SIP/Asterisk), `AsteriskSemanticConventions.Events` (span-event names), `RemotePushEvent` envelope, and new `Verbara.Sdk.Push.Nats` subscribe-side options. Package count stable at 24 on nuget.org.

### Added

- **`Verbara.Sdk.AsteriskSemanticConventions`** — new public static catalog (54 const strings across 11 nested classes) standardizing OpenTelemetry attribute names for SIP/Asterisk telephony. Consumers reference by name (`AsteriskSemanticConventions.Channel.Id`, `AsteriskSemanticConventions.VoiceAi.Provider`, etc.) so dashboard/query code remains stable across SDK versions. Pinned by 14 unit tests. Backed by the draft in `docs/research/2026-04-19-otel-sip-semantic-conventions.md`. ([c62f8ce](https://github.com/verbara/Verbara.Sdk/commit/c62f8ce), [066cb3c](https://github.com/verbara/Verbara.Sdk/commit/066cb3c))
- **`AsteriskSemanticConventions.Events`** nested class — span event names for transient, event-shaped telemetry (use with `Activity.AddEvent`, not `SetTag`). Five entries: `asterisk.channel.hangup`, `asterisk.dtmf.received`, `asterisk.media.started`, `asterisk.media.buffering`, `asterisk.media.mark_processed`. `WebSocketAudioSession` now emits these events on `Activity.Current` when the matching chan_websocket control message arrives. No-op when no span is active. XON/XOFF flow-control signals intentionally NOT instrumented (too noisy for span events). ([df0fe93](https://github.com/verbara/Verbara.Sdk/commit/df0fe93), [2a7af1a](https://github.com/verbara/Verbara.Sdk/commit/2a7af1a))
- **`Verbara.Sdk.Push.Nats` subscribe side (bidirectional bridge)** — closes T2 of the v1.13 roadmap. New `NatsBridgeOptions.NodeId` (optional, enables loop prevention) and nested `Subscribe` options (`SubjectFilters`, `QueueGroup`, `SkipSelfOriginated`) turn the bridge bidirectional. Incoming NATS messages materialize as `Verbara.Sdk.Push.Events.RemotePushEvent` (new public envelope) and are republished to the local `RxPushEventBus` so SSE / Webhook / dashboard subscribers on receiving nodes see the events without change to their filtering code. Loop prevention via optional `"source":"nodeId"` field in the JSON envelope + a .NET-type guard that never republishes a `RemotePushEvent`. New metrics: `EventsReceived`, `EventsSkipped`, `EventsDecodeFailed`. Extension point `INatsPayloadDeserializer` lets consumers round-trip to their concrete `PushEvent` subclasses if desired; default ships envelope-only. Queue-group semantics are opt-in; default pub/sub matches the local bus fan-out contract. JetStream / durable replay remain out of MIT (ADR-0011 boundary). Backed by [ADR-0025](docs/decisions/0025-push-nats-subscribe-and-loop-prevention.md). ([059e46d](https://github.com/verbara/Verbara.Sdk/commit/059e46d) through [c98229f](https://github.com/verbara/Verbara.Sdk/commit/c98229f))
- **Six new example apps** under `Examples/` (16 → 22): `VoiceAiCartesiaExample`, `VoiceAiAssemblyAiExample`, `VoiceAiSpeechmaticsExample`, `WebSocketMediaExample` (chan_websocket control protocol), `AriOutboundExample`, `NatsBridgeExample`. All v1.12 features now have runnable showcases. ([991078e](https://github.com/verbara/Verbara.Sdk/commit/991078e), [60fcdbb](https://github.com/verbara/Verbara.Sdk/commit/60fcdbb))
- **4 `Verbara.Sdk.Push.Nats` Testcontainers integration tests** against real `nats:2.10-alpine` covering subject prefix, payload bytes, multi-event delivery, and custom prefix behavior. `[Trait("Category", "Integration")]`. ([7a6f6fa](https://github.com/verbara/Verbara.Sdk/commit/7a6f6fa))
- **Shared `WebSocketTestServer`** in `Tests/Verbara.Sdk.TestInfrastructure/WebSocket/` — TcpListener + manual HTTP/1.1 upgrade + `WebSocket.CreateFromStream(IsServer=true)`. Unblocks `ws.Abort()` test paths that previously hung on Linux under `HttpListener`. 2 new abort tests added (AssemblyAi STT, Speechmatics STT) closing the silent coverage gap. ([b02bf18](https://github.com/verbara/Verbara.Sdk/commit/b02bf18))

### Changed

- **Activity.SetTag call-sites aligned to `AsteriskSemanticConventions`.** Five `Diagnostics/*ActivitySource.cs` files (VoiceAi, VoiceAi.AudioSocket, VoiceAi.OpenAiRealtime, Live, Sessions) now emit the conventions-matching attribute names: `voiceai.channel_id` → `asterisk.channel.id`, `originate.context/extension` → `dialplan.context/extension`, `session.direction/state/duration_ms` → `call.direction/state/duration_ms`. A T1.2 cross-package sweep added `agi.channel` → `asterisk.channel.name` to the list. Zero behavior change; consumer dashboards asserting on the old names will need to update. ([066cb3c](https://github.com/verbara/Verbara.Sdk/commit/066cb3c), [4125c9e](https://github.com/verbara/Verbara.Sdk/commit/4125c9e))
- **Cartesia STT/TTS hardening**: linked `CancellationTokenSource` between send/receive loops + 2-second `CloseOutputAsync` timeout. Production path is robust against half-dead WebSocket sockets. ([c0890ac](https://github.com/verbara/Verbara.Sdk/commit/c0890ac))

### Tests

- **Zero deferred tests anywhere in repo.** The 2 `[Fact(Skip=…)]` Cartesia abort tests are un-skipped and passing against the new `WebSocketTestServer`. 2 new abort tests added for AssemblyAi STT + Speechmatics STT. ([b02bf18](https://github.com/verbara/Verbara.Sdk/commit/b02bf18))
- **3 regression fixes** in `LiveActivitySourceTests` and `SessionActivitySourceTests` — assertions updated to match the new conventions-aligned tag names. ([ed7c2cd](https://github.com/verbara/Verbara.Sdk/commit/ed7c2cd))
- **AudioSocketSession flake hardening** — replaced fixed `Task.Delay(100-200)` waits with `TaskCompletionSource` signals on `AudioStreamState` transitions. Avg test duration 210 ms → 28 ms. ([b384bde](https://github.com/verbara/Verbara.Sdk/commit/b384bde))
- Unit tests **2,703 → 2,729** (+26: deferred cleanup +4, T1.1 pilot +4, T1.1 expansion +3, Tier 2 +2, pin-test extensions). Integration tests 59 → 65 (+6: 4 Push.Nats baseline + 2 bidirectional). Total across all categories: **2,948 pass / 0 fail / 0 Skip**.

### CI

- **New `pack-check` job** running `dotnet pack -p:TreatWarningsAsErrors=true` on every push/PR. Surfaces PackageValidation baseline drift, PublicAPI drift, missing release notes/icons, license-expression issues at PR time. 24/24 packages pack clean at HEAD. ([7174559](https://github.com/verbara/Verbara.Sdk/commit/7174559))

### Documentation

- **ADR-0025** — `push.nats` subscribe + loop prevention rationale. Captures the `source`-header design, `RemotePushEvent`-as-envelope decision, queue-group default (pub/sub), and rejection of JetStream durable consumers (ADR-0011 boundary). ([64f0719](https://github.com/verbara/Verbara.Sdk/commit/64f0719))
- **Benchmark re-baseline** — `docs/research/benchmark-analysis.md` §1a confirms hot-path parser/dispatcher numbers are stable vs v1.11.1 after the v1.13 changes. AMI `ParseSingleEvent` 619 ns vs 618 ns baseline; ARI `ParseStasisStart` within noise floor. Const folding validated by exclusion. ([fb078d5](https://github.com/verbara/Verbara.Sdk/commit/fb078d5))
- **CONTRIBUTING** — new Release Process section + safe `NUGET_API_KEY` rotation flow (`pbpaste | gh secret set …` pattern) to prevent chat-exposure during future key rotations. Lesson learned from the v1.12.0 403 publish incident. ([25dc7e7](https://github.com/verbara/Verbara.Sdk/commit/25dc7e7))
- `docs/plans/active/2026-04-20-v1.13.0-roadmap.md`, `2026-04-20-deferred-tests-cleanup.md`, and `2026-04-20-v1.13-tier2-push-nats-subscribe.md` — v1.13 planning + completed cleanup + Tier 2 execution retrospectives.
- `docs/research/2026-04-19-otel-sip-semantic-conventions.md` — §6 items 1-2 marked shipped. ([2ebacfe](https://github.com/verbara/Verbara.Sdk/commit/2ebacfe))

### Notes

- 0 build warnings, 0 trim warnings across all 24 NuGet packages. Native AOT clean.
- 15 ADRs (post-v1.12.0) → 25 ADRs (post-v1.13.0). Only ADR-0025 added in v1.13.
- 30 commits on `main` since `v1.12.0` tag, all CI-verified on ubuntu-latest runners.

## [1.12.0] - 2026-04-19

**Asterisk 23 modernization + voice-agent readiness.** No breaking changes. Package count grows 23 → 24 (one new — `Verbara.Sdk.Push.Nats`). Three new VoiceAI providers ship as subfolders inside the existing `VoiceAi.Stt` / `VoiceAi.Tts` packages (Deepgram/Azure convention, not new top-level packages).

### Added

- **`Verbara.Sdk.Push.Nats`** (new MIT package): NATS bridge for `RxPushEventBus`. Subscribes to the local Push bus and republishes every event to a NATS subject derived from the topic hierarchy. Unlocks multi-node deployments (one NATS cluster, N SDK instances, fan-out via subject-tree filtering). `NATS.Client.Core 2.5.10` — AOT-clean, zero reflection. `NatsSubjectTranslator` handles `/` and `.` separators, sanitizes wildcards and control chars. Meter `Verbara.Sdk.Push.Nats` (`events.published`, `events.failed`). Publish-only in v1.12; subscribe-side planned for v1.12.x.
- **ARI outbound WebSocket listener**: new `IAriOutboundListener` + `AriOutboundListener` under `src/Verbara.Sdk.Ari/Outbound/`. The SDK acts as the WS server that Asterisk 22.5+ `application=outbound` dials into. Validates upgrade path, Basic-Auth credentials, and app allowlist. Exposes each accepted connection as an `AriOutboundConnection` with an `IObservable<AriEvent>`. Mirrors the RFC-6455 handshake pattern from `WebSocketAudioServer`. `AriOutboundListenerHostedService` in `Verbara.Sdk.Hosting` for lifecycle management. DI: `services.AddAriOutboundListener(opts => ...)`.
- **`chan_websocket` JSON control protocol on `WebSocketAudioSession`**: Asterisk 22.8 / 23.2+ sends JSON control messages over TEXT frames (MEDIA_START, MEDIA_BUFFERING, MARK_MEDIA, SET_MEDIA_DIRECTION, XON/XOFF, DTMF, HANGUP). Session now exposes `IObservable<ChanWebSocketControlMessage>` via a new `IChanWebSocketSession : IAudioStream` sub-interface, plus send-side methods `SendMarkAsync`, `SendXonAsync`, `SendXoffAsync`, `SendSetMediaDirectionAsync`. Polymorphic JSON via source-gen `ChanWebSocketJsonContext`. Binary audio path unchanged. Writes serialized through a `SemaphoreSlim` so audio and control frames coexist safely on one WebSocket.
- **VoiceAI — Cartesia** (STT + TTS): `src/Verbara.Sdk.VoiceAi.Stt/Cartesia/` (Ink-Whisper over WebSocket, streaming transcripts) and `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/` (Sonic-3 at 40-90ms TTFA — the lowest in market as of 2026). Raw WS per ADR-0014. `AddCartesiaStt` + `AddCartesiaTts` DI extensions.
- **VoiceAI — AssemblyAI** (STT): `src/Verbara.Sdk.VoiceAi.Stt/AssemblyAi/`. Universal Streaming v3 protocol — fills the vacuum left by the discontinued official .NET SDK (April 2025). Parses `Turn` messages, ignores `Begin` / `Termination` lifecycle events. `AddAssemblyAi` DI extension.
- **VoiceAI — Speechmatics** (STT + TTS): `src/Verbara.Sdk.VoiceAi.Stt/Speechmatics/` (Realtime v2 WebSocket — sub-150ms, 55+ languages) and `src/Verbara.Sdk.VoiceAi.Tts/Speechmatics/` (REST synthesis — ~27× cheaper than ElevenLabs). Opens the enterprise price-sensitive segment. `AddSpeechmaticsStt` + `AddSpeechmaticsTts` DI extensions.
- **`.github/workflows/publish.yml`**: automated nuget.org release on `v*` tag push. Builds Release, packs all shipping projects, runs `dotnet nuget push ... --skip-duplicate` with `NUGET_API_KEY` secret. Concurrency-guarded per tag. Closes the manual-publish exposure risk documented in v1.11.1. `CLAUDE.md`'s claim about CI-driven releases is now accurate.
- **`Verbara.Sdk.Push.Nats`** meter enrolled in `AsteriskTelemetry.MeterNames` (14 meters total; `MeterNames_ShouldContainAllPackages` assertion updated accordingly).

### Documentation

- **9 retrospective ADRs — 0016 through 0024** — backfills the load-bearing decisions identified in the v1.11.1 product alignment audit §4. ADR-0016 VoiceAi `ProviderName` virtual override (92× speedup); ADR-0017 AudioSocket codec negotiation; ADR-0018 Sessions soft-TTL reconciliation (not native Redis/Postgres TTL); ADR-0019 Push bus `TraceContext` ambient capture at publish time; ADR-0020 Webhook delivery retry-only without durable DLQ; ADR-0021 AMI heartbeat strategy (30 s / 10 s, on by default); ADR-0022 Activity `CancelAsync()` as first-class alongside `CancellationToken`; ADR-0023 PublicAPI tracker adoption across all 24 packages; ADR-0024 `BannedSymbols.txt` as build-time AOT policy. Catalog grows 15 → 24.
- **`docs/research/2026-04-19-v1.12.0-product-opportunities.md`** — three-angle investigation (internal codebase + deferred work + external market) that reframed v1.12 from housekeeping to strategic release. Convergence on `chan_websocket` across all three angles was the strongest signal.
- **`docs/plans/active/2026-04-19-v1.12.0-scope.md`** — four-tier execution plan with acceptance criteria.
- **`docs/research/2026-04-19-otel-sip-semantic-conventions.md`** — draft OpenTelemetry semantic conventions for SIP / Asterisk telephony. Proposes attribute names (`sip.call_id`, `sip.response_code`, `asterisk.channel.id`, `call.direction`, `call.state`, `voiceai.provider`, etc.) grounded in the 9 ActivitySources + 14 Meters the SDK already ships. Addresses the unresolved `open-telemetry/opentelemetry-specification#2517`. Code-side alignment (emit the proposed attribute names) is deferred to v1.13 after field validation.

### Scope clarifications (from v1.11.1 planning)

Two items originally scoped for v1.12.0 were found to be **already shipped pre-v1.12** during Week 1 kickoff and removed from scope:

- ARI exception context mapping (`AriNotFoundException` / `AriConflictException` with resource name + id) — shipped in v1.6.0 Sprint 1 (task B1). `AriHttpExtensions.EnsureAriSuccessAsync(resource, id)` + `AriResourceErrorContextTests` already present.
- New AMI events for Asterisk 22/23 (`ChannelTalkingStartEvent`, `ChannelTalkingStopEvent`, `BridgeVideoSourceUpdateEvent`, `ApplicationRegisteredEvent`, `ApplicationUnregisteredEvent`, `QueueMemberEvent.Logintime`) — all shipped in earlier cycles (`PublicAPI.Shipped.txt` lines 1843-2006).

### Notes

- 0 build warnings, 0 trim warnings across all 24 NuGet packages. Native AOT clean.
- Unit tests 2,637 → 2,703 (+66: +20 chan_websocket, +19 ARI outbound, +6 Cartesia, +4 AssemblyAi, +7 Speechmatics, +10 NATS). Two Cartesia-provider abort-path tests `[Fact(Skip=)]` due to HttpListener fake-server hang — tracked for v1.12.1; production path against real Cartesia endpoint not observed to hang.
- 15 ADRs → 24 ADRs.
- First release that will flow through `.github/workflows/publish.yml` rather than manual `dotnet nuget push`.

## [1.11.1] - 2026-04-19

### Performance

- **AMI event parser** — Fast-path length check on `Output` header accumulation in `AmiProtocolReader`. Restores ~35 ns of the v1.0 → v1.11 regression in `ParseSingleEvent`; `key.Length == 6` short-circuit lets 99%+ of non-`Output` keys skip the `Equals("Output", OrdinalIgnoreCase)` compare. Throughput 1.53M → 1.62M events/sec single-thread (AMD Ryzen 9 9900X, .NET 10.0.6). 633 AMI unit tests unchanged. ([41fff67](https://github.com/verbara/Verbara.Sdk/commit/41fff67))

### Documentation

- **ADR-0013** — `ISessionHandler` as the VoiceAi dispatch seam. Captures why turn-based (`VoiceAiPipeline`) and streaming (`OpenAiRealtimeBridge`) both implement a single-method interface and why consumers swap by DI registration alone.
- **ADR-0014** — Raw HTTP / `ClientWebSocket` for VoiceAi providers. Captures why every STT + TTS provider is hand-rolled against the vendor's public API instead of depending on official vendor SDKs (AOT incompatibility).
- **ADR-0015** — AMI string interning pool (FNV-1a, 2048 buckets). Captures why the 344-LOC pool in `AmiStringPool` is load-bearing at 100K+ events/s workloads and why alternatives (`ConcurrentDictionary`, `FrozenDictionary`, `string.Intern`) are inadequate for UTF-8-span lookup.
- **Product alignment audit** — [docs/research/2026-04-19-product-alignment-audit.md](docs/research/2026-04-19-product-alignment-audit.md) reconciles the 12 accepted ADRs, 4 archived plans, and 6 archived specs against the v1.11.0 product state. Confirms `api-completeness-plan.md` is legitimately closed: 148/152 AMI (97%) + 94/98 ARI (96%) reflect an intentional scope decision, not abandoned work. Documents 12 further load-bearing decisions as ADR candidates for future releases.

### Notes

- No API changes. No breaking changes. 0-warning build preserved across all 23 NuGet packages.
- 12 ADRs → 15 ADRs in `docs/decisions/`.

## [1.11.0] - 2026-04-18

### Added

- **`Verbara.Sdk.OpenTelemetry`** (new MIT package): batteries-included OpenTelemetry wiring. `services.AddAsteriskOpenTelemetry(b => b.WithAllSources().WithPrometheusExporter().WithOtlpExporter(...))` enrolls every `AsteriskTelemetry.ActivitySourceNames` + `MeterNames` and attaches Console / OTLP / Prometheus exporters. `ConfigureTracing` / `ConfigureMetrics` escape hatches give direct access to the underlying `TracerProviderBuilder` / `MeterProviderBuilder` for samplers, views, and custom processors. Uses OpenTelemetry 1.15.2 (avoids 1.10.x vulnerability).
- **`Verbara.Sdk.Push.Webhooks`** (new MIT package): outbound HTTP webhook delivery consuming the Push bus. `services.AddAsteriskPush().AddAsteriskPushWebhooks(opts => ...)` registers `IWebhookSubscriptionStore` (in-memory default), `IWebhookSigner` (HMAC-SHA256 default), `IWebhookPayloadSerializer` (UTF-8 JSON envelope, AOT-safe), and a `WebhookDeliveryService` `BackgroundService`. Per-delivery HMAC-SHA256 signature in `X-Signature` header, exponential retry capped at `MaxDelay`, trace-context propagation via `traceparent`, per-subscription `MaxRetries`/`Headers` overrides, dead-letter metrics. Meter `Verbara.Sdk.Push.Webhooks` (enrolled in `AsteriskTelemetry.MeterNames`): counters `deliveries.succeeded`, `deliveries.failed`, `deliveries.retried`, `deliveries.dead_letter`.
- **Contact-center activities** (in `Verbara.Sdk.Activities`): four new supervisor/transfer primitives.
  - `AttendedTransferActivity` — wraps AMI `Atxfer` via a new `AmiActivityBase` (takes `IAmiConnection` instead of `IAgiChannel`); required when the supervisor operates outside a live AGI context.
  - `ChanSpyActivity` — AGI `ChanSpy` application with `ChanSpyMode` enum (`Both`, `SpyOnly`, `WhisperOnly`, `Coach`) plus free-form `Options` string for the full flag set.
  - `BargeActivity` — AGI `ChanSpy` with the `B` (barge) flag; supervisor joins as audible third party.
  - `SnoopActivity` — ARI snoop channel creation via `IAriClient.Channels.SnoopAsync`; exposes the resulting snoop channel via `SnoopChannel` property.
- **`Verbara.Sdk.Sessions.Redis`** (new MIT package): `RedisSessionStore : SessionStoreBase` promoted from the prior spike. Fluent `UseRedis(...)` extension with three overloads — `Action<RedisSessionStoreOptions>`, pre-built `IConnectionMultiplexer`, and raw connection string. Data layout: one JSON snapshot per session, secondary linked-id index, active set (cursor-scanned), completed sorted-set with TTL-driven eviction. Pipelined I/O via `CreateBatch()` + `Task.WhenAll(...).WaitAsync(ct)`. Cancellation honored at entry and around all batch awaits. AOT-safe (source-gen `SessionJsonContext`). Integration tests use Testcontainers (`redis:7-alpine`, no env-var dependency).
- **`Verbara.Sdk.Sessions.Postgres`** (new MIT package): `PostgresSessionStore : SessionStoreBase` using Npgsql 10 + Dapper + JSONB. Fluent `UsePostgres(...)` extension with the same three overloads as Redis. UPSERT via `INSERT ... ON CONFLICT (session_id) DO UPDATE`. `SaveBatchAsync` in a transaction with rollback. Partial index `ix_asterisk_sessions_active` backs `GetActiveAsync`. Identifier validation (`TableName`, `SchemaName`) at resolve time against `^[A-Za-z_][A-Za-z0-9_]*$` via `AddOptions<T>().Validate`. Migration SQL (`001_create_sessions_table.sql`) ships in the `.nupkg` at `contentFiles/any/any/Migrations/`.
- **`Verbara.Sdk.Sessions.ISessionStore`** interface: additive companion to `SessionStoreBase` — enables NSubstitute mocking in tests and supports factory-based DI registration. `SessionStoreBase` now declares `: ISessionStore`; zero breaking changes for existing consumers.
- **`Verbara.Sdk.Sessions.Extensions.ISessionsBuilder`** fluent-builder interface: entry point for backend-specific registration (`UseInMemory`, `UseRedis`, `UsePostgres`). Exposed by two new overloads in `Verbara.Sdk.Hosting`: `AddAsteriskSessionsBuilder(...)` and `AddAsteriskSessionsMultiServerBuilder(...)`. The existing `AddAsteriskSessions` / `AddAsteriskSessionsMultiServer` methods still return `IServiceCollection` — consumers opt into the builder at their own pace.
- **`docs/guides/session-store-backends.md`**: decision guide, registration patterns, data layout, identifier-safety notes, benchmark reference.
- **README:** CI + AOT Trim workflow badges, NuGet download badge, Native AOT badge; `## Documentation` table of contents linking guides/benchmarks/technical+commercial READMEs/CHANGELOG/CONTRIBUTING/SECURITY; **Session Store Backends** subsection in the Packages table.
- **README Quick Start:** 10-line "First contact" preamble showing a minimal `AddAsterisk` snippet and a pointer to `Examples/BasicAmiExample/`.
- **`.github/dependabot.yml`:** daily NuGet updates (grouped: Microsoft.Extensions, test stack, analyzers) + weekly github-actions updates.
- **`.github/workflows/codeql.yml`:** CodeQL C# analysis on push + PR + weekly Sunday cron with `security-extended,security-and-quality` query suites.
- **`tools/install-hooks.sh`:** one-time installer for a local `pre-commit` hook that runs `claudelint` when `CLAUDE.md` or `.claude/` files are staged.

### Changed

- **`Verbara.Sdk.Sessions`:** `CallSessionSnapshot` + `SessionJsonContext` hoisted from the Redis spike into `src/Verbara.Sdk.Sessions/Serialization/` as `internal` — shared round-trip between Redis and Postgres backends. `InternalsVisibleTo` grants added for `Verbara.Sdk.Sessions.Redis`, `Verbara.Sdk.Sessions.Postgres`, and the matching test projects.

### Removed

- **`Tests/Verbara.Sdk.Redis.Spike`**: retired after migration to production package `Verbara.Sdk.Sessions.Redis`. Spike tests moved to `Tests/Verbara.Sdk.Sessions.Redis.Tests/` (integration-tagged) and `Tests/Verbara.Sdk.Sessions.Tests/SnapshotSerializationTests.cs` (unit). Latency smoke-test preserved with `[Trait("Category", "Benchmark")]` so CI integration filters can exclude it.
- **`Tests/Verbara.Sdk.Redis.Spike.Aot`**: orphaned AOT smoke-check for the retired spike. Production `Verbara.Sdk.Sessions.Redis` + `Verbara.Sdk.Sessions.Postgres` are covered by the repo-wide AOT Trim workflow (`<IsAotCompatible>true</IsAotCompatible>` inherited from `Directory.Build.props`).

### Notes

- No breaking changes. All shipped API surfaces from v1.10.2 remain intact; new features are additive. `AddAsteriskSessions` continues to return `IServiceCollection`; consumers wanting fluent-builder access call `AddAsteriskSessionsBuilder` instead.
- Dapper's runtime IL emit is AOT-safe in .NET 10 under current toolchain; verified by the AOT Trim workflow.

---

## [1.10.2] - 2026-04-18

### Fixed

- **Push:** `RxPushEventBus.PublishAsync` now captures the ambient W3C traceparent from `Activity.Current` into `PushEventMetadata.TraceContext` when the publisher has not already set it. Previously the `ExecutionContext` flow was broken at the bus's internal `Channel` boundary (the dispatch loop runs under a `Task.Run` started at construction time), causing downstream transports — SSE endpoints and `Verbara.Sdk.Pro.Push` backplanes — to see a null trace context and start receiver spans as new trace roots. The capture is guarded (`TraceContext: null` only) so publishers remain free to override the trace context explicitly.

### Notes

- Source- and binary-compatible with v1.10.1. Transparent behaviour change that only activates when an `Activity` is live at publish time.

---

## [1.10.1] - 2026-04-18

### Added

- **Push:** `PushEventMetadata.TraceContext` — optional `string?` parameter carrying a W3C traceparent (`00-{trace-id}-{span-id}-{flags}`) for cross-boundary distributed tracing. When present, transports crossing process/network boundaries (SSE endpoints in `Verbara.Sdk.Push.AspNetCore`, backplane relays in `Verbara.Sdk.Pro.Push`) inject it into the wire envelope so downstream subscribers can continue the publisher's trace. Null default; older consumers safely ignore the unknown field. Establishes the pattern for future cross-boundary propagation (AMI/ARI, tracked in a separate spec).

### Notes

- Fully source- and binary-compatible with v1.10.0. Additive optional parameter on a positional record — existing call sites with 5 args continue to compile and bind unchanged.
- 19 packages on nuget.org. 0 build warnings, 0 trim warnings.

---

## [1.10.0] - 2026-04-17

### Added

- **VoiceAi:** `SpeechRecognizer.ProviderName` and `SpeechSynthesizer.ProviderName` virtual properties — stable, allocation-free identifiers for the underlying STT/TTS provider. Default implementation returns `GetType().Name` (backwards-compatible for out-of-tree subclasses). Overridden with literals in built-in providers: `"Deepgram"`, `"Google"`, `"Whisper"`, `"AzureWhisper"`, `"Azure"`, `"ElevenLabs"`, `"Fake"` (STT + TTS).

### Changed

- **VoiceAi:** `VoiceAiPipeline` hot path now reads `_stt.ProviderName` / `_tts.ProviderName` instead of calling `GetType().Name` on every utterance — removes per-utterance reflection from STT recognition and TTS synthesis activity tags.
- **PublicAPI:** Promoted `PublicAPI.Unshipped.txt` → `PublicAPI.Shipped.txt` for the six VoiceAi packages (`VoiceAi`, `VoiceAi.Stt`, `VoiceAi.Tts`, `VoiceAi.Testing`, `VoiceAi.OpenAiRealtime`, `VoiceAi.AudioSocket`). Consolidates the v1.9.0 telemetry stack (Metrics + HealthCheck + ActivitySource) along with the new `ProviderName` virtual property.

### Fixed

- **Tests:** `AsteriskTelemetryTests.ActivitySourceNames_ShouldContainAllPackages` / `MeterNames_ShouldContainAllPackages` — updated stale counts (6→9 and 7→12) to reflect the VoiceAi telemetry registrations added in v1.9.0.

### Notes

- Fully source- and binary-compatible with v1.9.0. Additive public API only.
- 19 packages on nuget.org. 0 build warnings, 0 trim warnings.

---

## [1.9.0] - 2026-04-17

### Added

- **VoiceAi telemetry — full stack in 5 packages:**
  - `VoiceAiMetrics`, `SpeechRecognitionMetrics`, `SpeechSynthesisMetrics`, `AudioSocketMetrics`, `OpenAiRealtimeMetrics` — counters, histograms, gauges per package (sessions started/completed/failed, transcription/synthesis latency, synthesis characters, session duration, bytes/frames).
  - `VoiceAiActivitySource`, `AudioSocketActivitySource`, `OpenAiRealtimeActivitySource` — distributed tracing for pipeline/session/recognition/synthesis spans.
  - Health checks: `VoiceAiHealthCheck`, `SttHealthCheck`, `TtsHealthCheck`, `AudioSocketHealthCheck`, `OpenAiRealtimeHealthCheck`.
- **Hosting:** `AsteriskTelemetry.ActivitySourceNames` count 6→9 and `MeterNames` count 7→12 to include VoiceAi/AudioSocket/OpenAiRealtime.

### Fixed

- **VoiceAi OpenAiRealtime:** Guard `SessionsCompleted` counter on failure path so the metric is not double-counted when a session throws.
- **VoiceAi AudioSocket:** Wire frame/byte counters inside `AudioSocketSession` for per-session I/O telemetry.
- **Ari:** `AriChannel.Creationtime` changed to `string?` (tolerant reader — some Asterisk versions omit the field).
- **Live:** `LiveMetrics` now uses a per-instance `Meter` with an explicit `<long>` gauge type so multiple hosts in the same process don't collide.
- **Packaging:** `CompatibilitySuppressions.xml` added in `Sdk` and `Ari` to accept accepted ABI shifts against the 1.5.3 baseline.

### Notes

- 19 packages on nuget.org. 0 build warnings, 0 trim warnings.
- Three Asterisk PBX integration tests explicitly skipped pending docker infra: Session `Local/s`, Session `Local/101`, LiveMetrics per-instance meter.

---

## [1.8.0] - 2026-04-13

### Added

- **NEW PACKAGE — `Verbara.Sdk.Push.AspNetCore` (MIT):** SSE endpoint extraction from downstream consumers. `AddAsteriskPushAspNetCore()` DI registration and `IEndpointRouteBuilder.MapPushEndpoints(prefix = "/api/v1/push")` extension wire up Server-Sent Events delivery on top of `IPushEventBus`. Closes the v1.7+ deferred extraction.
- **Push:** Hierarchical topic routing primitives in the `Verbara.Sdk.Push.Topics` namespace.
  - `TopicName` value object (segmented topic identifiers).
  - `TopicPattern` with single-segment (`*`) and multi-segment (`**`) wildcards plus `{self}` placeholder resolution against the current subscriber.
  - `ITopicRegistry` / `TopicRegistry` for mapping event types to topic templates.
- **Push:** Subscription authorization in the new `Verbara.Sdk.Push.Authz` namespace — `ISubscriptionAuthorizer`, `AuthorizationResult` (`Allow()` / `Deny(reason)`), `ITopicPermissionMap`, and `AllowAllSubscriptionAuthorizer` default.
- **Push:** New `PushEventMetadata.TopicPath` and `SubscriberContext.RequestedTopicPattern` fields enable topic-aware routing without breaking the existing constructor signature (additional parameters default to `null`).
- **Hosting:** `AddAsteriskPush()` now also registers `ITopicRegistry` (singleton) and `ISubscriptionAuthorizer` (singleton, defaults to `AllowAllSubscriptionAuthorizer`).

### Changed

- **Push:** `DefaultDeliveryFilter.IsDeliverableToSubscriber` now applies optional topic pattern matching when the subscriber declares `RequestedTopicPattern` and the event carries `TopicPath`. Backwards-compatible: subscribers/events without these fields behave as before.

### Notes

- 19 packages on nuget.org (was 18 in v1.7.0; the new package is `Verbara.Sdk.Push.AspNetCore`).
- 0 build warnings, 0 trim warnings, all unit tests pass.
- `PublicAPI.Shipped.txt` finalized for `Verbara.Sdk.Push`, `Verbara.Sdk.Push.AspNetCore`, `Verbara.Sdk.Hosting`, `Verbara.Sdk.Sessions`, and `Verbara.Sdk.Live` (the latter three promote leftover entries from v1.5.x and v1.7.0 that were never moved out of Unshipped at release time).

---

## [1.7.0] - 2026-04-13

### Added

- **Sessions:** `AgentSession` + `AgentSessionTracker` — per-agent state with rolling statistics (calls handled, talk/hold/wrap-up time, idle), driven by `ICallSessionManager.Events`. New `AgentSessionStateChanged` domain event.
- **Sessions:** `QueueSession` + `QueueSessionTracker` — aggregate queue SLA using the previously-defined-but-unused `SessionOptions.SlaThreshold` (20s) and `.QueueMetricsWindow` (30m).
- **Sessions:** `SessionReconciliationService` (`IHostedService` with `PeriodicTimer`) — drives the previously-orphaned `SessionReconciler.TryMarkOrphaned` / `.TryMarkTimedOut` on a `SessionOptions.ReconciliationInterval` (30s) cadence.
- **Sessions:** `SessionOptions.WrapUpDuration` (default 30s).
- **Observability:** `ActivitySource`s for `Verbara.Sdk.Live`, `Verbara.Sdk.Sessions`, and `Verbara.Sdk.Push` (now 6/6 core packages).
- **Observability:** `IHealthCheck` for Live, Sessions, and Push (now 6/6 core packages, auto-registered in `AddAsterisk()` / `AddSessionsCore()` / `AddAsteriskPush()`).
- **Hosting:** `AsteriskTelemetry` static helper exposes `ActivitySourceNames[]` (6) and `MeterNames[]` (7) — discoverability without coupling to OpenTelemetry.

### Fixed

- **Sessions:** `CallSessionManager.PersistAsync` now uses the stored shutdown token instead of `CancellationToken.None`, enabling graceful shutdown.

---

## [1.6.0] - 2026-04-13

### Added

- **NEW PACKAGE — `Verbara.Sdk.Push` (MIT):** Domain-layer push event bus with `IPushEventBus` (Rx-based default), `PushEvent` base record + `PushEventMetadata`, `IEventDeliveryFilter` / `DefaultDeliveryFilter`, `ISubscriptionRegistry` / `InMemorySubscriptionRegistry`, `PushMetrics`, and `BackpressureStrategy` (`DropOldest`/`DropNewest`/`Block`).

### Fixed

- **ARI:** Tightened exception scopes during event enrichment so a single bad event no longer kills the stream.
- **Config:** `#include` directives now resolve relative to the current file's directory.
- **AMI:** Restored `EventsDropped` counter regression coverage.

---

## [1.5.3] - 2026-03-30

### Fixed

- **Hosting:** Added `AriAudioHostedService` to start/stop ARI audio servers (`AudioSocketServer`, `WebSocketAudioServer`) automatically with the application host — without this, `ExternalMedia` channels could not connect because TCP listeners were never opened

---

## [1.5.2] - 2026-03-30

### Fixed

- **Hosting:** Registered `AgiHostedService` in DI so the FastAGI server starts automatically with the application host
- **Hosting:** Added `AriConnectionHostedService` to connect/disconnect the ARI WebSocket client automatically with the application host

---

## [1.5.1] - 2026-03-26

### Fixed

- **VoiceAi:** Fixed `CancellationTokenSource` leak in `VoiceAiPipeline.DisposeAsync` — `_ttsCts` was not disposed
- **VoiceAi:** Fixed `ContinueWith` in `VoiceAiSessionBroker` to use `TaskScheduler.Default`, preventing synchronization context capture

### Improved

- **Build:** Added SourceLink, deterministic builds, and PackageValidation baseline (v1.5.0)
- **Build:** Added code quality analyzers — Meziantou, IDisposableAnalyzers, Threading Analyzers (Layers 1-3)
- **Build:** Populated `PublicAPI.Shipped.txt` for all 17 packages (API surface tracking)
- **Tests:** 1,430 unit tests (+364 since v1.5.0) — all assemblies at 82%+ coverage
  - Ari: 306 → 357 (AudioSocketServer, WebSocketAudioSession, event parse, metrics)
  - Ami: 82%, Agi: 86%, Live: 81.6%, Ari: ~83%

### Changed

- **Repo:** PbxAdmin moved to standalone repository (`Verbara.Sdk.PbxAdmin`)

---

## [1.5.0] - 2026-03-24

### Added

- **AMI:** `Context` and `Priority` fields on `ListDialplanEvent`; `Context` filter on `ShowDialplanAction`
- **AMI:** Accumulate multi-line `Output:` headers for Command responses
- **CI:** GitHub Actions pipeline with unit tests, AOT verification, and functional tests (Testcontainers)

### Fixed

- **AMI:** Fix `QueueManager.RemoveQueue` to properly clean up secondary indices

### Changed

- **Repo:** PbxAdmin moved to standalone repository ([Verbara.Sdk.PbxAdmin](https://github.com/verbara/Verbara.Sdk.PbxAdmin))

---

## [1.4.0] - 2026-03-22

### Added

- **AMI:** 11 new actions — `VoicemailRefresh`, `VoicemailUserStatus`, `PresenceState`, `PresenceStateList`, `QueueReload`, `QueueRule`, `DBGetTree`, `CoreShowChannelMap`, `Flash`, `DialplanExtensionAdd`, `DialplanExtensionRemove`
- **AMI:** 3 new response events — `QueueRuleEvent`, `QueueRuleListCompleteEvent`, `DbGetTreeResponseEvent`
- **AudioSocket:** 8 new high sample rate frame types for Asterisk 23 — `AudioSlin12` (12 kHz) through `AudioSlin192` (192 kHz)
- **AudioSocket:** `GetSampleRate()` and `IsAudio()` extension methods on `AudioSocketFrameType`
- **AudioSocket:** `WriteAudioAsync` overload accepting explicit `AudioSocketFrameType` for high-rate audio

### Compatibility

- AMI Action coverage: 150/152 (99%) of Asterisk 22-23 actions (remaining 2: DAHDI-specific)
- ARI endpoint coverage: 92/98 (94%)
- AudioSocket: full Asterisk 18-23 protocol support including high sample rate types

---

## [1.3.1] - 2026-03-22

### Added

- **ARI:** `SetEventFilterAsync` on Applications resource — filter WebSocket events per app (reduces traffic at scale)
- **ARI:** `GetStoredFileAsync` on Recordings resource — binary download of stored recordings (enables CallAnalytics transcription)
- **ARI:** `GenerateUserEventAsync` on AriClient — emit custom user events between Stasis apps

---

## [1.3.0] - 2026-03-22

### Added

- **ARI:** New `AriAsteriskResource` — 16 endpoints for system info, modules, logging, config, and global variables
- **ARI:** New `AriMailboxesResource` — 4 endpoints for mailbox state management (list, get, update, delete)
- **ARI:** 8 new `AriChannelsResource` endpoints — `Move`, `Dial`, `GetRtpStatistics`, `Silence/StopSilence`, `StartMoh/StopMoh`, `StopRing`
- **ARI:** 5 new `AriBridgesResource` endpoints — `CreateWithId`, `SetVideoSource`, `ClearVideoSource`, `StartMoh`, `StopMoh`
- **ARI:** 8 new `AriRecordingsResource` endpoints — `ListStored`, `GetStored`, `CopyStored`, `Cancel`, `Pause/Unpause`, `Mute/Unmute`
- **ARI:** 2 new `AriApplicationsResource` endpoints — `Subscribe`, `Unsubscribe` event sources
- **ARI:** 3 new `AriEndpointsResource` endpoints — `ListByTech`, `SendMessage`, `SendMessageToEndpoint`
- **ARI:** 11 new models — `AriAsteriskInfo`, `AriBuildInfo`, `AriSystemInfo`, `AriConfigInfo`, `AriStatusInfo`, `AriAsteriskPing`, `AriLogChannel`, `AriModule`, `AriMailbox`, `AriConfigTuple`, `AriRtpStats`
- **ARI:** `IAriClient` extended with `Asterisk` and `Mailboxes` resource properties

### Compatibility

- ARI endpoint coverage: ~94/98 (96%) of Asterisk 22-23 endpoints
- AMI Action coverage: 139/152 (91%)

---

## [1.2.0] - 2026-03-22

### Added

- **AMI:** 11 PJSIP management actions — `PJSIPShowAors`, `PJSIPShowAuths`, `PJSIPShowRegistrationsInbound`, `PJSIPShowRegistrationsOutbound`, `PJSIPShowResourceLists`, `PJSIPShowSubscriptionsInbound`, `PJSIPShowSubscriptionsOutbound`, `PJSIPRegister`, `PJSIPUnregister`, `PJSIPQualify`, `PJSIPHangup`
- **AMI:** 7 bridge management actions — `BridgeDestroy`, `BridgeInfo`, `BridgeKick`, `BridgeList`, `BridgeTechnologyList`, `BridgeTechnologySuspend`, `BridgeTechnologyUnsuspend`
- **AMI:** 2 transfer actions — `BlindTransfer`, `CancelAtxfer`
- **AMI:** 6 new response events for event-generating actions (`BridgeListItem`, `BridgeListComplete`, `BridgeTechnologyListItem`, `BridgeTechnologyListComplete`, `ResourceListDetailComplete`, `SubscriptionsComplete`)

### Compatibility

- AMI Actions coverage: 139/152 (91%) of Asterisk 22-23 actions

---

## [1.1.0] - 2026-03-22

### Added

- **AMI:** 3 new actions for Asterisk 20+ compatibility (`PJSIPShowContacts`, `PJSIPShowEndpoint`, `PJSIPShowRegistrationInboundContactStatuses`)
- **ARI:** `AriBridgesResource` — bridge management operations (create, addChannel, removeChannel, startMoh, stopMoh, record)
- **ARI:** Extended `IAriClient` with `Bridges` property for ARI bridge operations

### Fixed

- **AMI:** Complete queue event fields (`QueueEntryEvent`, `QueueMemberStatusEvent`, `QueueMemberPauseEvent`, `PeerEntryEvent`) for Asterisk 18-23 compatibility
- **Live:** Use `Location` field for queue member interface on Asterisk 22+ (falls back to `StateInterface`)

### Compatibility

- Tested with Asterisk 18, 20, 22, and 23

---

## [1.0.0] - 2026-03-21

First stable release of Verbara.Sdk — a .NET 10 Native AOT SDK for Asterisk PBX.

**API Stability:** API is frozen as of v1.0.0. Semantic versioning applies — no breaking changes in 1.x releases.

### Core SDK (9 packages)

- **Verbara.Sdk** — Core interfaces, base types, enums, and attributes shared across all layers
- **Verbara.Sdk.Ami** — AMI client with 115 actions, 249 events, and 17 typed responses. Zero-copy TCP parsing via `System.IO.Pipelines`. MD5 challenge-response authentication. Auto-reconnection with exponential backoff. Configurable heartbeat monitoring. Source-generated action serialization and event deserialization (zero reflection).
- **Verbara.Sdk.Agi** — FastAGI server with 54 commands and pluggable script mapping strategies (`SimpleMappingStrategy`). Per-connection timeout, status 511 hangup detection, and `AgiMetrics` instrumentation.
- **Verbara.Sdk.Ari** — ARI REST + WebSocket client with 8 resource APIs (channels, bridges, playbacks, recordings, endpoints, applications, sounds, device states). Domain exceptions for HTTP error mapping. WebSocket reconnect with exponential backoff. Source-generated JSON serialization via `AriJsonContext`.
- **Verbara.Sdk.Live** — Real-time in-memory tracking of channels, queues, agents, and conference rooms from AMI events. Secondary indices for O(1) lookups by name. Observable gauges and event counters via `System.Diagnostics.Metrics`.
- **Verbara.Sdk.Activities** — High-level telephony operations (Dial, Hold, Transfer, Park, Bridge, Conference) modeled as async state machines with `IObservable<ActivityStatus>` tracking. Real cancellation support, re-entrance guards, and channel variable capture.
- **Verbara.Sdk.Sessions** — Session Engine: AMI event correlation into unified call sessions using LinkedId grouping. State-machine lifecycle (Ringing, Answered, OnHold, Transferred, Completed), domain events (`SessionStarted`, `SessionEnded`, `SessionStateChanged`), automatic orphan detection via `SessionReconciler`, and pluggable extension points (`ISessionEnricher`, `ISessionPolicy`, `ISessionEventHandler`).
- **Verbara.Sdk.Config** — Asterisk `.conf` file parser including `extensions.conf` dialplan support. Quote-aware comment stripping.
- **Verbara.Sdk.Hosting** — DI registration via `AddAsterisk()` with AOT-safe options validation. `IHostedService` lifecycle for AMI and Live API. `IHealthCheck` for AMI connection state. Meta-package referencing all core sub-packages.

### Voice AI (7 packages)

- **Verbara.Sdk.Audio** — Pure C# polyphase FIR resampler with 12 pre-computed rate pairs (8 kHz ↔ 16 kHz ↔ 24 kHz ↔ 48 kHz). Zero-alloc output buffers, PCM16 processing, RMS energy measurement, and voice activity detection. Zero external dependencies.
- **Verbara.Sdk.VoiceAi** — Voice AI orchestration pipeline (`VoiceAiPipeline`). Dual-loop design: audio monitor + pipeline. VAD → STT → `IConversationHandler` → TTS with barge-in detection. `ISessionHandler` interchange point makes `VoiceAiPipeline` and `OpenAiRealtimeBridge` drop-in replacements for each other.
- **Verbara.Sdk.VoiceAi.AudioSocket** — AudioSocket server and client using `System.IO.Pipelines` for zero-copy bidirectional PCM streaming. `AudioSocketSession` handles bidirectional audio with backpressure. `AudioSocketClient` enables local testing without a live Asterisk instance.
- **Verbara.Sdk.VoiceAi.Stt** — Speech-to-text providers: Deepgram (WebSocket streaming, real-time), OpenAI Whisper (batch REST), Azure Whisper, and Google Speech (REST). DI registration via `AddDeepgramSpeechRecognizer()`, `AddWhisperSpeechRecognizer()`, `AddAzureWhisperSpeechRecognizer()`, `AddGoogleSpeechRecognizer()`.
- **Verbara.Sdk.VoiceAi.Tts** — Text-to-speech providers: ElevenLabs (WebSocket streaming, ultra-low-latency) and Azure TTS (REST). DI registration via `AddElevenLabsSpeechSynthesizer()`, `AddAzureTtsSpeechSynthesizer()`.
- **Verbara.Sdk.VoiceAi.OpenAiRealtime** — Bridges Asterisk AudioSocket directly to the OpenAI Realtime API, bypassing the STT+LLM+TTS chain entirely. Single persistent WebSocket with bidirectional PCM (resampled 8 kHz ↔ 24 kHz). Server-side VAD, function calling (`IRealtimeFunctionHandler`), and typed observable events (`RealtimeSpeechStartedEvent`, `RealtimeTranscriptEvent`, `RealtimeFunctionCalledEvent`).
- **Verbara.Sdk.VoiceAi.Testing** — Fake implementations (`FakeSpeechRecognizer`, `FakeSpeechSynthesizer`, `FakeConversationHandler`) for unit testing Voice AI pipelines without real API calls.

### Key Properties

- **.NET 10 Native AOT** — Zero runtime reflection, 0 trim warnings
- **Source generators** — 4 compile-time generators for AOT-safe AMI serialization (`ActionSerializerGenerator`, `EventDeserializerGenerator`, `EventRegistryGenerator`, `ResponseDeserializerGenerator`)
- **System.IO.Pipelines** — Zero-copy TCP parsing with backpressure for AMI, AGI, and AudioSocket transports
- **System.Threading.Channels** — Async event pump with configurable capacity and drop metrics
- **System.Reactive** — Observable state machines in Live, Activities, and Session layers
- **Multi-server support** — `IAmiConnectionFactory` + `AsteriskServerPool` for federated N-server deployments with agent routing
- **Observability** — `System.Diagnostics.Metrics` counters, histograms, and observable gauges in `AmiMetrics` and `LiveMetrics`; `IHealthCheck` integration
- **Reconnection** — Exponential backoff with configurable max attempts for AMI and ARI WebSocket connections
- **Thread safety** — `ConcurrentDictionary` for all entity collections, per-entity `Lock` for atomic property updates, copy-on-write volatile arrays for zero-alloc observer dispatch
- **878 unit tests, 25 integration tests, 15 benchmarks**
- **14 standalone examples** covering every SDK layer, including a full Blazor Server PBX administration panel

### Requirements

- .NET 10.0.100 or later
- Asterisk 13+ (tested through Asterisk 21.x LTS)

## v1.5.0 (2026-03-24)

### AMI
- Add `Context` and `Priority` properties to `ListDialplanEvent`
- Add optional `Context` filter to `ShowDialplanAction`
- Fix: accumulate `Output:` headers for AMI Command responses
- Add `AddDelete(section, key, value)` overload to `UpdateConfigAction`

### Live
- Add `QueueManager.RemoveQueue()` for runtime queue removal
- Fix: show logged-off agents in queue member listing
- Fix: allow file-mode config writes for queue sync

### Notes
- PbxAdmin example has been moved to its own repository: [Verbara.Sdk.PbxAdmin](https://github.com/verbara/Verbara.Sdk.PbxAdmin)
