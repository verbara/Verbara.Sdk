# ADR-0057: A write to a departed session is an ending, not a failure

- **Status:** Accepted
- **Date:** 2026-09-21
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0053 (a streaming session's ending is classified by who ended it — this ADR carries
  that decision to the write path, where its R2 does *not* hold as written), ADR-0054 (the same
  sweep's two `VoiceAiPipeline` defects; this is the third site of that class and the first on the
  write path), ADR-0050 (E9 — counting a cut-short synthesis as completed is standing debt, and this
  decision inherits it rather than settling it)

## Context

`VoiceAiPipeline.PipelineLoop` wrote every synthesized chunk to the audio session from inside the same
`try` whose last clause books a synthesis failure:

```csharp
await foreach (var audioChunk in _tts.SynthesizeAsync(response, _options.OutputFormat, linked.Token))
{
    // ttfa recorded on the first chunk
    await session.WriteAudioAsync(audioChunk, linked.Token);
}
```

That `try` sorts its endings into three clauses:

| clause | filter | what it does |
|---|---|---|
| the caller | `when (ct.IsCancellationRequested)` | rethrows; the session counts completed (ADR-0054 R1) |
| the synthesis's own source | `when (ttsCts.IsCancellationRequested)` | `tts.syntheses.completed` +1, `SynthesisEndedEvent` |
| everything else | `catch (Exception ex)` | `tts.syntheses.failed` +1, activity `Error`, a Warning line, `PipelineErrorEvent` with source `Tts` |

The **write** reached the third clause with nothing having failed. `AudioSocketSession.WriteAudioAsync`
opens with a guard on `_disposed`, a field documented *0 = live, 1 = torn down — by any cause*; the
read loop's `finally` runs the same private terminate for every ending the session has — a hangup
frame, an error frame, a socket EOF, a transport error — and that terminate sets the flag as its first
statement, before it fires `OnHangup`. **The write-side guard is not the read-side guard.**
`ReadAudioAsync` guards on `_consumerDisposed`, which only the owner's own `DisposeAsync` sets; the
write guards on *any* ending.

So the commonest ending a call has — the caller hangs up while the assistant is still speaking — landed
on the next chunk as an `ObjectDisposedException`. It is not an `OperationCanceledException`, and a
far-end hangup cancels neither the caller's token nor `ttsCts`, so neither of the first two clauses
could see it. That turn was reported as `tts.syntheses.failed` 1, `tts.syntheses.completed` 0, one
`PipelineErrorEvent` with source `Tts`, a Warning line and the synthesis activity set to `Error`. A
dashboard of TTS failures therefore rose with the hangup rate rather than with the failure rate, and
the one signal an operator has for *the caller heard nothing* fired on the way every call normally
ends.

The session layer was already right, which is why nothing caught this: the exception never left the
synthesis catch chain, so `HandleSessionAsync` counted the session completed and rethrew nothing. The
defect was exactly one layer wide.

**The answer already existed at the other door.** `OpenAiRealtimeBridge` wraps its own
`session.WriteAudioAsync` in `catch (ObjectDisposedException) { return; }`, under a comment calling the
caller hanging up mid-speech "the commonest ending of all" and citing ADR-0053. But ADR-0053's Decision
scopes that sentence to the bridge, and its living-spec scenario is about playback stopping and the
*session* ending normally; synthesis counters were never in its reach. Every hangup in the pipeline
suites landed after the turn was already over, by another cause.

## Decision

**A write that finds the audio session already gone is that session's ending arriving on the write
path — ADR-0053 R3 seen from the other side — and not a fault of the synthesizer.**

**R1 — ADR-0053 R2 does not carry across the two doors.** On the read path,
`ObjectDisposedException` means "the owner disposed, then someone read", because the guard there reads
the owner's intent. On the write path the same exception type answers a different question, because the
guard there reads the teardown flag that *every* ending sets. One type, two doors, two meanings — and
the discriminator is which field the guard reads, not the exception.

**R2 — the ending is accounted exactly as a barge-in is.** `tts.syntheses.completed` +1, one
`SynthesisEndedEvent`, no `PipelineErrorEvent`, `tts.syntheses.failed` untouched, the activity never set
to `Error`, the turn left out of the conversation history, and one Debug line naming the channel. Both
endings are someone outside the pipeline ending this playback; they differ only in who. Tying them
together means one future instrument separating "the caller heard the answer" from "the caller heard
part of it" moves both at once. What that counter means today is ADR-0050 E9's standing debt, inherited
here and neither fixed nor required.

**R3 — the catch is scoped to the write call alone, never to the synthesis.** An
`ObjectDisposedException` raised by the *synthesizer* — a provider client used after its own disposal —
still reaches the failure clause it belongs to.

**R4 — the exception's type is the discriminator only because both of its origins are the same
teardown.** For this sealed session type, the guard is armed by the private terminate and the transport
a flush would touch is what the same terminate closes last. A genuine transport failure on a session
that is **still connected** surfaces as `IOException`/`SocketException`, is not caught, and stays a
synthesis failure. That is the line between *the far end left* and *the transport broke while it was
still there*.

**R5 — playback stops rather than draining.** Leaving the enumeration lets `await foreach`'s own
`DisposeAsync` release the provider sequence instead of synthesizing an answer nobody can hear.

