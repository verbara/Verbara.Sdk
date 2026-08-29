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

- [x] 1.3 Same discipline for the second surface §1.1 turned up: write the failing test on
      Speechmatics, record its verbatim failure, before its guard is moved. Scope widened with the
      operator's approval after §1.1 measured it — the change was authored against a four-way table
      and the package ships six synthesizers.

      `SpeechmaticsSpeechSynthesizerTests.SynthesizeAsync_ShouldThrowOperationCanceled_WhenTextIsWhitespaceAndTokenAlreadyCancelled`,
      committed red. Verbatim, and word-for-word what Cartesia's says:

      ```
      Expected a <System.OperationCanceledException> to be thrown, but no exception was thrown.
      Failed!  - Failed: 2, Passed: 0, Skipped: 0, Total: 2
      ```

      The recorded-response stub is left **armed** rather than removed: if the guard were later
      placed too far down and a request escaped, the stub would match and return audio, so the test
      would fail on the request assertion rather than pass on an unmatched request. An unarmed stub
      would make "no request was issued" and "the request did not match" the same green.

## 2. The fix

- [x] 2.1 Move the cancellation observation ahead of the blank-text `yield break` in
      `src/Verbara.Sdk.VoiceAi.Tts/Cartesia/CartesiaSpeechSynthesizer.cs`, in **its own commit**.
      Use the same spelling the other four use so they read alike.

      `e5bea3f8`. The guard comment is copied verbatim from Deepgram and ElevenLabs, which carry it
      identically, so all six now read alike at that line. The *ordering* rule went into the
      blank-text comment instead of the guard's — that is where an author who reorders the two would
      be standing, and putting it in the guard would have made these two files read differently from
      the four they were brought up to match.

- [x] 2.2 The same edit in `src/Verbara.Sdk.VoiceAi.Tts/Speechmatics/SpeechmaticsSpeechSynthesizer.cs`,
      in **its own commit**. One `Fixed` CHANGELOG line covers both, naming both surfaces — the
      defect and the remedy are identical and splitting the entry would imply two findings.

      `2f106e4d`, and the CHANGELOG entry names both surfaces under one `Fixed` heading.

- [x] 2.3 Leave the non-cancelled blank-text path exactly as it is on both — zero frames, no session
      opened, no request issued. Assert it, because the obvious way to get this wrong is to make
      every blank request throw. Speechmatics' existing guard comment records a measured reason for
      that branch (a live route answers blank text with 0.24 s of audible audio); moving the
      cancellation check ahead of it must not weaken it.

      **Already asserted, and left standing rather than duplicated.**
      `CartesiaSpeechSynthesizerTests.SynthesizeAsync_ShouldYieldNothingWithoutConnecting_WhenTextIsWhitespace`
      and Speechmatics' `…_ShouldYieldNothingWithoutRequesting_WhenTextIsWhitespace` are exactly this
      assertion and both still pass. §4.4's re-measurement confirms it independently across all seven
      paths: `no throw, 0 frames` on every one. The Speechmatics comment is untouched — the guard
      moved above it, its measured justification did not change.

## 3. Cover every path, not just the ones being fixed

- [x] 3.1 One test per **selectable path** for the blank-text-plus-cancelled-token input — seven,
      not one per provider name: Cartesia, Deepgram, ElevenLabs, Lmnt WS, Lmnt HTTP, Speechmatics,
      Azure. Five pass on the first run; that is the point — they pin the behaviour the other two
      were brought up to. Enumerating by provider is what let §1.1's predecessor miss Speechmatics.

      Seven added, `7 passed, 0 failed`. The five that were not fixed passed on their first run, as
      predicted. Each hands the token to the subject and `CancellationToken.None` to the enumerator
      (ADR-0052 F3), and each asserts its fake or mock server saw nothing — "it threw" alone would
      also pass if a session had been opened and the test got lucky. The two HTTP mock stubs are left
      **armed** with the normal recorded response, so an escaped request would match and return audio,
      failing the request assertion rather than passing as an unmatched request.

- [x] 3.2 Negative-test both new guards: remove each, observe its test red, record verbatim, restore,
      re-run green. A guard whose test stays green without it is not witnessed
      (`test-determinism`, "A fence is not witnessed by an assertion that would hold with the fence
      deleted").

      Both witnessed. Each guard neutralised alone, in shipped source, then restored — the
      modification is a measurement and is **not** in the tree (`git checkout --` after each, `src/`
      verified clean):

      | guard removed | result |
      |---|---|
      | `CartesiaSpeechSynthesizer` | `Failed: 1, Passed: 21, Total: 22` — the new test, and only it |
      | `SpeechmaticsSpeechSynthesizer` | `Failed: 1, Passed: 29, Skipped: 2, Total: 32` — likewise |

      Both failures verbatim, and identical to the red runs of §1.2 and §1.3:

      ```
      Expected a <System.OperationCanceledException> to be thrown, but no exception was thrown.
      ```

- [x] 3.3 State in Azure's test that it passes without a guard of its own — the throw comes from
      `HttpClient`, not from an ordering decision — so a later reader does not take its green as
      evidence that the file states the rule.

      Written into the test's `<remarks>`: there is no ordering in that file to state, because it has
      neither a guard nor a blank-text shortcut. The remark also names what the test is for — if a
      blank-text shortcut is ever added there, it must go below a token check, and this test is what
      fails if it does not.

## 4. Verification

- [x] 4.1 `dotnet build Verbara.Sdk.slnx` — zero warnings, Debug and Release.

      Both `0 Warning(s), 0 Error(s)`.

- [x] 4.2 `Verbara.Sdk.VoiceAi.Tts.Tests` green under the CI filter, with the count stated.

      `161 passed, 0 failed` — 154 at `1b9b984a` plus the seven of §3.1. Whole unit lane in Release
      under the CI filter: **30 assemblies, 3 295 passed, 0 failed** (3 288 + 7).

- [x] 4.3 `openspec validate --all --strict` green.

      10 passed, 0 failed.

- [x] 4.4 Re-run the §1.1 measurement after the fix and check the table in: all seven paths fault, and the non-cancelled blank-text path still yields zero frames on all seven.

      | surface | blank + cancelled | frames | blank + live token |
      |---|---|---|---|
      | Cartesia (WS) | `OperationCanceledException` (caller's own) | 0 | no throw, 0 frames |
      | Deepgram (WS) | `OperationCanceledException` (caller's own) | 0 | no throw, 0 frames |
      | ElevenLabs (WS) | `OperationCanceledException` (caller's own) | 0 | no throw, 0 frames |
      | Lmnt (WS) | `OperationCanceledException` (caller's own) | 0 | no throw, 0 frames |
      | Lmnt (HTTP) | `OperationCanceledException` (caller's own) | 0 | no throw, 0 frames |
      | Speechmatics (HTTP) | `OperationCanceledException` (caller's own) | 0 | no throw, 0 frames |
      | Azure (HTTP) | `TaskCanceledException` (caller's own) | 0 | `HttpRequestException` — see below |

      All seven fault, and the two that were fixed now carry the **caller's own** token rather than
      arriving by accident of transport.

      **Azure's last cell is an artifact of the harness, not a regression.** That synthesizer has no
      blank-text shortcut, so with a live token it genuinely issues the request — and the harness
      pointed it at a dead port to keep itself server-free. Its real suite, which points it at a mock
      server, is green. Nothing in this change touches that file.


## 5. Close-out

- [ ] 5.1 Fill the PR number into the CHANGELOG entry before archiving.
- [ ] 5.2 `openspec archive cartesia-tts-cancellation-precedence --yes` via the CLI.
