# ci-gating — Delta

## ADDED Requirements

### Requirement: Bot-authored PRs may skip the representative PR functional matrix

Bot-authored pull requests (currently `dependabot[bot]`) MAY skip the representative
`pull_request` functional matrix. The `merge_group` full Asterisk support matrix SHALL still run
for every PR — bot or human — and remains the authoritative landing gate: no change SHALL land on
`main` without the full-matrix `merge_group` validation passing. Skipping the representative
variant SHALL be expressed as a job-level condition so the skipped job reports skipped=success and
does not leave any required check Pending.

#### Scenario: A Dependabot PR skips the representative functional variant

- **GIVEN** a pull request authored by `dependabot[bot]` targeting `main`
- **WHEN** CI runs on the `pull_request` event
- **THEN** the representative functional job is skipped (reported as skipped=success, blocking no required check), and the PR is free to enter the merge queue

#### Scenario: The full matrix still gates every bot landing

- **GIVEN** a `dependabot[bot]` PR that entered the merge queue
- **WHEN** the `merge_group` run executes
- **THEN** the full `[22, 23]` Asterisk matrix runs and must pass before the change lands on `main`

#### Scenario: Human PRs still run the representative variant

- **GIVEN** a pull request authored by a human account targeting `main`
- **WHEN** CI runs on the `pull_request` event
- **THEN** the representative functional variant runs as before — the skip applies only to bot-authored PRs
