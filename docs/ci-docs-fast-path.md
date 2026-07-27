# CI: docs/data-only fast-path (ADR-0016)

Docs-only PRs — markdown, OpenSpec-archive `git mv` moves, `CHANGELOG.md` — skip the
heavy required CI jobs. A `gate` job in `ci.yml` and `codeql.yml` classifies the diff
(strict fail-closed allowlist, event-specific base) and the heavy jobs are guarded to
skip when the diff is docs-only. A job skipped via `if:` reports `skipped`, which GitHub
treats as satisfying a required check, so the PR still merges in both the `pull_request`
and `merge_group` phases. The diff-relevant required checks (e.g. OpenSpec Validate) still
run unconditionally — and the matrix-suffixed `Functional Tests (Testcontainers) (23)`
context keeps running its job so its suffixed check-run name still materializes.

**Do not** replace this with a workflow-level `paths-ignore` on a required-context
workflow — that strands the required contexts `Expected` forever and blocks the PR.

Standard, rationale, and the exact guard/classifier: verbara-meta **ADR-0016** (extends
ADR-0003).
