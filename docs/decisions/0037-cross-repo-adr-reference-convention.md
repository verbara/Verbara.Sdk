# ADR-0037: Cross-repo ADR reference convention

- **Status:** Accepted
- **Date:** 2026-05-23
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0022 (Activity cancellation semantics — this repo). Disambiguates references to `Platform/ADR-0022` (Platform API AOT shipping path) that appear in v2.2.x commit messages of this repo.

## Context

The Verbara ecosystem spans four repositories, each maintaining its own append-only ADR catalog under `docs/decisions/`:

- `Verbara.Sdk` (this repo) — 36 ADRs at the time of writing (0001..0036) before this one.
- `Verbara.Sdk.Pro` — independent catalog.
- `Verbara.Platform` — independent catalog.
- `Verbara.Platform.Web` — independent catalog.

ADR numbers are local to the repo that owns them; they are not coordinated across repos. This is intentional — each repo's catalog grows at its own pace, and a global numbering scheme would force cross-repo coordination on every new decision.

The friction surfaces when a commit message, plan, or design doc in repo A references an ADR that lives in repo B with a **bare number**. The audit on 2026-05-23 discovered the concrete instance:

- `Verbara.Sdk/docs/decisions/0022-activity-cancellation-semantics.md` — Activity cancellation semantics (`IActivity.CancelAsync()` separate from `CancellationToken`). Accepted 2026-04-18.
- `Verbara.Platform/docs/decisions/0022-platform-api-aot-shipping-path.md` — Platform API AOT shipping path (Native AOT mandate + Dapper ban). Referenced from this repo's v2.2.x commit messages as "ADR-0022 Phase D" (Dapper removal, v2.2.0) and "ADR-0022 Phase A.5" (Cluster.Postgres, v2.2.1).

Readers landing on `git log` in `Verbara.Sdk` see "ADR-0022 Phase D" and reasonably assume it refers to this repo's local 0022 — which is about an unrelated topic. The references are correct in the system that authored them (the workspace-level constraint catalogued under Platform), but ambiguous to anyone outside that system.

## Decision

When a commit message, spec, plan, or ADR in this repository references an ADR that lives in another Verbara repository, the reference is **repo-qualified**:

- `Platform/ADR-NNNN` for `Verbara.Platform/docs/decisions/NNNN-*.md`
- `Pro/ADR-NNNN` for `Verbara.Sdk.Pro/docs/decisions/NNNN-*.md`
- `Web/ADR-NNNN` for `Verbara.Platform.Web/docs/decisions/NNNN-*.md`

Bare `ADR-NNNN` (without a repo prefix) always refers to this repo's catalog (`Verbara.Sdk/docs/decisions/`).

This is a forward-only convention. Existing commit messages (immutable history) keep their bare references — readers consult this ADR to disambiguate when needed. The same convention should apply symmetrically in each sister repo when adopted there (each repo's bare `ADR-NNNN` refers to its own catalog; cross-repo references are repo-qualified).

## Consequences

- **Positive:** New commits and docs become unambiguous to anyone reading them. No renumbering of any existing ADR is required (Accepted ADRs are immutable per `docs/decisions/README.md`). The convention is lightweight — a short prefix, no tooling.
- **Negative:** Historical commit messages (`git log`) remain ambiguous; readers must know about this ADR to disambiguate. Cross-repo references in plans/specs that pre-date this ADR are NOT being mass-updated — too much churn for marginal value; opportunistic updates only.
- **Neutral / trade-off:** A global cross-repo ADR registry would also solve the problem but adds coordination cost on every new ADR. Local-first catalogs match how each repo owns its decisions; the prefix convention preserves that.

## Alternatives considered

- **Global cross-repo ADR registry.** Maintain a single catalog covering all four repos with globally unique numbers. **Rejected:** introduces cross-repo coordination on every new ADR; turns a 1-min decision-recording workflow into a multi-repo synchronization step. Local ADR catalogs are already the convention and they work.
- **Renumber the SDK-local ADR-0022.** Move ADR-0022 → some unused slot, freeing "0022" globally. **Rejected:** violates the "Accepted ADRs are immutable" rule in `docs/decisions/README.md`. Renumbering rewrites history that readers may already have linked to.
- **Free-form textual prefix without standardizing.** "Workspace ADR-0022", "the Platform ADR about Dapper", etc. **Rejected:** ambiguity persists in different shapes; greppability is poor; AI tools and humans cannot reliably resolve the reference.
