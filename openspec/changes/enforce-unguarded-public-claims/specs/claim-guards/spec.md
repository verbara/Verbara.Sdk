# claim-guards — Delta

## ADDED Requirements

### Requirement: Every quantitative public claim declares a guard class

Every quantitative claim in a living public document SHALL declare exactly one class, and a
quantitative claim with no declared class MUST NOT ship. The classes are exactly four: **ENFORCING**
(an executable gate fails when reality diverges from the claim), **COHERENCE** (a per-PR check that
the published number equals a committed record of the measurement it reports), **ATTRIBUTED**
(the number is a third party's published measurement, cited as theirs and pinned to the artifact it
describes), and **EVIDENCE** (a dated record of a first-party measurement of something outside this
repo's control, carrying no guard obligation — see ADR-0042 D1a). Living public documents are tracked
files a reader consumes as current — `README.md`, `CONTRIBUTING.md`, `docs/README-technical.md`,
`docs/README-commercial.md`, package and example READMEs, `docs/guides/` in full, and the
`<Description>` values in `src/*/*.csproj`, which are not Markdown but are published verbatim on
nuget.org and are read as current there. Dated
`CHANGELOG` history, `openspec/changes/archive/`, `docs/decisions/`, `docs/specs/`, `docs/research/`
and `docs/plans/{completed,archived}/` are excluded as period-correct records and SHALL be left
verbatim, matching the exclusion set the `docs-brand-consistency` capability already applies. The
class declarations SHALL live in a single committed registry, so "is this claim guarded?" is
answerable by reading one file rather than by searching the test projects, the canary and the
workflows.

A per-PR guard SHALL be reachable on a pull request that touches only the document it guards. A
document whose claims carry a COHERENCE or ENFORCING class and that the docs-only CI fast path
classifies as skippable has a decorative guard, not a real one, and the fast path's carve-out SHALL
name it.

#### Scenario: A new number added to the README without a class fails review

- **GIVEN** a pull request that adds a throughput, latency, accuracy or count figure to `README.md`
- **WHEN** the claim registry is not updated in the same pull request to declare that claim's guard class
- **THEN** the change is a review defect and the claim does not ship

#### Scenario: A guard the docs-only fast path skips is not a guard

- **GIVEN** a living public document whose figures declare COHERENCE, guarded by a test on the `Unit Tests` job
- **WHEN** a pull request changes one of those figures and touches no other file
- **THEN** the fast path MUST NOT classify that pull request as docs-only, because a guard that does not run on the change that breaks it reports success for the one case it exists to catch

#### Scenario: An EVIDENCE disposition does not excuse a gateable number

- **GIVEN** a document whose vendor-capture figures are classed EVIDENCE
- **AND** a figure in that same document counting this repository's own contents
- **WHEN** the classes are applied
- **THEN** the second figure is ENFORCING, because the class is declared per claim and not inherited from the document it sits in

#### Scenario: Historical numbers in the CHANGELOG are not claims

- **GIVEN** a dated `CHANGELOG.md` entry stating the numbers as they were measured at that release
- **WHEN** the guard classes are applied
- **THEN** the entry is out of scope, is not required to declare a class, and its wording is left verbatim as a period-correct record

### Requirement: Expensive claim measurement never runs per-PR

Measurement requiring statistically meaningful timing — BenchmarkDotNet suites and model inference
over a corpus — SHALL run only on the existing scheduled trigger plus `workflow_dispatch`. It MUST
NOT be added to the `pull_request` or `merge_group` events, and MUST NOT introduce a new required
check. A COHERENCE guard SHALL be implemented as an ordinary unit test inside a project already
covered by the default unit filter, so it rides the existing `Unit Tests` job and adds no check-run
name. This preserves the PR wall-clock budget ADR-0038 established and the bot-PR economics ADR-0039
recorded, and it keeps the never-reporting-required-context failure mode
(verbara-meta/ADR-0003, reconciled by hand in ADR-0038) structurally out of reach.

#### Scenario: The performance gate does not lengthen a pull request

- **GIVEN** a pull request touching AMI parsing code
- **WHEN** CI runs on the `pull_request` event
- **THEN** no BenchmarkDotNet suite executes, no new job appears, and the only claim-guard cost is the sub-second coherence assertions inside the existing `Unit Tests` job

#### Scenario: The merge queue is not lengthened either

- **GIVEN** a change entering the merge queue
- **WHEN** the `merge_group` run executes
- **THEN** the claim-guard workflow does not trigger — the queue is serial, so a benchmark job there would be paid per landing with no parallelism to absorb it

### Requirement: The performance claim is gated by relative regression against a committed baseline

The scheduled performance workflow SHALL fail when a benchmark's measured mean exceeds its committed
baseline entry in `Tests/Verbara.Sdk.Benchmarks/baseline.json` by more than that benchmark's declared
tolerance band. The comparison SHALL be relative to a hosted-runner baseline and MUST NOT compare
against the workstation figures published in `README.md`, which were measured on dedicated hardware a
shared runner cannot reproduce. The gate SHALL fail closed: a benchmark whose result is missing,
empty or unparseable counts as a breach, because each benchmark step is suffixed `|| true` and a
step that did not run would otherwise be indistinguishable from a passing one. A breach SHALL produce
a durable, assignable record that outlives the run — an issue naming the benchmark, its baseline, the
observed value and the band — in addition to failing the job; a red weekly run that notifies nobody
is observational under another name. Every `README.md` Performance row claiming ENFORCING SHALL have
a corresponding benchmark filter in the workflow and a corresponding baseline entry.

#### Scenario: A throughput regression fails the weekly run

- **GIVEN** a committed baseline entry for `AmiProtocolReaderBenchmark` with a declared tolerance band
- **WHEN** the scheduled run measures a mean above the baseline plus its band
- **THEN** the workflow job fails and an issue is filed or updated naming the benchmark, the baseline value, the observed value and the band

#### Scenario: A benchmark that fails to run is a breach, not a pass

- **GIVEN** a benchmark filter that matches no type, or a run that crashes and is swallowed by `|| true`
- **WHEN** the comparison step evaluates the collected results
- **THEN** the missing result is treated as a breach and the job fails, rather than reporting green on absent evidence

#### Scenario: The gate does not block anyone's pull request

- **GIVEN** a breached baseline on the weekly scheduled run
- **WHEN** an unrelated pull request is opened and enters the merge queue
- **THEN** the pull request is unaffected — the performance workflow contributes no required check and cannot block a landing

### Requirement: Benchmark baselines move only by human-authored commit

`Tests/Verbara.Sdk.Benchmarks/baseline.json` SHALL be updated only by a reviewed, human-authored
commit. CI MUST NOT write back to it under any trigger. A baseline-changing pull request SHALL state
which benchmark moved, in which direction, by how much, and why, so a legitimate speed-up, a runner
fleet change and a masked regression are distinguishable in review. This mirrors the coverage floor's
manual-ratchet semantics (ADR-0038 D2, verbara-meta/ADR-0003): a threshold that updates itself
ratchets away from the thing it was meant to protect.

#### Scenario: A green scheduled run does not rewrite the baseline

- **GIVEN** a scheduled run measuring means faster than the committed baseline
- **WHEN** the run completes successfully
- **THEN** `baseline.json` is unchanged in the repository and the improvement is adopted only by a later human-authored pull request

#### Scenario: A re-baseline states its cause

- **GIVEN** a pull request raising a benchmark's baseline entry
- **WHEN** it is reviewed
- **THEN** it names the benchmark, the old and new values, and the cause — an accepted trade-off, a runner change, or an accepted regression — and is rejected if it does not

### Requirement: Published workstation figures are bound to a committed measurement record

Every absolute performance figure published in `README.md` SHALL correspond to an entry in a
committed measurement record carrying the machine, runtime version, BenchmarkDotNet version and
measurement date. A COHERENCE test SHALL assert that the published figures and the record agree, so a
number edited in one place and not the other fails a pull request. This guard is about
correspondence between document and record; regression detection is the scheduled gate's job, and the
two failures SHALL remain separately detectable.

#### Scenario: Editing the README number alone fails the build

- **GIVEN** a pull request that changes a Performance-table figure in `README.md`
- **WHEN** the committed measurement record is not updated to match
- **THEN** the coherence test fails in the existing `Unit Tests` job

#### Scenario: The record states the machine the number came from

- **GIVEN** the measurement record backing the Performance table
- **WHEN** a reader asks what hardware produced a published figure
- **THEN** the record names the machine, the runtime version, the BenchmarkDotNet version and the date, and the README states the same machine alongside the table

### Requirement: A claim that cannot be measured in-repo is attributed and pinned, or deleted

A quantitative claim this repository cannot measure SHALL be either ATTRIBUTED or removed, and MUST
NOT be left stated in first-party voice. ATTRIBUTED obliges three things together: a citation to the
third party's published measurement; wording that reads as the third party's result rather than as a
first-party benchmark; and a pin binding the citation to the artifact actually shipped. For the
bundled turn-detection model the pin SHALL be a content-hash assertion over the embedded
`smart-turn-v3.2-cpu.onnx`, so replacing the model breaks the build instead of silently orphaning the
cited accuracy figure. The citation, the claim wording, the package `<Description>` and the shipped
resource filename SHALL all name the same model version — the present `smart-turn-v3` link against a
`smart-turn-v3.2` resource is the drift this requirement forbids. Where a first-party gate is
deferred, the deferral SHALL be recorded with its specific blocker and the condition that would
unblock it; the claim MUST NOT be presented as first-party until that gate exists.

#### Scenario: The accuracy figure reads as upstream's, not ours

- **WHEN** `README.md` states the turn detector's English accuracy
- **THEN** the figure is presented as the upstream model card's published measurement, with a citation to the model card for the exact model version shipped, and is not worded as a Verbara benchmark result

#### Scenario: Swapping the ONNX breaks the build

- **GIVEN** the content-hash assertion over the embedded turn-detection model
- **WHEN** the Git-LFS-tracked `.onnx` is replaced with a different model
- **THEN** the assertion fails in the existing `Unit Tests` job, forcing the citation and the accuracy claim to be revisited in the same pull request

#### Scenario: The deferred accuracy gate keeps its blocker on the record

- **GIVEN** no identified corpus of labelled turn-boundary speech whose licence permits redistribution from a public MIT repository
- **WHEN** the first-party precision/recall gate is deferred
- **THEN** the deferral is recorded with that blocker and its unblocking condition, and the accuracy figure remains ATTRIBUTED rather than being restated as a Verbara measurement

### Requirement: The turn-detection inference-latency claim is measured

The `~12 ms CPU inference` figure published for `Verbara.Sdk.VoiceAi.TurnDetection` SHALL be class
ENFORCING and SHALL be measured by a benchmark in `Tests/Verbara.Sdk.Benchmarks/` with a
corresponding `baseline.json` entry, on the same scheduled cadence and under the same
relative-regression, fail-closed and human-authored-baseline rules as every other ENFORCING claim.
Unlike the accuracy figure, this one measures code this repository owns — the mel-spectrogram
front-end and the ONNX session — and is therefore not eligible for ATTRIBUTED.

#### Scenario: The benchmark project reaches the turn detector

- **GIVEN** `Tests/Verbara.Sdk.Benchmarks/` today carries no project reference to `Verbara.Sdk.VoiceAi.TurnDetection`
- **WHEN** the inference-latency benchmark is added
- **THEN** the reference is added, the benchmark appears as a filter in the scheduled workflow, and a baseline entry with a declared tolerance band accompanies it

#### Scenario: Inference latency regressing is caught weekly

- **GIVEN** a change to the mel-spectrogram front-end that doubles per-frame cost
- **WHEN** the next scheduled performance run executes
- **THEN** the turn-detection benchmark breaches its band, the job fails, and an issue records the regression
