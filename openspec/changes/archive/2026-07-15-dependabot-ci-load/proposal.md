---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: CI cost & merge-queue latency (all downstream repos gate on Sdk CI)
decision_ref: Sdk/ADR-0039
---

# Proposal: dependabot-ci-load

## Why

Dependabot generated **31 PRs in Sdk in the last 30 days** (23 merged via automerge + merge
queue → every merged PR costs BOTH a `pull_request` run and a `merge_group` run). Post-ADR-0038,
each merged Dependabot PR costs **≈ 90 compute-min** (~65% of it the functional tests), for
**≈ 2,343 compute-min/month**. Sdk is public, so GitHub-hosted standard-runner minutes are FREE —
the real cost is **wall-clock latency** (~47 min of runner occupancy per merged PR),
**merge-queue serialization on batch days** (8 `merge_group` runs on 2026-06-29), and **7 PRs that
needed 2–3 queue attempts** in the window. This is the direct follow-up to `ci-pipeline-slimming`
(ADR-0038): that change slimmed *what every PR runs*; this one slims *how much a bot-authored PR
has to run to land*. Measured 2026-07-15 from the live `gh` API; evidence home is verbara-meta
`docs/research/2026-07-15-dependabot-ci-load.md`.

## What Changes

1. **Bot PRs skip the representative functional matrix on `pull_request`.** A job-level condition
   on `functional-tests` in `.github/workflows/ci.yml` skips the representative PR functional
   variant for `dependabot[bot]`-authored PRs — the merge queue's full `[22, 23]` matrix still
   runs on `merge_group` and remains the landing gate, so an automerged bot PR loses **zero**
   landing validation. Human PRs are untouched.
2. **Dependabot grouping.** `groups` in `.github/dependabot.yml` bundle compatible bumps (one
   group for the `Microsoft.Extensions.*`/runtime packages, one for `github-actions` bumps;
   minor + patch grouped) to cut raw PR count, per GitHub's own optimizing-PR-creation guidance.
   Majors stay ungrouped/individual.

No `src/` change, no public-API change, no version bump, no CHANGELOG package entry, and — unlike
ADR-0038 — **no branch-protection edit** (a job skipped by `if:` reports skipped=success and does
not block a required check).

## Capabilities

### New Capabilities

<!-- none -->

### Modified Capabilities

- `ci-gating`: the event-scoped CI-gating capability gains a requirement that bot-authored PRs MAY
  skip the representative PR functional matrix, while the `merge_group` full matrix remains the
  landing gate for every PR — bot or human.

## Impact

- `.github/workflows/ci.yml` — job-level `if:` on `functional-tests` (bot PRs skip the
  representative variant on `pull_request`; `merge_group` always runs the full matrix).
- `.github/dependabot.yml` — `groups` for compatible bumps (Microsoft.Extensions/runtime;
  github-actions); majors ungrouped.
- `docs/decisions/0039-*.md` — new ADR recording the durable decisions (D1 bot-skip condition,
  D2 grouping, D3 rejected/deferred alternatives).
- Branch protection — **no change** (a job skipped by `if:` reports skipped=success; required
  checks stay green, never Pending).
- Downstream (Pro/Platform) — none. CI-config only, no package rebuild, no pin bump.

## Architectural Risk

**Level:** LOW. **Affected:** the Sdk `pull_request` validation path for bot-authored PRs only
(the `merge_group` landing gate is unchanged, and human PRs are unchanged). **Mitigation:** the
condition keys on `github.event.pull_request.user.login` (GitHub-recommended — stable across human
re-runs, unlike `github.actor`) and leads with the `merge_group` term so the full `[22, 23]` queue
matrix always runs regardless of author; the job-level `if:` is required-check-safe (skipped=success,
no Pending), so no branch-protection edit is needed and the queue cannot hang on a never-reporting
context (the verbara-meta/ADR-0003 failure mode ADR-0038 had to reconcile). Verification lands a
real Dependabot PR and confirms it skips the PR functional variant yet drains through the queue
with `[22, 23]` running.
