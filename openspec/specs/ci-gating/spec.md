# ci-gating Specification

## Purpose
Event-scoped CI gating for Sdk: which validation runs on which event (`pull_request` vs
`merge_group`) and how coverage is gated exactly once per run. The merge queue is the authoritative
full-Asterisk-matrix gate; coverage is collected a single time and consumed (not re-run) by the
`Coverage Ratchet` required check. Recorded by ADR-0038 (D2 single-collection coverage, D3 full
queue matrix) and ADR-0051, which retired ADR-0038 D3's representative-PR-matrix arm: the
`pull_request` run no longer executes the functional suite at all by default. The `functional-tests`
job and its matrix still run on a PR — only the heavy Docker/Testcontainers steps are skipped — so
the matrix-suffixed required contexts materialize and report; skipping at job level instead would
collapse the matrix and strand the PR `BLOCKED` (#104/#105). The `ci:functional` label restores
PR-time functional feedback on demand. Bound to the ecosystem CI-gating & branch-protection standard
(verbara-meta/ADR-0003 — coverage ratchet, `merge_group` triggers, required-check reconciliation).
Operational note (ADR-0038 addendum, 2026-07-15, unchanged by ADR-0051): under this repo's classic
branch protection + merge queue, the `merge_group` full-matrix run is queue-validated as
**detection** (a version-specific regression surfaces as a visible red on `main`'s queue run), not
as an automatic hard enforcement gate — a true hard gate would require an event-scoped required
check.
## Requirements
### Requirement: The merge queue validates the full Asterisk support matrix

The `merge_group` CI run SHALL execute the functional-test suite against every supported Asterisk
version (currently 22 and 23), and no change SHALL land on `main` without that full-matrix
validation passing. The `pull_request` run SHALL NOT execute the functional suite by default; the
`functional-tests` job and its matrix SHALL still run so the matrix-suffixed required check-run name
materializes and reports, with only the heavy Docker/Testcontainers steps skipped.

#### Scenario: PR run gives fast representative feedback

- **GIVEN** a pull request targeting `main` that carries no `ci:functional` label
- **WHEN** CI runs on the `pull_request` event
- **THEN** feedback comes from the unit, analysis and packaging lanes; the `functional-tests` job still runs so its matrix expands and `Functional Tests (Testcontainers) (23)` reports success in seconds with the two heavy steps skipped; and the functional answer comes from the queue run instead

#### Scenario: A version-specific regression is caught in the queue

- **GIVEN** a change that regresses only on Asterisk 22
- **WHEN** the PR enters the merge queue and the `merge_group` run executes the full matrix
- **THEN** the queue run fails and the change does not land on `main`

#### Scenario: A functional regression on the representative version is caught in the queue

- **GIVEN** a change that breaks a functional test on Asterisk 23
- **WHEN** the PR goes green and enters the merge queue
- **THEN** the `merge_group` run fails, the change does not land on `main`, and the PR leaves the queue red

### Requirement: Coverage is collected once per validation run

The unit-test suite SHALL execute at most once per CI validation event. The coverage-floor gate
SHALL evaluate the coverage artifact produced by that single execution; it MUST NOT re-build or
re-run the suite. The committed floor and its manual-ratchet semantics are unchanged.

#### Scenario: The ratchet consumes the unit run's artifact

- **GIVEN** a CI validation where the unit-test job has completed with coverage collection
- **WHEN** the Coverage Ratchet job evaluates the floor
- **THEN** it downloads the coverage artifact, merges the report, and gates against the committed floor without a second unit-suite execution

### Requirement: Heavy functional work is opt-in on a pull request

The heavy functional steps SHALL run on a `pull_request` event when, and only when, the pull request
carries the `ci:functional` label. The condition SHALL be expressed at **step** level, never as a
job-level `if:`, and the `merge_group` term SHALL lead the expression — on a queue run every
`github.event.pull_request.*` field is empty, so a leading term reading one interrogates an object
that is not there. A job-level skip collapses the matrix into a single unsuffixed `SKIPPED` check
run, the matrix-suffixed required context never reports, and the pull request sits `BLOCKED`
forever (#104/#105, ADR-0039 addendum).

#### Scenario: A branch that touches the AMI surface opts in

- **GIVEN** a pull request labelled `ci:functional`
- **WHEN** CI runs on the `pull_request` event
- **THEN** the pre-pull and Testcontainers steps execute in full and the PR pays the functional wall-clock deliberately

#### Scenario: A docs-only diff skips the heavy steps on both events

- **GIVEN** a docs-only diff as classified by the docs-only gate
- **WHEN** CI runs on either `pull_request` or `merge_group`
- **THEN** the heavy steps are skipped and every required context still reports

### Requirement: The functional job does not wait for the unit job

The `functional-tests` job SHALL NOT declare a `needs:` edge on `unit-tests`. The two heaviest jobs
SHALL be free to start in the same instant, so a validation's wall-clock is the longer of the two
rather than their sum.

#### Scenario: The queue leg runs both heavy suites concurrently

- **GIVEN** a `merge_group` run
- **WHEN** the docs-only gate completes
- **THEN** `unit-tests` and `functional-tests` both start, and the run ends at the slower of the two rather than at their sum

#### Scenario: A failing unit suite no longer strands the functional matrix

- **GIVEN** a run whose `unit-tests` job fails
- **WHEN** `functional-tests` has already started
- **THEN** the run reports both results independently and the matrix-suffixed required context still reports

### Requirement: A superseded pull-request run is cancelled

`ci.yml` and `codeql.yml` SHALL declare a `concurrency` group keyed on the pull-request number, with
`cancel-in-progress` enabled for the `pull_request` event only. `merge_group` runs SHALL NEVER be
cancelled, because each queue entry is the authoritative landing gate. `codeql.yml`'s `push:[main]`
and scheduled runs SHALL NEVER be cancelled either, because they maintain the default-branch
security baseline.

#### Scenario: A second push supersedes the first run

- **GIVEN** a pull request whose CI run is in flight
- **WHEN** the branch is pushed again
- **THEN** the in-flight run is cancelled and its runner slots return to the pool

#### Scenario: A queue entry is never cancelled by a later one

- **GIVEN** a `merge_group` run in flight
- **WHEN** another entry enters the queue, or the source pull request is pushed again
- **THEN** the in-flight `merge_group` run continues to completion

