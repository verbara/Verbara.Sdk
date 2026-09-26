# Tasks

Execution follows `rules.tasks`: **a fresh subagent per task, never inline in the main session.**
Phase A is batched, Phase B is one focused subagent per component, Phase C is batched.

**This is not a bug fix in the regression-test sense** — no runtime behaviour is wrong. The equivalent
discipline is task 1.1: prove the property is unguarded **today**, recorded before anything changes, so
the same probe at the end proves it is guarded.

**The measurement that decides the design, already taken, so no task re-derives it.** A close-out pull
request is docs-only — `scripts/ci/classify-docs-only.sh:30` puts `openspec/*` and `CHANGELOG.md` on
the fast path — and on close-out #316 `Unit Tests`, `Coverage Ratchet`, `Pack Warnings Gate` and
`Audit Test Asserts` were all **SKIPPED**. What ran was `Docs-only gate`, `Docs-only gate (CodeQL)`,
`Dependency Review`, `Coverage Script Tests`, `OpenSpec Validate` and the short-circuited functional
job. **A Governance test therefore cannot be this guard** (design D2).

**Coordination.** Two open changes already record findings under a heading of their own:
`the-public-surface-is-declared-or-it-is-not-guarded` (`## 4. Measured elsewhere, or not yet owned`,
four findings) and `a-call-that-ended-is-released-when-it-ends` (`## 4. Recorded, not fixed here`,
four findings). Both are this owner's, both are committed on `main` with no task ticked and no
worktree, and this change edits **exactly two lines in each**: task 1.6 renames the heading line, and
task 1.2 appends the registry identifier to the one task line that works a row it owns
(`the-public-surface…` task 2.4 for `RS0016`; `a-call-that-ended…` task 2.3 for
`MaxCompletedSessions`). Nothing else in either file changes, so a later apply session merges cleanly
against it. `a-published-surface-is-one-something-measures` belongs to another session and records no
deferred finding (its `## Findings this change corrects…` section is corrections, and its only phrase
hit, line 185, is a quoted mention); leave it alone. Do not touch `src/Verbara.Sdk.Ari/**`,
`Tests/Verbara.Sdk.Ari.Tests/**`, `Tests/Verbara.Sdk.FunctionalTests/Verbara.Sdk.FunctionalTests.csproj`
or `docs/guides/README.md`.

## 1. Phase A — foundation (batched)