**R6 — the ending is visible below Warning and nowhere else.** A new source-generated
`[LoggerMessage]` at Debug, alongside `BargInDetected`. Internal, no reflection, no public API change.

## Consequences

- **A telemetry change in both directions, in the same family as ADR-0053 and ADR-0054.** A turn the
  caller hung up on moves from `tts.syntheses.failed` to `tts.syntheses.completed`, publishes
  `SynthesisEndedEvent` instead of `PipelineErrorEvent`, and stops logging at Warning. A dashboard whose
  TTS failure rate tracked the hangup rate will **drop**, without the failure rate having changed.
  Unchanged: `voiceai.sessions.*`, which were already right because the exception never reached the
  session; and every provider failure, including a synthesizer's own cancellation.
- **Where this rule stops is load-bearing, and the tests say so.** Widening the write's catch from
  `ObjectDisposedException` to `Exception` was measured against the whole suite, and it is caught by
  exactly two assertions — both of them the ones the suite labels *"today's accounting, not a
  requirement"* under ADR-0050 E9. Every requirement-level assertion (no fault, no `PipelineErrorEvent`,
  `tts.syntheses.failed` unmoved, nothing at Warning, the activity not `Error`, `voiceai.sessions.*`) is
  **satisfied** by the widened catch. So the boundary between "the far end left" and "something else
  went wrong at the write" is currently held by accounting this decision does not mandate. That is a
  property of where R2 draws its line, not a defect: deleting those two assertions as "not required"
  would let the widening through, and whoever settles E9's debt inherits the job of re-anchoring them.
- **The narrow scope is what makes the rule true, and it is pinned from the other side.** Moving the
  catch from the write to the whole synthesis `try` leaves the hangup regression test **green** and
  fails the control that keeps a synthesizer's own `ObjectDisposedException` a synthesis failure, on all
  seven of its classification assertions. Scope is not observable from the hangup side alone; R3 needs
  its own control, and has one.
- **The choice of accounting branch is carried by reading, not by a test.** Routing the ending into the
  pre-existing normal-completion tail instead of the barge-in accounting survives the whole suite. The
  two tails differ in three things and none is observable after a hangup: the `tts.syntheses.silent`
  guard cannot fire (the first chunk reached a live session), the Debug line is absent, and a
  `ConversationTurn` is appended to a history list that is read only by the *next* turn, which will never
  come. R2 is therefore a decision about meaning, and is written down here because no test can defend it.
- **Nothing here is refused at build time.** Unlike ADR-0058's neighbouring rule, no analyzer stands
  behind any of this: `catch (Exception)` at the write does not trip CA1031 at this `AnalysisLevel`, and
  an uncalled internal `[LoggerMessage]` partial does not trip CA1811/IDE0051 — every mutation tried
  built with 0 warnings and 0 errors. The tests are the whole guard, which is why the paragraphs above
  name which test holds which clause.
- **R6 had to be bound deliberately.** Asserting that nothing reaches Warning does not assert that the
  Debug line is written; deleting the log call left the suite green until an assertion for the entry was
  added. A requirement of the form "and it is visible below Warning" needs a positive assertion, not the
  absence of a negative one.
- **Residual, recorded rather than closed: the flush race.** A hangup landing between the guard and the
  flush can surface as `IOException` instead of `ObjectDisposedException`, and still counts as a
  synthesis failure. Closing it would mean asking the session whether it is still connected *as well as*
  reading the exception's type, and no test available today can tell that conjunct from its absence. The
  window is the width of one buffered write, so the misclassification is rare rather than impossible.
- Downstream (Pro, Platform): nothing to recompile. Anything counting `tts.syntheses.*`, or treating a
  `PipelineErrorEvent` with source `Tts` as "a provider let us down", sees the reclassification.

## Alternatives considered

**Add the conjunct now — read the exception's type *and* ask the session whether it is still
connected.** It is the honest fix for the residual above, and it was rejected for today because nothing
can hold it: no seam available to a test produces a write that fails while the session is still
connected, so the conjunct and its absence are indistinguishable, and `IsConnected` is itself a racing
read on the same teardown. Recorded as the residual instead of shipped as an unguarded refinement.

**Swallow the write inside `AudioSocketSession.WriteAudioAsync`.** It would fix every caller at once and
erase the distinction for every caller at once: a genuine use-after-dispose in a consumer would go
silent. That is the collapse ADR-0053 already rejected on the read side, for the same reason — the two
cases differ in *who* ended the session, which is precisely what a consumer can act on.

**Put the catch on the synthesis `try`, beside the two cancellation filters.** It reads better: three
filters and a failure clause, all in one place. Rejected, and then measured — it absorbs a synthesizer's
own `ObjectDisposedException` into the barge-in accounting, and the control for that fails. The tidier
shape is the one that cannot state R3.

**Let the ending fall through to the normal-completion tail.** The smallest possible diff: a `break` and
nothing else. Rejected on meaning rather than on evidence, since the suite cannot separate the two — the
tail exists to record a turn that was *delivered*, and appending a conversation turn nobody heard is a
claim, even an unobservable one, that this ADR declines to make.
