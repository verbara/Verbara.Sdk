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

- [ ] 3.1 On a real Dependabot PR, confirm `Functional Tests (Testcontainers) (23)` reports
      **success** on `pull_request` (job runs, heavy steps skip), no required check is left Pending
      or never-reporting, and the PR lands through the merge queue with the full `[22, 23]` matrix
      running on `merge_group`
      _(PENDING OBSERVATION — the **first** observation, 2026-07-15 on Dependabot PRs **#104/#105**,
      FAILED: the job-level `if:` (PR #103) collapsed the matrix so the required suffix context
      `Functional Tests (Testcontainers) (23)` never reported → both PRs `mergeStateStatus: BLOCKED`,
      merge queue empty, auto-merge never enqueued. Root cause + fix: ADR-0039 addendum (guard moved
      job → step level; the job now always runs so the `(23)` check-run name materializes and reports
      success in seconds on bot PRs). Re-observation now waits on the **next real Dependabot PR**
      after this step-level fix lands: watch its `pull_request` checks show `Functional Tests
      (Testcontainers) (23)` = success (heavy steps skipped) with no required check Pending, then its
      `merge_group` run show (22)+(23). Leave unchecked until then.)_
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
