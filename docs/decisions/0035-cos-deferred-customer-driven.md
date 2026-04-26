# ADR-0035: COS (Calling Permissions System) deferred — customer-driven trigger only

- **Status:** Accepted (deferral locked 2026-04-25; branch retained, not deleted)
- **Date:** 2026-04-25
- **Deciders:** Harold Reina
- **Renumbered:** 0031 → 0035 on 2026-04-26 (administrative — original number collided with prior Proposed ADR-0031 "Domain vs Integration events" added 2026-04-20 in the Event Model v2 batch). **Decision content unchanged.**
- **Related:**
  - R5 release train spec §344-356 "Branch `feat/calling-permissions` decision gate" (`Asterisk.Platform/docs/plans/active/2026-04-22-r5-production-readiness-release-train.md`)
  - Post-ship R5.1 triage D-FORCE-1 (`Asterisk.Platform/docs/plans/active/2026-04-25-r5.1-post-ship-triage.md`)
  - SDK ADR-0028 (cadence: 0 breaking in minors)
  - SDK ADR-0027 (stewardship pledge)

## Context

Branch `feat/calling-permissions` exists in this repo with **14 commits** spanning marzo 2026 (range `0f8aa04..9e6bb13`). Contents:

- `feat(cos): add COS database schema and model types`
- `feat(cos): add Pattern Groups list and edit pages`
- `feat(cos): add COS service with CRUD, assignment, and audit logging`
- `feat(cos): add pattern group service with Asterisk pattern validation`
- `feat(cos): add File repository implementations and COS resolver`
- `feat(cos): add COS repository interfaces and DB implementations`
- `feat(cos): add COS dialplan generator with Asterisk context hierarchy`
- `feat(cos): integrate COS with extension service, softphone, and DI`
- `feat(cos): add context discovery via AMI dialplan show`
- `feat(cos): add bulk COS assignment and dial simulator pages`
- `feat(cos): add Calling Permissions list, detail, and edit pages`
- `feat(cos): integrate COS dropdown into extension pages + Home KPIs`
- `feat(cos): add demo seed data and COS dialplan for both server modes`
- `feat(cos): add dial simulator with pattern matching and trace logging`

**Branch divergence:** 14 commits ahead of `main`, **298 commits behind `main`**. Rebase cost is non-trivial and grows monotonically each week the branch sits unmerged.

**Functional surface:** Calling Permissions System (COS) — pattern groups, dial-rule matching, dial simulator, AMI dialplan integration, PbxAdmin demo integration. Solves the "which extensions can dial what numbers" problem (long distance / international / blocked patterns).

**Why now is the decision:** R5 release train spec §344-356 defined gates for re-evaluation:

- **Gate R5.1 post-ship:** if customer enterprise asks for COS explicitly → merge to R5.2 with UI add-on.
- **Gate R5.2 post-ship:** re-evaluate; if no demand → deprecate explicitly.
- **Default if no gate trigger:** deprecate post-R5.3 with ADR explícito.

R5.1 shipped 2026-04-23. **No customer signal triggered Gate R5.1.** Triage D-FORCE-1 forces explicit decision now to stop branch rot rather than wait for R5.2/R5.3 gates.

## Decision

**Defer indefinitely. Branch retained, not deleted. Re-execution path is customer-driven trigger only.**

### What this means concretely

1. **Branch stays in place** as historical record. No deletion. Tag-equivalent: this ADR + branch HEAD `9e6bb13` are the canonical reference for what was built.
2. **No rebase, no merge, no QA.** Branch will continue diverging from `main`; that is intentional — the cost of carrying it as merge-ready is not warranted by current demand.
3. **No new feature work** lands on this branch. If a contributor wants to add to COS, they re-baseline on a fresh branch from the current `main` and deliver as a new feature spec.
4. **NuGet packages NOT published** for COS. The work remains source-only on the branch.
5. **PbxAdmin integration removed mentally** from the v1.x demo asset list. PbxAdmin demos run without COS.

