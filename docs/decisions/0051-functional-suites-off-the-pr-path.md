# ADR-0051: The functional matrix runs in the queue, not on every PR push

- **Status:** Accepted
- **Date:** 2026-08-18
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0038 (CI pipeline slimming — D3 established the representative PR matrix this
  ADR retires), ADR-0039 (Dependabot CI load — its addendum established the step-level guard this
  ADR widens), ADR-0043 (longevity evidence off the PR path — the same principle applied to soak
  evidence), ADR-0009 (three-tier test strategy), verbara-meta/ADR-0003 (CI-gating &
  branch-protection standard), verbara-meta/ADR-0016 (docs-only fast-path gate). Change:
  `functional-off-the-pr-path` (openspec).

## Context

ADR-0038 measured a 23-minute median and moved the Asterisk 22 leg of the functional matrix to the
merge queue. Fourteen months of habit later the number is worse, not better: **the median
`pull_request` validation is 29.0 min** (11 code-PR runs, 2026-08-16 → 2026-08-18), with tail
outliers of **51.4 min** and **170.4 min**. Landing a change costs ≈ **60 min** in series — the PR
run plus the `merge_group` run — and the maintainer opens or updates PRs more than three times a
day.

Measuring the critical path rather than the sum of jobs shows the shape (medians):

