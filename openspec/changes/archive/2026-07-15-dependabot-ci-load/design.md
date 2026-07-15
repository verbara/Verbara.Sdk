# Design — dependabot-ci-load

Direct follow-up to `ci-pipeline-slimming` (Sdk/ADR-0038). That change slimmed *what every PR
runs*; this one slims *how much a bot-authored PR has to run to land*, and cuts the raw count of
bot PRs at the source.

## Context

Dependabot produced 31 PRs in Sdk over the last 30 days; 23 merged via automerge + merge queue, so
each merged PR paid a `pull_request` run **and** a `merge_group` run. Post-ADR-0038 each merged
Dependabot PR is ≈ 90 compute-min (~65% functional tests) → ≈ 2,343 compute-min/month. Sdk is
public, so GitHub-hosted standard-runner minutes are **free**; the binding costs are wall-clock
(~47 min runner occupancy per merged PR), merge-queue serialization on batch days (8 `merge_group`
runs on 2026-06-29), and 7 PRs that took 2–3 queue attempts in the window. Evidence:
verbara-meta `docs/research/2026-07-15-dependabot-ci-load.md` (measured 2026-07-15, live `gh` API).

## Goals / Non-Goals

**Goals:**

- Stop paying the representative PR functional matrix on automerged bot PRs, without weakening the
  landing gate — the `merge_group` full `[22, 23]` matrix still runs and gates every landing.
- Cut the raw number of Dependabot PRs by grouping compatible bumps.
- Do it with **no** branch-protection edit (contrast ADR-0038, which had to reconcile a required
  context).

**Non-Goals:**

- No `src/` change, no public-API change, no version bump, no CHANGELOG package entry (CI-config
  only).
- Not touching what *human* PRs run — the representative variant stays for humans.
- Not moving to self-hosted/ARC runners (deferred; see D3).
- Not deferring functional tests to release-only (rejected; see D3).

## Decisions

### D1 — Bot PRs skip the representative functional matrix on `pull_request`

A job-level condition on `functional-tests` in `.github/workflows/ci.yml`, exactly this shape:

```yaml
if: github.event_name == 'merge_group' || github.event.pull_request.user.login != 'dependabot[bot]'
```

Three points make this the right shape:

- **`merge_group` term first.** The merge queue's actor is the *enqueuer*, not the bot, so
  `github.event.pull_request.user.login` is not the bot on a `merge_group` event — leading with
  `github.event_name == 'merge_group'` guarantees the full `[22, 23]` queue matrix always runs and
  remains the landing gate. An automerged bot PR loses **zero** landing validation.
- **`github.event.pull_request.user.login`, not `github.actor`.** GitHub-recommended: `github.actor`
  flips to the human who re-runs a job, so a human re-run of a bot PR would wrongly re-enable the
  matrix. The PR author login is stable.
- **Job-level `if:`, not whole-workflow path/branch filtering.** A job skipped by `if:` reports
  **skipped=success** and does **not** block a required check. Whole-workflow path/branch filtering
  instead leaves the required check **Pending forever** — a documented trap
  (docs.github.com "troubleshooting required status checks"). So this needs **no** branch-protection
  edit — unlike ADR-0038, which had to drop a required context.

### D2 — Dependabot `groups` to cut PR count

`groups` in `.github/dependabot.yml` bundle compatible bumps so several updates land as one PR:
one group for `Microsoft.Extensions.*`/runtime packages, one for `github-actions` bumps; minor +
patch grouped. Majors stay **ungrouped/individual** (a major is a real review event and should not
be hidden inside a group). Follows GitHub's own "optimizing PR creation for version updates"
guidance.

### D3 — Alternatives rejected / deferred

- **Functional tests only at release** — **REJECTED.** Industry anti-pattern: regressions batch up
  and bisection cost explodes. The Asterisk project itself runs a gate-subset on PRs + a
  nightly-full suite, never release-only. Keeping the full matrix on the merge queue (the landing
  gate) preserves per-landing validation while cutting only the redundant PR-time bot run.
- **Self-hosted / ARC runners on the operator's Talos lab** — **DEFERRED** with a named trigger:
  revisit only for a genuine capability need (e.g. R5.5 Phase B-LK load testing) or a real
  concurrency wall — **not** to save already-free public-repo minutes. GitHub officially discourages
  self-hosted runners on public repos (fork-PR arbitrary code execution), and a lab-offline runner
  would block the merge queue for every PR (`check_response_timeout` 60 min).

## Risks / Trade-offs

- **[A bot dependency bump breaks only on the functional path, undetected until queue time]** →
  Mitigation: the `merge_group` full matrix still runs on every bot landing and gates it; the skip
  only moves bot functional detection from PR-time to queue-time, exactly as ADR-0038 accepted for
  the 22-only case, and here it is scoped to bot PRs whose changes are dependency bumps, not source
  edits.
- **[Grouping hides an individual bump's changelog inside a combined PR]** → Mitigation: majors stay
  ungrouped, so the review-worthy bumps remain individual; only compatible minor/patch bumps are
  bundled.
- **[The `if:` expression is subtly wrong and either never skips or skips too much]** →
  Mitigation: verification (tasks 3.1/3.2) lands a real Dependabot PR and a human PR and confirms
  the observed behaviour on both events before the change is trusted.

## Field correction (2026-07-15) — D1's job-level `if:` collapsed the matrix name

D1 shipped in PR #103 as a **job-level** `if:` on `functional-tests`. It failed in the field. The
"skipped=success blocks no required check" reasoning was correct for a *plain* job name, but wrong
for a **matrix-named** required context:

- Classic branch protection on `main` requires the context `Functional Tests (Testcontainers) (23)`
  — with the matrix suffix.
- When the job-level `if:` evaluates false on a Dependabot PR, GitHub does **not** expand the matrix.
  It collapses the job into a single check run named `Functional Tests (Testcontainers)` (no `(23)`
  suffix) with conclusion SKIPPED.
- The required context `Functional Tests (Testcontainers) (23)` therefore **never reports** on bot
  PRs. The PR sits `mergeStateStatus: BLOCKED` with every other check green and the merge queue
  empty; auto-merge never enqueues. Observed live on Dependabot PRs **#104 and #105**
  (`gh pr view --json statusCheckRollup`; `gh api .../branches/main/protection` confirmed the
  required suffix context).

This is the same never-reporting-context failure verbara-meta/ADR-0003 codifies — reached here not
by whole-workflow filtering but by a job-level skip that changes the emitted check-run *name*.

### Correction — move the guard to STEP level (branch protection untouched)

The `functional-tests` job now **always runs** (so the matrix expands and the `(23)` check-run name
materializes and reports success on bot PRs). The bot skip moves to **step level**: the two heavy
steps — "Pre-pull Docker images and build Asterisk test image" and "Run functional + integration
tests" — each carry the *same* guard expression D1 used
(`github.event_name == 'merge_group' || github.event.pull_request.user.login != 'dependabot[bot]'`).
On a bot `pull_request` both heavy steps skip and the job completes SUCCESS in seconds; on human PRs
and on `merge_group` every step runs in full — behaviour byte-for-byte identical to D1's intent.
**No branch-protection edit** — the fix stays entirely in the workflow (ADR-0039 addendum,
2026-07-15).

## Recorded decisions

D1, D2, and D3 are durable CI policy → recorded as `Sdk/ADR-0039` (task 1.1); the D1 job-level →
step-level correction is the ADR-0039 **addendum** (2026-07-15). The `ci-gating` spec delta records
the normative requirement (bot PRs MAY skip the representative matrix's heavy work; the queue full
matrix remains the landing gate).
