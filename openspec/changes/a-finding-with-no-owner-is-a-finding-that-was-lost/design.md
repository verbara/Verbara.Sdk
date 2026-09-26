# Design: a-finding-with-no-owner-is-a-finding-that-was-lost

## Context

See `proposal.md` — *Why* for the measurement. The design-relevant shape of what is there today:

- `openspec/config.yaml:62` already requires the harvest and already says a label with no link does
  not exempt a finding. The rule is not missing; the mechanism is.
- Findings live in free prose inside `openspec/changes/archive/*/tasks.md`. Of 28 closed changes,
  **six** carry any recognisable phrase, against tens of follow-ups. A scan of the archive cannot
  recover what is already in it.
- `docs/claim-registry.md` exists to be *"the single answer to 'is this claim guarded?' — one file,
  rather than a search across the test projects, the AOT canary and the workflows"*, required by the
  living spec `claim-guards` under ADR-0042 D1. It is the exact shape this change needs, one artifact
  over.
- `claim-guards` also states the principle that settles what to do about the archive: dated records,
  `openspec/changes/archive/` named explicitly, are *"period-correct records and SHALL be left
  verbatim"*.
- **Measured, and it decides the enforcement point.** `scripts/ci/classify-docs-only.sh:30` treats
  `docs/*`, `openspec/*` and `CHANGELOG.md` as docs-only, so a close-out pull request takes the fast
  path. On close-out #316 the checks were: `Unit Tests` **SKIPPED**, `Coverage Ratchet` **SKIPPED**,
  `Pack Warnings Gate` **SKIPPED**, `Audit Test Asserts` **SKIPPED**. What ran was `Docs-only gate`,
  `Docs-only gate (CodeQL)`, `Dependency Review`, `Coverage Script Tests`, `OpenSpec Validate` and the
  short-circuited functional job.
- `OpenSpec Validate` already carries two steps, the second of which is this same class of gate:
  *"Archived changes must carry no unticked task."* A close-out property, checked at close-out.
- `Tests/Verbara.Sdk.Governance.Tests` is where this repository puts tree scans, in the shape
  `XxxScanner.cs` + `XxxGuardTests.cs` — `CancellationProvenanceGuardTests`,
  `ConformanceRecordGuardTests`, `LoopbackSeamGuardTests`, `FakeServerCaptureGuardTests`.

## Goals / Non-Goals

**Goals**

- Give every recorded finding an address that can be opened, and make the absence of one fail a check.
- Put that check where the pull request that closes a change will actually run it.
- Ship the registry with what is already known, so the guard has something to guard on day one.
- Say plainly what could not be recovered, instead of letting a seeded registry imply completeness.

**Non-Goals**

- Fixing any of the 83. This change gives them owners.
- Editing the 28 archived changes. Ruled out by the repository's own stated principle, not by
  convenience.
- Re-itemizing the 71 sweep rows that have no `file:line`. That is a separate, expensive read, and it
  gets a registry row of its own so it is owned rather than forgotten.
- Changing what `claim-guards` governs. The two capabilities are siblings; neither absorbs the other.

## Decisions

### D1 — A registry, not a scan of the archive to recover what is already there

The set of findings and their owners lives in one tracked file, `docs/findings-registry.md`, modelled
on `docs/claim-registry.md`.

*Why:* a scan cannot work backwards — six recognisable phrases across 28 closed changes is the
measurement, and the follow-ups number in the tens. A registry is also the artifact a reviewer can be
pointed at, which is the property `claim-registry.md` was created to provide for a different question.

*Why not edit the archive:* `claim-guards` already names `openspec/changes/archive/` as a
period-correct record to be left verbatim, and 26 of 28 are touched only by their own archive commit —
#290 (e91cb2d8) ticked residue in two to clear the `validate --archived` gate, the one time this
repository has edited the record for a gate. Adding `Owner:` lines to historical records would make
them describe a process that was not followed when they were written.

*Reading is not editing.* The check reads every `tasks.md` under `openspec/changes/`, the dated record
included; what it never does is write there. The close-out pull request is why: `openspec archive`
moves the change before the PR opens — #316's commit is six `R100` renames into
`archive/2026-09-25-…` — so a check that stops at `openspec/changes/*/` sees nothing on exactly the
pull request requirement 4 exists for. The second `OpenSpec Validate` step is the precedent:
`validate --archived` reads every archived change on every pull request.