| Job | Duration | Starts | Ends |
|---|---|---|---|
| **Functional Tests (Testcontainers) (23)** | **19.7 min** | 9.2 | **29.0** |
| Unit Tests | 9.0 min | 0.2 | 9.2 |
| Analyze (C#) — codeql.yml | 6.2–10.0 min | 0 | ≤10 |
| Pack Warnings Gate | 2.5 min | 0.2 | 2.7 |
| AOT Trim Check | 1.3 min | 0.2 | 1.5 |
| Coverage Ratchet | 0.5 min | 9.2 | 9.7 |
| gate / OpenSpec Validate / Audit Test Asserts / Coverage Script Tests | ≤0.3 min | 0 | ≤0.3 |

Everything except the two heavy suites has finished by minute 10. **The 29 minutes are 9.0 + 19.7,
in series** — and they are in series only because `functional-tests` carries `needs: unit-tests`, an
edge introduced in the very first CI commit (543a2bf0, 2026-03-22) with no recorded rationale.
ADR-0038 slimmed everything around that edge without revisiting it.

Three further facts decide this:

1. **The PR-time functional run has never been the signal that stopped a bad change.** Across
   **457 `ci.yml` runs (2026-05-06 → 2026-08-18)**, 57 failed. The failing job was Unit Tests on 47,
   Coverage Ratchet on 9, Pack Warnings Gate on 4, AOT Trim Check on 3, Coverage Script Tests on 2,
   and **Functional Tests on zero**. Stated honestly: on those 47 Unit-Tests failures the functional
   job was skipped by the `needs` edge and had no chance — but on the **~410 runs where it did
   execute it passed every single time**, for three and a half months.
2. **The cost is the tests, not the containers.** Inside the job, setup and image pre-pull take
   0.4 min; `dotnet test` spends ~3 min building the whole `.slnx` in Debug and probing 30
   non-matching assemblies, and **~16 min is `Verbara.Sdk.FunctionalTests.dll` alone** — 154 tests
   against the Asterisk container, serialized by `MaxCpuCount=1` with container restarts between
   classes. No caching change touches this.
3. **The outliers are runner starvation, not execution.** No workflow declared `concurrency:`, so a
   superseded PR run kept its ~42 runner-minutes to completion — **zero `cancelled` conclusions in
   457 runs.** On a burst day (15 runs on 2026-08-17) the 20-job public pool saturates: run
   31975392598 spent **2 h 22 min** with Unit Tests merely *queued*, and run 32021139519 lost
   20.7 min waiting for a functional runner.

ADR-0039 already carved bot-authored PRs out of these same steps. That precedent read a bot PR as
"a diff that does not need 19.7 min of Asterisk to be judged" — which describes almost every PR,
not only the bot's.

## Decision

### D1 — The functional/Testcontainers steps run on `merge_group` only

The two heavy steps of `functional-tests` (`Pre-pull Docker images…`, `Run functional + integration
tests`) run when `github.event_name == 'merge_group'`, or when the PR carries the **`ci:functional`**
label as an explicit opt-in. This widens ADR-0039's bot-only skip to every `pull_request` event and
retires ADR-0038 D3's representative-PR-matrix arm; D3's queue-side arm is untouched.

The guard stays at **step** level and the `pull_request` matrix stays `[23]`. This is not stylistic:
a false *job*-level `if:` collapses the matrix, GitHub reports a single unsuffixed `SKIPPED` check
run, the matrix-suffixed required context `Functional Tests (Testcontainers) (23)` never reports,
and the PR is stranded `BLOCKED` forever (observed on #104/#105; ADR-0039 addendum). The job and its
matrix therefore always run and report success in seconds, doing no work. The `merge_group` term
MUST lead the expression, because on a queue run every `github.event.pull_request.*` field is empty.

### D2 — `functional-tests` no longer depends on `unit-tests`

`needs: [unit-tests, gate]` becomes `needs: gate`, and the job-level condition simplifies to
`!cancelled()` — still never false, per D1's stranding hazard. The two heavy suites now start
together instead of in series.

### D3 — In-flight `pull_request` runs are superseded

`ci.yml` and `codeql.yml` declare `concurrency` keyed on the PR number, with
`cancel-in-progress` true **for `pull_request` only**. `merge_group` is never cancelled: each queue
entry is the authoritative landing gate. `codeql.yml`'s `push:[main]` and weekly schedule are never
cancelled either — they maintain the default-branch security baseline.

## Consequences

- **PR green drops from ~29 min to ~10 min**; the new critical path is Unit Tests (9.0–11.4) racing
  Analyze (C#) (6–10). **The queue leg drops from 29.7 to ~20.5 min** (D2), so a landing costs
  ≈ 31 min instead of ≈ 60.
- **Nothing lands on `main` unvalidated.** Every landing still runs the full `[22, 23]` matrix in
  the queue, and `(23)` remains a required context reporting on both events. What moves is *when* a
  functional regression is reported: queue time instead of PR time. Measured expected cost of that
  latency: **0 occurrences in 457 runs**. When it does happen the PR loses one queue cycle and pops
  out of the queue red — noisier than a red PR check, but not a landing.
- **A doomed run now burns ~2 extra runner-jobs** (D2 lets functional start before Unit Tests
  fails, on ~10% of runs). D3 more than refunds that: it retires the whole class of superseded runs.
- **The `ci:functional` label is the escape hatch** for a branch that genuinely touches the AMI/ARI
  surface and wants the answer before the queue. It is opt-in on purpose — a default-on heuristic
  over changed paths would have to model which C# edit can move a dialplan test, and getting that
  wrong is silent.
- ADR-0038's addendum still governs: under classic branch protection the `merge_group` full-matrix
  run is **detection**, not automatic hard enforcement. This ADR does not change that, and the
  detection surface is unchanged — the same matrix, on the same event.

## Alternatives considered

- **Shard `Unit Tests` (~9 min) across two runners.** Real (~6–7 min PRs) but it *suffixes a
  plain-named required context*, forcing the same-moment branch-protection reconciliation that
  ADR-0038 had to perform once already (verbara-meta/ADR-0003). Deferred until ~10-min PRs chafe.
- **Point the functional `dotnet test` at the 8 container-backed projects instead of the `.slnx`.**
  Saves ~2–3 min of Debug build and dead assembly probing, but a new `[Trait("Category",
  "Integration")]` in an unlisted project would then silently stop running. Wants a guard script
  first — a spec-drift class this repo already knows how to close.
- **Drop the Asterisk 22 leg from `merge_group`.** Rejected in ADR-0038 and rejected again: that
  deletes coverage rather than moving it.
- **Move the functional suites to a nightly schedule with no queue gate.** Rejected — it converts
  `main`'s gate into after-the-fact detection, exactly what ADR-0043 designed its scheduled train to
  avoid. A nightly full-matrix run on `main` is welcome as an *addition* (image-drift canary), never
  as the replacement.
- **NuGet / build caching.** Measured ≤1 min, almost all of it off the critical path. Not worth the
  cache-invalidation surface.

## Addendum (2026-08-18) — what #199 measured, and the one decision it did not exercise

The change landed as PR #199 (merged `18:53:56Z`). D1 and D2 are now measured rather than predicted;
D3 is not, and this addendum exists so that gap is recorded somewhere tracked rather than only in
the archived change's prose.

**Measured on #199.** The load-bearing risk — the #104/#105 stranding hazard — did not materialize:
`Functional Tests (Testcontainers) (23)` reported `success` in **19s** (and 17s on the second run),
matrix-suffixed and with both heavy steps `skipped`, so the required context materialized exactly as
the step-level guard intended. All nine required contexts reported and the PR reached `mergeable`.
The PR run cost **9m56s** (9m10s on the second), against ~29 min for the same PR shape before. The
`merge_group` leg ran the full `[22, 23]` matrix for real — (22) 18m59s, (23) 20m05s — and finished
in **20m20s** against #198's ~31.5 min on the identical leg. Both matrix legs started at `18:33:18Z`,
the same second as `Unit Tests`: that simultaneity *is* D2, visible in the clock.

**D3 is unexercised.** The `concurrency` block never fired — the two pushes on #199 never overlapped,
so no run was ever superseded, on either `ci.yml` or `codeql.yml`. The predicted saving (retiring the
class of superseded runs) therefore rests on the expression alone. It will confirm or refute itself
on the first branch that gets two pushes inside one run's wall-clock, which needs no ceremony to
observe. The failure mode to watch for is not a missing cancellation but the opposite one:
`cancel-in-progress` leaking to `merge_group`, which would let a later queue entry kill an in-flight
landing gate. **Acceptance:** a superseded `pull_request` run shows `cancelled`, and a `merge_group`
run that overlaps another entry — or whose source branch is pushed again — runs to completion. Until
both are seen, treat D3 as reasoned, not verified.

The `ci:functional` escape hatch is in the same position. The label exists (`#1D76DB`, "Run the
functional/Testcontainers matrix on this PR (ADR-0051 opt-in)") but no PR has wanted it yet, so the
labelled-PR path — the one that has to survive `github.event.pull_request.labels.*.name` resolving
on a `pull_request` event while staying inert on `merge_group` — is reasoned, not verified, for the
same reason. First branch that touches the AMI/ARI surface should use it deliberately and confirm
the heavy steps run.

## Addendum (2026-09-12) — the security baseline D3 protects holds only results this repository can act on

D3 keeps `codeql.yml`'s `push:[main]` and weekly runs uncancellable because they maintain the
default-branch security baseline. On `99162772` that baseline held 1,082 open alerts, and 633 of them
were in code the .NET SDK's own source generators write under `obj/**/generated/` during the build:
613 from `System.Text.Json.SourceGeneration`, 19 from `Microsoft.Extensions.Options.SourceGeneration`
and 1 from `System.Text.RegularExpressions.Generator` (`cs/useless-cast-to-self` 443,
`cs/useless-upcast` 184, `cs/missed-ternary-operator` 5, `cs/nested-if-statements` 1). No change in
this repository can reach that code.

**Decision.** `Analyze (C#)` analyses, filters, then uploads. `analyze` runs with
`upload: failure-only` and only writes its SARIF; `upload-sarif` sends what
`scripts/ci/filter-codeql-sarif.sh` leaves under the unchanged category `/language:csharp`, so every
other alert keeps its history and the generator ones close as fixed. A CodeQL config was not an
option: `paths` and `paths-ignore` are not honoured for a compiled language analysed from a traced
build, which this job is. The filter removes a result only when both of these hold:

1. *Location.* In the uri of its primary location, read as `/`-separated segments, the first `obj`
   segment is followed, at the first `generated` segment after it, by a folder whose name starts with
   `System.` or `Microsoft.` and then at least one more segment. Only that first `obj` and that first
   `generated` count, so no hint name of this repository's own generator
   (`Verbara.Sdk.Ami.SourceGenerators`) can bring its output under the rule, whatever subfolders it
   uses.
2. *Rule.* Its rule resolves in its run's `tool` and is not security-relevant — no `security` in
   `properties.tags` and no `security-severity` key in `properties`. An index (`ruleIndex` or
   `rule.index`) resolves into `tool.driver.rules`, or into
   `tool.extensions[rule.toolComponent.index].rules` when the reference names a component by index,
   and must land on a rule whose `id` matches the result's rule id when the result gives one; without
   one, `ruleId` or `rule.id` resolves to every rule with exactly that `id` in `tool.driver.rules` and
   every `tool.extensions[].rules`, and one security-relevant match is enough. A rule that does not
   resolve (nothing found, contradicting or wrong-typed references, a wrong-typed `tool` or component),
   or whose `properties` or `tags` have the wrong type, keeps its result just as a security-relevant
   rule does. Each result the location rule matches but this check keeps gets one `::warning::` naming
   its rule id, its uri and the reason.

**What holds it in place.**

- *Never a security result.* "No change here can reach that code" holds for a note about the
  generated code itself. It does not hold for a data-flow alert whose sink lies in generator output
  while its source, and its fix, lie in this repository — a `[LoggerMessage]` method expands, through
  a `Microsoft.*` generator, into an `ILogger` call. On `99162772` no result the location rule matches
  has a security rule (55 of the 164 rules carry the `security` tag, 53 a `security-severity`, and all
  633 results cite one of four maintainability rules), so the check removes nothing more and nothing
  less there today.
- *Fail closed.* Input the filter cannot read as one SARIF 2.1.0 log exits 2 and leaves no output, so
  the job goes red and nothing is uploaded: the baseline is never replaced by an unfiltered or partial
  log. When analysis itself fails, `failure-only` keeps what `always` did — the Action's post step
  uploads the failed-run diagnostics.
- *One category, one upload.* The Action refuses a second upload for one category in a job, and a
  different category would strand every existing alert open under the old one.
  `scripts/tests/test_filter_codeql_sarif.sh`, in the always-run `Coverage Script Tests` job, asserts
  that wiring, every rule in both directions, and that no project here is named `System.*` or
  `Microsoft.*` — the rule matches the generator's assembly name, so such a project's output would
  be hidden.
- The required context is still `Analyze (C#)`, so no branch-protection edit is needed.

**Measured offline, not yet on GitHub.** On the SARIF of main's analysis of `99162772` (CodeQL 2.27.0)
the filter keeps 449 of 1,082 results and prints no warning; an independent regex over the same uris
selects the same 633, every other field of the log is unchanged, and no kept result has an `obj` or
`generated` segment. Anchoring the location rule and adding the rule check left that output
byte-identical. The harness passes 445 checks. Run against scratch copies of the filter, it fails 102
on the first version of the rule (no anchor, no rule check); 13 with the generator-name condition
deleted and 4 with `obj` matched as a substring; 2 when any `generated` after any `obj` counts and 1
when the first `generated` after each `obj` does; 15 without the `security` tag and 6 without
`security-severity`; 65 when a rule that does not resolve is removable and 6 when one with
wrong-typed metadata is; 35 without the warnings. That the 633 close as fixed, and nothing else
does, is reasoned until the first `push:[main]` run after the merge shows it.
