# Tasks: cartesia-tts-cancellation-precedence

## 1. Reproduce before fixing

- [x] 1.1 Re-measure the four-way table in the proposal against the tree at implementation time —
      the line numbers move. Confirm Cartesia still returns 0 frames without throwing, and that the
      other three still throw with the **caller's own** token (not a linked one), because that
      difference is what makes "match the other three" a well-defined target.

      **Measured at `1b9b984a`** by a throwaway harness that put blank text plus an already-cancelled
      token to every synthesizer the package ships, with the token handed to the subject and
      `CancellationToken.None` to the enumerator (ADR-0052 F3). Verbatim:

      | surface | outcome | token | message | frames |
      |---|---|---|---|---|
      | Cartesia (WS) | **NO THROW** | — | — | 0 |
      | Deepgram (WS) | `OperationCanceledException` | caller's own | `The operation was canceled.` | 0 |
      | ElevenLabs (WS) | `OperationCanceledException` | caller's own | `The operation was canceled.` | 0 |
      | Lmnt (WS) | `OperationCanceledException` | caller's own | `The operation was canceled.` | 0 |
      | Lmnt (HTTP) | `OperationCanceledException` | caller's own | `The operation was canceled.` | 0 |
      | **Speechmatics (HTTP)** | **NO THROW** | — | — | 0 |
      | Azure (HTTP) | `TaskCanceledException` | caller's own | `A task was canceled.` | 0 |

      **The four-way table is under-scoped by two, and one of the two is a second instance of the
      defect.** The package ships **six** `SpeechSynthesizer` subclasses over **seven** selectable
      paths, not four. `SpeechmaticsSpeechSynthesizer.cs:76` carries the same
      `if (string.IsNullOrWhiteSpace(text)) yield break;` with no entry guard ahead of it, and
      measures the same answer as Cartesia. Naming four providers and calling that the contract is
      exactly what `test-determinism`'s own Purpose warns about — *"a closed provider list under an
      open contract hides the surfaces nobody looked at"* (ADR-0052). The proposal's precedent
      argument survives it and gets stronger: it is now **four synthesizers to two**, not three to
      one.

      **Azure is correct by accident, not by design.** It has no entry guard *and* no blank-text
      shortcut, so the cancellation surfaces from `HttpClient.SendAsync(..., ct)` as
      `TaskCanceledException`. That derives from `OperationCanceledException`, so the contract holds
      — but nothing in that file states the ordering, because that file has no ordering to state.

      **Lmnt is one path for this question, not two.** Its guard (`:110`) precedes the transport
      split (`:117`), which is why both transports measure identically; the two rows are evidence of
      the shared guard rather than of two separate ones.

      **No citation drifted.** `CartesiaSpeechSynthesizer.cs:52`, Deepgram `:61`/`:65`, ElevenLabs
      `:50`/`:54` and Lmnt `:110`/`:115` all read as the proposal states them.

- [x] 1.2 Write the failing test first, on Cartesia only, and record its verbatim failure. A fix
      whose test was written after it is not evidence.

      `CartesiaSpeechSynthesizerTests.SynthesizeAsync_ShouldThrowOperationCanceled_WhenTextIsWhitespaceAndTokenAlreadyCancelled`,
      placed beside the whitespace test whose behaviour it qualifies. Committed **red**, ahead of
      any `src/` edit. Verbatim:

      ```
      Expected a <System.OperationCanceledException> to be thrown, but no exception was thrown.
      Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1
      ```

      It asserts the fake stayed silent as well as the throw, for the reason the whitespace test
      above gives: "it threw" alone would also pass if the client had opened a session and been
      lucky. Scoped to Cartesia as the task says — Speechmatics gets its own red test when §2.1's
      scope question is settled, not folded in here.

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