- [ ] 1.1 Record the probe that proves the property is unguarded **today**, before any change. Two
      measurements, both read-only. First, the advisory layer's attested result: `gh pr view 316
      --json body -q .body | grep -c -E '^(Harvested|Cited|Referrer sweep):'` — paste the count (0)
      and the merge time. Second, the gates the close-out ran: on
      `openspec/changes/archive/2026-09-25-a-reconnect-reload-is-a-diff-not-a-wipe/tasks.md`, whose
      section 4 says "It has no open change of its own" and "It needs one" **four times each** —
      counted in the file, not recalled — confirm
      `npx @fission-ai/openspec@1.13.1 validate --all --strict --no-interactive` and
      `validate --archived --no-interactive` both pass. Paste both results verbatim into this file.
      That is the whole defect: the rule's own result line was absent, four findings were archived
      saying in as many words that they have no open change of their own — and two more were handed to
      another repository with no owner at all — and every gate the close-out ran said yes. **Do not
      edit the archived change** — this task reads it.

- [ ] 1.2 Write `docs/findings-registry.md`, modelled on `docs/claim-registry.md` — read that file
      first and follow its shape, its header, its `## Status legend` and its row density rather than
      inventing a format. Columns: `id` (`F-NNNN`, assigned in order, never reused) | `finding` — the
      bullet's bold lead-in, verbatim, whitespace-normalised; the key the check joins on |
      `where` (`file:line` or path; informational, never a key) | `recorded by` (change name) |
      `owner` | `status` (`OPEN`, or `CLOSED — <closer>`). Carry the legend's rule that a row is
      struck through, never deleted, and a header line stating the limit design D3 accepts: a finding
      written with none of the named phrases outside the heading is not detected.

      Seed it from `design.md`'s **Appendix A** — the only surviving copy of the 2026-09-24 sweep; do
      not go looking for another — with: the **12 defect rows (11 distinct)** it itemizes with
      `file:line`; the perf gate that was never turned on (`perf-regression.yml:66` still
      `PERF_GATE_ENFORCE: 'false'` since 2026-09-13, so `check-perf-baseline.py` emits `::warning::`
      and the gate observes without blocking — and `gates.yaml` G6 reads `na` for the same reason;
      **do not describe it as failing closed**); the five `FakeTimeProvider` copies and the five
      accept-backoff copies, itemized by running the two greps the record gives and pasting the hits
      as rows; and **one row** for the 12 surviving mutants, counted not itemized, owned by the same
      change 1.3 names.

      Then the reconnect archive's section 4 —
      `openspec/changes/archive/2026-09-25-a-reconnect-reload-is-a-diff-not-a-wipe/tasks.md:218`,
      **six bullets, four of them ending "It needs one"**; count them in the file rather than
      trusting this list: `StatusEvent`'s two dead properties and four missing ones (`OPEN`, new
      owner); session-table growth (`MaxCompletedSessions` — `OPEN`, owner
      `a-call-that-ended-is-released-when-it-ends`, whose task 2.3 works it: append the identifier to
      that task line); `RS0016` (`OPEN`, owner `the-public-surface-is-declared-or-it-is-not-guarded`,
      task 2.4: append the identifier to that line); `CallSessionStateTransitions` having no
      `Created → Ringing` (archive `tasks.md:258`, `OPEN`, new owner); and the two the section records
      for another repository — "how long a healthy inbound call sits in `Created`", a Platform
      measurement, and the product-level blast radius of a stranded call, also Platform — each owned
      by this change's decision record, which names the destination repository (D5).

      Then `the-public-surface…`'s four under its section 4: `Unshipped` never promoted to `Shipped`;
      `RS0037`/`RS0041` suppressed unmeasured; `.editorconfig:72` demoting `RS0026`; and the three
      test projects writing `<NoWarn>CA1707</NoWarn>` with no `$(NoWarn);` prefix.

      Then `a-call-that-ended…`'s four under its section 4: `_byChannelId`/`_bridgeToSession` entries
      stranded by an unobserved hangup — its cause is the wipe #315 fixed, so the row is
      `CLOSED — a-reconnect-reload-is-a-diff-not-a-wipe`, which resolves to the dated folder; the
      Postgres session store never deleting (`OPEN`, new owner); and the two that belong to other
      repos — cluster-layer sessions with no participants (Pro) and the product's retention service
      shipping disabled (Platform) — owned by this change's decision record naming the destination.
      That is **four** cross-repo rows in the seed with these two, which is the count D5 states.

      Then one row found while this change was reviewed: `ConformanceRecordGuardTests` reads
      `docs/guides/provider-wire-conformance.md` inside the gated `Unit Tests` job and that path is
      not in `classify-docs-only.sh`'s carve-out, so a docs-only PR that deletes a row from the record
      skips its only guard — the `claim-guards` failure "a guard the docs-only fast path skips is not
      a guard", inside the Governance project itself (`OPEN`, new owner). This change records it, so it
      also appears under this change's own `## 4. Findings without an owner`, as its own rule requires.

      Every `OPEN` row's owner is opened by **task 1.7**, which groups them by defect family; no row
      may name an owner 1.7 does not open. **Verify each seeded row's `file:line` against the tree
      before writing it** — the sweep was 2026-09-24 and `main` has moved since; a row that no longer
      resolves is a finding about the registry, not a row to copy. Verify also that every `OPEN` row's
      owner contains that row's `F-NNNN` (design D5), and that each cross-repo row names its
      destination repository.

- [ ] 1.3 Add the row design D4 requires for what the registry does **not** contain: the **71** rows
      of the 2026-09-24 sweep with no `file:line` — 12 surviving mutants and 59 known only by category
      (45 hardening, 17 docs, 9 cleanup, less the 12 mutants), per Appendix A of `design.md`. The
      per-change breakdown did not survive the sweep, so re-deriving them means re-reading the eight
      archived `tasks.md` files the record names. Give it an owner — a new open change, named in the row — so the gap is owned
      rather than noted. Verify the row states a count and names where the items live, and that it
      does not read as an inventory. This row is the one place this change could overstate itself.

