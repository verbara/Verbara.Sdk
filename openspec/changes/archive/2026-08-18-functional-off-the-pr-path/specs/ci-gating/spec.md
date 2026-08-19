# ci-gating — delta for functional-off-the-pr-path

## MODIFIED Requirements

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

## ADDED Requirements

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

## REMOVED Requirements

### Requirement: Bot-authored PRs may skip the representative PR functional matrix

**Reason:** subsumed. The bot-only carve-out was the first recognized case of a diff that does not
need the functional suite to be judged; every `pull_request` event now skips the heavy steps by
default, so a bot PR skips them for the general reason. The step-level-guard constraint the
requirement existed to protect is not lost — it is restated, and strengthened, in "Heavy functional
work is opt-in on a pull request" above.

## Architectural Risk

**Level:** MEDIUM

**Affected:** `main`'s landing gate for Verbara.Sdk. No package, public API, or downstream consumer
(Sdk.Pro, Platform) is touched — this change is entirely CI policy. The exposure is temporal, not
structural: a functional regression is now reported at queue time rather than PR time.

**Mitigation:** the full `[22, 23]` matrix still runs on every `merge_group` entry and
`Functional Tests (Testcontainers) (23)` remains a required context reporting on both events, so no
change lands on `main` without full-matrix validation. The measured base rate for the risk being
realized is zero functional failures across 457 runs (2026-05-06 → 2026-08-18). The `ci:functional`
label restores PR-time feedback on demand for a branch that does touch the surface. The
stranding hazard that this area's two prior incidents (#104/#105) came from is guarded by keeping
the condition at step level, which the requirement above now states as SHALL rather than as a
comment in YAML.
