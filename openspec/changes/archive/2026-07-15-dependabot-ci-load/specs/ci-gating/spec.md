# ci-gating — Delta

## ADDED Requirements

### Requirement: Bot-authored PRs may skip the representative PR functional matrix

Bot-authored pull requests (currently `dependabot[bot]`) MAY skip the representative
`pull_request` functional matrix. The `merge_group` full Asterisk support matrix SHALL still run
for every PR — bot or human — and remains the authoritative landing gate: no change SHALL land on
`main` without the full-matrix `merge_group` validation passing. The skip SHALL be expressed as a
step-level condition on the heavy functional steps (not a job-level `if:`) so the
`functional-tests` job always runs, its matrix expands, and the matrix-suffixed required check-run
name (`Functional Tests (Testcontainers) (23)`) materializes and reports success — leaving no
required check Pending or never-reporting. A job-level skip collapses the matrix into an unsuffixed
`SKIPPED` check run, so the matrix-suffixed required context never reports and the PR sits
`BLOCKED` (ADR-0039 addendum).

#### Scenario: A Dependabot PR skips the representative functional variant

- **GIVEN** a pull request authored by `dependabot[bot]` targeting `main`
- **WHEN** CI runs on the `pull_request` event
- **THEN** the `functional-tests` job still runs and its matrix-suffixed check `Functional Tests (Testcontainers) (23)` reports success in seconds (the two heavy Testcontainers steps skipped), blocking no required check, and the PR is free to enter the merge queue

#### Scenario: The full matrix still gates every bot landing

- **GIVEN** a `dependabot[bot]` PR that entered the merge queue
- **WHEN** the `merge_group` run executes
- **THEN** the full `[22, 23]` Asterisk matrix runs and must pass before the change lands on `main`

#### Scenario: Human PRs still run the representative variant

- **GIVEN** a pull request authored by a human account targeting `main`
- **WHEN** CI runs on the `pull_request` event
- **THEN** the representative functional variant runs as before — the skip applies only to bot-authored PRs
