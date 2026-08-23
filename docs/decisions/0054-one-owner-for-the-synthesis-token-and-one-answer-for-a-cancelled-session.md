# ADR-0054: One owner for the synthesis token, and one answer for a cancelled session

- **Status:** Accepted
- **Date:** 2026-08-22
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0053 (a session's ending is classified by who ended it — this ADR applies the same
  rule to the *other* `ISessionHandler`, and adopts its intent-versus-mechanism shape for a second
  disposable), ADR-0052 (cancellation throws at the iteration boundary; F3 governs how the
  regression tests hand tokens around), ADR-0045 (why neither regression test is allowed a delay)

## Context

ADR-0053's sweep found two defects in `VoiceAiPipeline` and deliberately left them alone, so that one
change would not carry four failure modes. This is that change.

**`_ttsCts` had four users and two owners.** `PipelineLoop` created the source before a synthesis and
disposed it in the `finally`; `DisposeAsync` disposed the same field again; `AudioMonitorLoop` — the
barge-in path — snapshotted the field, null-checked it, and cancelled it. `CancellationTokenSource`
throws `ObjectDisposedException` from `Cancel`/`CancelAsync` after `Dispose`, *even when the source
was already cancelled*, so a barge-in landing on a released source threw out of `AudioMonitorLoop`,
through `Task.WhenAll`, into the bare `catch` that wraps both loops. A successful barge-in — a
feature working exactly as designed — was booked as a failed session and rethrown at the caller.

Two orderings reach that throw, and they are not equally reproducible:

| Ordering | Window | Reproducible by construction? |
|---|---|---|
| barge-in racing `PipelineLoop`'s `finally` | between the field read and the `Cancel` call — no `await`, no state change, a few instructions | **No.** The `finally` nulls the field *before* disposing, so any seam a test can reach either side of that pair sees a coherent state. Closing the window is the only way to address it |
| barge-in after `DisposeAsync` | unbounded — `DisposeAsync` disposed without nulling, so the field stayed published and disposed indefinitely | **Yes.** Deterministic, no timing involved |

The second is the one the regression test pins, and it fails on `VoiceAiPipeline.cs:144` with
`ObjectDisposedException: The CancellationTokenSource has been disposed`. The first is closed by
construction rather than by test, and the reason is written down here rather than left as a gap
someone re-discovers.

**The two `ISessionHandler` implementations disagreed about what a cancelled session is.**
`VoiceAiPipeline` counted `voiceai.sessions.failed`, set the Activity to `Error`, and rethrew.
`OpenAiRealtimeBridge`, after ADR-0053, counted `openai_realtime.sessions.completed` and swallowed.
One interface, one shutdown, two numbers.

Neither defect was covered. The pipeline suite's cancellation test swallows whatever the session task
threw, so it was green through both behaviours and was never evidence of either.

## Decision

**R1 — a requested cancellation is a completion, for both implementations.** `HandleSessionAsync`
gains `catch (OperationCanceledException) when (ct.IsCancellationRequested)` ahead of its existing
handler, and swallows. This is ADR-0053's rule applied to the second implementation, not a new rule:
the caller asked, so the caller is not told about it twice.

**R2 — `_ttsCts` has exactly one owner: `PipelineLoop`.** It is the only member that creates or
disposes the source. Everyone else may only express intent, through a private `CancelSynthesis()`.

**R3 — the field is guarded, and nulled before release.** A `Lock` covers every read, write and
cancel. `PipelineLoop`'s `finally` nulls the field *under* the gate and disposes *outside* it. That
ordering is what makes cancel-after-dispose unreachable rather than merely unlikely: the only
reference obtainable is one taken while the field was live, and disposal happens only after nobody
can obtain one at all. Whichever side wins the gate, the loser sees a coherent state — a live source
to cancel, or a null field to skip.

**R4 — `DisposeAsync` cancels; it does not release.** ADR-0053's intent-versus-mechanism split,
applied to a second disposable. Publishing a new source and observing an already-disposed pipeline
happen under the same gate, so a `DisposeAsync` arriving as a synthesis starts cannot cancel nothing
and leave that synthesis running past the disposal meant to stop it.

