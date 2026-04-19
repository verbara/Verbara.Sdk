# ADR-0004: Central Package Management with TreatWarningsAsErrors

- **Status:** Accepted
- **Date:** 2026-03-16 (retrospective)
- **Deciders:** Harol A. Reina H.

## Context

The SDK ships **24 NuGet packages** across `src/` and hosts 30 test projects plus 19 examples. Without central version management:

- Each csproj would pin its own `System.Text.Json` / `Microsoft.Extensions.*` versions.
- Diamond-dependency conflicts would surface at publish time, not at commit time.
- Security-patch rotation (Dependabot) would open 58 PRs for one CVE.
- Accidental preview or beta pulls (1.0.0-rc.1) would slip through review.

Separately, the project has a strong "no warnings" culture inherited from asterisk-java's zero-warning build. This requires every consuming project to respect `TreatWarningsAsErrors`.

## Decision

1. **Every NuGet version lives in `Directory.Packages.props`** with `ManagePackageVersionsCentrally=true`. Individual csprojs reference packages with `<PackageReference Include="Foo" />` only — no version attribute.
2. **`TreatWarningsAsErrors=true`** is set globally via `Directory.Build.props` for every src/ + Tests/ project. Build is **0 warnings, 0 errors** at all times.
3. **Analyzers** (`Microsoft.CodeAnalysis.PublicApiAnalyzers`, `BannedApiAnalyzers`, `Meziantou.Analyzer`, `IDisposableAnalyzers`) run on every build; warnings surface immediately.

## Consequences

- **Positive:** Single source of truth for versions — one Dependabot PR per package. Conflicts surface at build time. Public API changes require explicit `PublicAPI.Unshipped.txt` edits, which the analyzer enforces. Onboarding contributors just need `dotnet build` to know they didn't break anything.
- **Negative:** New projects must remember to declare the package reference without a version (IDE snippets sometimes add one). Preview packages (OpenTelemetry 1.15.2-beta.1 for Prometheus exporter) need explicit `<NoWarn>$(NoWarn);NU5104</NoWarn>` overrides.
- **Trade-off:** We accept slightly higher friction for brand-new dependencies (you must add them to `Directory.Packages.props` first) in exchange for a build that's deterministic across clones.

## Alternatives considered

- **Per-project versioning** — rejected for the reasons above (diamond conflicts, churn).
- **Manual `.nuspec` coordination** — rejected because `.nuspec` drives NuGet pack only, not restore.
- **`paket` or `project.json`** — rejected because Central Package Management is the MSBuild-native, first-class solution shipped with .NET 6+ and requires zero extra tooling.

## Notes

- See `Directory.Packages.props` for the current pins.
- Dependabot is configured in `.github/dependabot.yml` to group Microsoft.Extensions, test-stack, and analyzer bumps; explicit ignore rules document why specific majors are pinned (xunit 2.x, FluentAssertions 7.x, coverlet 6.x).
