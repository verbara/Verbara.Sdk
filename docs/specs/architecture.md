# Architecture — Verbara.Sdk

> **Root** of the Verbara chain (`Verbara.Sdk → Verbara.Sdk.Pro → Verbara.Platform ← Verbara.Platform.Web`).
> Public, **MIT**-licensed open-core SDK — the free, decompilable-by-design tier; commercial IP lives
> downstream in the private `Verbara.Sdk.Pro`.

This is the contributor-facing charter (ADR-0014 §1). The machine-checkable enforcement of every
invariant it names lives in [`gates.yaml`](../../gates.yaml) (ADR-0014 §2).

## 1. Role & boundaries

Verbara.Sdk is a .NET 10, AOT-compatible client SDK for **Asterisk PBX** — AMI, AGI, ARI, a Live
aggregate-root API, Sessions, a Push bus, and a VoiceAi (STT/TTS/realtime) pipeline — ported from
`asterisk-java` with **zero runtime reflection**. It ships **29 packages** to nuget.org (`src/` is
the count).

**What it owns:** the wire protocols and client abstractions for talking to Asterisk, and the
provider *seams* (session stores, push transports, VoiceAi providers) as public contracts.

**What it must NOT reach into:** anything downstream. As the chain root it has **no dependency** on
Pro, Platform, or Web — the arrows point *at* it. It carries no product identity, no tenancy model,
no event store, no licensing (ADR-0026 keeps product identity in the runtime, not the SDK;
ADR-0033 keeps the durable event store in Pro — the SDK ships only an in-memory `EventLog`). The
open-core seam is a **hard** boundary: this repo is world-visible MIT source, so nothing commercial,
no partner data, and no absolute machine paths may appear in tracked `docs/{specs,decisions,research}/`.

## 2. Architecture style

**Modular AOT package graph, not a monolith.** Each `src/Verbara.Sdk.*` package is one
independently-versioned, single-responsibility NuGet unit with its own `Options` class and its own
DI registration extension. Consumers compose only the packages they need
(`builder.Services.AddVerbara(...)`); there is no god-assembly.

The graph has a deliberate **provider seam**: a package whose base name ends in `.Postgres`,
`.Redis`, or `.Nats` is a *concrete backing store / transport* that plugs in at the app composition
root. Providers may depend on their abstraction package; **no non-provider package may take a
compile-time `ProjectReference` on a provider** — that would hard-wire an infrastructure choice into
a portable abstraction and break the open-core / pluggability seam (ADR-0006 pluggable session
stores). This is enforced, not merely documented (§5, G4).

Source that must be generated from external truth (AMI Actions/Events ported from Java) is produced
by generators under `tools/generate-*.sh` and `Verbara.Sdk.Ami.SourceGenerators` — **edit the
generator, never its output**.

## 3. Design principles (as practised here)

- **Zero runtime reflection — an AOT-existential invariant.** No `Activator.CreateInstance(Type)`,
  no `Type.GetType`, no `MakeGenericType`/`MakeGenericMethod`, no `DynamicMethod`, no
  `Reflection.Emit`. Serialization uses `[JsonSerializable]` source-gen contexts; options validation
  uses `[OptionsValidator]`; dispatch is static. This is what lets the shippable image be Native AOT
  so Pro IP downstream never ships as decompilable IL (ADR-0001, ADR-0003, ADR-0022).
- **DI over service-locator.** Every package exposes an `AddVerbara…` extension binding
  `IOptions<T>`; nothing resolves services by locating a container at runtime. Configuration is
  `IOptions<T>` bound from `appsettings.json` or inline, validated by source-gen `[OptionsValidator]`.
- **One responsibility per package.** AMI parsing, ARI transport, Push routing, each VoiceAi
  provider — separate packages, separately versioned, separately testable. The provider-seam rule
  (§2) keeps that separation load-bearing rather than cosmetic.
- **Async-first, cancellation-first.** All I/O is `ValueTask`/`Task` + `CancellationToken`; wall-clock
  synchronization barriers (`Task.Delay`, `Thread.Sleep`, spin-loops) are banned from *test* code —
  they are the flake vector (ADR-0004 sync-fence ratchet).
- **Deterministic clock.** `DateTime.Now`/`DateTimeOffset.Now` are banned (`BannedSymbols.txt`); use
  `DateTime.UtcNow` or an injected `TimeProvider` so behavior is testable.
- **Public API is a tracked artifact.** `Microsoft.CodeAnalysis.PublicApiAnalyzers`
  (`PublicAPI.Shipped.txt`/`Unshipped.txt`) plus `EnablePackageValidation` against the last-published
  baseline make an accidental breaking change a build failure, not a downstream surprise (ADR-0023).

## 4. Constraints & banned dependencies

| Constraint | Why | Anchor |
|---|---|---|
| **Native AOT compatible; zero runtime reflection** | the shippable image must be Native AOT so downstream Pro IP never ships as decompilable IL | ADR-0001 / ADR-0022 |
| **Dapper / Dapper.AOT / `Verbara.Sdk.Dapper.Stubs` BANNED** | Dapper relies on `DynamicMethod` + `MakeGenericType` (runtime IL emit) — the last AOT blocker. Postgres access goes through `Verbara.Sdk.Data.Npgsql` (name-based `NpgsqlDataReader` getters, no reflection) | ADR-0022 (`BanDapperPackageReferences` MSBuild guard) |
| **Reflection APIs + `DateTime.Now` banned** | AOT-safety + testability | `BannedSymbols.txt` via `BannedApiAnalyzers` |
| **`TreatWarningsAsErrors=true`, `WarningLevel=9999`** | zero-warning tolerance across the tree | `Directory.Build.props` |
| **Central package management** | every NuGet version pinned once in `Directory.Packages.props` — no floating versions | ADR-0004 |
| **GPL / AGPL / SSPL dependencies denied** | incompatible with MIT redistribution | `dependency-review.yml` deny-list |

