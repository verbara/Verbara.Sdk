# Tasks: cartesia-tts-cancellation-precedence

## 1. Reproduce before fixing

- [ ] 1.1 Re-measure the four-way table in the proposal against the tree at implementation time —
      the line numbers move. Confirm Cartesia still returns 0 frames without throwing, and that the
      other three still throw with the **caller's own** token (not a linked one), because that
      difference is what makes "match the other three" a well-defined target.

- [ ] 1.2 Write the failing test first, on Cartesia only, and record its verbatim failure. A fix
      whose test was written after it is not evidence.

## 2. The fix

- [ ] 2.1 Move the cancellation observation ahead of the blank-text `yield break` in
      `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs`, in **its own commit**,
      with its own `Fixed` CHANGELOG line. Use the same spelling the other three use so the four
      read alike.

- [ ] 2.2 Leave the non-cancelled blank-text path exactly as it is — zero frames, no session opened.
      Assert it, because the obvious way to get this wrong is to make every blank request throw.

## 3. Cover all four, not just the one

- [ ] 3.1 One test per TTS surface for the blank-text-plus-cancelled-token input. Three of them pass
      on the first run; that is the point — they pin the behaviour the fourth was brought up to.

- [ ] 3.2 Negative-test the new Cartesia guard: remove it, observe the test red, record verbatim,
      restore, re-run green.

## 4. Verification

- [ ] 4.1 `dotnet build Verbara.Sdk.slnx` — zero warnings, Debug and Release.
- [ ] 4.2 `Verbara.Sdk.VoiceAi.Tts.Tests` green under the CI filter, with the count stated.
- [ ] 4.3 `openspec validate --all --strict` green.

## 5. Close-out

- [ ] 5.1 Fill the PR number into the CHANGELOG entry before archiving.
- [ ] 5.2 `openspec archive cartesia-tts-cancellation-precedence --yes` via the CLI.