### D2 — The gate is a step in `OpenSpec Validate`, not a Governance test

The check runs as a third step of the existing `OpenSpec Validate` job.

*Why:* a close-out pull request is docs-only, measured — `openspec/*` and `CHANGELOG.md` are both in
`classify-docs-only.sh`'s fast path, and on close-out #316 `Unit Tests` was **SKIPPED**. A Governance
test lives in that lane, so it would never run on the pull request that closes a change. It would pass
on every close-out by not executing, which is precisely the failure mode `claim-guards` already names:
*"a document whose … guard the docs-only CI fast path classifies as skippable has a decorative guard,
not a real one."* Putting the gate in `OpenSpec Validate` is not a preference; the alternative does
not work.

`OpenSpec Validate` is also the semantically right home: its second step already checks a close-out
property of archived changes.

*Alternative rejected:* add `docs/findings-registry.md` and `openspec/changes/**/tasks.md` to the
docs-only carve-out so the full lane runs on a close-out. It would work, and it would make every
close-out pay a full CI run — the unit lane, the coverage ratchet and the pack gate — for a check that
needs none of them. The carve-out exists for documents whose content is compiled; this is not that.

*Consequence to accept:* the check is a script, not a C# test, so it does not get the Governance
project's scanner infrastructure for free. Its harness goes in `scripts/tests/`, run by
`Coverage Script Tests`, which also runs on a docs-only pull request — measured on the same close-out.

This is the remedy verbara-meta/ADR-0016's 2026-08-17 addendum prescribes for a cheap, separable guard
over an allowlisted path — host it in an always-run **required** job; carve out only when it cannot be
separated — and ADR-0042 D3 is why it is a step in an existing required job rather than a job of its
own: a new job is a new context, and a new context is a branch-protection edit. (Citing a private
repository's ADR id in a public file is established practice here: `.github/workflows/ci.yml` cites
verbara-meta ADRs six times and `openspec/config.yaml` four.)

### D3 — A designated heading, so the signal exists before anything looks for it

A change records findings it does not fix under one designated heading in `tasks.md`. The check reads
that heading.

The heading is `## N. Findings without an owner`, matched by the check as the exact line
`^## [0-9]+\. Findings without an owner$` — never by a looser pattern on the word *Findings*, because
`a-published-surface-is-one-something-measures/tasks.md:188` is `## Findings this change corrects…`,
a section of corrections in a change this change may not edit. Two open changes already carry
findings under a heading of their own (`## 4. Measured elsewhere, or not yet owned`, `## 4. Recorded,
not fixed here`); Phase A renames those two lines, so the check's first run is on a tree it can pass.
There is no grandfathered spelling: a heading the check accepts "for now" is the label problem with a
different label.

*Why:* the present state is what free prose produces. The heading is the difference between a property
that can be checked and one that can only be hoped for. It also makes the check's failures precise —
it can say *this finding, in this change* rather than *something somewhere is unowned*.

*Second half, deliberately included and bounded:* the check also fails on the vocabulary
`openspec/config.yaml:62` already names as non-exempting when it appears outside the heading. Without
that, the heading becomes optional in practice: an author who writes the finding in a task note
satisfies the check by having an empty heading, which is the current behaviour with extra ceremony.
Measured over all 35 `tasks.md` (7 open, 28 archived): the ownership phrases — `no open change`,
`has no owner`, `not yet owned`, `needs its own` / `a new` / `an open change`, `it needs one`,
`finding, not deferred` / `not fixed`, `tracked separately`, `noted for the maintainer`,
`not deferred` — fire unconditionally; a phrase inside quotes, backticks or asterisks is a mention of
the label and does not fire (`a-published-surface…:185` quotes the label inside a harvest task;
`the-public-surface…:45` and `provider-dto-robustness-fences:209` use *out of scope* mid-sentence as
scope statements); `out of scope` fires only as a label — bold or sentence-initial — because
mid-sentence it is a scope statement half the time. Under those rules the open tree is clean once task
1.6 has renamed the two legacy headings, without editing a file this change is barred from, and the
archive yields ~14 real findings against one or two false positives. What this cannot catch is a
finding written with none of that vocabulary; the spec states that as a limit rather than solving it. A
false positive costs a reword on the pull request that introduced it.