- [ ] 1.4 Write `docs/decisions/NNNN-*.md` at the **next free number when this task runs** — both
      `the-public-surface…` and `a-call-that-ended…` claim `Sdk/ADR-0063` and neither has written
      it; correct this proposal's `decision_ref` to the number taken. Status: Proposed → Accepted at
      merge. It is the Sdk instance record under verbara-meta/ADR-0019 amendment 3, and it carries:
      D1–D7; the measurement that rules out a Governance test **and** the reason a Sdk.Pro-style
      always-run `Governance Guards` job is not the answer here (ADR-0042 D3: a new context is a
      branch-protection edit — otherwise the next reader "improves" D2 into the ADR-0003
      never-reporting-context failure); the obligation D5 creates, stated as a mechanism — a close-out
      re-homes or closes its rows *in the same pull request because the check fails there*; the
      asymmetry that an ADR owner is terminal and the token in the ADR is the only evidence that will
      ever exist; the four cross-repo rows with their identifiers and destination repositories in
      the body; and the sentence D6 requires about what the guard cannot enforce. Verify
      `openspec validate --all --strict` passes and the file exists.

- [ ] 1.5 Land the ADR-count coupling in the same commit as 1.4: bump `README.md`'s `**N ADRs**`
      figure, update its row in `docs/claim-registry.md`, and add the catalog row in
      `docs/decisions/README.md`. Verify `dotnet test Tests/Verbara.Sdk.OpenTelemetry.Tests/` passes —
      `ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk` and
      `TheDecisionCatalog_ShouldListEveryAdrOnDisk` both fail if any of the three is missed, and the
      catalog guard is a set equality both ways.

- [ ] 1.6 Migrate the two open changes that already record findings under a heading of their own, so
      the tree 2.2 first runs on is one it can pass. In
      `openspec/changes/the-public-surface-is-declared-or-it-is-not-guarded/tasks.md` replace the line
      `## 4. Measured elsewhere, or not yet owned` with `## 4. Findings without an owner`; in
      `openspec/changes/a-call-that-ended-is-released-when-it-ends/tasks.md` replace
      `## 4. Recorded, not fixed here` with the same. The bullets beneath are the record and stay
      verbatim. Verify `git diff --stat` shows exactly those two files, each at the heading line plus
      the one identifier line 1.2 added, and `openspec validate --all --strict` passes. No
      grandfather list for open changes: an exemption with no expiry is how the present state
      happened, and two renames cost less than an exemption branch, its fixture and its harness case.

- [ ] 1.7 Open the owners, grouped by defect family, because **every `OPEN` row's owner must exist
      before 2.2 can exit 0** and before `OpenSpec Validate`'s first step stays green. **Measured on a
      scratch copy of the tree:** a proposal-only change under `openspec/changes/` fails
      `openspec validate --all --strict` with *"Change must have at least one delta"*; with an
      `.openspec.yaml` carrying `schema: spec-driven`, `created:` and **`skip_specs: true`** it passes.
      So each stub is a directory with a `proposal.md` and that `.openspec.yaml`, its proposal states
      that the flag comes off when its first spec delta lands — it is the `--skip-specs` family
      verbara-meta/ADR-0019 item 2 warns about — and it contains the identifiers it owns, which is what
      D5 checks. This is Phase A's real cost; nothing else in the change makes it visible.

      **Group by family, not by row**, because a stub is a place work happens and the four
      shutdown-token sites are one piece of work rather than four: (a) lifecycle and shutdown-token
      capture — `VoiceAiSessionBroker`, the `AudioSocketServer` accept loop, the `NatsBridge`
      per-filter loop, the multi-server DI that registers no hosted service, and the key-only
      `TryRemove`; (b) silent transport failure — the two empty `catch (IOException)`, the descriptor
      leak window and the connection counted before `OnNext`; (c) synthesis accounting — a barge-in
      still counted completed and the flush race booked as a synthesis failure; (d) AMI and session
      modelling — `StatusEvent`'s dead and missing properties, and `CallSessionStateTransitions` having
      no `Created → Ringing`; (e) build configuration — the perf gate never enforced and the three test
      projects replacing `NoWarn`. Then four that belong to no family: (f) the duplicate test doubles
      (five `FakeTimeProvider`, five accept-backoff copies); (g) the public-API record trio (`Unshipped`
      never promoted, `RS0037`/`RS0041` unmeasured, `.editorconfig:72` demoting `RS0026`); (h) the
      Postgres session store's retention; (i) the conformance record's guard behind the docs-only fast
      path. With 1.3's change for the counted gap that is **ten** stubs rather than one per row, which
      the seed would make more than twenty — say the count and the grouping in each stub's proposal, so
      the drop is a stated decision and not an omission.

      `MaxCompletedSessions` and `RS0016` need no stub: their owners are open already, and 1.2 appends
      the identifier to the task line that works each. The four cross-repo rows are owned by 1.4's
      decision record, not by a stub. Verify `openspec list` shows every stub,
      `openspec validate --all --strict` passes with all of them present, and every `OPEN` registry
      row's owner resolves and contains that row's `F-NNNN`.

