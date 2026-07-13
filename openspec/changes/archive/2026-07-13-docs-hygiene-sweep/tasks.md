# Tasks — docs-hygiene-sweep (sdk child)

## 1. Grounding

- [x] 1.1 Reproduce the doctor sweep and confirm the 13-file inventory:
      `git ls-files '*.md' | grep -v -E '^docs/decisions/|^docs/specs/|^docs/plans/completed/|^docs/plans/archived/|^docs/research/|^openspec/changes/archive/|CHANGELOG'`
      then grep each for `AddAsterisk|AsteriskTelemetry|AsteriskSemanticConventions|AsteriskProTracing`.
      Expected 13 files (5 Examples/Ami/Hosting + 8 Push*/Sessions*/Resilience README family).
- [x] 1.2 Verify the replacement symbols exist in real source before any edit:
      `AddVerbara*` in `src/Verbara.Sdk.Hosting/ServiceCollectionExtensions.cs`
      (`:31,140,174,191,207,255,272` etc.); `VerbaraServer`/`VerbaraServerPool` in
      `src/Verbara.Sdk.Live/Server/`; `VerbaraOptions` in `src/Verbara.Sdk.Hosting/`.
- [x] 1.3 Confirm the two data-value preserve-classes: the NATS subjects `asterisk.sdk.calls…`
      in `Examples/NatsBridgeExample/README.md` and the `"Asterisk"` config-section key in
      `src/Verbara.Sdk.Hosting/README.md` (real binding: `GetSection("Asterisk:Ami")`).

## 2. Identifier purge (verified replacements, 13 files)

- [x] 2.1 `Examples/NatsBridgeExample/README.md` — `AddAsteriskPush` → `AddVerbaraPush` (line ~38).
      Do NOT touch the `asterisk.sdk.calls…` NATS subject literals (runtime data).
- [x] 2.2 `Examples/SessionExample/README.md` — `AddAsteriskSessions()` → `AddVerbaraSessions()`,
      `AddAsterisk()` → `AddVerbara()` (lines ~24, 33).
- [x] 2.3 `Examples/SessionExtensionsExample/README.md` — `AddAsteriskSessions()` →
      `AddVerbaraSessions()`, `AddAsterisk()` → `AddVerbara()` (lines ~27, 35).
- [x] 2.4 `src/Verbara.Sdk.Ami/README.md` — `AddAsterisk(` → `AddVerbara(` (line ~18).
- [x] 2.5 `src/Verbara.Sdk.Push/README.md` — `AddAsteriskPush(` → `AddVerbaraPush(` (line ~40).
- [x] 2.6 `src/Verbara.Sdk.Push.Nats/README.md` — `AddAsteriskPush()` → `AddVerbaraPush()`
      (line ~13).
- [x] 2.7 `src/Verbara.Sdk.Push.AspNetCore/README.md` — `AddAsteriskPushAspNetCore()` →
      `AddVerbaraPushAspNetCore()` (line ~18).
- [x] 2.8 `src/Verbara.Sdk.Push.Webhooks/README.md` — `AddAsteriskPush()` → `AddVerbaraPush()`,
      `AddAsteriskPushWebhooks` → `AddVerbaraPushWebhooks` (lines ~11, 12, 45).
- [x] 2.9 `src/Verbara.Sdk.Resilience/README.md` — `AddAsteriskResilience(` →
      `AddVerbaraResilience(` (line ~30).
- [x] 2.10 `src/Verbara.Sdk.Sessions/README.md` — `AddAsterisk(` → `AddVerbara(`,
      `AddAsteriskSessions(` → `AddVerbaraSessions(` (lines ~17, 18, 38).
- [x] 2.11 `src/Verbara.Sdk.Sessions.Redis/README.md` — `AddAsteriskSessionsBuilder()` →
      `AddVerbaraSessionsBuilder()` (lines ~8, 21).
- [x] 2.12 `src/Verbara.Sdk.Sessions.Postgres/README.md` — `AddAsteriskSessionsBuilder()` →
      `AddVerbaraSessionsBuilder()` (lines ~16, 29).

## 3. Hosting README — identifiers + bare types + fictional-API rewrite

- [x] 3.1 `src/Verbara.Sdk.Hosting/README.md` DI-call + prose identifiers:
      `AddAsterisk(` → `AddVerbara(` (lines ~7, 28, 47, 90); the `AsteriskOptions` param in the
      `AddVerbara(IConfiguration | Action<VerbaraOptions>)` prose (line ~7).
- [x] 3.2 Bare type references (outside the doctor regex, same defect):
      `AsteriskOptions` → `VerbaraOptions` (line ~8), `AsteriskServer` → `VerbaraServer`
      (lines ~9, 11, 62), `AsteriskServerPool` → `VerbaraServerPool` (line ~11).
      Each verified against `src/Verbara.Sdk.Live/Server/` + `src/Verbara.Sdk.Hosting/` in 1.2.
- [x] 3.3 **CRITICAL — REWRITE the fictional multi-server snippet (line ~79).**
      `AddAsteriskServerPool(pool => { pool.AddServer("dc-east", o => …) })` documents an API that
      NEVER existed post-rebrand — there is NO `AddVerbaraServerPool` and no builder-callback
      overload. REWRITE (do not token-swap) against `Examples/MultiServerExample/`
      (`Program.cs` + `README.md`): DI-time `builder.Services.AddVerbaraMultiServer();` then, after
      build, resolve the pool and add servers at runtime —
      `var pool = host.Services.GetRequiredService<VerbaraServerPool>();`
      `await pool.AddServerAsync("pbx-east", new AmiConnectionOptions { Hostname = "pbx-east", Port = 5038, Username = "admin", Password = "secret" });`
      (repeat for `pbx-west`). Keep the `See Examples/MultiServerExample/` pointer.
- [x] 3.4 **PRESERVE** the `"Asterisk"` `appsettings.json` config-section key (line ~36) — it is a
      runtime binding key (`GetSection("Asterisk:Ami")`), NOT an identifier. Leave byte-for-byte.

## 4. Changelog

- [x] 4.1 Add a `CHANGELOG.md` `## [Unreleased]` entry under `### Changed` (or `### Docs`):
      "docs: purge post-rebrand `Asterisk*` residue from 13 living package/example READMEs;
      rewrite the fictional multi-server snippet in `Verbara.Sdk.Hosting/README.md` against the
      real `AddVerbaraMultiServer()` + `VerbaraServerPool.AddServerAsync()` API
      (verbara-meta/ADR-0007)." Required by the `/xr:apply` commit gate.

## 5. Verification

- [x] 5.1 Re-run the 1.1 sweep — zero `AddAsterisk|AsteriskTelemetry|AsteriskSemanticConventions|
      AsteriskProTracing` hits across the 13 living docs; zero bare `AsteriskOptions|AsteriskServer|
      AsteriskServerPool` in `src/Verbara.Sdk.Hosting/README.md`.
- [x] 5.2 Confirm data values survived: `grep -n 'asterisk\.sdk\.calls' Examples/NatsBridgeExample/README.md`
      still present; `"Asterisk"` config key still present in `src/Verbara.Sdk.Hosting/README.md`.
- [x] 5.3 Confirm the rewritten snippet names only real symbols
      (`AddVerbaraMultiServer`, `VerbaraServerPool`, `AddServerAsync`, `AmiConnectionOptions`) —
      grep each against real source.
- [x] 5.4 `openspec validate --all --strict` passes. No `dotnet` build/test task applies (docs-only,
      no code/`.csproj`/`PackageVersion` change), so no `dotnet test` gate for this change.