### Trigger conditions for re-execution

This deferral is **lifted only if all of the following are true:**

1. **A specific customer or revenue-bearing deal** explicitly requires COS (signed engagement letter or formal RFP item naming "calling permissions" / "dial restrictions" / "pattern-based dial control"). "Nice to have" feedback does not qualify.
2. **The customer use case maps cleanly** to the existing COS design (pattern groups + dial simulator + dialplan generator). If the actual need is, say, schedule-based routing or call accounting, this branch is the wrong solution and a fresh design is required.
3. **The deal economics justify** ~M-L scope (rebase 298 commits + UI Platform integration + QA + docs + AOT validation + tests). Approximate dev investment: 2-3 weeks.

If 1+2+3 all hold, this ADR is amended (status flipped to "Superseded by [new ADR]") and a fresh execution plan is opened in `docs/plans/active/`.

### Re-baselining strategy if triggered

Do **not** rebase the existing branch. The 298-commit divergence makes rebase a higher-cost path than re-implementation. Instead:

1. Cherry-pick the design (database schema, pattern matching algorithm, AMI dialplan integration approach) — these are the durable insights.
2. Re-implement against current `main` using current testing patterns (Testcontainers, AOT-safe DI, current ActivitySource/Meter conventions).
3. Treat the original branch as **specification reference**, not as code to merge.

## Consequences

**Positivas:**
- Branch rot debt is **bounded** — the team stops paying weekly cost to "keep the option open".
- Future contributors are not confused by a stale branch with marketing-sounding name.
- `feat/calling-permissions` becomes a clear historical artifact rather than ambiguous WIP.
- R5 acceptance criterion #6 ("branch resolved — merged or explicitly deprecated via ADR") is now satisfied without forcing a merge that has no customer backing.
- Aligns with stewardship pledge (ADR-0027): we don't ship features without demand to inflate Pro tier.

**Negativas:**
- **Investment of ~2-3 weeks of historical dev work is not directly monetizable** until a customer triggers re-execution. The work is not lost (branch retained) but it does not ship.
- **PbxAdmin demo loses a feature** that some early-adopter audiences may have valued; demo storyline narrows slightly.
- **Future re-execution cost is higher than merge-now would have been** because of compounding divergence. Deliberate trade: avoid certain near-term cost, accept variable future cost contingent on a deal that may never materialize.

## Alternatives considered

- **Merge to `main` now** (option (a) from triage / Gate R5.1 of release train spec): rejected — no customer signal exists, violates "0 breaking changes in minors" via surface-area expansion, and adds maintenance burden (tests, docs, AOT validation, future telemetry) for zero validated demand.
- **Merge as opt-in `Asterisk.Sdk.CallingPermissions` package** (release train alternative): rejected — same demand problem; introduces a new package in 1.15.x cadence without spec acceptance, expanding the public API surface SDK is committed to maintaining.
- **Delete the branch**: rejected — destroys ~2-3 weeks of design work that may become valuable if a customer triggers re-execution. Retention cost is approximately zero (a branch reference in git).
- **Status quo (no decision, branch sits indefinitely)**: rejected — that's exactly what triage D-FORCE-1 surfaces as a problem. Ambiguity carries weekly cognitive cost on R5.x planning.

## References

- Original branch HEAD: `9e6bb13` `feat(cos): add demo seed data and COS dialplan for both server modes`
- Original branch base divergence point: ~marzo 2026 (~298 commits behind `main` as of 2026-04-25)
- Release train decision gates: `Asterisk.Platform/docs/plans/active/2026-04-22-r5-production-readiness-release-train.md` §344-356
- Triage forcing decision: `Asterisk.Platform/docs/plans/active/2026-04-25-r5.1-post-ship-triage.md` §D-FORCE-1
- R5 acceptance criterion #6: `Asterisk.Platform/docs/plans/active/2026-04-22-r5-production-readiness-release-train.md` §"Aceptación final del Release Train R5"
