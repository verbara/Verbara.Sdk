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
| `bytes` | BenchmarkDotNet's `Memory.BytesAllocatedPerOperation` — allocated bytes per operation, **independent of the CPU model and ISA, not of the core count** — read on the hosted runner |
| `bytes_tolerance` | a **two-sided** absolute bound in bytes; `0` (the default) means exact |
| `jit_isa` (top level) | the instruction-set level every BenchmarkDotNet log must report (`x86-64-v3`) |
| `environment` (top level) | the exact .NET SDK and runtime the job installs and measures with, and the logical core count it expects; every report must show all three |
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

## Bytes decide, time informs

Time on a shared runner depends on the host; allocated bytes depend on it far less. Two local runs of the whole
suite on .NET 10.0.14, one with the JIT at x86-64-v3 and one at x86-64-v4, gave **identical bytes on
every synchronous row**; on that host an interleaved run moved time by at most about 5%, and the spread across
hosted runners is what a re-calibration under the ISA pin measures. So a
row whose allocation is deterministic carries `bytes`, gated exactly (`bytes_tolerance: 0`) or
within a stated bound.

**Bytes do move with the logical core count.** A `ConcurrentDictionary` built without a concurrency
level sizes its lock array by `Environment.ProcessorCount`, so `ChannelManagerBenchmark.Create1000Channels`
allocates 64,512 B more per operation on a 24-core workstation than on the 4-core hosted runner, on
the same code, to the byte. So `bytes` are read from a hosted run of this workflow (its uploaded
report carries `HostEnvironmentInfo.LogicalCoreCount`), or locally under `DOTNET_PROCESSOR_COUNT=4`,
which reproduces the hosted figure exactly — never from an unpinned workstation run. A pinned local
report still records the machine's own `LogicalCoreCount`, so its breach message names that count, not
the pin. One run, then confirmed by the next. A row may carry `bytes` alone, without `mean_ns`/`tolerance_pct`, until
two weekly runs give its time band: it is then gated on bytes and reports its time.

A row whose allocation is **not** deterministic gets no `bytes` entry: a bimodal row (for example
`AsyncEventPumpBenchmark.EnqueueAndConsume1000Events`, about 32 KB or about 50 KB by a
writer/consumer race) is reported, never gated on bytes. A row with a fixed but small
non-deterministic part carries a bound and says why in `_bytes_note`.

A bytes breach is reported as `MORE-BYTES` (the regression) or `FEWER-BYTES` (a stale baseline, or
work that went away — a human looks, as for `FASTER`). No CPU model or ISA moves it, so against a
baseline read on the same core count a bytes breach is the code; the breach message names the core
count the run had.

## The pinned ISA

`ubuntu-latest` mixes CPUs with and without AVX-512, and .NET 10 targets AVX-512 wherever it
exists, so before the pin the same code read faster or slower by host. The workflow sets
`DOTNET_EnableAVX512=0` for the whole job, which holds the JIT at x86-64-v3 on every host. **The
variable name matters:** on .NET 10.0.14 `DOTNET_EnableAVX512F=0` is accepted and ignored — the JIT
still reported x86-64-v4 under it. `jit_isa` makes the comparison check every source's
BenchmarkDotNet log; a log that names another ISA, or no log at all, is a structural failure.

The time bands were calibrated **before** the pin, pooled across both CPUs; a re-calibration under
the pin is its own PR, from observed runs, with the protocol below.

