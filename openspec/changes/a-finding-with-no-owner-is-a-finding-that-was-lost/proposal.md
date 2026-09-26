---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Every future reader of a change's close-out — and the eight September changes whose recorded defects nobody has picked up, four of which are still open in the tree today
decision_ref: Sdk/ADR-0064
---

# Proposal: a-finding-with-no-owner-is-a-finding-that-was-lost

## Why

Every bug-fix change in this repository finds the sibling sites of the defect it fixes, writes them
down honestly, and then nobody reads them back.

**Measured.** A sweep on 2026-09-24 across the eight September archived changes extracted **89**
recorded follow-ups and verified **83** still open against the tree: 45 hardening, 17 docs, 12 defect
rows (11 distinct), 9 cleanup. Six had been closed by later changes. Spot-checked again on 2026-09-25
and still open, with the same line numbers:

- `src/Verbara.Sdk.VoiceAi/Pipeline/VoiceAiSessionBroker.cs:34,52` — `StartAsync` stores the token for
  the process lifetime and `StopAsync` is `Task.CompletedTask`. That is the `#286` defect verbatim,
  in a second place, recorded when `#286` was fixed and never picked up.
- `src/Verbara.Sdk.Ari/Audio/AudioSocketSession.cs:86,144` — two empty `catch (IOException) { }`, so a
  transport failure mid-call is indistinguishable from a clean hangup.

**The rule already exists, a reviewer is what checks it, and that has failed three times.** The
harvest is verbara-meta/ADR-0019 item 3, byte-identical in every repo's `openspec/config.yaml`
archive guidance (here, line 62): *"harvest every deferred AND incidentally discovered follow-up into
an open change or an ADR addendum"*, and since the 2026-09-13 amendment a label such as *out of
scope*, *noted for the maintainer* or *tracked separately* **with no link does not exempt** a finding
and the close-out PR body carries a `Harvested:` line. ADR-0019's own Consequences say the guidance
"raises the floor; it does not enforce". It lapsed after being encoded on Sdk #215 and #263
(verbara-meta anti-patterns ledger row 7), and again on #316 (2026-09-25): no `Harvested:`, `Cited:`
or `Referrer sweep:` line in the body, the archived `tasks.md` saying "It has no open change of its
own" four times (counted in the file), and no `(#315)` in `CHANGELOG.md`. That third lapse is the condition
verbara-meta/ADR-0006's 2026-09-25 amendment named as what would move close-out into a CI-side
check. This change is that check, in the repo where the evidence is; the rule's owner records the
decision as ADR-0019 amendment 3 and names this repository wave 1.

**Why it fails, measured rather than guessed.** The follow-ups live inside task-completion notes in
`openspec/changes/archive/*/tasks.md` under **no consistent heading**, so they are not greppable. Of
the 28 archived changes, only **six** carry any signal phrase at all:

| phrase | archived changes containing it |
|---|---|
| `tracked separately` | 6 |
| `no open change` | 3 |
| `Out of scope` | 2 |
| `It needs one` | 1 |
| `not yet owned` | 1 |
| `has no owner` | 0 |

So a grep over the archive cannot find what is already there. That is a measurement, and it decides
the design: the enforcement has to be **forward-looking**, and the existing backlog has to be carried
by something other than a scan.

**Now, because the cost compounds.** Each change that closes produces more of these, and the last
sweep to recover them cost eight agents and 814k tokens. The next eight changes will produce another
eighty.

## What Changes

- **A findings registry, tracked, one row per finding, every row naming an owner.** The precedent is
  exact and already in this repository: `docs/claim-registry.md` exists to be *"the single answer to
  'is this claim guarded?' — one file, rather than a search across the test projects, the AOT canary
  and the workflows"*, and `claim-guards` requires it because ADR-0042 D1 says a figure with no row
  does not ship. A finding with no row is the same failure on a different artifact. An owner is an
  open change or an ADR — never a prose label.
- **A consistent, greppable heading** in a change's `tasks.md` for findings it records but does not
  fix, so the signal exists in the first place. Six phrases across 28 changes is what "record it
  honestly and hope" produces.
- **A guard that runs on the pull request that closes a change.** The check is a script,
  `scripts/ci/check-finding-ownership.sh`, run as a third step of the existing, required
  `OpenSpec Validate` job — **not** a test in `Tests/Verbara.Sdk.Governance.Tests`. A close-out pull
  request is docs-only (`scripts/ci/classify-docs-only.sh:30` puts `openspec/*` on the fast path), and
  on close-out #316 `Unit Tests` — the job that runs every Governance test — was **SKIPPED** while
  `OpenSpec Validate` and `Coverage Script Tests` ran. A Governance test would pass every close-out by
  not executing (design D2). The check reads every task list under `openspec/changes/`, the dated
  record included, because `openspec archive` moves the change before the close-out PR opens (#316 is
  six `R100` renames), and it fails when a registry row names no owner; when an owner does not resolve
  or does not carry the row's identifier; when a change records a finding under the designated heading
  with no registry row; and when finding-shaped prose appears outside that heading. Its harness,
  `scripts/tests/test_check_finding_ownership.sh`, runs in `Coverage Script Tests`, which is likewise
  required and always-run.
