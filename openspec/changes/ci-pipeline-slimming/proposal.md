---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: CI reliability (all downstream repos gate on Sdk CI)
decision_ref: Sdk/ADR-0038
---

# Proposal: ci-pipeline-slimming

## Why

Sdk CI is the ecosystem's slowest and most-failing gate: **median 23 min per validation, ~25%
failed runs** over the recent window, so landing a PR through the merge queue costs ≈ 46 min
(PR run + merge_group run) — and a flake adds another full cycle (measured 2026-07-06; recorded
in verbara-meta `docs/research/2026-07-06-ci-pipeline-durations.md`). Every other repo lands in
3–5 min. The cost concentrates in three places:

1. **Functional matrix on both events** — Asterisk 22 + 23 (~20 min each, parallel) run on
   `pull_request` *and* `merge_group`, so the same ~20 min wall-clock is paid twice per landing.
2. **Coverage Ratchet duplicates the unit suite** (~11 min) — a second full build + test run
   whose only purpose is coverage collection.
3. **A wall-clock-raced TTS cancellation test flakes the queue-blocking Unit Tests job** —
   `DeepgramSpeechSynthesizerTests.SynthesizeAsync_ShouldAbort_WhenCancelled` races a 200 ms
   `CancellationTokenSource` against a hanging fake server (observed failing on PR#85: the
   WebSocket connect beat the timer). ElevenLabs uses the same 50 ms pattern. The
   `stt-cancellation-test-fence` change (PR#77) already established the deterministic
   pre-cancelled-token idiom for exactly this problem — STT got the fence; TTS never did
   (its synthesizers have no iterator-entry cancellation check at all).

## What Changes

1. **TTS cancellation fence (de-flake):** mirror the STT fence into TTS — iterator-entry
   `ct.ThrowIfCancellationRequested()` in the TTS synthesizers, and convert the
   Deepgram/ElevenLabs cancellation tests to the deterministic pre-cancelled-token pattern
   (no wall-clock timer racing the fake). Lmnt already uses a causal trigger; audit only.
2. **Single-collection coverage:** the Unit Tests job collects coverage and uploads the
   artifact; the Coverage Ratchet job becomes a fast consumer (merge + floor check,
   `needs: unit-tests`) instead of re-building and re-running the whole suite. Required-check
   names are preserved.
3. **Representative functional matrix:** `pull_request` runs Asterisk 23 only (fast feedback);
   `merge_group` runs the full [22, 23] matrix — nothing lands on `main` without full-matrix
   validation. Required-check contexts reconciled accordingly.

## Capabilities

### New Capabilities

- `ci-gating`: the repo's CI gate behaviour becomes a specced capability — full-matrix
  validation in the merge queue, representative validation on PRs, and single-execution
  coverage collection.

### Modified Capabilities

- `test-determinism`: adds the TTS-synthesis counterpart of the existing STT cancellation
  requirement (deterministic cancellation at the iteration boundary, pre-cancelled token
  throws before any provider request, no wall-clock races against fakes).

## Impact

- `src/Verbara.Sdk.VoiceAi.Tts/` — iterator-entry cancellation fence in the synthesizers
  (behavioural early-exit only; mirrors the STT precedent — patch release).
- `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/` — deterministic cancellation tests.
- `.github/workflows/ci.yml` — coverage single-collection + conditional functional matrix.
- Branch-protection required-check contexts — reconciled in the same landing.
- `docs/decisions/0038-*.md` — new ADR recording the durable CI-policy decisions.
- Downstream (Pro/Platform): no API change; patch pin bump rides the next release train.

## Architectural Risk

**Level:** MEDIUM. **Affected:** the Sdk merge path (re-shaping queue-required checks — a
mis-declared context can hang the merge queue, the exact failure mode verbara-meta/ADR-0003
documents) and the TTS streaming call path (early-exit ordering for pre-cancelled tokens).
**Mitigation:** required-check names kept stable (the `Coverage Ratchet` job survives as the
artifact consumer); protection-context changes land in the same PR as the workflow change; the
full matrix still gates every landing via `merge_group`; the TTS fence copies the proven STT
pattern and is verified with the same 30× repeat-run protocol.