The pin does not make the fleet one machine. Under it the hosted runs have landed on four CPU
models (EPYC 9V45, 9V74, 7763, Xeon 6975P-C), and on 2026-11-11 an EPYC 7763 read
`ChannelManagerBenchmark.Update1000ChannelStates` 26.4% over its baseline with the code unchanged
(an interleaved local run against the code of the previous 7763 run read 1.007 and 1.025). Its band
and `Create1000Channels`' are therefore wider than the rest (35% and 40%);
`_channel_manager_widened_bands_note` in `baseline.json` carries the runs, the ratios and what the
widening stops the gate from seeing. They were provisional until a comparison of a reference build
against this one inside the same job, on the same host, enforced; it does since 2026-11-19, judges
both rows on time, and is described under
[In-job comparison against the reference tag](#in-job-comparison-against-the-reference-tag).

## The pinned SDK and runtime

The ISA pin holds the JIT's instruction set; it does not hold the JIT itself. A .NET runtime patch
can change code generation on its own (10.0.12 is when the JIT began targeting AVX-512 on the hosts
that have it), and before this pin the job measured with whatever `10.0.x` the runner image
carried, so a time breach could be the runtime and nobody could tell which.

So `environment` declares the SDK (`dotnet_sdk`), the runtime (`dotnet_runtime`) and the core count
(`logical_cores`) this baseline is read under, and three things follow from that one declaration:

1. The workflow reads `dotnet_sdk` from `baseline.json` (the version is not repeated in the
   workflow) and installs that SDK into an empty directory (`DOTNET_INSTALL_DIR`), away from the
   SDKs and runtimes preinstalled on the image. Roll-forward has nothing newer to pick.
2. Before building, the job checks that the declared SDK and the declared runtime are the **only**
   ones reachable, and stops if not, before 15 minutes of benchmarks are spent.
3. The comparator reads every source's `HostEnvironmentInfo` (`DotNetCliVersion`, the version in
   `RuntimeVersion`, `LogicalCoreCount`) and fails closed when one differs from the declaration or
   is not recorded.

The pin applies to this job only. The rest of the repository keeps `10.0.x` and `global.json`'s
`rollForward: latestFeature`; `publish.yml` prints `dotnet --version` and `dotnet --info` so each
published package's toolchain is on record.

**The cost: a new .NET patch is not measured until a PR moves the declaration.** That PR is a
re-baseline under the protocol below: it names the new SDK and runtime, and it re-reads every row
on the new runtime (or says, row by row, why the old figure stands), because a runtime that moved
the numbers is exactly the event this pin exists to make visible. The declared SDK must ship the
declared runtime (the .NET release notes pair them; step 2 fails the run otherwise).

## Reported both ways, failing one way

A breach is reported when `|observed − baseline| / baseline` exceeds the band **in either
direction**:

- **SLOWER** — the regression this gate exists to catch. It **fails** an enforcing run.
- **FASTER** — *not* good news on its own. It is either a stale baseline (re-baseline per the
  protocol below) or a benchmark whose work got optimised away and is no longer measuring the thing
  it is named after. Both want a human; neither should pass silently. It is **reported**: an
  annotation, its own section of the run summary, and the breach issue. It does not fail the run.

Bytes follow the same split: **MORE-BYTES** fails, **FEWER-BYTES** is reported. Every structural
failure fails. The hosted fleet reads the same code up to 2–3× apart from run to run, mostly faster
than the calibration, so a gate that failed on a faster week would be red for reasons that have
nothing to do with this repository's code.

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
| **The .NET SDK or runtime moved** (`environment`). | Name the old and new SDK and runtime, the runs read on the new runtime, and every row the move changed. A row that moved with nothing else changed is a runtime effect: say so, so nobody reads it as code. |
| **The benchmark itself changed.** | A renamed or rewritten benchmark is a **new** entry, not an edited one. Remove the old key, add the new one, and update the `sources` row if the class moved. |

Not accepted, in any form: *"the run was red"*, *"CI is noisy"*, *"widen the band until it passes"*.
If the band is genuinely mis-calibrated, that is its own PR, with the runs it was re-derived from
named and counted — never a widening folded into a red-run fix.

## Adding a benchmark

Three edits, in one PR:

1. a `--filter` step in `.github/workflows/perf-regression.yml`;
2. a `sources` row in `baseline.json` mapping that step's `--artifacts` subdirectory to the
   BenchmarkDotNet type whose report will land there;
3. for each `[Benchmark]` method, keyed by **`FullName`** (`Namespace.Type.Method`, plus its
   `(Param: value)` suffix for a parameterised case): a `benchmarks` entry — `bytes` from one run
   wherever the row's allocation is deterministic, and `mean_ns` + `tolerance_pct` once its
   hosted-runner spread has been observed — or, until then, an `unbanded` entry with its `reason`.

