---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Anyone who has to trust a green VoiceAi suite — and every developer paying 25 s of pure timeout on each local run of the OpenAiRealtime tests
decision_ref: Sdk/ADR-0045
---

# Proposal: websocket-fake-protocol-contract

## Why

The SDK has **nine** in-process WebSocket fake servers. The `wiremock-http-provider-substrate`
change fixed **five defects across four of them** (Cartesia STT, Deepgram TTS, LMNT TTS) after CI
caught one instance and a sweep for the *class* found the rest. Those fixes named three defect
classes:

- **Class A — answers on a timer, not on protocol.** A fixed `Task.Delay` stands in for "the client
  has finished its request." Whether the test's assertion holds is then a race the fake wins on this
  machine and may lose on another.
- **Class B — a hold-open flag that does not hold.** Implemented as `await receiveTask`, which exits
  the instant the client half-closes, tearing the session down exactly when a cancellation test
  needs it alive.
- **Class C — hands tests the live mutable collection** the background receive loop is still
  appending to.

`RealtimeFakeServer` (`Tests/Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests/Internal/RealtimeFakeServer.cs`)
was never touched by that change and carries **all three**, plus a fourth problem that is unique to
it. Everything below was read out of the current tree or measured on it, not inferred.

### The suite spends 25 seconds doing nothing

Measured 2026-08-10, `-c Release`, this machine, per-test durations from
`--logger "console;verbosity=detailed"`:

| Test | Duration |
|------|----------|
| `HandleSessionAsync_SendsSessionUpdate_OnConnect` | **5 s** |
| `HandleSessionAsync_PublishesResponseStartedAndEndedEvents` | **5 s** |
| `HandleSessionAsync_PublishesTranscriptEvents` | **5 s** |
| `HandleSessionAsync_PublishesSpeechEvents` | **5 s** |
| `HandleSessionAsync_PublishesErrorEvent_OnOpenAiError` | **5 s** |
| `HandleSessionAsync_CancellationToken_TerminatesBothLoops` | 245 ms |
| whole project, including build | 44.3 s |

Five tests take **exactly** the 5 seconds of their own
`CancellationTokenSource(TimeSpan.FromSeconds(5))`, because that timeout is the *only* thing that
ends them. `HandleSessionAsync` is `Task.WhenAll(InputLoop, OutputLoop)`
(`src/Verbara.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeBridge.cs:89`). `InputLoop` iterates
`session.ReadAudioAsync(ct)` and ends only when the audio client hangs up or `ct` fires — and each
of these tests hangs up in *Cleanup*, **after** the `await`. So the assertion runs 5 seconds after
the data it asserts on arrived. Roughly 25 s of every unit-lane run, local and CI, is spent waiting
for a token to expire.

### Class A — `Task.Delay` where a protocol sentinel exists

`RealtimeFakeServer.cs:98` — `await Task.Delay(30)`, commented *"Small delay to let client process
session.created and send session.update"*. That is the whole synchronisation:
`HandleSessionAsync_SendsSessionUpdate_OnConnect` asserts the fake captured exactly one
`session.update`, and nothing makes the fake wait for one. The fake then sends its configured events
5 ms apart (`:106`), waits 100 ms (`:110`) and closes.

The sentinel is available and unused: `session.update` is the client's **first** frame, sent
unconditionally right after `ConnectAsync` (`OpenAiRealtimeBridge.cs:80-84`) — the same shape as the
`Flush` frame the Deepgram TTS fake now waits on.

### Class B — no hold-open capability at all

The fake closes ~130 ms after accept, unconditionally (30 ms + 5 ms/event + 100 ms).
`HandleSessionAsync_CancellationToken_TerminatesBothLoops` cancels at **200 ms** — 70 ms after the
socket is already gone. `OutputLoop` returns on the Close frame (`OpenAiRealtimeBridge.cs:196`), so
the loop named in the test's title terminates on the server's close, **not** on cancellation. The
`InputLoop` half is genuine (`ReadAudioAsync` is still blocked; only `ct` releases it, which the
measured 245 ms is consistent with) — but the output half of "TerminatesBothLoops" is not exercised
today, and cannot be, because the fake offers no way to keep the socket alive.