## 5. The Gate Contract

The heart of this document: each invariant above maps to a **concrete gate that fails the build**.
This turns "we value clean code" into "here is exactly what CI rejects." The machine-readable form
is [`gates.yaml`](../../gates.yaml), swept by verbara-meta `/xr:doctor`.

| Invariant | Enforcing gate | CI job / script / guard |
|---|---|---|
| Compiles clean, unit tests green | `Unit Tests` job (build `-c Release` + test) | `.github/workflows/ci.yml` → `unit-tests` |
| Zero warnings | TWAE / `WarningLevel 9999`; `Pack Warnings Gate` re-asserts at pack time | `Directory.Build.props`; `ci.yml` → `pack-check` |
| No Dapper | `BanDapperPackageReferences` MSBuild target (build fails on reference) | `Directory.Build.props` |
| No banned reflection APIs / `DateTime.Now` | `BannedApiAnalyzers` + `ReflectionBanScanner` (Roslyn, zero-tolerance) | `BannedSymbols.txt`; `Tests/Verbara.Sdk.Governance.Tests/ReflectionBanGuardTests` |
| Provider seam (no non-provider → provider `ProjectReference`) | `LayeringGuardTests` (in-process project-graph guard, liveness self-test ≥20 projects) | `Tests/Verbara.Sdk.Governance.Tests/LayeringGuardTests` |
| No net-new wall-clock sync barriers in tests | `SyncFenceRegressionGuardTests` net-new-only ratchet vs `sync-fence-baseline.json` | `Tests/Verbara.Sdk.Governance.Tests/SyncFenceScanner` |
| Doc snippets still compile against real API | `DocSnippetCompilationTests` (compiles every ```csharp block) | `Tests/Verbara.Sdk.DocSnippets.Tests` |
| No assertion-free "smoke" tests | `Audit Test Asserts` job | `ci.yml` → `audit-test-asserts` (`tools/audit-test-asserts.sh`) |
| Coverage floor + patch coverage + exclusion baseline | `Coverage Ratchet` + `Coverage Script Tests` (ADR-0013 triplet) | `ci.yml` → `coverage`, `coverage-scripts`; `scripts/check-*.py` |
| Public API / package not silently broken | `Pack Warnings Gate` (`EnablePackageValidation` baseline + PublicAPI drift) | `ci.yml` → `pack-check` |
| The image really publishes as AOT (ship-shape at PR time) | `AOT Trim Check` job — trim-safe self-contained publish of `tools/AotCanary` + smoke-run | `ci.yml` → `aot-check`; `tools/verify-aot.sh`; also `aot-validate.yml` |
| No high-severity vulns / forbidden licenses | CodeQL + Dependency Review + Dependabot (with release cooldown) | `codeql.yml`, `dependency-review.yml`, `dependabot.yml` |
| Specs/changes parse | `OpenSpec Validate` job (pinned CLI, `--all --strict`) | `ci.yml` → `openspec` |

Every governance guard carries a **liveness self-test** (ADR-0014 §4): it must prove it actually
scanned the tree (`ReflectionBanGuardTests` ≥400 files, `SyncFence` ≥250, `LayeringGuardTests` ≥20
projects) so a broken file locator can never present a false green.

## 6. Testing conventions

- **Naming:** `Method_ShouldExpected_WhenCondition` (xunit 2.9.3 + FluentAssertions 7.1.0 +
  NSubstitute 5.3.0, all pinned in `Directory.Packages.props`).
- **Three-tier strategy** (ADR-0009): fast **unit** tests (default CI filter,
  `Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike`);
  **Functional/Integration** tests spin real Asterisk + Postgres + toxiproxy via **Testcontainers**
  (ADR-0005) — Docker required, run on a matrix of Asterisk 22/23 in the merge queue.
- **Governance tests are ordinary xunit** (no category trait) so they run in every PR's unit pass —
  the architecture guards (§5) are self-tested code, not scripts.
- **Every test must assert.** `tools/audit-test-asserts.sh` fails the build on any `[Fact]`/`[Theory]`
  whose body has no `Assert.`/`.Should()`/mock-verify expression.
- **Benchmarks** (`Tests/Verbara.Sdk.Benchmarks`, BenchmarkDotNet) back the README performance
  numbers; run weekly by `perf-regression.yml`, **not** a per-PR gate.

## 7. Where decisions live

- **ADRs:** `docs/decisions/` (append-only; once `Accepted`, superseded not edited). Load-bearing
  anchors: 0001 (Native-AOT-first), 0003 (source-generators over reflection), 0004 (central package
  management), 0005 (Testcontainers), 0006 (pluggable session stores), 0022 (Dapper ban / AOT),
  0023 (PublicAPI tracker), 0024 (`BannedSymbols` as AOT policy), 0038/0039 (CI slimming +
  Dependabot load).
- **Cross-repo standards** (this Gate Contract's parent authority): verbara-meta **ADR-0014**
  (repo-admission & gate-class contract), ADR-0013 (coverage-gate-v2), ADR-0003 (CI-gating baseline).
- **Contributor guidance:** `CLAUDE.md` (local-only, gitignored — never ships publicly),
  `CONTRIBUTING.md` (public).
- **Expert agent:** `.claude/agents/asterisk-22-expert.md` — Asterisk 22 LTS internals for
  SDK work; `.claude/skills/asterisk-sdk-reference` for the full package dependency graph.