The script tests check step 3 on every pull request: for each `sources` class, the `[Benchmark]`
methods in its source must be exactly the methods its `benchmarks` and `unbanded` entries name, so a
method added, renamed or removed without its entry is red on the PR that does it. An `unbanded` entry
is listed in the run summary as *declared without a band*: a notice while it carries a measurement, so
the PR that adds a benchmark is not blocked by the absence of a band it cannot yet know — but its
presence and its measurement are checked like any other row's.

**Calibrate the band from observation, not from a guess:** dispatch the workflow a few times
(`gh workflow run "Perf Regression"`) and use the observed across-run spread. `sources`,
`benchmarks` and `unbanded` must agree — an entry whose type no `sources` row declares is a hard
configuration error (exit 2), because its report would never be looked for and its absence would go
unnoticed.

Declared in `unbanded` today, on purpose:

- `ObserverDispatchBenchmark.Dispatch_1Observer` — resolution floor, `_dispatch_1_observer_note`.
- `TurnDetectionBenchmark.EndOfUtteranceDecision`, all four `UtteranceSeconds` cases —
  `sources._turn_detection_note`.
- `AsyncEventPumpBenchmark.EnqueueAndConsume1000Events` — bimodal bytes, `_bytes_note`.
- `AsyncEventPumpBenchmark.Refuse9000Of10000Events` — not yet read on the hosted runner.
- `ConferenceManagerBenchmark`, all four rows — renamed in 2.12.0 (it was `MeetMeManagerBenchmark`);
  banded from its own runs in its own PR.

Banded on bytes only, time not yet banded: `ChannelManagerBenchmark.Update1000ChannelStates_OneSubscriber`
(`_channel_manager_one_subscriber_note`), `ChannelManagerBenchmark.Create1000Channels_TwoLegCalls`,
the two deterministic `AsyncEventPumpBenchmark` rows and every `QueueManagerBenchmark` row. Their
time bands come from two weekly runs.

## Removing a band without removing the benchmark

A benchmark whose mean has fallen to BenchmarkDotNet's resolution floor can no longer carry a
percentage band: the band then tracks the harness rather than the code, and every fleet or codegen
change reads as a breach. Move its `benchmarks` entry to `unbanded` and leave its `--filter` step
and its `sources` row in place — the step still runs, the report is still required, and the
comparison still checks that the benchmark ran and measured; only its value stops being banded. A
mean of `0` there is BenchmarkDotNet's floor (`ZeroMeasurement`), a measurement, not a failure.

Three obligations, in the same PR:

1. state the evidence in the PR description — the observed mean, the observed StdDev, and the
   BenchmarkDotNet warning (`ZeroMeasurement`) if it fired;
2. record the absence as deliberate with an `unbanded` entry whose `reason` says why, plus a
   `_`-prefixed note at the top level of `baseline.json` when one line is not enough, the way
   `_dispatch_1_observer_note` does;
3. say where the published figure it backed is guarded instead, or delete that figure.

This is not a re-baseline and does not use the table above: no number moves. Removing the
`--filter` step or the `sources` row as well is a different change — that stops the measurement
entirely, and the `|| true` hole reopens.


## How it fails closed

Every benchmark step in the workflow is suffixed `|| true`, so a benchmark that never ran — bad
filter, build break, timeout, renamed class — leaves no report and the job would otherwise be
green. `check-perf-baseline.py` treats each of these as a **breach**, not a skip:

- the class's `results/` directory is absent;
- the `…-report-full-compressed.json` report is absent or unparseable;
- the report's `Benchmarks` array is empty or missing;
- a `FullName` listed in `baseline.json` — in `benchmarks` or in `unbanded` — never appears in the
  parsed set;
- a banded row's `Statistics.Mean` is missing, non-numeric, not finite, or not positive;
- any other observed row — declared in `unbanded` or not declared at all — carries no measurement:
  `Statistics` is null or absent, or its `Mean` is missing or not a finite number. A finite mean of
  `0` or less there is the harness's resolution floor and is listed, not failed;
