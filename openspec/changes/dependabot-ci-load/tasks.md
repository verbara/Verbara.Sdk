# Tasks — dependabot-ci-load

## 1. Foundation

- [ ] 1.1 Author `docs/decisions/0039-dependabot-ci-load.md` recording D1 (bot-skip condition),
      D2 (Dependabot grouping), and D3 (rejected/deferred alternatives — functional-at-release
      rejected, self-hosted/ARC runners deferred with a named trigger) as durable CI policy

## 2. CI re-shape

- [ ] 2.1 `.github/workflows/ci.yml`: add a job-level `if:` on `functional-tests` so
      bot-authored PRs skip the representative `pull_request` variant while `merge_group` always
      runs the full matrix — exactly:
      `if: github.event_name == 'merge_group' || github.event.pull_request.user.login != 'dependabot[bot]'`
      (`merge_group` term first; `github.event.pull_request.user.login`, not `github.actor`;
      job-level `if:` so the skip reports skipped=success and blocks no required check)
- [ ] 2.2 `.github/dependabot.yml`: add `groups` for compatible bumps (one group for
      `Microsoft.Extensions.*`/runtime packages, one for `github-actions` bumps; minor + patch
      grouped) per GitHub's optimizing-PR-creation guidance; keep majors ungrouped/individual

## 3. Verification

- [ ] 3.1 On a real Dependabot PR, confirm `Functional Tests (Testcontainers) (23)` reports
      **skipped** on `pull_request`, no required check is left Pending, and the PR lands through
      the merge queue with the full `[22, 23]` matrix running on `merge_group`
- [ ] 3.2 On a human PR, confirm the representative `Functional Tests (Testcontainers) (23)`
      variant **still runs** on `pull_request` (the skip applies only to bot-authored PRs)
- [ ] 3.3 Scope note (CI-config only): **NO** version bump, **NO** CHANGELOG package entry (there
      is no `src/` change), and **NO** branch-protection edit — a job skipped by `if:` reports
      skipped=success, so required checks stay green (contrast ADR-0038, which had to reconcile a
      required context). `openspec validate --all --strict` green before the PR.