### D4 — The registry ships seeded, and carries its own gap as a row

The registry lands with what survived the 2026-09-24 sweep — 12 defect rows (11 distinct) with
`file:line`, the perf gate that was never turned on, and two groups of five itemized by a grep — with
the findings the reconnect change, `the-public-surface-is-declared-or-it-is-not-guarded` and
`a-call-that-ended-is-released-when-it-ends` recorded, and with **one row for the 71 sweep rows that
have no `file:line`**, owned by whatever change eventually re-reads them.

*Why seeded:* a guard over an empty registry passes and proves nothing, and the 83 would stay exactly
as lost as they are now while the repository gained a process that says otherwise.

*Why the gap is a row:* this is the one place the change could quietly overstate itself. A seeded
registry reads as an inventory. Stating the unrecovered count as an owned row is the difference
between "we know of the rows 1.2 seeds" and "we know of those, and that 71 more rows exist — 12
surviving mutants and 59 known only by category — and here is who will find them". The earlier "~64"
was 83 − 19, and the 19 counted four findings from an open change that are not among the 89; the
seed's provenance is the sweep record in Appendix A, the only copy that survived.

### D5 — An owner must resolve, and must name the finding

A row's owner is the name of an open change under `openspec/changes/` or a decision record under
`docs/decisions/`, and the check resolves it.

*Why:* an unresolvable owner is a label with better punctuation. This is the failure that would
otherwise replace the current one — rows filled in to make the check pass, naming changes nobody
opened.

*Identifier and joins.* Every registry row carries `F-NNNN`, assigned in order and never reused. The
check joins a bullet under the designated heading to its row by the recording change's name and the
bullet's bold lead-in, verbatim and whitespace-normalised — the key every existing finding bullet
already has, in files this change may not otherwise edit — so `file:line` is informational and never
a key, and no recording bullet needs the identifier. The check joins a row to its owner by a literal
search for the row's `F-NNNN` in one file: the owning change's `tasks.md` (a task line, any position),
or the decision record's body or a dated `## Addendum`.

*Why the owner must name it:* the rule this change enforces already says "harvest … **into** an open
change or an ADR addendum" (`openspec/config.yaml` archive guidance; verbara-meta/ADR-0019 closing
routine step 2), not "point at". A row whose owner exists but never mentions the finding is that
rule's failure with better punctuation. For a change owner the gap is bounded — its folder moves at
close-out, the row stops resolving, and this check fails on that pull request. For a decision record
it is not: an ADR never archives and nothing ever re-homes its rows, so the token in the ADR is the
only evidence that will ever exist that the ADR is the decision about that finding. A task line in the
owner is also what makes the finding visible to `/xr:pending`, which reads `openspec list` and never
`docs/*-registry.md`.

*Why not `decision_ref`'s one-way shape:* `decision_ref` records why a change exists, and a wrong ref
is a mislabel. An owner records where work will happen, and a wrong owner is the loss this change
exists to prevent.

*A finding this repository cannot work* — a Pro or Platform site surfaced here — is owned by this
change's own decision record, whose body names the destination repository beside the identifier; the
work is hosted there via `/xr:change` (verbara-meta/ADR-0006). That is the one owner form D5 admits
for it, and the four such rows in the seed are the reason the ADR exists at authoring time rather
than as an addendum.

*Consequence:* the registry carries a `status` column with two machine-read states, and a row is
never deleted — `claim-registry.md` keeps its `**DELETED**` rows for the same reason. `OPEN`: the
owner MUST resolve to `openspec/changes/<owner>/` — the change's own directory, never a dated folder
under `openspec/changes/archive/` — or to `docs/decisions/<owner>*.md`, and MUST contain the row's
identifier. `CLOSED — <closer>`: the owner is not resolved; the closer MUST resolve to
`openspec/changes/archive/*-<closer>/` or a decision record, and a `#NNN` beside it is prose, not the
thing resolved. There is no third state: a row whose owner was archived and whose finding was not
fixed is a failure, and because `openspec archive` moves the owner's directory in the close-out commit
itself, that failure lands on the owning change's close-out pull request — where this obligation is
meant to bite.

### D6 — The guard's liveness is proven by a committed negative control

A script adds a finding with no owner, shows the check failing on it, and restores every file it
modified on every exit path.

