# Design: the-public-surface-is-declared-or-it-is-not-guarded

## Context

See `proposal.md` — *Why* for the measurement and the mechanism. The design-relevant shape of what is
there today:

- `RS0016` is suppressed by `Directory.Build.props:15`'s `<NoWarn>` and cannot be re-enabled from
  `.editorconfig`; `NoWarn` wins. Two comments, at lines 13 and 16, describe an arrangement that has
  never been in force.
- With the suppression removed, the solution build reports **783** distinct undeclared symbols:
  **671** emitted by the C# compiler for `record` types or by the `System.Text.Json` source
  generator, and **112** written by hand.
- The declared records already total **9568 lines** — 8635 in `PublicAPI.Shipped.txt` and 933 in
  `PublicAPI.Unshipped.txt`, with 8 of the 29 packages carrying an empty `Unshipped`. The repository
  clearly has a convention for these files; what it does not have is completeness.
- `Microsoft.CodeAnalysis.PublicApiAnalyzers` **5.6.0** is the installed version. Its package carries
  `Microsoft.CodeAnalysis.PublicApiAnalyzers.CodeFixes.dll`, its own documentation states plainly
  that *"all public types and members should be declared in PublicAPI.txt"*, and it exposes **no**
  configuration option to exclude compiler-emitted members — searched across its `documentation/` and
  its four shipped `.editorconfig` presets.

## Goals / Non-Goals

**Goals**

- Make `RS0016` a guard that fails a build, and leave nothing in the configuration that claims
  otherwise.
- Complete the declared records so the guard can be on without a permanent backlog of noise.
- Separate "record what is there" from "decide whether it should have been there", so the 112
  hand-written members get read rather than rubber-stamped.
- Leave behind a way to prove the guard fires that outlives this change.

**Non-Goals**

- Removing any public member. Every removal is a `CP0002` break with its own release tier, and
  deciding 112 of them inside a configuration fix would bury each decision.
- Promoting `Unshipped` entries to `Shipped`. Eight packages have an empty `Unshipped` and others
  carry a populated one; that asymmetry is real but it is a separate question about release
  bookkeeping, and this change neither worsens nor resolves it.
- `RS0037` and `RS0041`, suppressed alongside `RS0016` for their own stated reasons and not
  implicated by this measurement.
- Any change to `PackageValidation`, the baseline version, or `CompatibilitySuppressions.xml`.

## Decisions

### D1 — The guard is `RS0016` itself, not a script that imitates it

The suppression comes out of `NoWarn` and the diagnostic is allowed to fail the build under the
repository's existing `TreatWarningsAsErrors`.

*Why:* the analyzer already knows the language rules — what counts as public, what a record emits,
how nullability is spelled in these files. A bespoke script comparing a reflected surface against the
records would have to re-derive all of it and would drift from the compiler on the next language
version. The repository already pays for this analyzer on every build; what it lacks is permission
for it to speak.

*Alternative rejected:* a CI-only guard that diffs a generated API dump against the records. It
catches the same additions, but only after a push, and it introduces a second definition of "the
public surface" that can disagree with the analyzer's. One definition, enforced at the earliest point,
is the cheaper contract.

### D2 — The entries are written from the compiler's own `RS0016` output, and the compiler verifies them

The route is decided, not searched for. Measured 2026-09-25 (analyzer 5.6.0): on
`Verbara.Sdk.Cluster.Primitives`, 19 undeclared symbols parsed from the build, appended, rebuilt with
`RS0016` live and `TreatWarningsAsErrors` at the repository default — 0 warnings, 0 errors; and on a
throwaway project built to hit what a records-only package cannot — an implicit constructor, a `const`
whose value contains escaped quotes, `default(System.Threading.CancellationToken)`, a generic `T!` —
28 of 28 lines round-tripped green, and one mistyped line failed the rebuild with `RS0017` naming it
and `RS0016` naming the symbol it no longer covered. `tools/declare-public-api.sh` carries the four
parts.

1. **Inventory build with the suppression lifted as a global property**, so nothing is edited and
   nothing has to be restored: `dotnet build Verbara.Sdk.slnx -c Release --no-incremental
   -p:NoWarn=CS1591%3BRS0037%3BRS0041 -p:TreatWarningsAsErrors=false`. A global `-p:` cannot be
   overridden by `Directory.Build.props` — nor by a project's own `NoWarn` append, which is why it is
   right for an inventory and wrong for the negative control (D5). `TreatWarningsAsErrors=false` is
   required, or the first project that fails stops its dependents and the inventory is partial.
2. **The message is the record line.** In 5.6.0 the text after `Symbol '` is exactly what the file
   takes — modifiers, nullability annotations, return type, an implicit constructor as
   `T.T() -> void`, a constant with its value. The SARIF property bag (`-p:ErrorLog=<file>,version=2`,
   key `APIName`) carries the same string and can cross-check the parse; the console is sufficient.
