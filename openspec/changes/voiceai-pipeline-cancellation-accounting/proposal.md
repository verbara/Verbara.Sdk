---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Anyone reading VoiceAi session telemetry, and any caller who can barge in while the assistant is speaking
decision_ref: Sdk/ADR-0053
---

# Proposal: voiceai-pipeline-cancellation-accounting

## Why

`voiceai-session-teardown-races` swept `src/` for the two shapes behind ADR-0053 — session state read
at iteration time, and a `ct`-taking `await` outside the handler that guards it. The sweep found no
further instances of either. It did find two defects of the *same class* in a third file, and left
them alone deliberately: folding them in would have made one PR carry four failure modes with four
separate verification stories.

They are recorded here so they are not lost.

**1. `_ttsCts` has four users and two owners.** In `VoiceAiPipeline`:

| Line | Who | What it does |
|---|---|---|
| `:276` | `PipelineLoop` | assigns a fresh `CancellationTokenSource` before synthesis |
| `:279` | `PipelineLoop` | reads `.Token` into a linked source for the whole synthesis |
| `:142-144` | `AudioMonitorLoop` | snapshots the field, null-checks it, then `CancelAsync()`s it — this is barge-in |
| `:337-339` | `PipelineLoop`'s `finally` | snapshots, nulls the field, disposes |
| `:366` | `DisposeAsync` | disposes it again |

The null-check at `:143` and the `CancelAsync` at `:144` are separated by an `await`, and nothing
orders them against the dispose at `:339`. A barge-in landing as a synthesis completes cancels a
source that has already been disposed, which throws out of `AudioMonitorLoop`, through
`Task.WhenAll`, into the bare `catch` at `:90`. The session is counted as failed and the exception is
rethrown to the caller — on a *successful* barge-in, which is a feature working, not a fault.

`DisposeAsync` disposing the same field is a second, independent path to the same throw.

**2. The two `ISessionHandler` implementations disagree about what a cancelled session is.**
`VoiceAiPipeline.HandleSessionAsync`'s handler at `:90` is a bare `catch` — it catches
`OperationCanceledException` too, increments `VoiceAiMetrics.SessionsFailed`, sets the Activity to
`Error` and rethrows. `OpenAiRealtimeBridge`, after ADR-0053, counts the same event as a completion.
Two handlers behind one interface, one shutdown, two different numbers. Whichever is right, they
cannot both be.

Neither defect is hypothetical and neither is covered: the pipeline suite's cancellation test
(`VoiceAiPipelineTests.cs:202`) swallows everything in a bare `catch`, so it is green either way and
is not evidence of anything.

## What changes

- `_ttsCts`'s lifetime gets one owner, so cancel-versus-dispose cannot race. The likely shape is the
  same one ADR-0053 used for `AudioSocketSession`: separate the *intent* to cancel from the
  *mechanism* that releases, and never expose a raw disposable through a mutable field two loops
  read.
- `HandleSessionAsync`'s handler stops treating a requested cancellation as a fault, matching the
  bridge — or the bridge is changed to match it, but the two agree afterwards and the choice is
  recorded rather than left to whichever file the reader opens.
- Both get regression tests ordered by construction. A barge-in racing a synthesis completion is a
  genuine race and will need a seam; finding one that is not a sleep is the main risk here.

## Impact

- `voiceai.sessions.failed` changes meaning if the cancellation decision goes the bridge's way.
  That is a telemetry break in the same family ADR-0053 already documented for
  `openai_realtime.sessions.failed`, and belongs in the same CHANGELOG paragraph style.
- A barge-in that currently faults the session stops doing so. No consumer can be relying on that:
  it surfaces as a rethrown `ObjectDisposedException` from a method that documents no such thing.

## Architectural Risk

Medium. The fix is small; the *reproduction* is the hard part, and a race test built on a delay would
reintroduce inside this change the exact defect class ADR-0045 and ADR-0053 exist about. If no
construction-ordered seam is available without an `internal` hook, that trade-off gets stated in the
change rather than resolved quietly with a sleep.