*Why:* a guard over a registry that is already correct passes whether it works or not. The lesson is
one this repository has now paid for twice — `RS0016` was suppressed for months behind a green build,
and a functional check reported success in eight seconds without starting a container.

*Restore discipline, from the same experience:* restore from a copy taken before the edit, with the
trap installed before the first write; never `git checkout --`, `git restore` or `git stash`, which
replace a developer's uncommitted work with `HEAD`; and verify by comparing content, because an empty
difference report is also what a wrong restore produces.

### D7 — The pre-guard set is a committed list of archived names, not a date and not a diff

`scripts/ci/finding-ownership-pre-guard.txt` lists, by directory name, the 28 changes archived on the
day this merges. It is never appended to. A task list whose directory is in the list is exempt; every
other one, open or archived, gets the full check. No open change is listed: after Phase A (task 1.6
and the precision rules in D3) the open tree passes on its own.

*Why not a date cutoff:* the CLI prefixes `formatLocalDate()` at archive time, not merge time
(`@fission-ai/openspec@1.13.1`, `dist/core/archive.js:1142`). A close-out archived locally before this
lands and merged after carries a pre-cutoff date and would dodge the guard; a name list is clock-proof.
If another change archives before this merges, its name is absent, the merge queue fails visibly, and
the fix is to add the name in this pull request — the list is frozen at the merge, not at authoring.

*Why not a diff against the base:* it needs `fetch-depth: 0` and the event base on `OpenSpec Validate`
(verbara-meta/ADR-0016 §3.1), it is not reproducible on a plain checkout, it needs `--no-renames` — on
#316 default rename detection plus `--diff-filter=A` returns nothing over `openspec/changes/`,
measured — and it checks the property once: a registry row deleted after the close-out would pass.

*Consequence, measured:* 15 of 28 archived task lists carry a phrase D3's second half fails on. The
list is the only reason the script can exit 0 without editing any of them — which is also the honest
statement of what D1 means by "not a scan of the archive": the archive is read, never recovered from.

## Risks / Trade-offs

- **The registry becomes a graveyard** → rows accumulate, owners are named and never opened, and the
  check keeps passing. D5 makes an owner resolve to something real, which is the most this change can
  enforce; whether the owner does the work is not a property a build can check, and the ADR says so
  rather than implying the guard solves it.
- **The heading becomes a formality** → D3's second half fails on finding-shaped prose outside the
  heading, so the cheap way out is closed.
- **A close-out is blocked by a finding the author would rather defer** → that is the intended
  behaviour and the only friction this change adds. The escape hatch is a registry row naming a new
  change, which is thirty seconds and is exactly what `config.yaml:62` already asks for.
- **An archived owner orphans its rows** → D5's consequence; stated in the ADR and in the registry's
  own header so the next close-out knows it inherits them.
- **The check is a shell script rather than a C# test**, so it does not share the Governance project's
  scanner abstractions and needs its own harness. Accepted: D2's measurement leaves no alternative that
  runs where it must.

## Migration Plan

No consumer migration: nothing under `src/` changes, no public API moves, no package contents change.

The one migration is procedural. From the pull request that lands this, a change recording a finding
needs a registry row before it can close. The ADR carries that sentence and `openspec/config.yaml`'s
archive guidance gains the heading's name and the registry's path, so the rule and its mechanism are
stated in the same place a close-out already reads.

Two open changes record findings under an earlier heading and are migrated in Phase A by renaming that
one line each (task 1.6). Their findings are seeded into the registry in the same phase, so the
migration leaves neither change with a finding the check would call unowned.

Rollback is a revert. The registry's content is the only durable artefact, and it is worth keeping even
if the guard is removed — which the ADR notes, because a revert that deletes the seeded inventory would
lose what a sweep paid 814k tokens to recover.

## Open Questions

None. The enforcement point was the one genuine unknown and it is measured: a close-out pull request
is docs-only, the unit lane is skipped on it, and `OpenSpec Validate` and `Coverage Script Tests` both
run. The tasks execute that arrangement rather than search for one.


## Appendix A — the 2026-09-24 sweep, what survived of it

*This is the seed's provenance, the only surviving copy. It is an appendix rather than the separate
`sweep-2026-09-24.md` the audit asked for because the editing pass that landed these amendments was
scoped to this change's four planning artifacts; task 1.2 may split it out unchanged.*