- a row baselined on `bytes` has no numeric `Memory.BytesAllocatedPerOperation` (the memory
  diagnoser was removed);
- a source's BenchmarkDotNet log names a JIT ISA other than `jit_isa`, or no log names one;
- a source's report shows an SDK, runtime or logical core count other than `environment`'s, or
  does not record one.

Separately, exit code **2** means the *guard* is broken (bad arguments, unreadable or invalid
`baseline.json`, including an `environment` that is partial, names a range instead of an exact
version, or carries a key the comparator does not check, and a malformed `unbanded` entry: not an object, no `reason`, a class no
`sources` row declares, or a key that is also in `benchmarks`) and fails the job in **both** modes — a misconfigured guard reading green is the
`|| true` hole wearing a different hat.

## In-job comparison against the reference tag

No band that covers four CPU models sees a 10-20% time regression: the SLOWER bar sits +29% to +80%
over a row's usual reading on most hosts. So, after the absolute gate, the weekly job measures the
code against a **reference build in the same job, on the same machine**, and judges time by the
ratio. `scripts/ci/run-perf-abab.sh` runs it and `scripts/ci/check-perf-abab.py` judges it; both
read the `abab` block of `baseline.json`.

- **The reference** is `abab.reference_tag`, built from the tag's own tree (its `src/` and its own
  benchmark project) in a worktree beside HEAD, with the same pinned SDK, the same ISA pin and the
  same command line. It is declared, not discovered: it moves by PR at a release, like
  `environment`. When a newer release tag exists the report carries a notice, never a failure.
- **The order** is `abab.order`, `R H H R R H`: one BenchmarkDotNet invocation per position with
  every class of `sources` in it (one generated-project build each). Round *k* pairs the *k*-th R
  with the *k*-th H, so round 1 is (position 1, position 2), round 2 is (position 4, position 3) and
  round 3 is (position 5, position 6). Three rounds, because in-job noise is between processes: a
  launch-level spike lands on a different row each run, and more processes per arm is what dilutes
  it. The order does not cancel a linear drift exactly: it leaves a third of one step's drift in
  ln g, an estimated 0.25% at the order effect P3 allows, which no bar is near. A first-invocation
  penalty lands on R and pushes toward FASTER, which never fails.
- **The launches.** Each invocation runs every case in `abab.launch_count` processes (2), so each
  arm is six processes per case. `run-perf-abab.sh` passes `--launchCount 2` to both arms of the
  in-job run, and nothing else gets it: the absolute gate's steps and the `--list flat` calls run
  the sources' job as written. BenchmarkDotNet rewrites the `[ShortRunJob]` job in place
  (`ShortRun(IterationCount=3, LaunchCount=2, WarmupCount=3)`, one job, N = 6), so the benchmark
  sources are unchanged; in a local probe the bytes read the same as with one launch. The field has
  no default: a baseline without it, or with anything but a positive integer, is refused by both
  scripts (exit 2), and the manifest records the launch count the run used, which the comparator
  checks against the field. Every case of every invocation must show it: its job text states
  `LaunchCount=2`, and its `Measurements` hold three Result iterations from each of launches 1 and
  2, and nothing from any other. Anything else is **structural**: the override did not apply, and
  the run measured another design.
- **The statistic.** Per case and round, r_k = mean(H) / mean(R), each mean BenchmarkDotNet's
  `Statistics.Mean` over the six Result iterations of the invocation's two launches, and g is the
  geometric mean of the r_k. **SLOWER** when g is over `slower_ratio` (1.10) and so is a majority of
  the rounds (two of three); **FASTER** when g and a majority are under `faster_ratio` (0.909);
  **UNCONFIRMED** when only one of the two holds for a bar (g beyond it without the majority, or the
  majority without g); ok otherwise. A single round beyond a bar with g inside it is ok and counted
  as a **lone round**: one process spike is what the majority exists to absorb. Written as "g beyond
  the bar and more than half of the rounds beyond it", the rule reads the same with two rounds as
  the two-round design did (both rounds). The run's noise statistics read g, the statistic the
  verdict uses; each arm's last-over-first ratio is shown too: it is the run's own A/A noise.