- **The registry ships seeded, not empty.** It lands with what survived the sweep, attached as
  Appendix A of `design.md` in this change: 12 defect rows (11 distinct) with `file:line`, the perf
  gate that was never turned on, and two groups of five itemized by a grep — and with the findings the
  reconnect change, `the-public-surface-is-declared-or-it-is-not-guarded` and
  `a-call-that-ended-is-released-when-it-ends` recorded. Seeding matters: an empty registry with a
  guard over it is a guard with nothing to guard.
- **BREAKING for the close-out routine, not for any consumer.** A change that records a finding
  cannot archive without a registry row for it. That is the point, and it is the only thing about
  this change anyone will feel.
- **Not in scope, deliberately:** fixing any of the 83. This change gives them owners; the owners do
  the work. Also out of scope: **editing the archived changes**. 26 of 28 are touched only by their own
  archive commit; #290 (e91cb2d8) ticked residue in two to clear the `validate --archived` gate — the
  one time this repository has edited the record for a gate, and the reason this change states the
  choice rather than assumes it. `claim-guards` already states the principle for this repository — dated records
  including `openspec/changes/archive/` are *"period-correct records and SHALL be left verbatim"*.
  Retrofitting `Owner:` lines into 28 historical records is rejected on that basis, and the registry
  is what carries them instead.

## Capabilities

### New Capabilities

- `finding-ownership` — what this repository guarantees about a defect or follow-up it discovered but
  did not fix: that it is recorded where something can find it, that it names an owner which is an
  open change or an ADR rather than a label, and that the guard saying so is itself demonstrable.

### Modified Capabilities

None. `claim-guards` governs **quantitative claims in living public documents** — a number a reader
takes as current — and its registry is for guard classes. A finding is not a number and the archive is
outside that capability's stated scope. The two are siblings by design, and this proposal cites
`claim-guards` as the precedent rather than extending it.

## Impact

- **New:** `docs/findings-registry.md`, seeded; `scripts/ci/check-finding-ownership.sh` (the check),
  `scripts/tests/test_check_finding_ownership.sh` (its harness),
  `scripts/ci/check-finding-ownership-guard-fires.sh` (the negative control) and
  `scripts/ci/finding-ownership-pre-guard.txt` (the frozen list of 28 archived names, design D7);
  `docs/decisions/0064-*.md`, at whichever number is free when task 1.4 runs.
- `.github/workflows/ci.yml` — `OpenSpec Validate` gains the check as a third step and
  `Coverage Script Tests` gains the harness. Both are required contexts with no `needs: gate` edge, so
  both run on a docs-only close-out. No new job and no new required check (ADR-0042 D3).
- **This change's own pull request is not docs-only** — `scripts/ci/*` and `.github/workflows/ci.yml`
  are nested non-doc paths (`classify-docs-only.sh:38`) — so it takes the full lane, and its own
  `OpenSpec Validate` and `Coverage Script Tests` runs exercise the gate it installs.
- `openspec/config.yaml` — two edits of different ownership. `rules.tasks` gains the heading's name and
  a pointer to the registry: Sdk-local (task 2.1), because `rules.tasks` already differs across the
  three .NET repos. `operations.archive.guidance` item 3 — the harvest entry at line 62, byte-identical
  in five repos — takes the amendment's wording verbatim (task 2.4, blocked on the precondition below),
  so the rule and the mechanism that enforces it are stated in the same place without forking a uniform
  block.
- `docs/decisions/NNNN-*.md` — adding an ADR also moves `README.md`'s `**N ADRs**` figure, its
  `docs/claim-registry.md` row and the `docs/decisions/README.md` catalog row, all in the same pull
  request.
- **No source file under `src/` changes, no public API moves, and no package contents change.** No
  consumer is affected in any way.
- **Precondition, outside this repository:** verbara-meta lands ADR-0019 amendment 3 (item 3 becomes a
  deterministic gate per verbara-meta/ADR-0012, stated as a per-repo property: the close-out PR's own
  change is inspected although the CLI has already moved it; an owner resolves and carries the
  finding; a negative control is committed; landing per repo once its registry is seeded, as
  verbara-meta/ADR-0023 §7 staggered `validate --archived`), the uniform wording of the harvest entry
  that task 2.4 applies here, the ledger row 7 update and the ADR-0006 note that its flip condition
  fired on #316. That amendment is its own pull request from a verbara-meta session — this change
  neither writes nor edits it, and task 1.8 verifies it has merged before task 2.4 lands the Sdk
  instance of the wording. This change's ADR cites that amendment; the amendment names this change as
  wave 1.
- **Coordination:** `the-public-surface-is-declared-or-it-is-not-guarded` and
  `a-call-that-ended-is-released-when-it-ends` are open and record four findings each under a heading
  of their own; this change seeds registry rows for all eight and edits exactly two lines in each
  file — the heading, and the task line that carries an owned row's identifier. Nothing else in them
  changes. `a-published-surface-is-one-something-measures` belongs to another session and records no
  deferred finding.
- **What this change does NOT claim:** that the 83 are fully itemized. **Twelve** defect rows and the
  perf gate are, with `file:line`; two groups of five are itemized by a grep; the other **71** rows are
  counted, not itemized — 12 surviving mutants and 59 known only by category — because re-deriving
  them means re-reading the eight archived `tasks.md` files, which is the cost that produced the count.
  What survived the sweep is Appendix A of `design.md` in this change; it is the only copy, and the
  agents' transcripts were not kept. The registry carries the 71 as a row of its own rather than
  implying a complete inventory.