- [ ] 1.8 Verify the cross-repo precondition has merged, before 2.4 edits the archive guidance. **This
      is a dependency on another repository, not work this change does.** The harvest entry in
      `operations.archive.guidance` is uniform by decision across five repos (verbara-meta/ADR-0019;
      grep-confirmed byte-identical, Web differing by quote style only), so the Sdk copy may only take
      the wording ADR-0019 amendment 3 fixes; forking it locally would make five copies disagree where
      `xr-doctor` check 18 cannot see it — it counts entries (`n_archive > 0`), not their text. That
      amendment is its own pull request written from a `verbara-meta` session, because verbara-meta is
      outside this session's working directories and cross-repo work runs from that cockpit. Read-only
      here: confirm on `verbara-meta`'s `main` that ADR-0019 carries amendment 3, that anti-patterns
      ledger row 7 names this check, and that ADR-0006 records its flip condition firing on #316; paste
      the commit hash and the amendment's exact wording of the harvest entry into this task, and state
      that this change **cites** the amendment rather than forking the standard. If it has not merged,
      only 2.4 is blocked — every other task in this change still lands.

## 2. Phase B — critical components (one focused subagent each)

- [ ] 2.1 Fix the signal before building anything that reads it (design D3) — the **Sdk-local half**,
      which no other repository shares. `rules.tasks` in `openspec/config.yaml` is repo-shaped
      (verbara-meta/ADR-0019: only `archive.guidance` is uniform by decision, and the three .NET repos'
      `rules.tasks` already differ — three hashes today), so this entry is written here and needs no
      cross-repo agreement: add the entry naming the heading `## N. Findings without an owner`, the
      bold-lead-in form of a finding bullet, and `docs/findings-registry.md`. Verify
      `openspec instructions tasks --change <any-open-change> --json` returns the new `rules.tasks`
      text, since that is the path by which it reaches whoever writes a task list — a guidance line
      nothing delivers is the same class of defect this change exists to remove. Do **not** touch
      `operations.archive.guidance` here: that is task 2.4, and it is blocked on 1.8.

