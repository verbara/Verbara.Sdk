---
tier: PEQUEÑO
owner: Harol
approver: Harol
stakeholder: Anyone who cancels a synthesis and expects the same answer from every provider — and the maintainer, who would otherwise carry one silent provider out of four
decision_ref: Sdk/ADR-0053
---

# Proposal: cartesia-tts-cancellation-precedence

## Why

`voiceai-midstream-cancellation-coverage` §2.1 measured all four TTS synthesizers against blank text
handed to an already-cancelled enumeration. Three of the four throw. One does not:

| synthesizer | blank text + pre-cancelled token | why |
|---|---|---|
| **Cartesia** | **no throw, 0 frames** | the blank-text `yield break` at `CartesiaSpeechSynthesizer.cs:52` runs first |
| Deepgram | `OperationCanceledException`, caller's token | guard at `:61`, ahead of its `yield break` at `:65` |
| ElevenLabs | `OperationCanceledException`, caller's token | guard at `:50`, ahead of its `yield break` at `:54` |
| Lmnt | `OperationCanceledException`, caller's token | guard at `:110`, ahead of its `yield break` at `:115` |

The other three place the entry guard **before** the blank-text branch, so the question of ordering
already has a three-to-nothing precedent in this repo rather than a preference. Cartesia has no entry
guard at all — on a non-blank input its throw comes out of `ClientWebSocket.ConnectAsync` carrying the
linked connect token, measured 10/10 — which is why the divergence only shows on the one input where
the connect is never reached.

`streaming-session-lifecycle` already states the contract this breaks, in the scenario *"The
consumer's own cancellation still faults"*: an `OperationCanceledException` **takes precedence over
the sequence ending quietly**. Cartesia ends quietly.

The size of this is one input, and that is the argument for fixing it rather than for ignoring it: a
consumer that cancels and receives an empty sequence cannot tell "cancelled" from "nothing to say",
and the SDK offers no other signal that would let it.

## What Changes

1. Move the cancellation check ahead of the blank-text early return in
   `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs`, matching where the other
   three place theirs. This is a **product behaviour change** and lands in its own commit with its
   own `Fixed` CHANGELOG line — it must not arrive disguised as test coverage, which is exactly why
   `voiceai-midstream-cancellation-coverage` declined to make it (§2.3, its own gate did not trigger).
2. Cover the divergence on all four surfaces, not only the one being fixed, so the next synthesizer
   added to this package inherits an assertion rather than a convention.
3. Record the ordering rule where the next author will hit it — the guard precedes the empty-input
   branch, and the reason is the precedence sentence in `streaming-session-lifecycle`.

## Impact

- `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs` — one guard, one commit.
- `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/` — one test per TTS surface.
- Consumers who relied on Cartesia returning an empty sequence for blank text **while cancelled** now
  get the exception the other three already threw. Blank text with a live token is unchanged.

## Architectural Risk

Low, and bounded to one input. The change cannot affect any enumeration whose token is not already
cancelled, because the guard it moves only fires on a cancelled token; and it cannot affect a
cancelled enumeration of non-blank text, which already throws from the connect. The one behaviour
that moves is the intersection of the two, which is the defect.
