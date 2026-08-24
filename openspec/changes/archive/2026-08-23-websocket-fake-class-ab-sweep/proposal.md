---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Anyone who has to trust a green VoiceAi streaming suite — eight of the nine fakes behind those suites carry fences nobody has ever watched fail
decision_ref: Sdk/ADR-0045
---

# Proposal: websocket-fake-class-ab-sweep

## Why

ADR-0045 states four rules for this repo's nine in-process WebSocket fake servers and enforces the
two that a source scan can reach. `websocket-fake-protocol-contract` then converted **one** suite —
`Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests` — end to end, and named the eight it did not touch rather
than implying anything about them.

This change is that sweep. It exists because the guards ADR-0045 landed do **not** cover rules 1 and
2:

- **Rule 3** (snapshot, not the live collection) is enforced across all nine by
  `FakeServerCaptureScanner`.
- **Rule 4** (shared `WebSocketTestServer` substrate) already holds for all nine.
- **Rules 1 and 2** — answer on a protocol sentinel, hold on the fake's own token — are enforceable
  only by negative-testing each fence: remove it, watch the test fail; restore it, watch it pass.
  Nobody has done that for the eight.

The reason this matters is a matter of record, not a hypothesis. In the converted suite, the
Class B hold-open flag was implemented as `await receiveTask`, which returns the instant the client
half-closes. That fence had been green for months while holding nothing, and the test whose name
says "cancellation" was exercising the server's teardown instead. Reviewing the code did not find
it; asserting the live socket state immediately before the cancel did.

**What the eight actually look like today**, measured rather than assumed:

| Fake | Protocol sentinel (`TaskCompletionSource`) | Hold-open flag |
|---|---|---|
| `AssemblyAiFakeServer` (STT) | none | none |
| `CartesiaFakeServer` (STT) | none | none |
| `DeepgramFakeServer` (STT) | none | none |
| `SpeechmaticsFakeServer` (STT) | none | none |
| `CartesiaFakeServer` (TTS) | yes | none |
| `DeepgramTtsFakeServer` (TTS) | yes | yes |
| `ElevenLabsFakeServer` (TTS) | yes | none |
| `LmntWsFakeServer` (TTS) | yes | yes |

The four STT fakes have no sentinel at all, so whatever sequences them with the client is something
else and has never been identified. The four TTS fakes have one, but only the shape of it is known —
not whether removing it fails anything.

All eight call **both** `CloseAsync` and `CloseOutputAsync`. That combination is what produced the
substrate finding behind ADR-0045's wall-clock result: `CloseAsync` also *receives*, so calling it
while a background loop has an outstanding `ReceiveAsync` on the same socket lets the close frame
reach the peer while the handshake never completes. The symptom is invisible — the session handler
simply returns when the client dies instead of when its code says. In the converted suite that was
**4 987–4 992 ms per test**, and nothing in the suite's own output pointed at it. Whether the eight
have the same collision is unknown; it is one grep and one instrumented run per fake to find out.

## What Changes

For each of the eight fakes:

1. Identify what currently sequences the fake with the client, and replace it with an explicit
   protocol sentinel where the client sends an unconditional frame — bounded by a timeout set far
   above any plausible scheduling delay, so reaching it fails the test's own assertion rather than
   hanging the suite.
2. Give the hold-open path (where the suite has cancellation tests that need one) a wait on the
   fake's own token, and drain the receive loop only on dispose.
3. **Negative-test both fences**: clear the flag, confirm the dependent test fails; restore it,
   confirm it passes. A fence nobody has watched fail is not evidence.
4. Check the `CloseAsync`/`CloseOutputAsync` pairing for the concurrent-receive collision, and
   record the measured session-handler return time either way.
5. Sweep the tests that drive each fake for ADR-0045's test-side corollary — a token expiry used as
   the *normal* path to an assertion rather than as a hang bound — and retire each one found onto
   the signal it actually asserts.

Where a fake's `sync-fence-baseline.json` entries reach zero, delete the entry outright, as
`websocket-fake-protocol-contract` did for its three.

## Impact

- **Tests only.** No `src/` change is in scope; anything found in production code is recorded and
  routed to `voiceai-session-teardown-races` rather than fixed here.
- Affected: `Verbara.Sdk.VoiceAi.Stt.Tests`, `Verbara.Sdk.VoiceAi.Tts.Tests`.
- `sync-fence-baseline.json` moves down only — the ratchet is net-new-only
  (`verbara-meta/ADR-0004`).
- Expect a wall-clock drop of the same shape as the converted suite if the concurrent-receive
  collision is present, and none if it is not. **The claim to make is whatever gets measured**, not
  the 25 s the first suite happened to yield.

## Architectural Risk

Low. The rules, the substrate and both enforcement idioms already exist and have been exercised on
one suite; this is application, not design. The real risk is the opposite of a regression — a sweep
that converts eight fakes without negative-testing each fence would produce exactly the state
ADR-0045 was written about: fences that look right and hold nothing. Task 3 is the load-bearing
step, not a formality.
