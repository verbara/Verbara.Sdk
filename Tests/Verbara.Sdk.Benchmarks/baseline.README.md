# `baseline.json` — what it is, and how it moves

`baseline.json` is the committed hosted-runner reference that
[`.github/workflows/perf-regression.yml`](../../.github/workflows/perf-regression.yml) compares
every weekly BenchmarkDotNet run against, via
[`scripts/ci/check-perf-baseline.py`](../../scripts/ci/check-perf-baseline.py). It is the
ENFORCING guard behind the AMI-throughput claim and the rest of `README.md`'s Performance hot paths
(ADR-0042 D1, D4).

## The two rules

1. **CI never writes back.** No workflow, script or bot edits this file. Ever. (ADR-0042 D6 —
   the same manual-ratchet semantics the coverage floor uses, for the same reason: a threshold that
   updates itself ratchets away from the thing it was supposed to protect, and the day it matters
   it has already moved out of the way.)
2. **A baseline moves only by a human-authored, reviewed PR** that states, per changed benchmark:
   **which** benchmark moved, in **which direction**, by **how much**, and **why**. All four. "Perf
   run was red, rebaselined" is a request to delete the guard written in a way that gets approved.

## What these numbers are — and what they are not

| | |
|---|---|
| `mean_ns` | mean-of-means across 13 real runs of `perf-regression.yml`, on **github-hosted `ubuntu-latest`**, in **nanoseconds** |
| `tolerance_pct` | a **two-sided** band, in percent, calibrated to cover the observed across-run spread with headroom |
| **not** | the workstation figures published in `README.md` / `docs/README-technical.md` |

The published figures were measured on an AMD Ryzen 9 9900X (12C/24T) and are **1.97×–2.35×
faster** than what a shared hosted runner achieves. Gating against them would be permanently red,
and the only way to make it green would be to rewrite the published numbers downward to whatever CI
happens to manage — publishing a worse product than the one that ships (ADR-0042 D4, Option D).
Those published figures are guarded separately, as COHERENCE against a committed measurement record
(ADR-0042 D7). **Regression is this file's job; document-vs-record correspondence is that one's.
They are different failures and they are detected separately.**

## Why the bands look wide

`ubuntu-latest` is not one machine. Across the 13 calibration runs it alternated between two CPUs —
**AMD EPYC 7763** (9 runs) and **AMD EPYC 9V74** (4 runs) — and the 9V74 is uniformly faster, by up
to **20.7%** on some benchmarks. The workflow cannot choose its host, so there is one pooled band
per benchmark, wide enough for either fleet.

The trap this avoids, stated once so nobody re-derives it:

> **Never set a band from `Statistics.StandardDeviation` in a BenchmarkDotNet report.**
> That is the *within-run* deviation — CV 0.11%–0.67% on this suite. The spread a *weekly* gate
> actually experiences is the *across-run* one — CV 0.5%–11.2%. A band derived from the report's own
> standard deviation is roughly **20× too tight** and would be red most weeks for reasons that have
> nothing to do with this repository's code.

Under the committed bands: **0 breaches across all 13 calibration runs; tightest headroom 5.8
percentage points.**

## Two-sided, on purpose

A breach is reported when `|observed − baseline| / baseline` exceeds the band **in either
direction**:

- **SLOWER** — the regression this gate exists to catch.
- **FASTER** — *not* good news on its own. It is either a stale baseline (re-baseline per the
  protocol below) or a benchmark whose work got optimised away and is no longer measuring the thing
  it is named after. Both want a human; neither should pass silently.

## The re-baseline protocol

A PR that touches `baseline.json` must carry a table like this in its description, one row per
changed entry:

| benchmark | old `mean_ns` | new `mean_ns` | direction | delta | why |
|---|--:|--:|---|--:|---|
| `AriJsonBenchmark.SerializeChannel` | 363.01 | 291.40 | faster | −19.7% | `Utf8JsonWriter` reuse in `#NNN` — the record is `docs/…` |

