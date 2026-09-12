# xUnit v3 / v4 Migration Readiness — Watch List

**Date:** 2026-05-03 · **Last updated:** 2026-09-12 (gates re-verified with a probe project — see *Verification 2026-09-12*)
**Status:** Watch (not migrating). Gates 1 and 3 have flipped; gate 2 cannot flip upstream, so migration now waits on an **owner ruling** — see *When to migrate*.
**Re-evaluate when:** any criterion below flips to ✓ — see *Re-evaluation criteria* and *When to migrate*.

## TL;DR

The SDK stays on **xunit 2.9.3** for now. As of 2026-09-12, two of the three blocking gates have flipped. FluentAssertions 7.x reports its failures as xunit assertions under xunit.v3 — the fix was backported to 7.1.0 in January 2025, so this document's May claim that no 7.x fix existed was already wrong. And xunit.v3 **4.0.0** shipped stable on 2026-08-15. The remaining gate will never flip upstream: xunit closed #3167 as won't-fix, so NSubstitute setups that omit an optional `CancellationToken` still raise `xUnit1051`, which `TreatWarningsAsErrors=true` turns into a build error. Migration therefore needs a decision rather than a wait: suppress `xUnit1051`, or pass `Arg.Any<CancellationToken>()` at each affected setup.

The same check surfaced a new cost. On the .NET 10 SDK, `dotnet test` refuses to run an xunit.v3 4.x project in VSTest mode, so migrating also means adopting Microsoft Testing Platform and moving coverage off the `coverlet.collector` VSTest path. Effort estimate when triggered: **3–5 days** (2026-05-03: 2–4).

## Current state (locked)

Counts refreshed 2026-09-12 by `grep` over `Tests/`. They are occurrence counts, and the 2026-05-03 figures (in parentheses) were taken with a different method, so the two are not strictly comparable.

- xunit **2.9.3** + xunit.runner.visualstudio **2.8.2** + Microsoft.NET.Test.Sdk **17.14.1**, with xunit.analyzers **2.0.0**, FluentAssertions **7.2.2**, NSubstitute **6.2.0** and coverlet.collector **10.0.1**.
- 34 test projects reference xunit (33); 311 files carry `[Fact]`/`[Theory]` (275); 2,828 `[Fact]`/`[Theory]` attributes (2,338 method declarations).
- 6,539 `.Should()` calls (5,371 FluentAssertions calls); 122 `Substitute.For` sites (not counted before).
- Pattern usage: 46 `IAsyncLifetime` occurrences (36), 69 `[Collection]`/`[CollectionDefinition]` (56), 6 `ITestOutputHelper` occurrences (2), 3 `Skip = "..."` strings (4). Not re-counted: 8× `IClassFixture`/`ICollectionFixture`, 247× `[InlineData]`/`[MemberData]`, 7× `TheoryData<>` (2026-05-03).
- Build is `TreatWarningsAsErrors=true` globally (`Directory.Build.props`).
- `dependabot.yml` keeps `ignore` rules for major bumps of `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` and `FluentAssertions`, pinned until this watch list flips (re-checked 2026-09-12).
- CI collects coverage with `--collect:"XPlat Code Coverage" --settings coverlet.runsettings` (`ci.yml`) — the VSTest data-collector path.

## Why deferred

The four blockers as recorded on 2026-05-03, each followed by its status on 2026-09-12:

1. **FluentAssertions 7.x detection bug under xunit.v3.** FA throws a generic `AssertionFailedException` instead of `XunitException`, so xunit cannot tag the failure as an assert vs. a code error. No fix confirmed in the FA 7.x branch as of 2026-05-03; FA 8.x ships under a commercial license (incompatible with this MIT SDK; see `dependabot.yml` ignore rule for FluentAssertions). Tracking: [fluentassertions #2935](https://github.com/fluentassertions/fluentassertions/issues/2935), [fluentassertions #2709](https://github.com/fluentassertions/fluentassertions/issues/2709).

   **2026-09-12 — resolved, and already resolved in May.** The fix was backported to the 7.x line in [fluentassertions #2970](https://github.com/fluentassertions/fluentassertions/pull/2970) (merged into `support-7.0` on 2025-01-16) and shipped in **7.1.0** on 2025-01-17, the day #2935 closed. Probe: under xunit.v3 4.0.0 with FA 7.2.2, a failing `1.Should().Be(2)` throws `Xunit.Sdk.XunitException` from `xunit.v3.assert`, which implements `Xunit.Sdk.IAssertionException` — the interface xunit uses to classify a failure as an assertion.

2. **NSubstitute 5.x + xunit.v3 = `xUnit1051` false-positive.** xunit.v3 raises `xUnit1051` ("pass `TestContext.Current.CancellationToken`") on NSubstitute mock setup calls that cannot accept one. With `TreatWarningsAsErrors=true`, this **breaks the build on day 1**. Tracking: [xunit #3167](https://github.com/xunit/xunit/issues/3167).

   **2026-09-12 — will not be fixed upstream.** #3167 was closed as won't-fix: at analysis time `Substitute.For<T>()` only returns a `T`, so the analyzer cannot tell a mock setup from a real call, and the maintainer's guidance is to disable `xUnit1051` or pass the token argument explicitly. Probe with xunit.analyzers 2.0.0 (the version this repo already pins) and NSubstitute 6.2.0: the issue's repro — a setup of `GetSomethingAsync(Arg.Any<int>())` on a method whose `CancellationToken` parameter is optional — fails the build with `error xUnit1051`; rewritten as `GetSomethingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())` it builds clean. How many of this repo's 122 `Substitute.For` sites would trip the rule is unknown until a trial branch runs the analyzer.

3. **Native AOT in test projects only in v4 alpha.** The headline AOT benefit shipped only in xunit.v3 prerelease v4.0.x (current latest 4.0.0-pre.108), with caveats: no generic test method support, degraded stack traces, reduced object-formatting fidelity in assertion failures. v3.2.2 stable does **not** ship AOT.

   **2026-09-12 — resolved.** `xunit.v3` **4.0.0** was published stable on nuget.org on 2026-08-15, and xunit.analyzers 2.0.0 adds rules specific to v3 Native AOT projects. The AOT caveats listed above were not re-checked.

4. **No reference migration in a comparable Microsoft .NET SDK.** dotnet/aspire migrated 2026-04 ([dotnet/aspire #8293](https://github.com/dotnet/aspire/issues/8293)) — too recent to use as a canary. dotnet/runtime, dotnet/aspnetcore, dotnet/efcore remain on xunit 2.x.

   **2026-09-12 — still not.** `dotnet/aspnetcore`'s `eng/Versions.props` pins xunit **2.9.2**. `dotnet/runtime`'s `eng/Versions.props` does not pin xunit directly, so its state was not determined.

### New blocker found 2026-09-12 — `dotnet test` requires Microsoft Testing Platform

On the .NET 10 SDK (10.0.401), `dotnet test` on an xunit.v3 4.0.0 project with `xunit.runner.visualstudio` 4.0.0 and `Microsoft.NET.Test.Sdk` 18.10.0 fails before running a single test:

```
error : Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later.
```

The same project passes under `dotnet test` once `global.json` sets `"test": { "runner": "Microsoft.Testing.Platform" }`, and it also runs as an executable (`dotnet run`). The probe did not try to force the VSTest path back, for example by disabling the MTP MSBuild integration; whether that is possible, and supported, belongs to the trial. Unless it is, MTP is no longer orthogonal to this migration (see *Out-of-scope*): CI's `--collect:"XPlat Code Coverage" --settings coverlet.runsettings` is a VSTest data collector, so coverage — ratchet included — has to move to an MTP-compatible collector in the same change.

## Re-evaluation criteria (gates)

| # | Gate | Source to watch | Status @ 2026-05-03 | Status @ 2026-09-12 |
|---|---|---|---|---|
| 1 | FA #2935 detection bug fixed in FluentAssertions 7.x | https://github.com/fluentassertions/fluentassertions/issues/2935 | ✗ open | **✓** fixed in 7.1.0 (#2970); verified under xunit.v3 4.0.0. The May "open" was inaccurate — #2935 had been closed since 2025-01-17 |
| 2 | xunit #3167 NSubstitute compat resolved (or analyzer suppression accepted) | https://github.com/xunit/xunit/issues/3167 | ✗ open | **✗ won't fix upstream** — only the suppression branch of the rule remains; owner ruling pending |
| 3 | xunit.v3 v4.0 stable released with full Native AOT support | https://www.nuget.org/packages/xunit.v3 | ✗ at 4.0.0-pre.108 | **✓** 4.0.0 stable, 2026-08-15 (AOT caveats not re-checked) |
| 4 | Reference migration in dotnet/runtime *or* dotnet/aspnetcore | https://github.com/dotnet/runtime , https://github.com/dotnet/aspnetcore | ✗ both still on xunit 2.x | ✗ aspnetcore on 2.9.2; runtime not determined |

## When to migrate

Migrate when **gate 1** is ✓ AND (**gate 2** is ✓ OR a reasoned analyzer suppression is documented in this file) AND **gate 3** is ✓.

**As of 2026-09-12, gates 1 and 3 are ✓ and gate 2 cannot become ✓**, so the rule reduces to one decision this document does not make:

- **Suppress `xUnit1051`** in the test projects. It is one line, but it also silences the rule where it is right: a real call that should forward `TestContext.Current.CancellationToken`.
- **Keep `xUnit1051` and pass `Arg.Any<CancellationToken>()`** at every NSubstitute setup that omits an optional token. That keeps the rule's value, at a per-site edit whose count is unknown until a trial branch runs the analyzer.

A trial branch that migrates one test project and counts the `xUnit1051` hits would turn this into a measured choice. Record the ruling here, with its reasoning, before migrating.

**Gate 4 is informational** — a Microsoft canary migration is reassurance, not a precondition. If gates 1–3 flip cleanly and 4 lags, schedule the migration anyway.

## Effort estimate (when triggered)

| Area | Cost |
|---|---|
| Rename `xunit` → `xunit.v3`, and move `xunit.runner.visualstudio` / `Microsoft.NET.Test.Sdk` to lines compatible with it (the probe used 4.0.0 / 18.10.0), in [Directory.Packages.props](../../Directory.Packages.props); lift the matching `dependabot.yml` ignores | minutes |
| Add `<OutputType>Exe</OutputType>` to the 34 test csproj (xunit.v3 test projects are executables) | minutes (script) |
| Migrate the `IAsyncLifetime` implementations (46 occurrences) — it now extends `IAsyncDisposable`; verify no fixture also implements `IDisposable` to avoid silent double-disposal | ~1 day |
| Move `using Xunit.Abstractions;` → `using Xunit;` for `ITestOutputHelper` (6 occurrences) | trivial |
| Audit `[Collection]`/`IClassFixture`/`InlineData`/`TheoryData` (no API changes — *should* be untouched) | spot check |
| **Adopt Microsoft Testing Platform** — `global.json` test runner, every `dotnet test` invocation in CI and scripts | ~0.5 day |
| **Move coverage off `--collect:"XPlat Code Coverage"`** to an MTP-compatible collector and re-establish parity with the coverage ratchet | ~0.5–1 day |
| Resolve `xUnit1051` per the gate-2 ruling | unknown until the trial counts the hits |
| Run full unit + functional + integration suite, fix regressions | ~0.5–1 day |

**Total:** 3–5 working days (2026-05-03: 2–4, before the MTP requirement was known). Risk concentrated on `IAsyncLifetime` teardown order and on coverage parity.

## Out-of-scope

- **Microsoft Testing Platform (MTP)** — *moved in scope on 2026-09-12.* The original entry read: "xunit.v3 supports it, but MTP is orthogonal to the v3 migration — the SDK stays on VSTest collector path consistent with `coverlet.collector` 10.x. Re-evaluate MTP separately when there's a concrete benefit." That holds only while the SDK stays on xunit 2.x: on the .NET 10 SDK, `dotnet test` requires MTP for xunit.v3 4.x (see *New blocker*).
- Migrating to **FluentAssertions 8.x**. Commercial license is incompatible with the MIT licensing of this SDK. Stay on FA 7.x; if FA 7.x ever stops receiving security fixes, evaluate alternatives (Shouldly, AwesomeAssertions fork) at that time.

## Verification 2026-09-12

An isolated probe project outside the repository: `net10.0`, `OutputType=Exe`, `TreatWarningsAsErrors=true`, .NET SDK 10.0.401, packages `xunit.v3` 4.0.0, `xunit.runner.visualstudio` 4.0.0, `Microsoft.NET.Test.Sdk` 18.10.0, `FluentAssertions` 7.2.2 and `NSubstitute` 6.2.0; `xunit.analyzers` 2.0.0 resolved transitively.

| check | result |
|---|---|
| Gate 1 — type of the exception a failing FA assertion throws | `Xunit.Sdk.XunitException` from `xunit.v3.assert` 4.0.0.0, implementing `Xunit.Sdk.IAssertionException`; the probe test passed 1/1 via `dotnet run` and via `dotnet test` in MTP mode |
| Gate 2 — the #3167 repro | build fails with `error xUnit1051`; with `Arg.Any<CancellationToken>()` passed explicitly it builds clean |
| `dotnet test`, default (VSTest) mode | fails before running: "Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later." |
| `dotnet test` with the `global.json` MTP runner | passes |
| Upstream | FA #2970 merged into `support-7.0` 2025-01-16, listed in the 7.1.0 release notes; xunit #3167 closed won't-fix; `xunit.v3` 4.0.0 published 2026-08-15; `dotnet/aspnetcore` `eng/Versions.props` pins xunit 2.9.2 |

## Sources of truth

- xUnit v3 Migration Guide: https://xunit.net/docs/getting-started/v3/migration
- What's New in xUnit v3: https://xunit.net/docs/getting-started/v3/whats-new
- xUnit v3 Native AOT support: https://xunit.net/docs/getting-started/v3/native-aot
- xUnit v3 release notes: https://xunit.net/releases/v3
- xunit.analyzers 2.0.0 release notes: https://xunit.net/releases/analyzers/2.0.0
- FluentAssertions 7.x backport of xUnit 3 support: https://github.com/fluentassertions/fluentassertions/pull/2970
- Microsoft Testing Platform code coverage: https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-code-coverage

## Maintenance

This document is updated **in place** when any gate flips, when a new blocker is discovered, or when the underlying assumption changes (e.g. xunit 2.x announces deprecation). It is **not** an ADR — ADRs are immutable post-Accepted; this watch list deliberately mutates as the upstream landscape evolves.
