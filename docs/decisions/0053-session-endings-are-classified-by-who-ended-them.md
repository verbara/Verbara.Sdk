# ADR-0053: A streaming session's ending is classified by who ended it, not by where it landed

- **Status:** Accepted
- **Date:** 2026-08-21
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0014 (VoiceAi providers are hand-rolled streaming clients, so each surface owns its
  own teardown), ADR-0045 (the two defects below were found while converting the Realtime fake
  server and were deliberately left unfixed there, because that change touched no `src/`), ADR-0052
  (cancellation throws at the iteration boundary — R1 below is that rule applied to this type, and
  F3 governs how the regression tests hand tokens around)

## Context

Two independent defects, one shape. Both are invisible on a quiet machine, and both were found by
instrumenting rather than by reading.

**`AudioSocketSession.ReadAudioAsync` was an `async` iterator that read session state.** Its body
opened a linked token source over `_cts.Token`. An `async IAsyncEnumerable` body does not run at call
time — it runs on the first `MoveNextAsync`. A hangup frame arriving in between completes the audio
channel and then disposes `_cts`, so the consumer's first read evaluated `.Token` on a disposed
source and got:

```
System.ObjectDisposedException : The CancellationTokenSource has been disposed.
   at System.Threading.CancellationTokenSource.get_Token()
   at AudioSocketSession.ReadAudioAsync(CancellationToken ct)+MoveNext()
```

The `ObjectName` is empty, so the exception never named the session, and up to 256 already-received
frames were discarded with it. The window is as wide as the gap between accepting a session and
enumerating it, which for a consumer that queues sessions before attaching a transcriber is seconds,
not microseconds.

Measuring the other four orderings was what turned one defect into three:

| Ordering | Before | Verdict |
|---|---|---|
| (a) hangup completes before the first read | `ObjectDisposedException`, empty `ObjectName`, buffered frames lost | the reported defect |
| (b) far-end hangup lands mid-enumeration | ends normally, **200/200** | correct already — `TryComplete()` strictly precedes `CancelAsync()`, and a linked child source survives its parent's disposal |
| (b′) the **owner** disposes mid-enumeration | `OperationCanceledException`, **200/200** | new: disposal never fired the hangup, so cancel preceded complete. A routine host shutdown was booked as a failed session |
| (c) read issued after the owner disposed | no throw at the call; `ObjectDisposedException` later, from a `MoveNext` frame | wrong *where*, right *what* |
| (d) socket EOF with no hangup frame | ends normally, but the `TcpClient` and the `CancellationTokenSource` are never released | new: `IsConnected` kept returning `true`, and the owner's registry had already dropped the session, so nothing could ever reclaim it |

(b) and (b′) look identical from the outside and had opposite outcomes. That is the finding: the
observable result depended on *which* teardown path ran, and a consumer cannot see that.

**`OpenAiRealtimeBridge` had a setup window outside its own handler.** `ConnectAsync`, the write-lock
acquisition and the `session.update` send took `ct` while sitting above the `try` whose
`catch (OperationCanceledException) when (ct.IsCancellationRequested)` existed for exactly them. A
cancel landing there escaped as a `TaskCanceledException`, and — because the `finally` owning the
clean close, `SessionsCompleted`, `SessionDurationMs` and `SessionEnded` was attached to the *inner*
try — the session vanished from telemetry entirely, having already incremented `SessionsStarted`.
Nine existing bridge tests covered none of it; they had been restructured to route *around* the
window, with the race recorded in their `<remarks>` as a known production defect.

## Decision

**A session's ending is classified by who ended it.** Three rules, evaluated at two different points,
and the precedence between them is structural rather than a written convention:

**R1 — the consumer's own cancellation faults.** A cancelled token passed to the read raises
`OperationCanceledException` at the next iteration boundary (ADR-0052). Checked first at every
boundary, so a long drain cannot delay it.

**R2 — `ObjectDisposedException` means "the owner disposed, then someone read", and nothing else.**
It is thrown **from the call**, not from a later `MoveNext`, and it names the type. This requires the
read method to stop being an iterator: a non-iterator wrapper carries the guard and returns a private
iterator core.

**R3 — every other ending ends the sequence**, after delivering frames already received. Far-end
hangup, error frame, application-initiated hangup, EOF, transport error, and an owner disposal that
lands after enumeration legitimately began all terminate identically.

