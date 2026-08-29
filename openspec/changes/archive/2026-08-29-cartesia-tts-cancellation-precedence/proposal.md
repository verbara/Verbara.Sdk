---
tier: PEQUEÑO
owner: Harol
approver: Harol
stakeholder: Anyone who cancels a synthesis and expects the same answer from every provider — and the maintainer, who would otherwise carry two silent providers out of six
decision_ref: Sdk/ADR-0053
---

# Proposal: cartesia-tts-cancellation-precedence

## Why

`voiceai-midstream-cancellation-coverage` §2.1 measured **four** TTS synthesizers against blank text
handed to an already-cancelled enumeration and found one that does not throw. Re-measured at
`1b9b984a` across **every synthesizer the package ships** — six classes over seven selectable paths,
because Lmnt carries two transports behind one entry — the picture is the same defect on **two**
surfaces, not one:

| surface | blank text + pre-cancelled token | why |
|---|---|---|
| **Cartesia** (WS) | **no throw, 0 frames** | the blank-text `yield break` at `CartesiaSpeechSynthesizer.cs:52` runs first, with no guard ahead of it |
| **Speechmatics** (HTTP) | **no throw, 0 frames** | the same, at `SpeechmaticsSpeechSynthesizer.cs:76` |
| Deepgram (WS) | `OperationCanceledException`, caller's token | guard at `:61`, ahead of its `yield break` at `:65` |
| ElevenLabs (WS) | `OperationCanceledException`, caller's token | guard at `:50`, ahead of its `yield break` at `:54` |
| Lmnt (WS **and** HTTP) | `OperationCanceledException`, caller's token | guard at `:110`, ahead of both the `yield break` at `:115` and the transport split at `:117` |
| Azure (HTTP) | `TaskCanceledException`, caller's token | no guard **and no blank-text branch** — the cancellation surfaces from `HttpClient.SendAsync(…, ct)` |

Four synthesizers place the observation **before** the blank-text branch, so the ordering has a
four-to-two precedent in this repo rather than a preference. The two that diverge have no entry
guard at all — on non-blank input their throw comes from the transport (`ClientWebSocket.ConnectAsync`
on Cartesia, measured 10/10 with the linked connect token) — which is why the divergence shows only
on the one input where the transport is never reached.

**Azure satisfies the contract without stating it.** It has neither a guard nor a shortcut, so its
`TaskCanceledException` — which derives from `OperationCanceledException` — comes out of the HTTP
client rather than out of an ordering decision. It needs the assertion, not the fix.

**Why the first measurement missed one.** It enumerated by provider name over a list of four. The
package ships six, and `test-determinism`'s own Purpose already names this failure mode: *"a closed
provider list under an open contract hides the surfaces nobody looked at"* (ADR-0052). Coverage here
is enumerated by selectable code path.

`streaming-session-lifecycle` already states the contract this breaks, in the scenario *"The
consumer's own cancellation still faults"*: an `OperationCanceledException` **takes precedence over
the sequence ending quietly**. Cartesia and Speechmatics end quietly.

The size of this is one input, and that is the argument for fixing it rather than for ignoring it: a
consumer that cancels and receives an empty sequence cannot tell "cancelled" from "nothing to say",
and the SDK offers no other signal that would let it.

## What Changes

1. Move the cancellation observation ahead of the blank-text early return in
   `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs` and in
   `src/Verbara.Sdk.VoiceAi.Tts/Speechmatics/SpeechmaticsSpeechSynthesizer.cs`, matching where the
   other four place theirs. This is a **product behaviour change**; each synthesizer lands in its
   own commit, under one `Fixed` CHANGELOG line — it must not arrive disguised as test coverage,
   which is exactly why `voiceai-midstream-cancellation-coverage` declined to make it (§2.3, its own
   gate did not trigger).
2. Cover the input on **all seven paths**, not only the two being fixed, so the next synthesizer
   added to this package inherits an assertion rather than a convention. Azure and the four already
   correct are pinned by test, not by the fix.
3. Record the ordering rule where the next author will hit it — the guard precedes the empty-input
   branch, and the reason is the precedence sentence in `streaming-session-lifecycle`.

## Impact

- `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs` — one guard, one commit.
- `src/Verbara.Sdk.VoiceAi.Tts/Speechmatics/SpeechmaticsSpeechSynthesizer.cs` — one guard, one commit.
- `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/` — one test per selectable path, seven in total.
- Consumers who relied on Cartesia or Speechmatics returning an empty sequence for blank text **while
  cancelled** now get the exception the other four already threw. Blank text with a live token is
  unchanged on every surface.

## Architectural Risk

Low, and bounded to one input on two surfaces. The change cannot affect any enumeration whose token
is not already cancelled, because the observation it moves only fires on a cancelled token; and it
cannot affect a cancelled enumeration of non-blank text, which already throws from the transport. The
one behaviour that moves is the intersection of the two, which is the defect. Widening the fix from
one synthesizer to two does not widen that risk — it is the same edit at the same position in a file
that answers the same way.