### Class C — the live collection

`RealtimeFakeServer.cs:18` exposes `public List<string> ReceivedMessages { get; } = []`, appended by
the background receive loop at `:86` while four assertions read it directly
(`OpenAiRealtimeBridgeTests.cs:31`, `FunctionCallTests.cs:139,141,170`). No lock, no snapshot — the
same torn-read exposure fixed in the Cartesia, Deepgram and LMNT fakes.

### The fourth problem: it is the last fake on the substrate the repo already replaced

`WebSocketTestServer`'s own XML doc states it *"replaces `HttpListener`-based fakes whose
`AcceptWebSocketAsync` + `ws.Abort()` dispose path hangs on Linux test plumbing."* ADR-0041 records
the WebSocket streaming surfaces as running on `WebSocketTestServer`. **This one does not** — it is
`HttpListener` + `AcceptWebSocketAsync`, and the test project does not even reference
`Verbara.Sdk.TestInfrastructure`.

Consequences that come with that substrate and disappear with it:

- **The TOCTOU port probe** (`:23-50`): bind a `TcpListener` on port 0, read the port, `Stop()` it,
  hand the now-free port to `HttpListener`, retry ten times on collision — 25 lines with a
  `goto success`. ADR-0044 documents that probe as *unavoidable for `HttpListener`*, which cannot
  adopt an existing socket. `WebSocketTestServer` binds `TcpListener(IPAddress.Loopback, 0)`
  directly and needs none of it.
- **A concurrent-receive violation on the close path** (`:112-123`): the fake calls `CloseAsync`
  while its own background receive loop has an outstanding `ReceiveAsync` on the same socket. Only
  one receive may be in flight, so the call raises into the surrounding `catch { }`. Whether the
  peer still observes the close frame depends on ordering inside `ManagedWebSocket` that nothing
  here pins. Confirming or refuting that is task §1.4 — the migration removes the question either
  way, because on `WebSocketTestServer` the per-connection handler owns the socket.

### Why now

Eight of nine surfaces are on the shared substrate, four have been fenced, and the defect taxonomy
is written down. Leaving the ninth on the old substrate with all three defects is the residue of a
sweep that stopped at the project boundary. The fakes are also where the next reader learns the
house style — an unfenced one teaches the wrong thing.

## What Changes

- **`RealtimeFakeServer` moves onto `WebSocketTestServer`.** Adds the
  `Verbara.Sdk.TestInfrastructure` `ProjectReference` to
  `Tests/Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests`. Deletes the TOCTOU port probe, the `goto
  success` control flow, the ten-attempt retry loop and the `HttpListener` close path. `Port`,
  `Start()`, `EventsToSend` and `ReceivedMessages` keep their names so this step lands with **no
  change to either test file**.
- **Protocol sentinels replace the three `Task.Delay` calls.** A
  `TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)` released by the
  client's `session.update` frame gates event delivery, with a bounded timeout so a client that
  never sends cannot stall the suite — the `WaitForRequestOrTimeoutAsync` idiom already in the
  Deepgram and LMNT fakes.
- **A `HoldOpenUntilDisposed` flag implemented as `Task.Delay(Timeout.Infinite, ct)`** — explicitly
  *not* `await receiveTask`, the Class B trap this repo has already paid for once.
- **`ReceivedMessages` becomes a snapshot** (`IReadOnlyList<string>`, `lock` + `ToArray()`), matching
  the three fixed fakes. `EventsToSend` stays a plain `List<string>`: it is test→server
  configuration written before `Start()`, not a capture the receive loop mutates.
- **The nine bridge/function-call tests end on the signal they assert**, not on a 5-second token
  expiry or a `Task.Delay(300)`. The cancellation test sets `HoldOpenUntilDisposed` so that
  `OutputLoop` is genuinely blocked on a live socket when the token fires — the coverage the test
  has always claimed.