Making R2 possible means separating the owner's *intent* from the teardown *mechanism*: disposal sets
a flag and delegates to a private terminate, while every session-initiated ending calls terminate
directly. The terminate completes the audio channel itself, which is what makes R3 structural rather
than dependent on the read loop unwinding first, and it releases the transport, which is what closes
(d). Ordering (b)'s existing correctness is preserved by construction rather than by accident.

**And the read path reads no session lifetime state at iteration time.** The channel's completion is
what says the session ended; the consumer's token is the only token the enumeration observes.

For the bridge: **one `try`, one terminal block.** The setup window moves *inside* the existing
region rather than getting a handler of its own. The cancellation filter stays written as
`when (ct.IsCancellationRequested)` — a cancelled `ConnectAsync` surfaces a `TaskCanceledException`
carrying a *different* token, so an identity check against `ex.CancellationToken` would silently not
match. The far end departing mid-playback is handled where the write happens, because it is the same
misclassification arriving through the other door.

## Consequences

- A consumer's `catch (ObjectDisposedException)` around the read becomes unnecessary. It stays
  correct — R2 still throws for the case it was written for — but it no longer fires on ordinary
  hangups.
- **A routine host shutdown stops being counted as a failed session.** For a consumer that fans a
  session's transcript streams out with `Task.WhenAll`, this also stops one stream's ending from
  killing its sibling.
- `openai_realtime.sessions.failed` **will start reading non-zero** for genuine connect failures
  (refused, TLS, HTTP 4xx). Those previously escaped with no accounting at all, so a dashboard that
  has always read zero will move without the failure rate having changed.
- `sessions.completed` still does not distinguish "completed" from "cancelled" — it did not before
  either, on the loops path. Anyone reading it as "sessions that produced value" is reading it wrong;
  a separate instrument is the fix, and is not in this change.
- The Realtime test assembly disables test parallelisation, because its metric assertions read
  process-wide instruments that carry no tags. Measured cost: **0.55–0.58 s serialised against
  0.34–0.35 s parallel**, both under the 0.8 s ADR-0045 recorded for this suite.
- Six regression tests, all ordered by construction rather than by delay. The AudioSocket ordering
  edge is the FIN: the transport dispose is the *last* statement of the teardown, so draining the
  peer's read to EOF is a happens-after edge on the whole teardown and cannot invert. The bridge's is
  a listener that accepts the TCP connection and never writes the `101`, so the handshake *cannot*
  complete and only the token can end the connect. With `src/` reverted, all six fail; five of the
  six fail on the specific consequence they name.
- Two adjacent defects in `VoiceAiPipeline` were found by the same sweep and deliberately left out:
  a disposed-from-two-places cancellation source that a barge-in can fault the session on, and a bare
  `catch` that counts every cancellation as a failure where the bridge counts it as a completion.
  They are the same class in a different file and get their own change; folding them in would make
  one PR carry four failure modes.

## Alternatives considered

**Capture `_cts.Token` at construction.** This was the original proposal, and probing it is what
killed it: the captured token is *already cancelled* by the time the deferred body runs, so
`ReadAllAsync` throws `OperationCanceledException` carrying a foreign token instead of
`ObjectDisposedException`, still yields zero frames, and still strands everything buffered. The
headline symptom survives while the regression test goes green — the worst available outcome.

**Collapse (a) and (c) into one behaviour.** Tempting, because both end at "the session is gone", and
either uniform answer is defensible in isolation. But (a) is the far end hanging up — the way calls
normally end — and (c) is a use-after-dispose bug in the consumer. Giving them one answer means
either every hangup needs a `catch`, or a real bug goes silent. They differ in who ended the session,
which is precisely what the consumer can act on.

**Add a `catch` around `ConnectAsync` that also records the terminal telemetry.** It fixes the
symptom and leaves two sites per instrument. The next person to re-nest the loops gets
double-counting, silently. Widening the existing region keeps exactly one site each, so the property
holds under rearrangement instead of under review.

**Make the clean protocol close actually fire on the cancelled path.** It cannot, and the spec was
reworded rather than the code: cancelling any WebSocket operation *aborts* the socket — platform
contract, not a defect here — so the state at that point is `Aborted` or `Closed` and there is
nothing left to close politely. This is true of the loops path today as well, and always has been.
Making it fire would mean the output loop stopping its pending receive rather than cancelling it,
which is a redesign with a fresh concurrent-send hazard. Recorded, not done.