**TL;DR.** On 2026-09-24 eight subagents read the `tasks.md` of the eight September archived changes — the archive folders dated 2026-09-13 to 2026-09-23 (the two 2026-09-24 archives, #304 and #308, landed the same day as the sweep) — and extracted **89** recorded follow-ups. **83** were verified still open against the tree: 45 hardening, 17 docs, 12 defect rows (11 distinct), 9 cleanup; 6 had been closed by later changes. Only these totals and the table below survived; the per-change breakdown and the agents' transcripts were not kept. Every source is an archived `tasks.md` in this repository; nothing here is private.

### Itemized with `file:line` — 12 defect rows, 11 distinct (re-verified 2026-09-25)

| Defect | Where | Sibling of |
|---|---|---|
| key-only `TryRemove` on a reusable channel id — a late hangup can unregister a live session of another call | `src/Verbara.Sdk.Ari/Audio/AudioSocketServer.cs:272`, `src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketServer.cs:210` | #281 |
| stores the `StartAsync` token for the process lifetime, `StopAsync` is `Task.CompletedTask`, handler never unsubscribed | `src/Verbara.Sdk.VoiceAi/Pipeline/VoiceAiSessionBroker.cs:34,52` | #286 |
| accept loop gated on the host start token — a bound, listening server whose loop never runs, reporting no fault | `src/Verbara.Sdk.VoiceAi.AudioSocket/AudioSocketServer.cs:80` | #281 |
| per-filter consume loop gated on `stoppingToken`; a stop racing subscription start drops that filter silently | `src/Verbara.Sdk.Push.Nats/NatsBridge.cs:236` | #286 |
| two empty `catch (IOException) { }` — a transport failure mid-call is indistinguishable from a clean hangup | `src/Verbara.Sdk.Ari/Audio/AudioSocketSession.cs:86,144` | #291 |
| multi-server DI registers no hosted service — no reconciliation sweep at all in a clustered deployment | `src/Verbara.Sdk.Hosting/ServiceCollectionExtensions.cs:212` | #286 |
| ADR-0050 E9: a synthesis cut short by barge-in still counts in `tts.syntheses.completed` and publishes `SynthesisEndedEvent` | `src/Verbara.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs:411` | #284 |
| flush race: a hangup between the write guard and the flush surfaces as `IOException` and is booked as a synthesis failure | `src/Verbara.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs:330` | #284 |
| descriptor leak window — a throw between accept and `Task.Run` leaks one per iteration, and under A′ the loop now continues | `src/Verbara.Sdk.Ari/Outbound/AriOutboundListener.cs:181` | #291 |
| counts a connection in `ActiveConnectionCount` before `OnNext`, so a throwing subscriber leaves a closed connection counted | `src/Verbara.Sdk.Ari/Outbound/AriOutboundListener.cs:289` | #291 |

Ten table rows, eleven sites (the `TryRemove` row is two), twelve sweep rows (one was recorded twice).

### Itemized with `file:line`, outside the table

- The perf gate was never turned on: `.github/workflows/perf-regression.yml:66` reads `PERF_GATE_ENFORCE: 'false'` since 2026-09-13, so `check-perf-baseline.py` emits `::warning::` and `gates.yaml` G6 reads `na` — it observes, it does not fail closed. Recorded in `openspec/changes/archive/2026-09-13-enforce-unguarded-public-claims/tasks.md`. Whether it is one of the 89 was not recorded.

### Counted, itemizable today by a grep

- Five hand-written `FakeTimeProvider` copies — `grep -rl 'class FakeTimeProvider' Tests/` (5 files on 2026-09-25).
- Five copies of the accept-backoff constants — `grep -rl InitialAcceptBackoff src/ --include='*.cs'` (5 files on 2026-09-25).

### Counted, not itemized

- 12 of the 45 hardening rows are surviving mutants — correct code no test binds. Only the archive's mutation tables can name them.
- The rest of 45 hardening + 17 docs + 9 cleanup, known by category only.

### Arithmetic

83 = 12 (defect rows, `file:line`) + 12 (surviving mutants, counted) + 59 (known only by category). Whether the perf gate and the two groups of five sit inside those 59 or outside the 83 was not recorded. **71 rows have no `file:line`.** An earlier draft said "~64": that was 83 − 19, and the 19 included four findings from `the-public-surface-is-declared-or-it-is-not-guarded`, which are not among the 89.