- [ ] 2.2 Write `scripts/ci/check-finding-ownership.sh`. It reads `docs/findings-registry.md` and
      every `tasks.md` under `openspec/changes/`, the dated record included, skipping only the
      directory names in `scripts/ci/finding-ownership-pre-guard.txt` (design D7; seed it with
      `ls -1 openspec/changes/archive/`, 28 names, and freeze it at the merge). It fails when: a
      registry row names no owner; an `OPEN` row's owner does not resolve to
      `openspec/changes/<owner>/` itself (a dated folder under `archive/` does not count) or to
      `docs/decisions/<owner>*.md`; an `OPEN` row's owner resolves but contains no occurrence of the
      row's `F-NNNN`, reported as *owner does not carry F-NNNN*; a `CLOSED` row names no closer, or
      its closer does not resolve to `openspec/changes/archive/*-<closer>/` or a decision record; a
      bullet under the designated heading has no row whose `finding` equals its bold lead-in, or has
      no bold lead-in at all; or one of D3's phrases — used, not quoted, and `out of scope` only when
      bold or sentence-initial — appears outside the heading. The heading is the exact line
      `^## [0-9]+\. Findings without an owner$`. Each failure names the finding and the change. Count
      matches without `grep -c`, which exits 1 on zero matches and under `set -e` kills the passing
      branch. Verify against the tree as it stands after Phase A **including 1.6 and 1.7**: the script
      exits 0. Before 1.6 it must exit non-zero naming
      `the-public-surface-is-declared-or-it-is-not-guarded` and
      `a-call-that-ended-is-released-when-it-ends`; record both outputs here. Then add
      `scripts/tests/test_check_finding_ownership.sh` following
      `scripts/tests/test_package_validation_coverage.sh`'s shape — one fixture per rule above,
      each asserted to exit non-zero with the right message, plus: an archive directory not in the
      list with an unowned finding under the heading (fails, named); an archive directory in the list
      carrying `Out of scope` and `has no open change of its own` (passes); the same open change under
      `archive/2026-10-01-<name>/` (fails); a registry row deleted while the archived heading still
      lists the finding (fails); a quoted `*tracked separately*` and a mid-sentence `out of scope`
      (pass); `## Findings this change corrects in the notes that handed it over` with bullets and no
      rows (passes — not the designated heading); and the passing tree asserted to exit 0. Verify
      `bash scripts/tests/test_check_finding_ownership.sh` passes and that removing any one failure
      branch from the script turns one fixture red.

- [ ] 2.3 Wire the gate where it will actually run (design D2). Add the script as a third step of the
      **`OpenSpec Validate`** job in `.github/workflows/ci.yml`, beside the two `validate` steps it
      already has, and add the harness to **`Coverage Script Tests`**. Both jobs run on a docs-only
      pull request — measured on close-out #316. Verify by reading the job's `if:` conditions that
      neither is gated on `docs_only == 'false'`, and state in the commit message which two jobs were
      chosen and which one was rejected, so the next reader does not re-open D2. No `fetch-depth` or
      base-SHA change to the job: the step reads the checked-out merge ref, as `validate --archived`
      beside it already does.

- [ ] 2.4 Land the Sdk instance of the harvest wording — **only after 1.8 confirms the meta amendment
      has merged**. This is the cross-repo half of what used to be one task; it is independent of 2.2
      and 2.3, so it may land last. `operations.archive.guidance` item 3 is uniform across five repos
      by decision, so apply verbara-meta/ADR-0019 amendment 3's wording **verbatim** — not a local
      paraphrase — carrying the heading's name and `docs/findings-registry.md` exactly as the amendment
      states them, so the five copies stay byte-identical (`xr-doctor` check 18 counts entries and
      would not see a fork). This change cites the amendment; it does not fork the standard. Verify
      `diff` between the harvest line in `openspec/config.yaml` and the same line pasted into 1.8 is
      empty, and that `openspec instructions archive --change <any-open-change> --json` returns the new
      text — the path by which it reaches whoever closes a change.

## 3. Phase C — integration (batched)

- [ ] 3.1 Commit the negative control as `scripts/ci/check-finding-ownership-guard-fires.sh`
      (design D6, spec requirement 6) — a script, not a documented sequence, because restoration must
      run on every exit path. It appends **a row with an empty owner** to `docs/findings-registry.md` —
      the one input that always exists, so the control never depends on an open change carrying the
      designated heading on the day it runs, and it exercises the check's first failure branch — then
      runs 2.2's script, and restores the file from a copy taken before the edit with the trap
      installed **before** the first write: `reg=docs/findings-registry.md; orig="$(mktemp)";
      cp -- "$reg" "$orig"; trap 'cp -- "$orig" "$reg"; rm -f -- "$orig"' EXIT`.
      **Never restore with `git checkout --`, `git restore` or `git stash`** — on a tree with
      uncommitted work they replace the developer's file with `HEAD`. Refuse to start if the probe
      row is already present. Exit 0 **only** when the guard failed naming that row; any other
      failure, and a pass, exit non-zero saying the guard was not proven. Verify three times — clean
      tree, tree with an unrelated uncommitted edit to the registry, and interrupted by SIGINT —
      capturing `sha256sum` and `git status --porcelain=v1 --untracked-files=all` before and after:
      both must be **the same, not empty**. Contrast it with 1.1's recorded output in the same place,
      so the pair reads as "unguarded before, guarded after".

