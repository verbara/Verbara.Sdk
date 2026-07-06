# Tasks — ci-pipeline-slimming

## 1. Foundation

- [ ] 1.1 Author `docs/decisions/0038-ci-pipeline-slimming.md` recording D2 (single-collection
      coverage) and D3 (representative PR matrix / full queue matrix) as durable CI policy

## 2. TTS cancellation fence (de-flake)

- [ ] 2.1 Add iterator-entry `ct.ThrowIfCancellationRequested()` to the TTS synthesizers
      (Deepgram, ElevenLabs, Lmnt), mirroring the STT fence (PR#77)
- [ ] 2.2 Convert `DeepgramSpeechSynthesizerTests.SynthesizeAsync_ShouldAbort_WhenCancelled`
      to the pre-cancelled-token pattern (drop the 200 ms timer race)
- [ ] 2.3 Convert the ElevenLabs cancellation test likewise (drop the 50 ms timer race)
- [ ] 2.4 Audit the Lmnt causal-trigger test; align only if a race is found
- [ ] 2.5 Repeat-run the converted tests under load (mirror the 30× protocol from
      `stt-cancellation-test-fence`) — zero flakes

## 3. CI re-shape

- [ ] 3.1 `unit-tests` job: add coverage collection (`--collect` + `coverlet.runsettings`) and
      artifact upload
- [ ] 3.2 `Coverage Ratchet` job: consume the artifact (`needs: unit-tests`), merge with
      `reportgenerator`, gate with `check-coverage-floor.py`; remove the duplicate build + test
- [ ] 3.3 `functional-tests`: conditional matrix — `pull_request` → `[23]`,
      `merge_group` → `[22, 23]`
- [ ] 3.4 Reconcile branch-protection required-check contexts in the same landing: drop
      `Functional Tests (Testcontainers) (22)` from the PR-required set (it remains
      queue-validated via `merge_group`) — the verbara-meta/ADR-0003 required-check rule

## 4. Verification

- [ ] 4.1 `dotnet test` green locally, zero warnings (TreatWarningsAsErrors)
- [ ] 4.2 Observed `pull_request` CI wall-clock ≤ ~8 min; `merge_group` still validates the
      full matrix
- [ ] 4.3 CI green on both events; merge queue drains normally after the context reconciliation

## 5. Release

- [ ] 5.1 Bump `Directory.Build.props` `PackageVersion` (patch) + CHANGELOG entry — the TTS
      fence is a `src/` behaviour fix and publishing triggers only on the `v*` tag
- [ ] 5.2 Downstream pin bump (Pro/Platform) rides the next release train
