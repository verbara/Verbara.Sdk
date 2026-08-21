# ADR-0045: In-process WebSocket fakes answer on protocol, hold on their own token, and hand out snapshots

- **Status:** Accepted
- **Date:** 2026-08-20
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0009 (three-tier test strategy — the unit tier runs in-process and in parallel,
  which is why these fakes exist at all), ADR-0014 (VoiceAi providers are hand-rolled
  `ClientWebSocket` code, so each streaming surface carries its own fake server), ADR-0041
  (WireMock.NET is the *HTTP* provider substrate; the WebSocket surfaces stay in-process on the
  shared `WebSocketTestServer`), ADR-0044 (IPv4 loopback literal — the previous defect in the same
  layer, and the reason a Governance source-scanning guard is the enforcement shape here),
  ADR-0052 (cancellation throws at the iteration boundary; its F3 finding is the second guard this
  ADR lands), `verbara-meta/ADR-0004` (ecosystem-wide deterministic-test-fences programme — the
  net-new-only barrier ratchet `sync-fence-baseline.json` implements)

## Context

Nine in-process WebSocket fake servers stand behind this repo's streaming provider suites. They were
written one at a time, each solving its own suite's problem, and three defects recur across them.
All three are invisible on a quiet machine: every affected suite was green, thirty runs in a row,
before this was written.

**Class A — the fake answers on a timer instead of on the protocol.** `RealtimeFakeServer` sent
`session.created`, slept 30 ms, and then delivered its configured events, on the assumption that the
client had sent its `session.update` within that window. Instrumentation put the client's frame at
5.5 ms and the delay's expiry at 33.5 ms, so the assumption held here — by 28 ms, on this machine,
with these cores. Nothing enforced it. A test asserting on that frame passes or fails on which of
two unrelated clocks wins.

**Class B — the hold-open flag is implemented as `await receiveTask`.** A cancellation test needs the
socket alive when its token fires; otherwise the loop under test exits on the server's close and the
test attributes to cancellation something cancellation did not do. Awaiting the receive loop does not
hold anything: the loop ends the instant the client half-closes, while the socket is still perfectly
readable. Asserting the live server-side socket state immediately before the cancel produced

```
Expected fakeOpenAi.SocketState to be WebSocketState.Open {value: 2} … but found
WebSocketState.CloseSent {value: 3}.
```

The teardown was already half-done. That test had been green for months while exercising half of
what its name claims.

**Class C — the fake hands tests the live collection its receive loop writes.** A `public List<T>` of
captured frames is read by the assertion on one thread while the session handler appends on another:
a count that changes between two assertions in the same test, or an enumerator throw, with nothing in
the code to suggest either.

Two more findings came out of measuring rather than reading:

- The old `HttpListener`-based session called `CloseAsync` while its own background receive loop had
  an outstanding `ReceiveAsync` on the same socket. The close *frame* does reach the peer, so the
  symptom was invisible; the *handshake* never completes, because the outstanding receive owns the
  path and `CloseAsync` can never read the reply. The session handler therefore did not return at
  ~130 ms as its code reads — it returned when the client died, **4 987–4 992 ms** into each test.
  That is the mechanism behind the suite's wall clock, not the delays themselves.
- The tests were built on the same principle as the fake. Five of them handed
  `HandleSessionAsync` a `CancellationTokenSource(5s)` and asserted whatever had accumulated when it
  expired; three more sat on `Task.Delay(300)`. The project ran in **25.87–26.06 s across thirty
  runs** — a 0.19 s spread. Runtime dominated by real work varies with load; runtime dominated by
  fixed timeouts does not. The tightness was the signature of five tokens expiring on schedule.

## Decision

Four rules. The first three are the defect classes stated as contracts; the fourth is the substrate
they run on.

**1. A fake answers on a protocol sentinel, never on a wall-clock delay.** Where the client sends an
unconditional frame — `session.update` for the Realtime bridge, the terminal EOF for LMNT — that
frame is the join point. The wait is bounded by a timeout set far above any plausible scheduling
delay, so reaching it means the protocol assumption is wrong rather than that the machine was busy,
and the test then fails on its own assertion instead of hanging the suite.

**2. Hold-open waits on the fake's own cancellation token, never on the receive loop.** The client's
half-close is not the end of the session. The hold releases when the fake is disposed, and only then
drains the receive loop.

**3. Captured frames are exposed as a snapshot taken under the lock the receive loop writes under** —
`IReadOnlyList<T>` returning a copy, never the backing `List<T>` and never a read-only interface over
the live list. Collections carrying test→server *configuration* are explicitly exempt: they are
written by the test before the server starts, or seeded once in the fake's constructor, and no reader
can be racing either.