3. **What the parse must get right:** (a) MSBuild prints every diagnostic at least twice — dedupe
   per project; (b) the owning package is the `[…/X.csproj]` suffix, not the source path — the
   JSON-context members sit in generated files; (c) anchor on the fixed text
   `' is not part of the declared public API`, not on the closing quote — the help URL follows it,
   and symbol names contain `<Clone>$`, `operator ==`, `default(…)` and, for constants, quotes;
   (d) all 58 record files begin with `#nullable enable` and end with LF — assert both before
   appending (the 8 header-only `Unshipped` files are exactly one line); (e) the analyzer is
   order-insensitive and the existing files are not sorted, so sort only the appended block (ordinal)
   and leave existing lines alone.
4. **The oracle is the compiler, not a diff against the inventory.** Rebuild with `RS0016` live and
   `TreatWarningsAsErrors` at its default. `RS0017` is not suppressed today and the build is green, so
   a mistyped, duplicated or stale entry fails the build.
   **The global property of step 1 is wrong for this step, measured.** It replaces the value every
   project inherits, so it also discards the six per-project appends this tree has — `CA1707` at
   `Directory.Build.props:101` for every test project, `CA1707` again in three test `.csproj` files
   that *replace* rather than append, `CA1822` in `Verbara.Sdk.Benchmarks` and `NU5104` in
   `Verbara.Sdk.OpenTelemetry`. Run solution-wide with `TreatWarningsAsErrors` at its default it
   produced **3055 `CA1707` errors and zero `RS0016`** on 2026-09-25. Verify one `src/` project at a
   time with the global property (they carry no appends), and take the solution-wide proof from the
   commit that removes the suppression from the file, where every `$(NoWarn)` still resolves. That
   build was measured at **0 warnings, 0 errors** with all 783 entries written.

*Why not `dotnet format`:* `dotnet format analyzers <csproj> --diagnostics RS0016 --severity warn`,
re-run with the workspace loading cleanly, reports "Formatted 0 of N files" and leaves
`PublicAPI.Unshipped.txt` byte-identical. The failure is not workspace loading; the fixer is not
applied from this tool. It gets no task.

*Why the script is committed:* the next C# version's synthesised members need the same parse
(Risks), and a transcript does not run.

### D3 — New entries go to `Unshipped`, and `Shipped` is not touched

Everything this change adds is written to each package's `PublicAPI.Unshipped.txt`.

*Why:* `Shipped` means "this was in a published package", and it is what a reader consults to ask what
2.6.0 promised. These members were in published packages, so an argument exists for `Shipped` — but
writing 783 entries into the file that records release history, in a change that ships no release,
makes the record harder to trust rather than easier. `Unshipped` is where the analyzer directs new
declarations, and the next release's bookkeeping can promote them.

*Consequence:* after this change, 8 packages no longer have an empty `Unshipped`, and a reviewer
reading `Unshipped` will see entries that are not new API. The change must say so in the ADR, or the
next reader will misread the file the way this proposal's first draft misread
`Cluster.Primitives`'s.

### D4 — The 671 synthesised members are declared without assessment; the 112 are read one by one

The compiler's record members and the JSON source generator's context members are declared
mechanically. The 112 hand-written members get a pass by a human eye, and the finding — "intended" or
"this should not have been public" — is recorded in the ADR.

*Why:* the two groups differ in what a declaration means. Declaring `PrintMembers` states a fact about
C#; nobody chose it and nobody can unchoose it without abandoning `record`. Declaring
`AriClientFactory` states that a public factory type is part of the contract, which somebody either
decided or did not. Treating them alike is how the second kind stops being a decision.

*Alternative rejected:* assess all 783. It costs the reviewer's attention on 671 items that have one
possible answer, which is the reliable way to make the remaining 112 get skimmed.

### D5 — The negative control is committed, it runs on every pull request, and it restores the tree

A script lands in the repository (`scripts/ci/check-public-api-guard-fires.sh`) that adds an
undeclared public member, builds with the **committed** configuration — no `-p:NoWarn` and no
`-p:TreatWarningsAsErrors` on its command line, because an override would prove the analyzer works,
not that the repository lets it — shows the build failing with `RS0016` naming the member, and
restores every file it modified byte-for-byte from a copy taken before the edit, on every exit path
including interruption. Build outputs under `bin/` and `obj/` are outside that guarantee. The restore
is checked by content, never by a clean `git status`: an empty status is also what a restore that
overwrote uncommitted work produces, and the archived change
`2026-09-13-enforce-unguarded-public-claims` (task 6.6) records that accident. The script exits 0
only on `error RS0016` naming the probe; any other build failure, and a green build, exit non-zero
with "not proven". It runs as the last step of `Pack Warnings Gate`, after everything that consumes
the build output, so a toolchain change that stops the diagnostic being reported — an analyzer or SDK
bump, which arrives by Dependabot — is caught by the pull request that brings it.

