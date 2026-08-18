# Tasks — functional-off-the-pr-path

The YAML edits are three small, coupled changes to two files, so they land as one batch rather than
in FCM phases. What cannot be verified locally is the part that matters — how GitHub materializes a
matrix-suffixed required check — so §3 is the load-bearing section and it runs on this change's own
PR.

## 1. Decision record

- [x] 1.1 Write `docs/decisions/0051-functional-suites-off-the-pr-path.md` with the measured
      critical path, the 457-run failure attribution, and the three decisions (D1 merge_group-only
      heavy steps, D2 drop the `needs` edge, D3 concurrency)
- [x] 1.2 Cross-reference ADR-0038 (D3 retired on the PR arm, queue arm intact), ADR-0039 (bot-only
      skip widened; its addendum's step-level constraint preserved) and ADR-0043 (same principle,
      applied to soak evidence)
- [x] 1.3 Move Status to `Accepted` in the same commit that lands — no follow-up edit (ADRs are
      append-only once Accepted)

## 2. Workflow edits

- [x] 2.1 `ci.yml`: `concurrency` group keyed on `github.event.pull_request.number || github.ref`,
      `cancel-in-progress` gated on `github.event_name == 'pull_request'`
- [x] 2.2 `codeql.yml`: the same block, keyed independently so the two workflows cancel separately
- [x] 2.3 `ci.yml` `functional-tests`: `needs: [unit-tests, gate]` → `needs: gate`, job-level `if:`
      → `!cancelled()` (must remain never-false — matrix-collapse hazard)
- [x] 2.4 Both heavy step guards: lead with `github.event_name == 'merge_group'`, then
      `contains(github.event.pull_request.labels.*.name, 'ci:functional')`, AND'ed with the
      unchanged ADR-0016 docs-only term
- [x] 2.5 Rewrite the stale comment blocks — the ones describing the bot-only skip and the
      representative PR matrix now describe something the YAML no longer does
- [x] 2.6 Both workflow files parse as YAML and the job graph is intact

## 3. Verification on this change's own PR (the part that cannot be checked locally)

- [ ] 3.1 Confirm `Functional Tests (Testcontainers) (23)` **reports success in seconds** on the PR
      run — not `SKIPPED`, not unsuffixed, not absent. This is the #104/#105 failure mode and the
      single most important check in this change
- [ ] 3.2 Confirm all nine required contexts report on the PR run, and that the PR reaches
      `mergeable` rather than sitting `BLOCKED`
- [ ] 3.3 Measure the PR run's wall-clock and confirm it lands near the predicted ~10 min
- [ ] 3.4 Push a second commit while the first run is in flight and confirm the first run is
      **cancelled** (the `concurrency` block's first live exercise)
- [ ] 3.5 Confirm the `merge_group` run executes the full `[22, 23]` matrix with both heavy steps
      running, and measure it against the predicted ~20.5 min
- [ ] 3.6 Create the `ci:functional` label in the repo so the escape hatch exists before someone
      needs it, and verify a labelled PR runs the heavy steps

## 4. Closing

- [x] 4.1 CHANGELOG entry under `[Unreleased]`
- [x] 4.2 `openspec validate --all --strict` green (also a CI gate) — 11 passed, 0 failed
- [x] 4.4 `scripts/tests/test_classify_docs_only.sh` still green (37/37); its self-validation case
      pins that a `ci.yml` edit is NOT docs-only, so this change's own PR exercises the full gate
- [ ] 4.3 Archive this change once the queue run has confirmed §3.5