**R5 — the event subject is completed on disposal, not disposed.** Both loops outlive `DisposeAsync`
— `Task.WhenAll` has not observed them — and they publish as they unwind. `Subject<T>.OnCompleted`
drops every observer, after which `OnNext` is a silent no-op; `Subject<T>.Dispose` instead makes
every subsequent `OnNext` throw `ObjectDisposedException`. Disposing it was a *second* path to the
same class of defect this ADR removes, reached through the barge-in's own `Publish` call, and
reverting only this line is enough to fail the regression test.

## Consequences

- **`voiceai.sessions.failed` changes meaning.** A cancelled session — a host shutdown, a caller
  timeout — used to land there and now lands in `voiceai.sessions.completed`. Same family of
  telemetry break ADR-0053 documented for `openai_realtime.sessions.failed`, in the opposite
  direction: a dashboard that has been counting shutdowns as failures will drop.
- **`HandleSessionAsync` no longer rethrows on cancellation.** A caller with
  `catch (OperationCanceledException)` around it keeps compiling and stops firing. This matches what
  the bridge already does, which is the point.
- **A barge-in can no longer fault a session.** No consumer can have been relying on the old
  behaviour: it surfaced as a rethrown `ObjectDisposedException` from a method documenting no such
  thing.
- **`sessions.completed` still does not distinguish "completed" from "cancelled"** — as with the
  bridge, a separate instrument is the fix and is not in this change.
- **Barge-in cancellation callbacks now run on `AudioMonitorLoop`'s thread.** `CancelAsync` became
  `Cancel` because the call sits inside a lock. The monitor loop already awaited those callbacks to
  completion, so the latency it sees is unchanged; what changed is which thread runs them. The gate
  is reentrant, so a continuation resuming inline and reaching `PipelineLoop`'s `finally` on the same
  thread completes rather than deadlocking.
- **Every class in `Verbara.Sdk.VoiceAi.Tests` that runs a pipeline session now shares one xUnit
  collection.** The session counters are untagged process-wide statics, so two pipeline classes in
  parallel would add to the same instrument and a test asserting `failed == 0` would fail on someone
  else's session. This is the narrow form of what ADR-0053 did to the Realtime assembly with
  `DisableTestParallelization`; classes emitting nothing stay parallel. Measured cost for the whole
  assembly: **16 s serialised for the four classes, 76 tests**.
- **Two regression tests, both ordered by construction, each failing on its own half.** The
  synthesizer parks between chunks and says so; the turn detector — which the pipeline calls
  synchronously, once per frame, on the monitor loop's thread — says which frame it just decided on.
  Reverting only the `catch` fails the cancellation test; reverting only the ownership change fails
  the barge-in test; reverting only `_events.Dispose()`'s removal fails the barge-in test.

## Alternatives considered

**Give the barge-in an `internal` hook so the `finally` race is testable.** Rejected. The hook would
exist only for a window the fix closes, and would have to sit between a field read and the next
statement — a seam with no meaning in production code. Closing the window and writing down that it is
closed by construction is the honest version; a test that needs a sleep to hit it would reintroduce
inside this change the exact defect class ADR-0045 and ADR-0053 exist about.

**Keep `CancelAsync` and use a `SemaphoreSlim` instead of `Lock`.** It would let the barge-in await
the cancellation off-thread while still holding exclusion. Rejected: an async gate around three
instructions costs an allocation and a state machine on the audio path to buy back a thread-affinity
property nothing here depends on, and it is not reentrant, so the inline-continuation case above
becomes a real deadlock instead of a non-event.

**Never dispose `_ttsCts` at all.** The source owns no timer and no surviving registration once
`linked` is disposed, so leaking it is nearly free and removes the race by removing the disposal.
Rejected: "nearly free" is a claim about the current implementation of a framework type, and the
next reader has no way to tell a deliberate non-disposal from a forgotten one.

**Change the bridge to match the pipeline instead.** Both directions produce one number, and this one
loses on the merits: ADR-0053 argued the classification from *who ended the session*, and nobody but
the caller ends a cancelled one. It would also mean rewriting a decision recorded one day earlier
because the second implementation had not been read yet.
