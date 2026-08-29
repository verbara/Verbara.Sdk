---
tier: PEQUEÑO
owner: Harol
approver: Harol
stakeholder: Anyone who has to believe a green VoiceAi suite means a cancelled call actually tears the session down — and the maintainer, who inherited two hold-open flags with nothing to prove them
decision_ref: Sdk/ADR-0045
---

# Proposal: voiceai-midstream-cancellation-coverage

## Why

`websocket-fake-class-ab-sweep` negative-tested thirteen fences across eight WebSocket fakes and
closed with three items it named but did not build. This change builds them. It also corrects the
sentence the sweep used to name the first one, which is **false as shipped**.

### The claim on `main` today, and what is actually true

`CHANGELOG.md` carries, under the sweep's entry:

> *"No suite in either tree cancels a session that is already streaming. Eight fakes, eight
> cancellation tests, every one throwing before the socket opens — which is why neither hold-open
> has a consumer."*

Three of those assertions do not survive a read of the tree at `5a0458ba`:

- **"Eight cancellation tests" — there are seven.**
  `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/Cartesia/CartesiaSpeechSynthesizerTests.cs` has 19 `[Fact]`,
  no `CancellationTokenSource`, and no test taking a token. Its only `Cancel`/`Abort` hits are
  `SynthesizeAsync_ShouldThrowTransportFailure_WhenServerAbortsMidSession` (:266), which is a
  server-side abort, not a cancellation.
- **"Every one throwing before the socket opens" — six of the seven.**
  `LmntSpeechSynthesizerTests.cs:444-491` (`LmntSpeechSynthesizerWsTests`) sets
  `HoldOpenUntilDisposed`, waits on the fake's `FirstMessageReceived` protocol signal, captures
  `_server.SocketState`, cancels, and then **asserts the state at the cancel was
  `WebSocketState.Open`** (:487-491). That socket was live and the client's first frame had already
  crossed it.
- **"Neither hold-open has a consumer" — one of the two has one.**
  `HoldOpenUntilDisposed` is set by exactly that Lmnt test. `HangForever`
  (`DeepgramTtsFakeServer`) is the one with no assignment anywhere in the repository.

The defensible version is narrower, and it is still a gap worth closing: **no suite cancels a
session with server→client frames in flight.** Six of the seven cancel a token before the socket is
opened at all — each asserting the fake saw nothing (`ReceivedFrameCount.Should().Be(0)` on the four
STT surfaces, `ReceivedJsonMessages.Should().BeEmpty()` on Deepgram and ElevenLabs TTS). The
seventh cancels on a live socket that the test has deliberately silenced first
(`_server.AudioFramesToSend.Clear()`), so nothing is ever mid-delivery. **Cancellation while audio
or transcripts are actually arriving is untested on all eight fakes.**

That is precisely the shape that would exercise the two hold-open flags. It is also why the sweep
could measure `HoldOpenUntilDisposed` as 10/10 green when swapped for the `await receiveTask` trap:
the flag has a consumer, but a consumer that cannot fail either way.

### The third item: a fence witnessed only by a vacuous assertion

All four STT fakes carry `while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)`
(`AssemblyAiFakeServer.cs:237`, `CartesiaFakeServer.cs:248`, `DeepgramFakeServer.cs:215`,
`SpeechmaticsFakeServer.cs:263`) so a half-closing client does not end the session early. The four
`StreamAsync_ShouldNotHalfCloseTheOutputSide_WhenInputEnds` tests assert
`ReceivedClientCloseFrame.Should().BeFalse()`.

`grep -rn 'CloseAsync\|CloseOutputAsync' src/Verbara.Sdk.VoiceAi.Stt/` finds one hit and it is a
**comment** (`CartesiaSpeechRecognizer.cs:153`, describing a half-close that was removed). No shipped
STT recognizer sends a close frame. So those four assertions pass because nothing is ever sent, not
because the fence catches anything — delete the `or WebSocketState.CloseSent` disjunct and they stay
green. The fence is true and unwitnessed, and the sweep proved it live only by temporarily
reinstating a defect in `src/`, which no committed test may do.

