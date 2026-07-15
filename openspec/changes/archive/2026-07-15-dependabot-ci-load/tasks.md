# Tasks — dependabot-ci-load

## 1. Foundation

- [x] 1.1 Author `docs/decisions/0039-dependabot-ci-load.md` recording D1 (bot-skip condition),
      D2 (Dependabot grouping), and D3 (rejected/deferred alternatives — functional-at-release
      rejected, self-hosted/ARC runners deferred with a named trigger) as durable CI policy

## 2. CI re-shape

- [x] 2.1 `.github/workflows/ci.yml`: add a job-level `if:` on `functional-tests` so
      bot-authored PRs skip the representative `pull_request` variant while `merge_group` always
      runs the full matrix — exactly:
      `if: github.event_name == 'merge_group' || github.event.pull_request.user.login != 'dependabot[bot]'`
      (`merge_group` term first; `github.event.pull_request.user.login`, not `github.actor`;
      job-level `if:` so the skip reports skipped=success and blocks no required check)
- [x] 2.2 `.github/dependabot.yml`: add `groups` for compatible bumps (one group for
      `Microsoft.Extensions.*`/runtime packages, one for `github-actions` bumps; minor + patch
      grouped) per GitHub's optimizing-PR-creation guidance; keep majors ungrouped/individual
      (the nuget `microsoft-extensions` group already existed — scoped it to minor+patch and added
      `System.*`; the `github-actions` ecosystem gained its first `groups` block; other existing
      nuget groups — test-stack, analyzers, nuget-security — preserved unchanged)

## 3. Verification

- [x] 3.1 On a real Dependabot PR, confirm `Functional Tests (Testcontainers) (23)` reports
      **success** on `pull_request` (job runs, heavy steps skip), no required check is left Pending
      or never-reporting, and the PR lands through the merge queue with the full `[22, 23]` matrix
      running on `merge_group`
      _(CONFIRMED 2026-07-15, after the step-level guard landed in PR #106 (merged
      2026-07-15T13:46Z) and a `@dependabot rebase`. Dependabot PR **#104** (`chore(ci): Bump
      actions/download-artifact from 7.0.0 to 8.0.1`): its `pull_request` run reported `Functional
      Tests (Testcontainers) (23)` = COMPLETED SUCCESS (heavy Testcontainers steps skipped, the
      matrix-suffixed check-run name materialized) with no required check left Pending. It
      auto-enqueued and landed through the merge queue at 2026-07-15T14:28:06Z; the `merge_group`
      CI run (run id **29421534165**) ran the FULL matrix — `Functional Tests (Testcontainers) (22)`
      SUCCESS **and** `(23)` SUCCESS. Corroborated by sibling Dependabot PR **#105**, which showed
      the identical `pull_request` pattern ((23) SUCCESS, heavy steps skipped). The first
      observation (both PRs BLOCKED under the job-level `if:`) is preserved in the ADR-0039 addendum
      as the root-cause record.)_
- [x] 3.2 On a human PR, confirm the representative `Functional Tests (Testcontainers) (23)`
      variant **still runs** on `pull_request` (the skip applies only to bot-authored PRs)
      _(CONFIRMED: PR #103's own `pull_request` run — a human PR — shows `Functional Tests
      (Testcontainers) (23)` COMPLETED SUCCESS with the heavy Testcontainers steps executing. The
      step-level guard in this fix is byte-for-byte identical in effect on human PRs and on
      `merge_group` (full run); only bot-authored `pull_request` events skip the heavy steps.)_
- [x] 3.3 Scope note (CI-config only): **NO** version bump, **NO** CHANGELOG package entry (there
      is no `src/` change), and **NO** branch-protection edit — with the **step-level** guard the
      job always runs, so the matrix expands and the required `(23)` check-run name reports
      success (contrast ADR-0038, which had to reconcile a required context).
      `openspec validate --all --strict` green before the PR.
      _(Correction, 2026-07-15: the original "a job skipped by `if:` reports skipped=success, so
      required checks stay green" assumption FAILED for a **matrix-named** required context — a
      job-level skip collapses the matrix so the `(23)`-suffixed context never reports (PRs
      #104/#105 BLOCKED). The fix moved the guard to STEP level so the job — and its matrix — always
      run; still **NO** branch-protection edit. Confirmed: no `src/` touched, no version/CHANGELOG
      package entry, no branch-protection edit; `openspec validate --all --strict` green.)_
