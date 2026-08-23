---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Anyone reading a green VoiceAi pipeline suite — 18 of its tests are paced by a clock, and the one whose name says "cancellation" stayed green through two opposite behaviours
decision_ref: Sdk/ADR-0045
---

# Proposal: voiceai-pipeline-harness-wall-clock-sweep

## Why

`test-determinism` already carries the requirement — **"A test ends on the signal it asserts, not on
a cancellation timeout"** — and `sync-fence-baseline.json` already grandfathers the files that
violate it. This change is not the discovery of a gap. It is the work the ratchet was put there to
make eventually unavoidable, for the one substrate no open change owns.

The three files and their grandfathered counts, measured against the baseline rather than assumed:

| File | Baseline | `Task.Delay` sites | Harness call sites | `[Fact]`s |
|---|---|---|---|---|
| `Pipeline/VoiceAiPipelineTests.cs` | 8 | 8 | 13 | 13 |
| `Pipeline/VoiceAiPipelineTurnDetectorTests.cs` | 4 | 4 | 2 | 2 |
| `Pipeline/VoiceAiPipelineTtfaTests.cs` | 1 | 1 | 3 | 8 |

Eight harness helpers order their phases this way — `RunPipelineWithSingleUtterance` (two copies,
one per file), `RunPipelineWithMultipleUtterances`, `RunPipelineWithContinuousVoice`,
`RunPipelineWithBargIn`, `RunPipelineWithEndlessFrames`, `RunPipelineWithFrames`,
`RunPipelineWithBargInSequence` — with `Task.Delay(500)` between phases, `Task.Delay(200)` before a
barge-in, and `Task.Delay(20)` per frame as a pacer.

**One token is the inverted case, and the rest are not.** `VoiceAiPipelineTests.cs:222` ends its
session with `new CancellationTokenSource(TimeSpan.FromMilliseconds(200))` — the token expiring *is*
the path to the assertion, which is exactly what the requirement forbids. The 10 s / 20 s / 30 s
tokens in the same three files are hang bounds and are already correct; a sweep that "fixes" those
would be removing the safety net the requirement explicitly protects. This distinction is the part
most likely to be got wrong, so it is stated before any work starts.

**Why now, and why this is application rather than design.** `ADR-0054` had to order a barge-in
against a synthesis completion without a delay, and in doing so found and proved the two seams this
harness needs — both now living in this very assembly:

- **`ITurnDetector.Analyze` is called synchronously, once per frame, on the monitor loop's own
  thread.** That makes "send one frame, wait for its signal" an exact ordering primitive: when the
  signal completes, that frame has been decided on and the next has not been read.
- **A synthesizer can park between chunks and say so**, which turns "a synthesis is in flight" from
  a probability into a fact.

`VoiceAiPipelineCancellationAccountingTests` is the worked example, shipped and green. Nothing here
needs inventing.

**The cost of leaving it is already documented, not hypothetical.**
`HandleSessionAsync_ShouldTerminateCleanly_WhenCancelled` — the 200 ms token — passed while a
cancelled session was counted as a *failure* and rethrown, and passes now that it is counted as a
*completion*. `ADR-0054` annotated it with a `<remarks>` naming what it does not cover, because
annotating was all that change could afford. Retiring it onto the signal it actually asserts is this
change's job, and it is the reason the sweep is worth more than its barrier count suggests.

## What Changes

1. **Replace each phase barrier with a signal.** Each of the eight helpers gains explicit ordering
   from the primitives above — a scripted detector that announces the frame it just decided on, and
   a synthesizer that announces when it has yielded. Where a helper's phases are genuinely
   independent, say so and drop the barrier rather than replacing it.
2. **Retire the 200 ms token** onto the signal `HandleSessionAsync_ShouldTerminateCleanly_WhenCancelled`
   asserts on, and remove the `<remarks>` `ADR-0054` left, since it will no longer be true.
3. **Leave the 10 s / 20 s / 30 s tokens alone**, and record in `tasks.md` that they were examined
   and kept, so the next sweep does not re-open them.
4. **Negative-test every replacement**: remove the new signal, confirm the dependent test fails;
   restore it, confirm it passes. A fence nobody has watched fail is not evidence — this is the
   load-bearing step, per `websocket-fake-class-ab-sweep`'s finding that a Class B flag had been
   green for months while holding nothing.
5. **Lower all three `sync-fence-baseline.json` entries in the same commit**, to whatever is actually
   left. The ratchet is net-new-only; a count that cannot reach 0 is reported with the reason rather
   than quietly parked above zero.
6. **Record the measured wall-clock before and after** for the three files. The claim to make is
   whatever gets measured, not a number borrowed from another suite.

## Impact

- **Tests only.** No `src/` change is in scope. Anything found in production code is recorded and
  routed to a new change rather than fixed here — the rule `voiceai-session-teardown-races` and
  `voiceai-pipeline-cancellation-accounting` both followed.
- Affected: `Tests/Verbara.Sdk.VoiceAi.Tests/Pipeline/` — three files, eight helpers, 18 harness call
  sites.
- `Verbara.Sdk.VoiceAi.Tests` currently runs 76 tests in ~15 s, the bulk of it these barriers; the
  four pipeline classes are already serialised into one xUnit collection by `ADR-0054`, so the saving
  is not recovered by parallelism and shows up directly.
- `sync-fence-baseline.json` moves down only.

## Architectural Risk

Low, and the risk is not regression. Both ordering primitives exist, are proven, and live in this
assembly; the substrate is a real `AudioSocketServer`/`AudioSocketClient` pair over an IPv4 loopback
literal, unchanged by this work. The real risk is the one `ADR-0045` was written about: a sweep that
removes 13 delays and replaces them with signals nobody has watched fail would leave the suite
looking deterministic while ordering nothing. Task 4 is the change, not a formality. The second risk
is over-reach — deleting the multi-second hang bounds along with the barriers, which would remove the
only thing standing between a deadlock and a suite that hangs forever.
