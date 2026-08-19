# ADR-0052 — Cancellation throws at the iteration boundary: ADR-0050 E6 is narrowed to the loops that yield nothing

- **Status:** Accepted
- **Date:** 2026-08-19
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0050 (its **E6** is the clause this ADR narrows; ADR-0050 stays `Accepted` and is
  **not edited** — its 2026-08-19 addendum recorded the conflict and left the choice open, and this
  ADR is that choice). ADR-0049 (E6 exists because ADR-0049 models two outcomes and cancellation is
  the third). ADR-0044 (the same lesson one layer down: a test that passes for a reason other than
  the one it names is not evidence).

## Context

Two `Accepted` artifacts in this repo gave opposite instructions about the same line of code.

**ADR-0050 E6** — *"Cancellation is never a failure. Zero output caused by the caller's own
cancellation does not throw and is not counted as a failure."*

**The living spec `test-determinism`**, requirement *TTS synthesis observes cancellation
deterministically* — *"a token cancelled before or during `SynthesizeAsync` enumeration SHALL surface
`OperationCanceledException` at the next iteration boundary."* The same wording binds STT streaming in
the sibling requirement.

Two synthesizers implemented E6 on the path that yields to the caller:

| Site | Enclosing method | Behaviour on cancel mid-read |
|---|---|---|
| `SpeechmaticsSpeechSynthesizer.cs:118` | `SynthesizeAsync` (public) | `yield break` — stream ends, no throw |
| `LmntSpeechSynthesizer.cs:431` | `SynthesizeHttpAsync` | `yield break` — stream ends, no throw |

### The suite could not have caught this, and that is the load-bearing fact

All **ten** cancellation tests in the VoiceAi suites — seven STT, three TTS — enumerate with
`ToListAsync(cts.Token)`. `ToListAsync` checks that token itself at each iteration boundary, so it
throws whether or not the subject does. The assertion passes over a `yield break` identically to a
propagated throw. Every one of those tests measures `ToListAsync`.

All ten pass for the right reason today: the four WebSocket recognizers yield through
`channel.Reader.ReadAllAsync(ct)`, which honours the token; the three HTTP batch recognizers throw
before their first request; and all three TTS tests turn out to enumerate compliant paths too —
`LmntSpeechSynthesizerTests`' cancellation test builds its subject on `LmntTransport.WebSocket`, so
it never enters the HTTP read loop that carries the defect. But *none of the ten can demonstrate
it*, so a regression on any path they do cover would be exactly as invisible.

**Which means the blindness is not how the two defective sites escaped — nothing covers them at
all.** Speechmatics TTS has no cancellation test whatsoever, and LMNT's HTTP transport has none
either. The scenario that enumerates the providers closes its list at "(Deepgram, ElevenLabs,
Lmnt)", under a requirement whose normative sentence binds every TTS synthesizer; a closed
enumeration under an open contract is how a surface goes uncovered while the suite reads complete.
The two failures compound: had a Speechmatics test existed in the blind style, it would have been
green over the `yield break` and the defect would have looked actively verified.

### Why the two clauses could disagree for two days without anything going red

Nothing compares an ADR against a living spec, and nothing compares either against `src/`. The
conflict was found by reading, while closing an unrelated change — the same way the stale rows in
`docs/guides/provider-wire-conformance.md` were found the same day. This is a recurring shape in this
repo and it is worth naming: **an artifact that no test can contradict decays silently.**

## Decision

**F1 — The spec wins; E6 is narrowed, not deleted.** A cancelled token surfaces
`OperationCanceledException` at the next iteration boundary of any method that yields to a caller.
E6's reasoning survives everywhere it was actually right: cancellation is still **not a provider
failure**, is still **not counted** by the zero-output counter (ADR-0050 E9), and still must not be
wrapped in either of ADR-0050 E4's two exception types. What E6 may no longer authorise is a silent
`yield break` on an `IAsyncEnumerable` the caller is enumerating.