Accepted reasons, and what each one obliges:

| reason | obligation |
|---|---|
| **A deliberate optimisation landed.** | Name the PR/commit that made it faster. The baseline follows the code, not the other way round. |
| **A deliberate, accepted slowdown landed.** | Name the PR **and** the trade it bought (correctness fix, feature). A slowdown with no named trade is the regression, not a new baseline. |
| **The runner fleet moved.** | Say which CPU models the new figures were observed on, and over how many runs. This is the one reason that carries no code meaning — say so explicitly, so a future reader does not mistake it for a performance event. |
| **The benchmark itself changed.** | A renamed or rewritten benchmark is a **new** entry, not an edited one. Remove the old key, add the new one, and update the `sources` row if the class moved. |

Not accepted, in any form: *"the run was red"*, *"CI is noisy"*, *"widen the band until it passes"*.
If the band is genuinely mis-calibrated, that is its own PR, with the runs it was re-derived from
named and counted — never a widening folded into a red-run fix.

## Adding a benchmark

Three edits, in one PR:

1. a `--filter` step in `.github/workflows/perf-regression.yml`;
2. a `sources` row in `baseline.json` mapping that step's `--artifacts` subdirectory to the
   BenchmarkDotNet type whose report will land there;
3. one `benchmarks` entry per `[Benchmark]` method, keyed by **`FullName`**
   (`Namespace.Type.Method`).

Until step 3 exists the comparison reports the benchmark as *measured but not baselined* — a notice,
never a failure, so the PR that adds a benchmark is not blocked by the absence of a band it cannot
yet know. **Calibrate the band from observation, not from a guess:** dispatch the workflow a few
times (`gh workflow run "Perf Regression"`) and use the observed across-run spread. `sources` and
`benchmarks` must agree — a baselined entry whose type no `sources` row declares is a hard
configuration error (exit 2), because its report would never be looked for and its absence would go
unnoticed.

## How it fails closed

Every benchmark step in the workflow is suffixed `|| true`, so a benchmark that never ran — bad
filter, build break, timeout, renamed class — leaves no report and the job would otherwise be
green. `check-perf-baseline.py` treats each of these as a **breach**, not a skip:

- the class's `results/` directory is absent;
- the `…-report-full-compressed.json` report is absent or unparseable;
- the report's `Benchmarks` array is empty or missing;
- a `FullName` listed in `baseline.json` never appears in the parsed set;
- `Statistics.Mean` is missing, non-numeric, or not positive.

Separately, exit code **2** means the *guard* is broken (bad arguments, unreadable or invalid
`baseline.json`) and fails the job in **both** modes — a misconfigured guard reading green is the
`|| true` hole wearing a different hat.

## Current status: OBSERVING, not enforcing

The comparison landed **observing-only** (openspec `enforce-unguarded-public-claims` §2.5): it
prints every delta, writes the run summary and files the breach issue (ADR-0042 D5), but does not
fail the job. A gate whose first act is a false red gets routed around, then ignored, then deleted.

The flip is **one line** — `PERF_GATE_ENFORCE: 'false'` → `'true'` in the job-level `env:` block of
`.github/workflows/perf-regression.yml` — and belongs in its own PR, after **two consecutive
scheduled runs** have passed under observation. That single flag arms both
`check-perf-baseline.py` and `report-perf-breach.sh` together.

## Related

- `docs/decisions/0042-public-claim-guard-classes.md` — D4 (relative gate, fail closed), D5 (durable
  breach artifact), D6 (no CI write-back), D7 (published figures are COHERENCE).
- `docs/claim-registry.md` — which public claim each guard answers for.
- `scripts/tests/test_check_perf_baseline.py`, `scripts/tests/test_report_perf_breach.sh` — the
  guard's own guards, in the always-on required `Coverage Script Tests` job.
