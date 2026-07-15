# Design — ci-pipeline-slimming

## D1 — TTS cancellation fence mirrors the STT fence (PR#77)

The TTS synthesizers get `ct.ThrowIfCancellationRequested()` at iterator entry — the same seam
`stt-cancellation-test-fence` gave the 7 STT recognizers — so a pre-cancelled token throws
`OperationCanceledException` before any provider request, independent of scheduling. Tests
switch from wall-clock races (`CancellationTokenSource(200 ms)` / `(50 ms)` against a hanging
fake) to the pre-cancelled pattern the STT suites use. No new abstractions, no fake-clock
machinery: this is Sdk's `test-determinism` living spec extended to the seam TTS owns —
part of the ecosystem deterministic-test convergence (verbara-meta/ADR-0004, adopt-on-touch).

Lmnt's test already completes on a causal signal (cancel-on-first-received-message with a 5 s
guard); it is audited, not rewritten.

## D2 — Coverage: collect once, gate on the artifact

Today `coverage` re-builds and re-runs the entire unit subset (~11 min) only to collect. New
shape:

- `unit-tests` runs with `--collect:"XPlat Code Coverage" --settings coverlet.runsettings` and
  uploads the raw results as an artifact (collection overhead is small against an ~11 min win).
- `Coverage Ratchet` (same job name — it is a required check) becomes `needs: unit-tests`:
  download artifact → `reportgenerator` merge → `check-coverage-floor.py`. No build, no test.

The floor, runsettings, and manual-ratchet semantics are untouched (verbara-meta/ADR-0003).

## D3 — Representative PR matrix, full queue matrix

`functional-tests` gets a conditional matrix: `pull_request` → `[23]` (newest supported),
`merge_group` → `[22, 23]`. The queue remains the authoritative full-matrix gate — a
22-only regression is caught before landing, at the price of queue-time detection instead of
PR-time (accepted: 22-only regressions are rare; the PR run keeps ~20 min of feedback wall-clock
out of every iteration).

Required-check reconciliation (the ADR-0003 footgun): `Functional Tests (Testcontainers) (22)`
must stop being PR-required (it will not report on `pull_request` anymore) while remaining
queue-validated via the `merge_group` run. The branch-protection edit lands with the workflow
change, not after it.

## Recorded decisions

D2 + D3 are durable CI policy → recorded as `Sdk/ADR-0038` (task 1.1). D1 is recorded by the
`test-determinism` spec delta itself.
