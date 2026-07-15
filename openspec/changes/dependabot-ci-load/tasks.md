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
      **skipped** on `pull_request`, no required check is left Pending, and the PR lands through
      the merge queue with the full `[22, 23]` matrix running on `merge_group`
      _(PENDING OBSERVATION — cannot be verified by this PR; verified by the **next real
      Dependabot PR** after this lands: watch its `pull_request` checks show `Functional Tests
      (Testcontainers) (23)` = skipped with no required check Pending, then its `merge_group` run
      show (22)+(23). Leave unchecked until then.)_
- [ ] 3.2 On a human PR, confirm the representative `Functional Tests (Testcontainers) (23)`
      variant **still runs** on `pull_request` (the skip applies only to bot-authored PRs)
      _(PENDING OBSERVATION — verified by **this very PR's own CI**: this branch is human-authored,
      so its `pull_request` run MUST show `Functional Tests (Testcontainers) (23)` **running**, not
      skipped. Confirm on the PR's checks, then check this box in the archive step.)_
- [x] 3.3 Scope note (CI-config only): **NO** version bump, **NO** CHANGELOG package entry (there
      is no `src/` change), and **NO** branch-protection edit — a job skipped by `if:` reports
      skipped=success, so required checks stay green (contrast ADR-0038, which had to reconcile a
      required context). `openspec validate --all --strict` green before the PR.
      _(Confirmed: no `src/` touched, no version/CHANGELOG package entry, no branch-protection edit;
      `openspec validate --all --strict` → 5 passed, 0 failed.)_
