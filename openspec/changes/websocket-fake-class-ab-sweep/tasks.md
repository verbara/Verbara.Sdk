# Tasks: websocket-fake-class-ab-sweep

Eight fakes, the same five steps each. The order below sweeps STT first because those four have no
protocol sentinel at all and therefore carry the larger unknown.

## 1. Baseline before touching anything

- [ ] 1.1 Record the current wall clock of `Verbara.Sdk.VoiceAi.Stt.Tests` and
      `Verbara.Sdk.VoiceAi.Tts.Tests` over 30 runs each, `-c Release --no-build`. A tight spread is
      the signature of fixed timeouts dominating; a wide one is real work. Note which it is —
      the *after* claim must be measured through this same harness, not against a number recorded
      at a different point.
- [ ] 1.2 For each of the eight, instrument the session handler's entry and return and record the
      measured return time. This is what surfaced the 4 987–4 992 ms concurrent-receive collision in
      the converted suite; reading the code did not.
- [ ] 1.3 Grep each fake's `CloseAsync` site for an outstanding `ReceiveAsync` on the same socket at
      that moment. Record present/absent per fake — the answer is expected to differ.

## 2. STT fakes (no sentinel today)

For each of `AssemblyAiFakeServer`, `CartesiaFakeServer` (STT), `DeepgramFakeServer`,
`SpeechmaticsFakeServer`:

- [ ] 2.1 Identify what actually sequences the fake with the client today, and write it down before
      changing it. "Nothing identified" is a finding, not a blocker.
- [ ] 2.2 Replace it with a `TaskCompletionSource` sentinel on the client's first unconditional
      frame, bounded by a generous timeout so expiry means the protocol assumption is wrong rather
      than that the machine was busy.
- [ ] 2.3 Negative-test it: remove the wait, confirm a dependent test fails; restore it, confirm it
      passes. Record both outcomes.
- [ ] 2.4 Add a hold-open path parked on the fake's own token **only if** the suite has a
      cancellation test that needs the socket alive when the token fires. Do not add one
      speculatively — an unused flag is another fence nobody watches.

## 3. TTS fakes (sentinel present, unverified)

For each of `CartesiaFakeServer` (TTS), `DeepgramTtsFakeServer`, `ElevenLabsFakeServer`,
`LmntWsFakeServer`:

- [ ] 3.1 Negative-test the existing sentinel. This is the whole point for these four: the shape is
      already right, and what is missing is evidence that it holds.
- [ ] 3.2 Negative-test the hold-open flag where one exists (`DeepgramTtsFakeServer`,
      `LmntWsFakeServer`) — clear it, confirm the cancellation test fails on the live socket state,
      restore it, confirm it passes.
- [ ] 3.3 For `CartesiaFakeServer` (TTS) and `ElevenLabsFakeServer`, which have no hold-open flag,
      check whether their suites contain a cancellation test that silently depends on the server
      staying up. If one does, it is the Class B defect wearing a different absence.

## 4. The test-side corollary

- [ ] 4.1 Sweep both suites for a `CancellationTokenSource(delay)` whose expiry is the *normal* path
      to an assertion rather than a hang bound, and for `Task.Delay` used to let something settle.
      `sync-fence-baseline.json` lists the candidates; the entries for
      `Cartesia/CartesiaFakeServer.cs`, `ElevenLabs/ElevenLabsFakeServer.cs` and
      `Lmnt/LmntWsFakeServer.cs` currently sit at zero and were left alone as out of scope of the
      previous change — confirm that is still accurate rather than inheriting it.
- [ ] 4.2 Retire each one found onto the signal the test actually asserts. Where a barrier must
      stay, mark it `// fence-allow: <REASON> — <why>` using the closed enum
      (`SIMULATED-WORK|GUARD-TIMEOUT|SETTLE|LOOP-DRIVER`).
- [ ] 4.3 Check every cancellation test in both suites against ADR-0052 F3 — the
      `CancellationProvenanceScanner` guard already fails the build on a cancelled token handed to
      the enumerator, so this is confirming the guard's verdict, not re-deriving it.
- [ ] 4.4 Delete any `sync-fence-baseline.json` entry that reaches zero, rather than leaving a
      zero-valued row behind.

## 5. Verification

- [ ] 5.1 `dotnet build Verbara.Sdk.slnx` — 0 warnings, 0 errors.
- [ ] 5.2 Unit lane green with the four-exclusion CI filter
      (`Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike`).
- [ ] 5.3 30× repeat-run determinism on both suites, idle and under CPU saturation
      (spinners at 2× core count; confirm they are reaped afterwards).
- [ ] 5.4 Like-for-like wall clock before/after through the §1.1 harness. State the delta against
      the measured noise floor — and state it as zero if that is what it is.
- [ ] 5.5 Record per fake which fences were negative-tested and what each failure looked like. A
      converted fake with no recorded failure text has not been swept.
- [ ] 5.6 `openspec validate --all --strict` green.
- [ ] 5.7 Route anything found in `src/` to `voiceai-session-teardown-races` rather than fixing it
      here.