### One finding that is not a test gap

`CartesiaSpeechSynthesizer` is the only one of the four TTS synthesizers with **no**
`ct.ThrowIfCancellationRequested()` (Deepgram, ElevenLabs and Lmnt each have one). The living spec
requires a pre-cancelled token to throw *before the first provider request is issued*; on this
surface the first thing a cancelled call reaches is `BuildUri()` and `ConnectAsync`. Whether the
contract holds here is unknown because nothing has ever asked. This change asks first and fixes
second — and if `src/` turns out to need the guard, that is a product change and gets its own commit
with its own CHANGELOG line.

## What Changes

1. **A mid-flight cancellation test per WebSocket surface.** Cancel once the fake has *delivered* a
   frame the caller has observed — not merely received one — so the socket is live, the session is
   mid-delivery, and the hold-open path is the thing keeping it open. This is the consumer
   `HangForever` has never had and the falsifier `HoldOpenUntilDisposed` has never had.

2. **A cancellation test for `CartesiaSpeechSynthesizer`**, the one WebSocket surface with none —
   preceded by establishing whether the pre-cancelled contract holds there at all, since the
   synthesizer carries no entry guard.

3. **A witness for the `CloseSent` fence.** A test driving the fake with a raw `ClientWebSocket`
   that half-closes, asserting the session survives it. This exercises the fence without touching
   the production recognizer and without reinstating a defect in `src/`.

4. **The CHANGELOG sentence corrected** — *done*, in this change's own opening PR (`54524877`,
   #220), which is why it is checked off in `tasks.md` §5.1 rather than pending. It was written when
   2.5.0 was still untagged, so it landed as a correction before publication; `v2.5.0` has since been
   tagged (`d8fc879b`, 2026-08-25), and the corrected bullet now sits inside the published `[2.5.0]`
   section. **The archived change is not touched**:
   `openspec/changes/archive/2026-08-23-websocket-fake-class-ab-sweep/` stays exactly as it shipped,
   a period-correct record, and this proposal is where the correction lives.

## Capabilities

### Modified Capabilities

- `test-determinism`: two requirements added. Cancellation coverage on a WebSocket surface is not
  satisfied by a pre-cancelled token alone, and a fence is not witnessed by an assertion that would
  hold with the fence deleted.

## Impact

- `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/` — four mid-flight cancellation tests, one `CloseSent`
  witness. The four fakes may need a "frame delivered" observable; none needs a new fence.
- `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/` — four mid-flight cancellation tests, plus the first
  cancellation test `CartesiaSpeechSynthesizerTests` has ever had.
- `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs` — **only if** task 2.1 finds
  the pre-cancelled contract does not hold. Not assumed here.
- `CHANGELOG.md` — the sweep's `[Unreleased]` bullet corrected; a new entry for this change.
- `openspec/specs/test-determinism/spec.md` — grows by two requirements on archive.
- Downstream (Pro / Platform): **none** unless task 2.1 changes `src/`, which would be a
  cancellation fix on one synthesizer with no API-surface change.
- CI cost: eight to ten sub-second tests inside the existing `Unit Tests` job. No new job, no new
  required check.

## Architectural Risk

**Level:** LOW. **Affected:** two unit-test projects already inside the default unit filter, and at
most one `src/` iterator entry guard. No public API surface, no wire behaviour, no workflow changes.
**Mitigation:** the new tests cancel on a *protocol signal the fake emits* rather than on a clock, so
they inherit the sweep's own fence discipline; each is negative-tested (fence removed, failure
recorded, fence restored) before it counts as evidence, per the requirement the sweep just added to
this same capability. The one place this change could reach production — the Cartesia entry guard —
is gated behind measuring the current behaviour first, so the change cannot silently become a
product fix.