- **The floor.** A case whose mean is under `min_mean_ns` (2 ns) in a HEAD invocation is shown and
  not judged: the ratio of a few cycles measures BenchmarkDotNet's overhead subtraction (the
  `_dispatch_1_observer_note` lesson). A case under the floor in the reference only is judged one
  way, by the same rule on mean(H) / max(mean(R), `min_mean_ns`): **SLOWER** when their geometric
  mean and a majority of them are over `sub_floor_slower_ratio` (2.0), so a 0.7 ns row that becomes
  30 ns is seen; it is never FASTER and stays out of the noise statistics.
- **Bytes in the job.** Both arms run on the same core count, so a bytes difference between them is
  the code. A case with a `bytes` entry in `benchmarks` is **MORE-BYTES** when every round's
  difference is over its `bytes_tolerance` and **FEWER-BYTES** when every round's is under minus it
  (every round, not a majority: bytes are deterministic);
  a case with no `bytes` entry shows its difference and is not judged. An exactly gated case whose
  bytes differ between the rounds of one arm is reported as a bytes anomaly.
- **Never dropped.** Each arm's `--list flat` output is what that tree declares. A method in HEAD's
  list only is reported as **HEAD only**, one in the reference's only as **reference only**, a
  parameter case one arm lacks as **case set differs**; none is compared and none fails. A case its
  arm declares that some invocation did not measure, a class with no report, a mean that is not a
  finite number, a case that does not show the declared launch count, a timed-out invocation,
  invocations that disagree on SDK, runtime, core count, JIT ISA, hardware intrinsics,
  BenchmarkDotNet version or processor, and a reference worktree whose commit is unknown or is not
  the tag's are **structural** failures.
- **A changed benchmark is not a pass.** When a compared class's own `.cs` file differs between
  the tag and HEAD, or a case's BenchmarkDotNet job (the text after `: ` in its `DisplayInfo`)
  differs between invocations, its rows are shown, not judged, and counted as a finding
  (`abab_blind`; the status reads breach, never pass): the ratio would include the benchmark's own
  change. A class with cases on both sides and no row judged on time or bytes, and a run that
  judged no row on time, are **structural**; the remedy is the next release's move of
  `abab.reference_tag`. Any other change under the benchmark project (except this file,
  `baseline.json`, `performance-record.json` and `README.md`) is a notice. When no compiled file
  differs at all, the report says the run measures the comparison's own noise.
- **Overrides and accepted costs.** `abab.rows["<FullName>"]` may set `slower_ratio`,
  `faster_ratio` or `judge_time: false`, always with a `reason`. `abab.accepted["<FullName>"]`
  records a cost a PR accepted, as `{ "ratio", "relative_to", "pr", "reason" }`; both bars are then
  centred on that ratio. `relative_to` must name `reference_tag`, or the comparator refuses the
  baseline (exit 2): an accepted ratio means nothing against another reference, so moving the tag
  forces its accepted entries out.
- **Moving the reference tag** at a release is a PR that changes `abab.reference_tag`, removes the
  `accepted` entries measured against the old tag, and says so.
- **The switch.** `PERF_ABAB_ENFORCE` in the job `env:` of `.github/workflows/perf-regression.yml`
  is `'true'` since 2026-11-19: SLOWER, MORE-BYTES and the comparison's structural failures fail
  the job. The comparison writes its section of the run summary, `perf-abab-report.md` and
  `perf-abab.json` in both modes; set back to `'false'`, it cannot fail the job, even on exit 2. Its only values are `'true'` and
  `'false'`: the comparator refuses any other spelling (exit 2), because the steps'
  `continue-on-error` compares with the exact string `'true'`. It is independent of
  `PERF_GATE_ENFORCE`, and its result does not file the breach issue. It was flipped after five
  consecutive runs met the prediction below, written before them.

### Before the flip: the prediction and the flip rule (third series)

