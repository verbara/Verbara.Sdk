# Tasks — ci-pipeline-slimming

## 1. Foundation

- [x] 1.1 Author `docs/decisions/0038-ci-pipeline-slimming.md` recording D2 (single-collection
      coverage) and D3 (representative PR matrix / full queue matrix) as durable CI policy

## 2. TTS cancellation fence (de-flake)

- [x] 2.1 Add iterator-entry `ct.ThrowIfCancellationRequested()` to the TTS synthesizers
      (Deepgram, ElevenLabs, Lmnt), mirroring the STT fence (PR#77)
- [x] 2.2 Convert `DeepgramSpeechSynthesizerTests.SynthesizeAsync_ShouldAbort_WhenCancelled`
      to the pre-cancelled-token pattern (drop the 200 ms timer race)
- [x] 2.3 Convert the ElevenLabs cancellation test likewise (drop the 50 ms timer race)
- [x] 2.4 Audit the Lmnt causal-trigger test; align only if a race is found
- [x] 2.5 Repeat-run the converted tests under load (mirror the 30× protocol from
      `stt-cancellation-test-fence`) — zero flakes

## 3. CI re-shape

- [x] 3.1 `unit-tests` job: add coverage collection (`--collect` + `coverlet.runsettings`) and
      artifact upload
- [x] 3.2 `Coverage Ratchet` job: consume the artifact (`needs: unit-tests`), merge with
      `reportgenerator`, gate with `check-coverage-floor.py`; remove the duplicate build + test
- [x] 3.3 `functional-tests`: conditional matrix — `pull_request` → `[23]`,
      `merge_group` → `[22, 23]`
- [x] 3.4 Reconcile branch-protection required-check contexts in the same landing: drop
      `Functional Tests (Testcontainers) (22)` from the PR-required set (it remains
      queue-validated via `merge_group`) — the verbara-meta/ADR-0003 required-check rule.
      DONE at landing: required checks reconciled to 8 contexts (dropped
      `Functional Tests (Testcontainers) (22)`). Empirical note: GraphQL `enqueuePullRequest`
      was refused with "Required status check ... (22) is expected" until the drop — required
      checks gate queue ENTRY at the PR level, so the reconciliation had to precede enqueue.

## 4. Verification

- [x] 4.1 `dotnet test` green locally, zero warnings (TreatWarningsAsErrors)
- [ ] 4.2 Observed `pull_request` CI wall-clock ≤ ~8 min; `merge_group` still validates the
      full matrix — NOT MET as written. Observed `pull_request` wall-clock ≈ 30 min (run
      2026-07-15 02:17:58→02:48:01 UTC: Unit Tests ~11 min, then Functional (23) ~19 min,
      serialized by the pre-existing `needs: unit-tests`). The ≤~8 min target was unrealistic:
      it counted the removed work but not the surviving serialization. The delivered win is PR
      runner-COMPUTE, not wall-clock — −1 functional variant (~19 min) and −the duplicate
      ratchet build+test (~11 min) — plus removal of the TTS flake vector. Future lever
      (OUT OF SCOPE here): de-serialize functional from `unit-tests` (drop the `needs:`) so
      Functional (23) and Unit Tests run in parallel; that is what would bring PR wall-clock
      near the target. Left unchecked to keep the record honest.
- [x] 4.3 CI green on both events; merge queue drains normally after the context reconciliation.
      MET: PR #101 green on both events. The `merge_group` run executed the full matrix
      (22)+(23) in parallel and passed (run 29385726367, 2026-07-15 03:06:37→03:37:59 UTC);
      the queue drained normally and PR #101 merged (d4f86bb0, 03:38:07 UTC).

## 5. Release

- [x] 5.1 Bump `Directory.Build.props` `PackageVersion` (patch) + CHANGELOG entry — the TTS
      fence is a `src/` behaviour fix and publishing triggers only on the `v*` tag
- [ ] 5.2 Downstream pin bump (Pro/Platform) rides the next release train — OUT OF SCOPE for
      this change; STAYS OPEN BY DESIGN. Tracked by the next `/xr:release` train (the pin
      cascade is that command's job, not this change's); the released Sdk 2.3.1 tag is the
      trigger. Not a lost follow-up — the release train is its authoritative owner.