- [ ] 3.2 Turn every scenario in `specs/finding-ownership/spec.md` into something actually checked.
      Requirement 1 (all three scenarios plus the owner-carries-it scenario), 2, 3 and 4 by 2.2's
      script and its harness; requirement 5 by the pre-guard list and its fixture pair (a listed
      archive with signal phrases passes; an unlisted archive with an unowned finding fails), and by
      `git diff --name-only main...HEAD -- openspec/changes/archive/` being empty on this pull request
      — nothing in the record was rewritten; requirement 6 by 3.1. Verify each scenario names what
      checks it, and say plainly which — if any — rests on nothing but a human reading a file, rather
      than rounding it up.

- [ ] 3.3 Write the `CHANGELOG.md` entry under `[Unreleased]`, labelled **`### Changed`** — not
      `BREAKING`. Nothing consumer-visible changes and no package contents change; what changes is
      this repository's close-out routine. **Pick an insertion anchor distinct from any other
      in-flight PR's and state it in the PR body**; `the-public-surface-is-declared-or-it-is-not-guarded`
      is open and will want one too.

- [ ] 3.4 Confirm the change moves no public surface and no package contents:
      `git diff main...HEAD -- 'src/**' '*PublicAPI*' '*CompatibilitySuppressions.xml'` is empty, and
      `Directory.Build.props` is absent from the diff.

- [ ] 3.5 **Verification.** On the integrated branch: `dotnet build Verbara.Sdk.slnx -c Release
      --no-incremental` with **0 warnings**; the full unit lane under the CI filter;
      `Tests/Verbara.Sdk.Governance.Tests` and `Tests/Verbara.Sdk.OpenTelemetry.Tests` (tree-scanning
      guards — green on touched projects is not green in CI); `bash scripts/tests/test_check_finding_ownership.sh`;
      `python3 -m unittest discover scripts/tests`; `openspec validate --all --strict` and
      `openspec validate --archived`. Then read `.github/workflows/ci.yml` and run the remaining fast,
      deterministic, non-service steps it lists rather than recalling job names.
      **Note in the commit that this change's own PR is not docs-only** — `scripts/ci/*.sh` and
      `.github/workflows/ci.yml` are nested non-doc paths (`classify-docs-only.sh:38`; the comment at
      `ci.yml:185` says so) — so it takes the same full lane 3.5 ran, and its own `OpenSpec Validate`
      and `Coverage Script Tests` runs exercise the check and its harness. The gate's *liveness* is
      proven by 3.1's control, not by a green run over a registry that is already correct.

- [ ] 3.6 Do **not** bump `Directory.Build.props`'s `PackageVersion`. The version is cut at release
      time (ADR-0055). Verify `PackageVersion` is absent from this change's diff.

## 4. Findings without an owner

*(The heading this change installs, used on itself. Every bullet below must have a registry row naming
an owner before this change can close — which is the mechanism proving itself. Each bullet takes its
`F-NNNN` prefix once task 1.2 assigns the identifiers; the check joins a bullet to its row by the
recording change's name and the bullet's bold lead-in, so the prefix is for the reader, not the key.)*

- **`RS0026` is demoted to `suggestion` in `.editorconfig:72`** for an "existing API" nobody has
  confirmed still exists. Recorded by `the-public-surface-is-declared-or-it-is-not-guarded` and
  seeded into the registry by task 1.2; owner to be named there, not here.
- **71 rows from the 2026-09-24 sweep have no `file:line` — 12 surviving mutants and 59 known only by
  category.** Task 1.3 gives this an owner. It is listed here as well because this change records it
  and therefore owes it a row under its own rule.
- **A guard the docs-only fast path skips is not a guard, inside the Governance project itself.**
  `ConformanceRecordGuardTests` reads `docs/guides/provider-wire-conformance.md` in the gated
  `Unit Tests` job, and that path is not in `classify-docs-only.sh`'s carve-out, so a docs-only pull
  request that deletes a row from the conformance record skips its only guard. Found while this change
  was reviewed; it is the failure D2 measures for this change's own gate, one artifact over. Task 1.2
  seeds the row and 1.7 opens its owner.