Written on 2026-11-18, before the first run of the third series, and not edited after it: a change
to any threshold below is a new prediction and starts a new series. The first prediction
(2026-11-17) failed P2 and P7; the second (2026-11-18, three rounds, one launch per invocation)
held P1, P3, P4, P5, P6 and P7 on its five A/A runs and C1 on its dispatch, and failed P2 in one
run (p95 of |ln g| 0.0599 against ln 1.06 = 0.0583; the other four 0.030 to 0.036; 0 of 195 g
outside the band). Both texts and both results are kept in the ADR-0042 addendum. Those ten runs
motivated this design and count for nothing below.

**What changed, and why it is the written route and not a fit.** The second prediction's flip rule
named this change before its runs: on a P2 failure, two launches per invocation on the in-job run
only, with the order and the budget restated. Re-measured on the ten runs, about 95% of a row's
per-invocation variance is between launches, so each invocation now measures two processes per
benchmark: `abab.launch_count` is 2 and `run-perf-abab.sh` passes `--launchCount 2` to both arms.
BenchmarkDotNet rewrites the `[ShortRunJob]` job in place
(`ShortRun(IterationCount=3, LaunchCount=2, WarmupCount=3)`, one job, N = 6); the benchmark
sources and the absolute gate's steps are untouched. The order stays `R H H R R H`; the statistic,
every bar and every P1 to P5 and P7 threshold are the second prediction's, unchanged. Only P6's
budget is restated, because a second launch nearly doubles the run time of an invocation. No band
is widened and no row gets its own threshold.

**The statistic.** Per judged row, r_k = mean(H) / mean(R) in round k (k = 1..3; rounds pair
positions 1-2, 4-3 and 5-6), where each mean is BenchmarkDotNet's `Statistics.Mean` over the six
result iterations of the invocation's two launches, and g is the geometric mean of the three r_k.
A row is **SLOWER** when g is over `slower_ratio` and at least two of the three r_k are over it;
**FASTER** when g is under `faster_ratio` and at least two r_k are under it; **UNCONFIRMED** when
exactly one of those two conditions holds for a bar (g beyond it without the majority, or the
majority without g); ok otherwise. A single r_k beyond a bar with g inside it is ok and is counted
as a lone round. A sub-floor row uses the same rule on mean(H) / max(mean(R), `min_mean_ns`)
against `sub_floor_slower_ratio`. Bytes are unchanged: every round. An invocation whose recorded
job is not `LaunchCount=2` with six result iterations per case is a structural failure: the
override did not apply and the run measured the old design.

**Protocol.** A branch `perf/abab-validation-3` cut from `e283c161` (the last commit of `main` at
which no compiled file differs from `v2.10.1`) plus the commits that carry this design; five
`workflow_dispatch` runs of `Perf Regression` on it, each after the previous one ends. Every run
is an A/A test: each manifest's `changed_compiled_paths` must hold no `.cs` or `.csproj`, or the
run is not part of the series and the branch is wrong. A run lost to the infrastructure (runner
lost, cancelled) is run again and does not count; no other run is discarded. The judged set is
every row the comparator judges on time with the ratio H/R (39 rows in the second series); a
sub-floor row's judgement is counted only by P1. The C1 dispatch runs first. It is not one of the
five and no threshold moves on what it shows; if C1, or P5 or P6 read on that dispatch, fails, the
five runs do not start and the flip rule below applies.

**Prediction.** Every clause must hold in every run:

- **P1.** Zero judged rows are SLOWER or FASTER, sub-floor rows included.
- **P2.** Of all judged g over the five runs (about 195), at most 1% fall outside [0.909, 1.10].
  In each run the p95 (nearest rank) of |ln g| is at most ln 1.06 and the median at most ln 1.02.