**F2 — The rule is scoped by what the `catch` ends, not by which package it sits in.** Swallowing
`OperationCanceledException` stays correct on a send loop, a teardown path, or a background task whose
completion the caller does not observe — `ElevenLabsSpeechSynthesizer.cs:133`,
`DeepgramSpeechSynthesizer.cs:168`/`:185` and `CartesiaSpeechSynthesizer.cs:174` are all of that
shape and are **not** changed. The discriminator is one question: *does this `catch` end a sequence
the caller is iterating?* If yes, it must rethrow.

**F3 — A cancellation test must not pass the cancelled token to the consumer.** `ToListAsync(ct)`,
`await foreach (... .WithCancellation(ct))` and any equivalent make the enumerator throw on the
consumer's behalf and destroy the test's discriminating power. The token goes to the **subject**
only; the enumeration is plain. This binds all ten existing tests and every future one.

**F4 — The behaviour change is breaking, and is released as such.** A caller that today enumerates a
cancelled synthesis with a plain `await foreach` receives a truncated stream and no exception; after
this ADR it receives `OperationCanceledException`. That is the intended trade — a truncated stream is
indistinguishable from a complete one, which is the silent-failure class ADR-0050 exists to retire —
but it is a behavioural break and gets a `BREAKING` CHANGELOG entry and a minor bump, matching how
ADR-0050 itself shipped.

## Consequences

- Two `src/` sites change. Eight `catch (OperationCanceledException)` sites in the same packages
  deliberately do not, and F2 is the stated reason so a future sweep does not "finish the job" by
  breaking teardown paths.
- Twelve tests change: ten rewritten, two written from nothing. **All ten rewrites are green today
  and stay green** — they are rewritten precisely because a green test that cannot fail for the right
  reason is the defect this ADR is about, and each now enumerates plainly so it could go red if its
  subject regressed. The two new tests are what the negative test actually rests on, because they
  cover the only two paths that were defective: a `WhenCancelledMidStream` test on Speechmatics TTS,
  and its counterpart on LMNT over `LmntTransport.Http`. Restore the `yield break` and both go red
  for want of a throw; remove it and both pass. The ten rewrites do not move in either direction,
  which is the honest report: they were never the ones holding the line.
- ADR-0050 stays `Accepted` with its addendum intact. The addendum posed the choice and stated the
  acceptance criteria for both directions; this ADR takes direction (a) and satisfies (b).
- **What this does not close:** nothing mechanically enforces F3. A future cancellation test can
  reintroduce `ToListAsync(ct)` and be green and blind, exactly like the ten before it. A Governance
  detector for that pattern is the obvious guard and is **deliberately not scoped here** — it belongs
  with `websocket-fake-protocol-contract`, whose `test-determinism` requirement *"A test ends on the
  signal it asserts, not on a cancellation timeout"* is the same class. Named here so it is a known
  hole rather than an assumed cover.
- **The living spec teaches the blind pattern and is not corrected here.** Its pre-cancelled scenario
  reads *"WHEN the stream is enumerated (e.g. `ToListAsync(ct)`)"* — the exact construction F3
  forbids — and its provider list is the closed enumeration named above. Both are wrong and neither
  is edited by this ADR: a living spec changes through an OpenSpec change, not by hand, and these
  two corrections belong in the same change as the F3 detector.

## Alternatives considered

**Amend the spec instead, and let E6 stand.** Rejected on three grounds. `IAsyncEnumerable`'s own
convention is that a cancelled enumeration throws — `[EnumeratorCancellation]` and `WithCancellation`
exist to make that work, and a library that opts out surprises every caller who knows the platform.
E6 read literally contradicts the spirit of its own ADR: a silently truncated stream is precisely the
"complete-and-empty" shape ADR-0050 was written to eliminate, arriving through a different door.
And the cost is asymmetric — two implementations move, against a requirement that binds ten providers
and every consumer expectation built on it.

**Change the two sites and leave the eight green tests alone.** Rejected: it would fix the instance
and leave the mechanism. The eight tests are the reason the defect survived, and rewriting only the
two that were wrong would re-arm the trap for the next regression.

**Add a Governance detector for `ToListAsync(ct)` in this ADR.** Rejected as scope, not as an idea —
see Consequences. Folding a new detector into a behavioural fix would make one PR carry two
unrelated failure modes, and this repo's house rule is one change per PR with its own measurement.
