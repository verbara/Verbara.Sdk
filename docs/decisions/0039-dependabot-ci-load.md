# ADR-0039: Dependabot CI load — bot PRs skip the representative functional matrix + dependency grouping

- **Status:** Accepted
- **Date:** 2026-07-15
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0038 (CI pipeline slimming — single-collection coverage + representative PR matrix; this is its direct follow-up), verbara-meta/ADR-0003 (CI-gating & branch-protection standard — `merge_group` triggers, required-check contexts, the never-reporting-context failure mode). Change: `dependabot-ci-load` (openspec). Evidence: verbara-meta `docs/research/2026-07-15-dependabot-ci-load.md` (measured 2026-07-15, live `gh` API).

## Context

Dependabot produced **31 PRs in Sdk over the last 30 days**; 23 merged via automerge + merge
queue, so each merged PR paid a `pull_request` run **and** a `merge_group` run. Post-ADR-0038 each
merged Dependabot PR costs ≈ 90 compute-min (~65% of it the functional tests) → ≈ 2,343
compute-min/month. Sdk is public, so GitHub-hosted standard-runner minutes are **free** — the
binding costs are **wall-clock latency** (~47 min of runner occupancy per merged PR),
**merge-queue serialization on batch days** (8 `merge_group` runs on 2026-06-29), and **7 PRs that
needed 2–3 queue attempts** in the window.

ADR-0038 slimmed *what every PR runs* (representative PR matrix, single-collection coverage). This
ADR slims *how much a bot-authored PR has to run to land*, and cuts the raw count of bot PRs at the
source. Both are durable CI-policy questions (which authors run which validation on which event;
how many PRs Dependabot opens) that outlive any single archived change, so they are recorded here.

## Decision

### D1 — Bot-authored PRs skip the representative functional matrix on `pull_request`

`.github/workflows/ci.yml`'s `functional-tests` job gains a job-level condition, exactly:

```yaml
if: github.event_name == 'merge_group' || github.event.pull_request.user.login != 'dependabot[bot]'
```

An automerged bot PR loses **zero** landing validation: the merge queue's full `[22, 23]` matrix
still runs on `merge_group` and remains the authoritative landing gate. Only the redundant
representative PR-time functional run is dropped for `dependabot[bot]`-authored PRs. Human PRs are
untouched. Three points fix the exact shape of the condition:

- **`merge_group` term first.** The merge queue's actor is the *enqueuer*, not the bot, so on a
  `merge_group` event `github.event.pull_request.user.login` is **not** the bot login (the
  `pull_request` context is absent/different on that event). Leading with
  `github.event_name == 'merge_group'` guarantees the full `[22, 23]` queue matrix always runs and
  keeps its role as the landing gate regardless of who authored the original PR.
- **`github.event.pull_request.user.login`, not `github.actor`.** GitHub-recommended. `github.actor`
  flips to the human who re-runs a job, so a human re-run of a bot PR would wrongly re-enable the
  matrix. The PR-author login is stable across re-runs.
- **Job-level `if:`, not whole-workflow path/branch filtering.** A job skipped by an `if:` reports
  **skipped=success** and does **not** block a required check. Whole-workflow path/branch filtering
  instead leaves the required check **Pending forever** — the documented trap
  (docs.github.com, "troubleshooting required status checks"; the same never-reporting-context
  failure mode verbara-meta/ADR-0003 codifies and ADR-0038 had to reconcile). Because the skip
  reports skipped=success, **no branch-protection edit is needed** — unlike ADR-0038, which had to
  drop `Functional Tests (Testcontainers) (22)` from the required set.

### D2 — Dependabot `groups` to cut PR count

`.github/dependabot.yml` groups compatible bumps so several updates land as one PR: a group for the
`Microsoft.Extensions.*`/runtime packages and a group for `github-actions` bumps, both scoped to
**minor + patch** so majors stay **ungrouped/individual** (a major is a real review event and must
not be hidden inside a group). This follows GitHub's own "optimizing PR creation for version
updates" guidance. The repo already carried nuget groups from an earlier pass (test-stack,
analyzers, a `nuget-security` security-updates collapse); D2 scopes the `Microsoft.Extensions.*`
grouping to minor+patch and adds the previously-missing `github-actions` group — the other existing
groups are preserved unchanged.

### D3 — Alternatives rejected / deferred

- **Functional tests only at release — REJECTED.** Industry anti-pattern: regressions batch up
  between releases and bisection cost explodes. The Asterisk project itself runs a gate-subset on
  PRs plus a nightly-full suite, never release-only. Keeping the full `[22, 23]` matrix on the
  merge queue (the landing gate) preserves per-landing validation while cutting only the redundant
  PR-time bot run — the surgical move, not the blunt one.
- **Self-hosted / ARC runners on the operator's Talos lab — DEFERRED** with a named trigger:
  revisit **only** for a genuine capability need (e.g. R5.5 Phase B-LK load testing, which needs
  hardware the hosted runners cannot provide) **or** a real concurrency wall — **not** to save the
  already-free public-repo minutes. Two hard reasons to defer: GitHub officially **discourages
  self-hosted runners on public repos** (a fork PR can run arbitrary code on the runner), and a
  lab-offline runner would **hold the merge queue hostage** — a `merge_group` check that never
  starts blocks every landing until the 60-min `check_response_timeout` expires, per PR.

## Consequences

- **Positive:** An automerged Dependabot PR stops paying the ~65% functional slice at PR time —
  roughly halving its runner occupancy — with **zero** loss of landing validation (the queue full
  matrix is unchanged and still gates `main`).
- **Positive:** Grouping compatible minor/patch bumps cuts the raw Dependabot PR count, which is
  the driver of merge-queue serialization on batch days.
- **Positive:** **No branch-protection edit.** A job skipped by `if:` reports skipped=success, so
  every required check name still reports and stays green — no required context is dropped, and the
  queue can never hang on a never-reporting context (contrast ADR-0038's required-check
  reconciliation).
- **Negative / accepted:** A bot dependency bump that breaks **only** on the functional path is
  detected at queue time rather than PR time. This shifts a rare failure later in the pipeline; the
  queue still blocks the landing, so `main` is never exposed. Scoped to bot PRs whose diffs are
  dependency bumps, not source edits — the same trade-off ADR-0038 accepted for the 22-only case,
  narrowed to bots.
- **Neutral / trade-off:** Grouping hides an individual minor/patch bump's changelog inside a
  combined PR. Mitigated by leaving **majors ungrouped**, so every review-worthy bump stays
  individual; only compatible minor/patch bumps are bundled.

## Alternatives considered

- **Key the skip on `github.actor`.** Simpler token. **Rejected:** `github.actor` flips to the
  human who re-runs a job, so a human re-run of a bot PR would wrongly re-enable the full matrix;
  the PR-author login is the stable, GitHub-recommended key.
- **Skip via whole-workflow path/branch filtering (top-level `on:` filters).** No per-job
  condition. **Rejected:** a required check that is filtered out at the workflow level never
  reports and leaves the PR **Pending forever** — the documented trap; a job-level `if:` reports
  skipped=success and needs no branch-protection edit.
- **Defer functional tests to release-only** and **move to self-hosted/ARC runners.** Both
  considered and set aside — see D3 (rejected / deferred with a named trigger).