*Why:* this defect existed because a green build was read as evidence the guard was working, and a
green build is also exactly what a silenced guard produces. The only observation that distinguishes
them is a failure that was made to happen on purpose. A note in a pull request does not survive; a
committed procedure does.

*Implementation note learned the hard way during the measurement:* the probe member must **read
instance data**, or `CA1822` ("can be marked as static") fails the build first and the control proves
nothing about `RS0016`. The first attempt at this negative control failed exactly that way.

### D6 — Breadth is proven by evaluation on every pull request; the control proves depth

The negative control (D5) shows the guard firing on one package through the real toolchain. It
cannot show that the other 28 are guarded: a project can append `RS0016` to its own `NoWarn` — six
projects already append or replace `NoWarn` this way, `Verbara.Sdk.OpenTelemetry` with `NU5104`,
`Verbara.Sdk.Benchmarks` with `CA1822`, and three test projects that write `<NoWarn>CA1707</NoWarn>`
with no `$(NoWarn);` prefix and so discard everything the repository sets — set
`TreatWarningsAsErrors` or
`CodeAnalysisTreatWarningsAsErrors` to `false` (the analyzer's own `buildTransitive` props then move
every `RS` id into `WarningsNotAsErrors`), lose both `PublicAPI.*.txt` (the `Exists` conditions in
`Directory.Build.props` and in the analyzer's targets then hand it nothing to check, and `RS0048`
fires only when exactly one is missing), or never receive the analyzer. The last is not
hypothetical: `Directory.Build.props:69` excludes `Verbara.Sdk.Ami.SourceGenerators` by name, and
that package is `IsPackable=true` with four public generator types and one-line record files that
nothing reads. Each of these builds exactly as green as a declared package, so "29 packages" was 28
before this change started.

Two decisions. First, the source-generator package gets the analyzer: attached in isolation on
2026-09-25 it reports exactly 12 `RS0016` symbols (4 types × type, implicit constructor,
`Initialize`) and no other diagnostic, so the cost is 12 lines and the spec's first requirement
carries no exception. `BannedApiAnalyzers` stays excluded for it — unmeasured, and not this change's
question. Second, a guard asserts the configuration across every packable project on every pull
request by MSBuild evaluation (`dotnet msbuild -getProperty/-getItem`, about 0.3 s a project, no
restore, no build), in the shape `scripts/ci/check-package-validation-coverage.sh` already has for
the same failure class (ADR-0055 addendum, 17 of 29 validated). It blocks the pull request that adds
an opt-out, which a control run afterwards cannot.

*Alternative rejected:* a Governance test that greps project files — rejected by the ADR-0055 guard
for reasons that apply unchanged (a nested props file, a property set through another property, or a
spelling variant is invisible to grep and visible to evaluation). What evaluation cannot see —
`.editorconfig` severity, a nested `.editorconfig`/`.globalconfig` under `src/`, `#pragma warning
disable RS0016`, `[SuppressMessage]` — is a tree scan, and that half does belong in
`Tests/Verbara.Sdk.Governance.Tests`.

## Risks / Trade-offs

- **The guard lands and still does not fire** → the worst outcome, and the one this change exists to
  prevent, so it is bound by the spec's liveness requirement and D5's committed control rather than by
  care.
- **The writer is coupled to the analyzer's message text** → a future `PublicApiAnalyzers` that
  changes the format breaks `tools/declare-public-api.sh` loudly, because D2's step 4 rebuilds and the
  compiler refuses the lines. That is the acceptable direction; the ADR names the version the format
  was measured on.
- **A package opts out after this change** → D6's guard fails the pull request that adds the opt-out,
  naming the project and the setting; a demotion evaluation cannot see is the Governance scan's.
- **A member is declared that should never have been public** → D4 makes the assessment a requirement
  and the finding explicit, while deliberately leaving the removal to a change that can carry a
  breaking tier. The risk is accepted, not eliminated: declaring an accidental export does entrench
  it further until that change happens.
- **`Unshipped` becomes misleading** → D3's consequence, stated in the ADR so the next reader is not
  the one who discovers it.
- **A future language version emits new synthesised members** → the build fails on the next upgrade
  until they are declared. That is the guard working, but it is a cost on every major C# bump, and the
  ADR should say so rather than let it arrive as a surprise.

## Migration Plan

Nothing to migrate. `PublicAPI.*.txt` files are analyzer metadata and are not shipped, no source file
changes behaviour, and no package contents change. `PackageValidation` compares a pack against the
previously published package rather than against these records, so the 2.6.0 baseline and the empty
suppression set stay valid throughout.

Rollback is a revert. The only durable artefact is the ADR, which records why the guard exists and
what the assessment of the 112 found.

## Open Questions

None. D2 was the one genuine unknown; it was measured on one package and on an adversarial probe
before the tasks were written, and the tasks execute the measured route rather than search for it.
