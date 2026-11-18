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
and `Create1000Channels`' are therefore **provisional** and wider than the rest (35% and 40%);
`_channel_manager_widened_bands_note` in `baseline.json` carries the runs, the ratios and what the
widening stops the gate from seeing. A comparison of a reference build against this one inside the
same job, on the same host, is what replaces them: it runs now, observing, and is described under
[In-job comparison against the reference tag](#in-job-comparison-against-the-reference-tag-observing).

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

Banded on bytes only, time not yet banded: `ChannelManagerBenchmark.Update1000ChannelStates_OneSubscriber`
(`_channel_manager_one_subscriber_note`), `ChannelManagerBenchmark.Create1000Channels_TwoLegCalls`,
the two deterministic `AsyncEventPumpBenchmark` rows and every `QueueManagerBenchmark` and
`MeetMeManagerBenchmark` row. Their time bands come from two weekly runs.

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

## In-job comparison against the reference tag (observing)

No band that covers four CPU models sees a 10-20% time regression: the SLOWER bar sits +29% to +80%
over a row's usual reading on most hosts. So, after the absolute gate, the weekly job measures the
code against a **reference build in the same job, on the same machine**, and judges time by the
ratio. `scripts/ci/run-perf-abab.sh` runs it and `scripts/ci/check-perf-abab.py` judges it; both
read the `abab` block of `baseline.json`.

- **The reference** is `abab.reference_tag`, built from the tag's own tree (its `src/` and its own
  benchmark project) in a worktree beside HEAD, with the same pinned SDK, the same ISA pin and the
  same command line. It is declared, not discovered: it moves by PR at a release, like
  `environment`. When a newer release tag exists the report carries a notice, never a failure.
- **The order** is `abab.order`, `R H H R`: one BenchmarkDotNet invocation per position with every
  class of `sources` in it (one generated-project build each). Round *k* pairs the *k*-th R with the
  *k*-th H, so round 1 is (position 1, position 2) and round 2 is (position 4, position 3). A linear
  drift over the run lands on the two rounds with opposite signs, so drift alone cannot make every
  round agree; a first-invocation penalty lands on R and pushes toward FASTER, which never fails.
- **The statistic.** Per case and round, r = mean(H) / mean(R). **SLOWER** when every round is over
  `slower_ratio` (1.10), **FASTER** when every round is under `faster_ratio` (0.909), **UNCONFIRMED**
  when the rounds disagree, ok otherwise. The point estimate is the geometric mean of the rounds.
  Each arm's last-over-first ratio is shown too: it is the run's own A/A noise.
- **The floor.** A case whose mean is under `min_mean_ns` (2 ns) in any invocation is shown and not
  judged: the ratio of a few cycles measures BenchmarkDotNet's overhead subtraction (the
  `_dispatch_1_observer_note` lesson).
- **Bytes in the job.** Both arms run on the same core count, so a bytes difference between them is
  the code. A case with a `bytes` entry in `benchmarks` is **MORE-BYTES** when every round's
  difference is over its `bytes_tolerance` and **FEWER-BYTES** when every round's is under minus it;
  a case with no `bytes` entry shows its difference and is not judged. An exactly gated case whose
  bytes differ between the rounds of one arm is reported as a bytes anomaly.
- **Never dropped.** Each arm's `--list flat` output is what that tree declares. A method in HEAD's
  list only is reported as **HEAD only**, one in the reference's only as **reference only**, a
  parameter case one arm lacks as **case set differs**; none is compared and none fails. A case its
  arm declares that some invocation did not measure, a class with no report, a mean that is not a
  finite number, a timed-out invocation, or invocations that disagree on SDK, runtime, core count,
  JIT ISA, BenchmarkDotNet version or processor are **structural** failures.
- **A changed benchmark.** When a compared class's own `.cs` file differs between the tag and HEAD
  its rows are shown and not judged: the ratio would include the benchmark's change. Any other
  change under the benchmark project (except this file, `baseline.json`, `performance-record.json`
  and `README.md`) is a notice. When no compiled file differs at all, the report says the run
  measures the comparison's own noise.
- **Overrides and accepted costs.** `abab.rows["<FullName>"]` may set `slower_ratio`,
  `faster_ratio` or `judge_time: false`, always with a `reason`. `abab.accepted["<FullName>"]`
  records a cost a PR accepted, as `{ "ratio", "relative_to", "pr", "reason" }`; both bars are then
  centred on that ratio. `relative_to` must name `reference_tag`, or the comparator refuses the
  baseline (exit 2): an accepted ratio means nothing against another reference, so moving the tag
  forces its accepted entries out.
- **Moving the reference tag** at a release is a PR that changes `abab.reference_tag`, removes the
  `accepted` entries measured against the old tag, and says so.
- **The switch.** `PERF_ABAB_ENFORCE` in the job `env:` of `.github/workflows/perf-regression.yml`
  is `'false'`: the comparison writes its section of the run summary, `perf-abab-report.md` and
  `perf-abab.json`, and cannot fail the job, even on exit 2. It is independent of
  `PERF_GATE_ENFORCE`, and its result does not file the breach issue. It is flipped only after five
  consecutive runs met a prediction written before them. Flipped, SLOWER, MORE-BYTES and its
  structural failures fail the job.

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