- **P3.** In each run the geometric mean over judged rows of R(position 5) / R(position 1) is in
  [0.97, 1.03], and the median |ln r_k| is at most 1.5 times the median |ln self-ratio| (each
  arm's last invocation over its first).
- **P4.** Zero MORE-BYTES, zero FEWER-BYTES and zero bytes anomalies: every exactly gated row has
  a difference of 0 in all three rounds and the same bytes in all three invocations of each arm.
- **P5.** Zero structural failures and zero rows not compared (`abab_blind` 0); `list-R.txt`
  equals `list-H.txt`; every invocation's recorded job is `LaunchCount=2`; the only row not judged
  on time by an override is `AsyncEventPumpBenchmark.EnqueueAndConsume1000Events`.
- **P6.** Every run completes with the job at most 105 min, the in-job phase at most 80 min (six
  invocations of two launches each), and no invocation timed out. Projected before the run from
  the second series' logs and a local launch-factor probe (1.93): phase about 67 min, job about
  86 min, an invocation about 11 min. The backstops, which are not the budget:
  `abab.invocation_timeout_s` 1080, the run step 125 min (six invocations and two list calls at
  their timeouts, each with its 30 s kill grace, are 116 min), the job 260 min (every step of the
  job has its own timeout, and at all of them together the job runs 251 min).
- **P7 (per row).** No judged row has 2 or more of its 5 g outside its band, and no judged row is
  UNCONFIRMED in 2 or more of the 5 runs. Lone rounds are reported and do not count. Named in
  advance as the likeliest to break it (the five widest rows of the second series by the root
  mean square of ln g, plus the two next by their largest single |ln g|):
  `ObserverDispatchBenchmark.Dispatch_10Observers`,
  `ChannelManagerBenchmark.LookupByUniqueId`,
  `MeetMeManagerBenchmark.Mute_NoSubscriber` (renamed `ConferenceManagerBenchmark` in 2.12.0),
  `ChannelManagerBenchmark.Create1000Channels`,
  `AmiProtocolReaderBenchmark.ParseResponse`,
  `AmiProtocolReaderBenchmark.ParseSingleEvent` and
  `QueueManagerBenchmark.QueueStatus_OneSubscriber`.
- **C1 (sensitivity, one more dispatch, run first, not one of the five).** A throwaway branch
  `perf/abab-c1-3` carries the series-3 base plus the same `src` probe the second prediction's C1
  used, on `AriParseEventBenchmark.ParseStasisStart`, whose cost was measured locally by an ABAB
  under the same ISA pin and `DOTNET_PROCESSOR_COUNT=4` at a ratio of 1.2244. That row is SLOWER
  under the rule above with g in [1.174, 1.274], and no other judged row is SLOWER or FASTER. The
  row is not one of the seven named in P7. The second prediction's C1 (g 1.2295 at one launch per
  invocation) does not count here: it validated another configuration.

**What the gate is expected to see** (stated, not a flip clause): a slowdown of 15% or more on a
judged row in nearly every run; one of 12% in about four runs of five, so within two weekly runs
when it persists. Under 10% it is not a gate.

**Flip rule.** `PERF_ABAB_ENFORCE` becomes `'true'` only when P1 to P7 and C1 all held, at the
thresholds written above. Each clause that fails blocks the flip; none has a looser band in which
it may flip anyway. What each failure leads to, none of which is a flip:

- **P1.** The majority is not enough on this runner: require every round beyond the bar as well as
  g, and state the sensitivity that costs.
- **P2.** Six processes per arm did not bring the noise inside the clause either, so more
  processes on a hosted runner are not the route: no third launch, no fourth round, no per-row
  threshold, no wider clause. The time half stays observing on hosted runners, and an ADR-0042
  addendum decides between a dedicated runner and keeping the comparison as a report. Bytes may
  still flip alone if P4 and P5 held.
- **P3.** The order is biased: change the order design.
- **P4.** Bytes in the job are not reliable. This blocks only the bytes half: MORE-BYTES then
  leaves the failing set, bytes stay on the absolute gate and the core-count check, and time may
  flip if every other clause held.
- **P5 or P6.** Fix the design or the budget. A `LaunchCount` other than 2 in any invocation is
  a P5 failure, not a run to discard.
- **P7.** That row needs an `abab.rows` override (`judge_time: false` or its own `slower_ratio`).
- **C1.** The bar or the statistic does not see the cost on this runner: measure the local-to-CI
  ratio bias on that row before changing either.

