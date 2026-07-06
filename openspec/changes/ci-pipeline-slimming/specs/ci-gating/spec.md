# ci-gating — Delta

## ADDED Requirements

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