**4. Every in-process WebSocket fake is built on the shared `WebSocketTestServer`**, never on
`HttpListener` + `AcceptWebSocketAsync`. A fake on the shared substrate carries no port-allocation
probe: `TcpListener(IPAddress.Loopback, 0)` binds a free port and keeps it, so the check-then-bind
window `HttpListener` forces — the one ADR-0044 could only mitigate — has no equivalent and the
probe is deleted rather than carried over.

And the corollary on the test side, which is where the time actually goes: **a test ends on the
signal it asserts, not on a cancellation timeout.** A token that exists only to bound a hang is a
safety net; reaching it must mean the test failed, never that it finished.

Each rule is enforced by something that fails, not by review:

| Rule | Enforcement |
|---|---|
| 1, and the test-side corollary | `sync-fence-baseline.json` net-new-only ratchet — per-file barrier counts go down, never up |
| 3 | `FakeServerCaptureScanner` + `FakeServerCaptureGuardTests` (Governance, Roslyn source scan) |
| ADR-0052 F3 | `CancellationProvenanceScanner` + `CancellationProvenanceGuardTests` |
| 2, 4 | Negative tests per fence: remove it, watch the test fail; restore it, watch it pass |

The Class C detector discriminates by **who writes**, not by member name: a capture is written by the
fake in its session handler, configuration is only read there or seeded in the constructor. No name
list, no ignore list. It carries two rules rather than one, because keying only on the member's own
name lets `public List<T> X => _privateList;` through — a hole found by negative-testing the guard,
not by reviewing it.

## Consequences

- The Realtime suite runs in **0.8 s against a 25.9 s baseline**, 59/59 green, and 20/20 under CPU
  saturation. The reclaimed ~25 s was never work.
- The Class B cancellation assertion is green for the first time; with the hold-open flag cleared it
  fails 3/3, reproducing the recorded `CloseSent` failure exactly.
- Two assertions were replaced rather than ported, because they measured the fake. A response
  `Duration > Zero` held only because the fake slept 5 ms between the two events; it now checks the
  interval between the two events the bridge published. An absence assertion ("does not crash") gained
  a positive sentinel, without which it is satisfied by a bridge that never ran.
- `sync-fence-baseline.json` drops from 329 barriers to 321, and the three entries for this suite are
  deleted outright.
- Two production-side findings are recorded and deliberately **not** fixed here, because this change
  touches no `src/`: a hangup/dispose race in `AudioSocketSession` (a hangup that overtakes
  `ReadAudioAsync`'s first `MoveNext` throws `ObjectDisposedException`), and one unguarded `await` in
  `OpenAiRealtimeBridge` before its loops start, where a cancel lands as a faulted session rather
  than a clean exit.
- **One suite is converted; eight remain, and they are named rather than implied.**
  `AssemblyAiFakeServer`, `CartesiaFakeServer` (STT), `DeepgramFakeServer`, `SpeechmaticsFakeServer`,
  `CartesiaFakeServer` (TTS), `DeepgramTtsFakeServer`, `ElevenLabsFakeServer`, `LmntWsFakeServer`.
  Their current state is not uniform and should not be assumed: rule 3 is *enforced* across all nine
  by the guard; rule 4 already holds for all nine; the two TTS fakes that answered on a timer were
  converted to causal waits in an earlier change, and the hold-open paths in `LmntWsFakeServer` and
  `DeepgramTtsFakeServer` already park on the server token. What has **not** been done for the eight
  is what was done here: negative-testing each sentinel, and sweeping the tests that drive them for
  the corollary — a token expiry as the normal path to an assertion. That sweep is a separate change,
  not an implication of this one, and until it runs no claim is made about those eight beyond what
  the guards enforce.

## Alternatives considered

**Leave the delays and raise them.** The failure mode of a too-short delay is a flake; the failure
mode of a long one is the 25.9 s this suite spent waiting. Neither is a contract, and a raised delay
still passes for the wrong reason on a machine that is merely slower than the one it was tuned on.

**A per-fake fix without a stated contract.** The three defects appear across nine independently
written fakes. Fixing one instance leaves the next author to rediscover it — which is what happened
between ADR-0044 and this ADR, in the same layer, eighteen days apart.

**A shared base class for the fakes rather than rules plus guards.** The surfaces differ genuinely in
protocol, and the substrate they truly share is already factored out as `WebSocketTestServer`. A base
class would centralise the session flow, which is exactly the part that must vary per provider, while
leaving the three defects expressible in every override. Guards catch the defect wherever it is
written; a base class only catches it where someone remembered to inherit.

**Wait for the second detector (ADR-0052 F3) to get its own change.** The scaffolding is the expensive
part — the tree locator, the reporting shape, the liveness self-test, the true-positive and
false-positive unit tests. A second detector landing later rebuilds all of it. Both ship here, sharing
one scaffold, and the F3 guard is negative-tested against history rather than a fixture: restoring the
ten pre-fix cancellation tests makes it report exactly those ten.
