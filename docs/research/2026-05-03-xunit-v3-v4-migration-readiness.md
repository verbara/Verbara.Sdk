# xUnit v3 / v4 Migration Readiness — Watch List

**Date:** 2026-05-03
**Status:** Watch (not migrating)
**Re-evaluate when:** any criterion below flips to ✓ — see *Re-evaluation criteria* and *When to migrate*.

## TL;DR

The SDK stays on **xunit 2.9.3** indefinitely. xunit.v3 (3.2.2 stable) and xunit.v4 (4.0.0-pre.108 alpha) are both tracked for adoption, but four readiness gates must flip first. Migrating today would (a) break the build day-1 due to a NSubstitute analyzer false-positive interacting with `TreatWarningsAsErrors=true`, and (b) not deliver the headline benefit (Native AOT in tests), which is exclusive to v4 alpha. Effort estimate when ready: **2–4 days**, ~70–100 files touched across 33 test projects.

## Current state (locked)

- xunit **2.9.3** + xunit.runner.visualstudio **2.8.2** + Microsoft.NET.Test.Sdk **17.14.1** — all supported, no end-of-life announced upstream.
- 33 test projects, 275 test files, 2,338 test method declarations (~2,802 unit tests including theory expansions).
- 5,371 FluentAssertions calls; **0** direct `Assert.X` calls.
- Pattern usage: 36× `IAsyncLifetime`, 56× `[Collection]`/`[CollectionDefinition]`, 8× `IClassFixture`/`ICollectionFixture`, 247× `[InlineData]`/`[MemberData]`, 7× `TheoryData<>`, 2× `ITestOutputHelper`, 4× `Skip="..."` strings.
- Build is `TreatWarningsAsErrors=true` globally (`Directory.Build.props`).
- `dependabot.yml` has explicit `ignore` rules for `xunit.runner.visualstudio` and `Microsoft.NET.Test.Sdk` major bumps — pinned to the v2 / v17 lines until this watch list flips.

## Why deferred

Four concrete blockers (with references):

1. **FluentAssertions 7.x detection bug under xunit.v3.** FA throws a generic `AssertionFailedException` instead of `XunitException`, so xunit cannot tag the failure as an assert vs. a code error. No fix confirmed in the FA 7.x branch as of 2026-05-03; FA 8.x ships under a commercial license (incompatible with this MIT SDK; see `dependabot.yml` ignore rule for FluentAssertions). Tracking: [fluentassertions #2935](https://github.com/fluentassertions/fluentassertions/issues/2935), [fluentassertions #2709](https://github.com/fluentassertions/fluentassertions/issues/2709).
2. **NSubstitute 5.x + xunit.v3 = `xUnit1051` false-positive.** xunit.v3 raises `xUnit1051` ("pass `TestContext.Current.CancellationToken`") on NSubstitute mock setup calls that cannot accept one. With `TreatWarningsAsErrors=true`, this **breaks the build on day 1**. Tracking: [xunit #3167](https://github.com/xunit/xunit/issues/3167).
3. **Native AOT in test projects only in v4 alpha.** The headline AOT benefit shipped only in xunit.v3 prerelease v4.0.x (current latest 4.0.0-pre.108), with caveats: no generic test method support, degraded stack traces, reduced object-formatting fidelity in assertion failures. v3.2.2 stable does **not** ship AOT.
4. **No reference migration in a comparable Microsoft .NET SDK.** dotnet/aspire migrated 2026-04 ([dotnet/aspire #8293](https://github.com/dotnet/aspire/issues/8293)) — too recent to use as a canary. dotnet/runtime, dotnet/aspnetcore, dotnet/efcore remain on xunit 2.x.

## Re-evaluation criteria (gates)

| # | Gate | Source to watch | Status @ 2026-05-03 |
|---|---|---|---|
| 1 | FA #2935 detection bug fixed in FluentAssertions 7.x | https://github.com/fluentassertions/fluentassertions/issues/2935 | ✗ open |
| 2 | xunit #3167 NSubstitute compat resolved (or analyzer suppression accepted) | https://github.com/xunit/xunit/issues/3167 | ✗ open |
| 3 | xunit.v3 v4.0 stable released with full Native AOT support | https://www.nuget.org/packages/xunit.v3 | ✗ at 4.0.0-pre.108 |
| 4 | Reference migration in dotnet/runtime *or* dotnet/aspnetcore | https://github.com/dotnet/runtime , https://github.com/dotnet/aspnetcore | ✗ both still on xunit 2.x |

## When to migrate

Migrate when **gate 1** is ✓ AND (**gate 2** is ✓ OR a reasoned analyzer suppression is documented in this file) AND **gate 3** is ✓.

**Gate 4 is informational** — a Microsoft canary migration is reassurance, not a precondition. If gates 1–3 flip cleanly and 4 lags, schedule the migration anyway.

## Effort estimate (when triggered)

| Area | Cost |
|---|---|
| Rename `xunit` → `xunit.v3` + `xunit.runner.visualstudio` 2.x → 3.x in [Directory.Packages.props](../../Directory.Packages.props) | minutes |
| Add `<OutputType>Exe</OutputType>` to 33 test csproj (xunit.v3 test projects are now executables) | minutes (script) |
| Migrate 36× `IAsyncLifetime` — now extends `IAsyncDisposable`; verify no fixture also implements `IDisposable` to avoid silent double-disposal | ~1 day |
| Move `using Xunit.Abstractions;` → `using Xunit;` for 2× `ITestOutputHelper` | trivial |
| Audit `[Collection]`/`IClassFixture`/`InlineData`/`TheoryData` (no API changes — *should* be untouched) | spot check |
| Re-establish coverage parity (coverlet 10.x ↔ MTP path if MTP is adopted) | ~0.5 day |
| Run full unit + functional + integration suite, fix regressions | ~0.5–1 day |

**Total:** 2–4 working days. Risk concentrated on `IAsyncLifetime` (silent regressions in fixture teardown order).

## Out-of-scope

- Migrating to **Microsoft Testing Platform (MTP)**. xunit.v3 supports it, but MTP is orthogonal to the v3 migration — the SDK stays on VSTest collector path consistent with `coverlet.collector` 10.x (post 2026-05-03 bump). Re-evaluate MTP separately when there's a concrete benefit.
- Migrating to **FluentAssertions 8.x**. Commercial license is incompatible with the MIT licensing of this SDK. Stay on FA 7.x; if FA 7.x ever stops receiving security fixes, evaluate alternatives (Shouldly, AwesomeAssertions fork) at that time.

## Sources of truth

- xUnit v3 Migration Guide: https://xunit.net/docs/getting-started/v3/migration
- What's New in xUnit v3: https://xunit.net/docs/getting-started/v3/whats-new
- xUnit v3 Native AOT support: https://xunit.net/docs/getting-started/v3/native-aot
- xUnit v3 release notes: https://xunit.net/releases/v3
- Microsoft Testing Platform code coverage: https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-code-coverage

## Maintenance

This document is updated **in place** when any gate flips, when a new blocker is discovered, or when the underlying assumption changes (e.g. xunit 2.x announces deprecation). It is **not** an ADR — ADRs are immutable post-Accepted; this watch list deliberately mutates as the upstream landscape evolves.