- **`sync-fence-baseline.json` ratchets down.** The three OpenAiRealtime files carry **8**
  grandfathered barriers today (2 + 3 + 3). The change lowers each entry to what actually survives;
  the net-new-only ratchet (verbara-meta/ADR-0004) then blocks reintroduction. No count is ever
  raised.
- **A Governance detector for Class C**, the mechanically decidable class: a `*FakeServer` type must
  not expose a mutable collection its receive loop writes. Ships with detector unit tests (true
  positive + immunity for config collections such as `EventsToSend`) and a liveness self-test, the
  shape established by `LoopbackSeamGuardTests`.
- **`Sdk/ADR-0045`** records the durable contract (the three classes as rules, plus "in-process
  WebSocket fakes run on `WebSocketTestServer`"), with a row in `docs/decisions/README.md` and a
  `CHANGELOG.md` entry under `[Unreleased]`.

**Not in scope.** The other eight WebSocket surfaces — already on `WebSocketTestServer`, four
already fenced; a sweep of the remaining four for Class B/C is follow-up work, not this change. No
production code changes: `OpenAiRealtimeBridge` is the system under test and stays exactly as it is.
No package version bump — this is test-only and ships with the next release train.

## Capabilities

### New Capabilities

None. The contract lands in the existing `test-determinism` capability.

### Modified Capabilities

- `test-determinism`: five ADDED requirements and **one MODIFIED** (the TTS cancellation
  requirement, corrected per ADR-0052 — see §5.11). The capability already owns this repo's
  deterministic-test contracts, and this is precisely the failure class it exists for — a test
  outcome that depends on scheduling rather than on the code under test. Its existing requirements
  fence *time* (cancellation observed at a deterministic seam) and *address space* (one owner per
  port); these fence the *fake server's own protocol*: when it answers, how long it stays, and what
  it hands the test.

## Impact

- `Tests/Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests`: the fake rewritten, one `ProjectReference` added,
  two test files adjusted.
- `Tests/Verbara.Sdk.Governance.Tests`: **two** new detectors sharing one scanner scaffold — the
  Class C capture-collection guard (§5.2) and the cancellation-token-provenance guard ADR-0052 left
  unscoped (§5.7) — their unit tests and a liveness self-test.
- `sync-fence-baseline.json`: three entries lowered.
- `docs/decisions/`: ADR-0045 + index row. `CHANGELOG.md`: one `[Unreleased]` entry.
- **`src/` untouched. Public API surface untouched** (`PublicAPI.*.txt` unchanged), so nothing
  cascades to `Sdk.Pro` or `Platform`.
- CI: no new external dependency, no Docker requirement, one more Governance test; the unit lane
  should get measurably *faster* — the 25 s of token-expiry above is the budget being reclaimed, and
  §7.4 measures the actual delta rather than assuming it.

## Architectural Risk

**Level:** LOW.

**Affected:** one test project and the Governance guard suite. No production code, no public API, no
downstream repo. The migration target is the substrate eight sibling suites already run on, so it
moves toward the validated path rather than away from it. The one genuinely new behaviour is
`HoldOpenUntilDisposed`: a fake that stays open until disposed can, if mis-wired, convert a fast
failure into a hang. That is why it is bounded by the server's own cancellation token and asserted
by a negative test (§4.7) rather than trusted.

**Mitigation:** the work is staged so the risky part is separable — §2 (substrate) lands with the
test files untouched and must be green on its own before §3 changes any timing; if the migration
destabilises the suite it can be dropped without losing the §3–§4 defect fixes, which stand on
either substrate. Each fence is negative-tested (remove the fence, watch the test fail; restore it,
watch it pass) so no fix is accepted on a green run alone — the lesson from the change that found
these classes, where 30× green locally was not enough. Acceptance is the repo's repeat-run
determinism protocol plus a measured before/after wall clock, and the zero-warnings
(`TreatWarningsAsErrors`) gate is unchanged.
