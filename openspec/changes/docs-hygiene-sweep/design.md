## Context

Sdk child of the cross-repo `docs-hygiene-sweep` train (host: Verbara.Platform;
`decision_ref: verbara-meta/ADR-0007`). The train's `impact.yaml`
(`Verbara.Platform/openspec/changes/docs-hygiene-sweep/impact.yaml`) declares `fixtures: []` — no
child consumes any wire shape; this change touches only prose and code identifiers in shipped
READMEs. This design exists only because the schema wires `tasks` behind it; it is deliberately
minimal and carries no wire-shape decision.

## Goals / Non-Goals

**Goals:**

- Every code identifier in the 13 living docs names a real post-rebrand public symbol.
- The fictional multi-server snippet compiles against the real surface.
- Runtime data values (NATS subjects, the `"Asterisk"` config key) are provably untouched.

**Non-Goals:**

- No code, public API, or `PackageVersion` change (README prose is not a shipped-binary change).
- No consumption of any endpoint/DTO/wire shape (the train's `fixtures: []`).
- No edits to exempt trees (`docs/decisions/`, `docs/specs/`, `docs/plans/{completed,archived}/`,
  `docs/research/`, `openspec/changes/archive/`, dated `CHANGELOG` history).

## Decisions

- **Verify-then-replace, per identifier.** Each replacement is grep-confirmed against the real
  source before edit — `ServiceCollectionExtensions.cs` for `AddVerbara*`, `Live/Server/` for
  `VerbaraServer(Pool)`, `Hosting/` for `VerbaraOptions`. No blind sed.
- **Rewrite, don't token-swap, the fictional snippet.** `README.md:79`'s
  `AddAsteriskServerPool(pool => { pool.AddServer(…) })` has no post-rebrand equivalent (there is
  no `AddVerbaraServerPool`). It is rewritten to the canonical `Examples/MultiServerExample/`
  shape: `AddVerbaraMultiServer()` at DI time, then runtime
  `pool.AddServerAsync("pbx-east", new AmiConnectionOptions { … })`. Rationale: a swap would
  produce another fictional API.
- **Data values are preserved by enumeration, not by regex luck.** The NATS subjects and the
  `"Asterisk"` config-section key are called out as explicit preserve-cases (see spec) so no
  sweep step can silently touch them.

## Risks / Trade-offs

- [Over-scrub a data value] → the two known classes are enumerated as preserve-scenarios; a final
  grep confirms `asterisk.sdk.calls` subjects and the `"Asterisk"` config key survive verbatim.
- [Under-verify a replacement symbol] → every replacement is grep-verified against real source
  before edit; the tasks' verification step re-greps the 13 files for any surviving `AddAsterisk`
  / bare `Asterisk<Type>` token.

## Open Questions

None. Scope, exemptions, the fictional-API rewrite target, and the data-value preserve-list are
all fixed by the train contract and this repo's source; no wire shape is in play.
