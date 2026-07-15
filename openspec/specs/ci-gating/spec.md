# ci-gating Specification

## Purpose
Event-scoped CI gating for Sdk: which validation runs on which event (`pull_request` vs
`merge_group`) and how coverage is gated exactly once per run. The merge queue is the authoritative
full-Asterisk-matrix gate — the `pull_request` run trades full-matrix wall-clock for fast
representative feedback, and coverage is collected a single time and consumed (not re-run) by the
`Coverage Ratchet` required check. Recorded by ADR-0038 (D2 single-collection coverage, D3
representative PR matrix / full queue matrix) and bound to the ecosystem CI-gating &
branch-protection standard (verbara-meta/ADR-0003 — coverage ratchet, `merge_group` triggers,
required-check reconciliation). Operational note (ADR-0038 addendum, 2026-07-15): under this repo's
classic branch protection + merge queue, the `merge_group` full-matrix run is queue-validated as
**detection** (a version-specific regression surfaces as a visible red on `main`'s queue run), not
as an automatic hard enforcement gate — a true hard gate would require an event-scoped required
check.

## Requirements
### Requirement: The merge queue validates the full Asterisk support matrix

The `merge_group` CI run SHALL execute the functional-test suite against every supported
Asterisk version (currently 22 and 23). The `pull_request` run SHALL execute at least one
representative version. No change SHALL land on `main` without the full-matrix `merge_group`
validation passing.

#### Scenario: PR run gives fast representative feedback

- **GIVEN** a pull request targeting `main`
- **WHEN** CI runs on the `pull_request` event
- **THEN** functional tests execute against the representative Asterisk version only, and the PR receives feedback without paying the full-matrix wall-clock

#### Scenario: A version-specific regression is caught in the queue

- **GIVEN** a change that regresses only on Asterisk 22
- **WHEN** the PR enters the merge queue and the `merge_group` run executes the full matrix
- **THEN** the queue run fails and the change does not land on `main`

### Requirement: Coverage is collected once per validation run

The unit-test suite SHALL execute at most once per CI validation event. The coverage-floor gate
SHALL evaluate the coverage artifact produced by that single execution; it MUST NOT re-build or
re-run the suite. The committed floor and its manual-ratchet semantics are unchanged.

#### Scenario: The ratchet consumes the unit run's artifact

- **GIVEN** a CI validation where the unit-test job has completed with coverage collection
- **WHEN** the Coverage Ratchet job evaluates the floor
- **THEN** it downloads the coverage artifact, merges the report, and gates against the committed floor without a second unit-suite execution

