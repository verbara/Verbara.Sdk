---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Consumers whose calls end the way real calls end — the caller hangs up, or the app cancels mid-session — and who currently get an exception for it
decision_ref: Sdk/ADR-0045
---

# Proposal: voiceai-session-teardown-races

## Why

Instrumenting the OpenAiRealtime fake server for `websocket-fake-protocol-contract` surfaced two
defects in `src/`. That change touched no production code by design, so both were recorded in
ADR-0045's Consequences and deliberately left. This change is where they get fixed.

Neither is hypothetical; both are visible in the current tree.

**1. `AudioSocketSession.ReadAudioAsync` throws when a hangup overtakes the consumer's first
`MoveNext`.** `DisposeAsync` cancels and then **disposes** the source:

```csharp
if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
await _cts.CancelAsync().ConfigureAwait(false);
_cts.Dispose();
```

`ReadAudioAsync` reads `_cts.Token` in its body:

```csharp
using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
```

`CancellationTokenSource.Token` throws `ObjectDisposedException` once the source is disposed, and
because `ReadAudioAsync` is an `async IAsyncEnumerable` iterator that line does not run at call time
— it runs on the first `MoveNextAsync`. The read loop itself calls `DisposeAsync` on a hangup frame,
so the racing pair is *the SDK's own hangup handling* against *the consumer starting to read*. The
caller hanging up first is not an edge case; it is how a large share of real calls end.

Two things make this worse than a lost race. `ReadAudioAsync` is the only public member of the type
with no `ObjectDisposedException.ThrowIf(_disposed == 1, this)` guard, so it is inconsistent with
`WriteAudioAsync` and its siblings — and the exception it does throw arrives from inside an
enumerator, at an arbitrary distance from any call the consumer can see.

**2. `OpenAiRealtimeBridge` faults instead of exiting cleanly when a cancel lands during setup.**
The `OperationCanceledException` handler guards only the loops:

```csharp
try { await Task.WhenAll(InputLoop(…), OutputLoop(…)); }
catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* expected */ }
```

But `await ws.ConnectAsync(uri, ct)`, `await wsWriteLock.WaitAsync(ct)` and the `session.update`
`await ws.SendAsync(…, ct)` all take the same `ct` and all sit **outside** that try. A cancel landing
in that window escapes as an unhandled `OperationCanceledException`. The consequences are not only
the exception:

- the inner `finally` never runs, so **no clean WebSocket close is sent** — the socket is torn down
  by `using var ws` instead;
- `RealtimeMetrics.SessionsCompleted` is never incremented and `RealtimeLog.SessionEnded` never
  fires, so the session is invisible in the telemetry that is supposed to account for it;
- the caller sees a faulted session for what is a normal, requested shutdown.

The window is small, which is why nothing caught it — and it is the exact window a consumer hits
when it cancels because the call already ended.

## What Changes

**`AudioSocketSession`**

- Capture the session token once, at construction, so `ReadAudioAsync` no longer reads `_cts.Token`
  after dispose may have run.
- Decide and state what a hangup-before-first-read *means*, rather than only stopping the throw. A
  hangup is normal termination of the audio stream, so completing the sequence is the behaviour that
  matches the domain; throwing `ObjectDisposedException` for an already-ended call makes every
  consumer write a catch. Whichever is chosen, it becomes a stated requirement rather than an
  emergent one.
- Make the type's disposed-state behaviour consistent across its public surface.

**`OpenAiRealtimeBridge`**

- Bring connect, lock acquisition and the `session.update` send under the same cancellation handling
  as the loops, so a cancel anywhere in the session's lifetime ends it the same way.
- Ensure the clean-close path and the session's terminal telemetry run on that path too.

**Both**

- A deterministic regression test per defect. Neither may be a timing test: the hangup/read race
  needs the hangup ordered *before* the first `MoveNext` by construction, and the bridge needs the
  cancel ordered inside the setup window by construction. A test that reproduces either by sleeping
  is a test that will stop reproducing it.
- ADR-0052 F3 applies to both: the cancelled token goes to the subject, and the enumeration takes
  `CancellationToken.None`. The `CancellationProvenanceScanner` guard fails the build otherwise.

## Impact

- **Affected `src/`:** `Verbara.Sdk.VoiceAi.AudioSocket` (`AudioSocketSession`),
  `Verbara.Sdk.VoiceAi.OpenAiRealtime` (`OpenAiRealtimeBridge`).
- **Behavioural change, user-facing.** If `ReadAudioAsync` completes rather than throws on a hangup,
  a consumer today catching `ObjectDisposedException` around it stops seeing that exception. That is
  the point, but it belongs in the CHANGELOG under `Fixed` with the before/after stated plainly, and
  it needs a version bump — unlike `websocket-fake-protocol-contract`, which touched no `src/` and
  bumped nothing.
- **Telemetry change:** sessions cancelled during setup begin to be counted and logged as ended.
  Anything downstream reading `SessionsCompleted` will see numbers that were previously missing.
- No API surface is added or removed.

## Architectural Risk

Medium, and concentrated in the AudioSocket decision rather than in the code. Both fixes are small
and local; the risk is choosing the wrong termination semantics for a hangup and then having to
change it again once consumers depend on it. That decision should be made explicitly here and
recorded, not settled by whichever fix is easiest to write.

The second risk is the tests. A race reproduced by sleeping is the defect class ADR-0045 exists
about, and reintroducing it inside the change that fixes two races would be its own kind of failure.
Both regression tests must order the race by construction.