Any change made after seeing these runs (an override, a threshold, the order, the number of
rounds or launches, a timeout, a fix for P5 or P6) needs a fresh series of five runs on the
changed configuration, judged by a prediction written before them. The runs that motivated a
change never count as its validation. If everything holds, the PR that flips the switch also
records the evidence in the ADR-0042 addendum and states the fate of the two provisional bands.

### The result (third series)

Five consecutive A/A runs on `perf/abab-validation-3` (runs 44445323031, 44455236182, 44465047964,
44474374249 and 44485419806, 2026-11-19, on EPYC 7763, 9V74 and 9V45 hosts), after the C1
dispatch (44435524176). Judged twice, by two scripts written apart, which agree on every clause:

- **C1** held: `ParseStasisStart` SLOWER at g 1.2327 (rounds 1.210, 1.193, 1.298), nothing else
  SLOWER or FASTER.
- **P1** held: 39 judged rows per run, none SLOWER, FASTER or UNCONFIRMED.
- **P2** held: 0 of 195 g outside [0.909, 1.10] (min 0.942, max 1.053); per run, the p95 of
  |ln g| 0.024 to 0.052 against 0.058, the median 0.004 to 0.017 against 0.020.
- **P3** held: R(5)/R(1) 0.980 to 1.003; median |ln r_k| 0.85 to 0.96 times the self-ratio's.
- **P4** held: 36 rows with bytes, every exactly gated one at a difference of 0 in every round.
- **P5** held: 0 structural, 0 blind, the lists equal, every invocation `LaunchCount=2`, and only
  `EnqueueAndConsume1000Events` not judged by an override.
- **P6** held: jobs 75 to 86 min against 105, the in-job phase 62 to 68 min against 80, the
  longest invocation 688 s, none timed out.
- **P7** held: no row with an out-of-band g or an UNCONFIRMED verdict; 6 lone rounds in all, at
  most one per row and run.

So `PERF_ABAB_ENFORCE` is `'true'`. **The two provisional bands** (`Update1000ChannelStates` 35%,
`Create1000Channels` 40%) stay at their width and stop being provisional. Both rows are judged on
time in the job in every run (g 0.995 to 1.007 and 0.998 to 1.024), while the absolute gate in
the same runs read them at 44 to 74 us and 270 to 544 us with the code unchanged: the band measures the
fleet, the comparison measures the code. Narrowed back to 15% and 30%, the EPYC 7763 hosts would
put them at their edge again (in this series Create read +18% and +25% there, Update +8% and
+12%; on 2026-11-11, +29% and +26%). What the width costs:
a drift under 10% per release is judged against the previous tag only, so it can accumulate up to
the band before the absolute gate sees it. They become provisional again if `PERF_ABAB_ENFORCE`
goes back to `'false'` or either row gets `judge_time: false`.

## Current status: ENFORCING

The comparison landed **observing-only**: it printed every delta, wrote the run summary and filed
the breach issue (ADR-0042 D5), but did not fail the job, because a gate whose first act is a false
red gets routed around, then ignored, then deleted.

`PERF_GATE_ENFORCE` is now `'true'` in the job-level `env:` block of
`.github/workflows/perf-regression.yml`. The plan was to flip it after two consecutive clean
scheduled runs, but no observed run was ever free of breaches: most were FASTER-only weeks, and the
one SLOWER week (2026-11-11) included `Create1000Channels`, whose slowdown is an accepted cost
against a time baseline read before it. So the flip came with two changes instead: the
one-sided failure rule above, and the re-baseline of `ChannelManagerBenchmark.Create1000Channels`
recorded in `_create_1000_channels_note`. That single flag arms both `check-perf-baseline.py` and
`report-perf-breach.sh` together.

## Related

- ADR-0042 — D4 (relative gate, fail closed), D5 (durable
  breach artifact), D6 (no CI write-back), D7 (published figures are COHERENCE).
- `docs/claim-registry.md` — which public claim each guard answers for.
- `scripts/tests/test_check_perf_baseline.py`, `scripts/tests/test_report_perf_breach.sh`,
  `scripts/tests/test_check_perf_abab.py`, `scripts/tests/test_run_perf_abab.sh` — the
  guards' own guards, in the always-on required `Coverage Script Tests` job.
