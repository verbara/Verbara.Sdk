---
tier: PEQUEÑO
owner: Harol
approver: Harol
stakeholder: Public API consumers reading package READMEs (Sdk is MIT, downstream Pro/Platform)
decision_ref: verbara-meta/ADR-0007
---

# Proposal: docs-hygiene-sweep (sdk child)

## Why

The post-2.0.0 `Asterisk*` → `Verbara*` rename shipped the code but left **rebrand residue in
13 living docs** (package + example READMEs): `AddAsterisk*` DI-call names, `AsteriskOptions`,
`AsteriskServer(Pool)` types — symbols that no longer exist. `/xr:doctor`'s `rebrand-residue`
WARN family has stood yellow since run 1 because its evidence lines truncate to 5 files
(`head -5`); the real hit list is 13. Worse, one README documents a **fictional API** that never
existed post-rebrand (`AddAsteriskServerPool` with a builder callback), so a reader copy-pasting
it gets a compile error. This is the Sdk child of the cross-repo `docs-hygiene-sweep` train
(verbara-meta/ADR-0007 governs the doctor-driven cleanup); host is Verbara.Platform.

## What Changes

- Purge all `AddAsterisk*` DI-call identifiers across the 13 living docs, replacing each with the
  real extension-method name verified against
  `src/Verbara.Sdk.Hosting/ServiceCollectionExtensions.cs` (`AddVerbara`, `AddVerbaraSessions`,
  `AddVerbaraSessionsBuilder`, `AddVerbaraPush`, `AddVerbaraPushWebhooks`,
  `AddVerbaraPushAspNetCore`, `AddVerbaraResilience`).
- Purge the bare `AsteriskOptions` / `AsteriskServer` / `AsteriskServerPool` type references in
  `src/Verbara.Sdk.Hosting/README.md` (outside the doctor's regex but the same defect) →
  `VerbaraOptions` / `VerbaraServer` / `VerbaraServerPool`, verified against
  `src/Verbara.Sdk.Live/Server/` and `src/Verbara.Sdk.Hosting/`.
- **REWRITE** the fictional multi-server snippet at `src/Verbara.Sdk.Hosting/README.md:79`
  (`AddAsteriskServerPool(pool => …)`) against the canonical `Examples/MultiServerExample/`
  (`AddVerbaraMultiServer()` at DI time + runtime `VerbaraServerPool.AddServerAsync(...)`) — a
  token-swap is forbidden because there is **no** `AddVerbaraServerPool` to swap to.
- **Preserve** runtime data values that only *look* like residue: the NATS subject strings
  `asterisk.sdk.calls…` in `Examples/NatsBridgeExample/README.md`, and the `"Asterisk"` JSON
  config-section key in `src/Verbara.Sdk.Hosting/README.md` (the real code binds
  `GetSection("Asterisk:Ami")`). These are DATA, not identifiers — untouched.

No code, no public API, no package version change: prose/identifiers in shipped READMEs only.

## Capabilities

### New Capabilities

- `docs-brand-consistency`: shipped-doc identifiers name only real, post-rebrand public API
  symbols; runtime data values (config-section keys, message-bus subjects) are exempt.

### Modified Capabilities

(none)

## Impact

Documentation only — 13 tracked living `*.md` files under `Examples/*/README.md` and
`src/*/README.md`. No `.cs`, no `.csproj`, no `Directory.Build.props` `PackageVersion` bump
(README prose is not a shipped-binary change). A `CHANGELOG [Unreleased]` entry is added (the
`/xr:apply` commit gate requires one). Exempt from scope: `docs/decisions/`, `docs/specs/`,
`docs/plans/{completed,archived}/`, `docs/research/`, `openspec/changes/archive/`, and dated
`CHANGELOG` history — period-correct records stay verbatim.
