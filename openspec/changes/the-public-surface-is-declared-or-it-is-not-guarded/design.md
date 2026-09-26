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
  its four shipped `.editorconfig` presets, and read from the DLL itself on 2026-09-26: the only
  options it addresses to itself are `dotnet_public_api_analyzer.skip_namespaces` and
  `.require_api_files`, both undocumented, and neither excludes a compiler-emitted member.

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
   `T.T() -> void`, a constant with its value. The SARIF property bag
   (`-p:ErrorLog=<file>%2Cversion=2`, key `APIName` — the comma escaped, or MSBuild reads
   `version=2` as a second property and csc writes SARIF 1.0) carries the same string and can
   cross-check the parse; the console is sufficient.
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

### D4 — The 675 synthesised members are declared without assessment; the 120 hand-written are read one by one

The compiler's record members, the JSON source generator's context members and the four implicit
constructors the compiler emits for the generator types are declared mechanically. The 120
hand-written members — 112 across the 28 packages that already had the analyzer, plus the 8
author-written entries of `Verbara.Sdk.Ami.SourceGenerators` (four `…Generator` types and their
four `Initialize` methods) — get a pass by a human eye, and the finding — "intended" or "this should
not have been public" — is recorded in the ADR.

*Why:* the two groups differ in what a declaration means. Declaring `PrintMembers` states a fact about
C#; nobody chose it and nobody can unchoose it without abandoning `record`. Declaring
`AriClientFactory` states that a public factory type is part of the contract, which somebody either
decided or did not. Treating them alike is how the second kind stops being a decision. The generator
types are the second kind: measured 2026-09-26 on SDK 10.0.401, an `internal sealed class …Generator`
marked `[Generator]` is discovered, instantiated and emits identically to the public one, so `public`
there is the Roslyn-template convention an author kept, not a load requirement — and a kept convention
is a decision the record can state in one paragraph.

*Alternative rejected:* assess all 795. It costs the reviewer's attention on 675 items that have one
possible answer, which is the reliable way to make the remaining 120 get skimmed.

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

### D6 — Breadth is proven from the arguments of the compile that produced each package; the control proves depth

The negative control (D5) shows the guard firing on one package through the real toolchain. It
cannot show that the other 28 are guarded: a project can append `RS0016` to its own `NoWarn` — six
projects already append or replace `NoWarn` this way, `Verbara.Sdk.OpenTelemetry` with `NU5104`,
`Verbara.Sdk.Benchmarks` with `CA1822`, and three test projects that write `<NoWarn>CA1707</NoWarn>`
with no `$(NoWarn);` prefix and so discard everything the repository sets — set
`TreatWarningsAsErrors` to `false`; set `CodeAnalysisTreatWarningsAsErrors` to `false` in a
`Directory.Build.props` (the analyzer's own `buildTransitive` props then move every `RS` id into
`WarningsNotAsErrors`; from a project body the same property does nothing, because those props
read it first — measured); set `WarningLevel` to `0`; set `RunAnalyzers` — or
`RunAnalyzersDuringBuild` with `RunAnalyzers` unset — to `false`; point `CodeAnalysisRuleSet` at a
ruleset that sets `RS0016` to `None`, which silences it with the root `.editorconfig` line at
`warning` (measured); remove the analyzer item or append to `NoWarn` inside a target that runs
before `CoreCompile`; put `dotnet_public_api_analyzer.skip_namespaces` in an analyzer-config file
for a namespace that has no record lines yet; or never receive the analyzer. Deleting the record
files is not on that list: with both `PublicAPI.*.txt` gone 5.6.0 reports every public symbol as
`RS0016`, and with exactly one gone it reports `RS0048` — both fail the build on their own
(measured). The last item is not hypothetical: `Directory.Build.props:69` excludes
`Verbara.Sdk.Ami.SourceGenerators` by name, and that package is `IsPackable=true` with four public
generator types and one-line record files that nothing reads. Each of these builds exactly as green
as a declared package, so "29 packages" was 28 before this change started.

Two decisions. First, the source-generator package gets the analyzer: attached in isolation on
2026-09-25 it reports exactly 12 `RS0016` symbols (4 types × type, implicit constructor,
`Initialize`) and no other diagnostic, so the cost is 12 lines; the spec's first requirement
carries no exception, and neither does its fourth — the 8 author-written entries are assessed in
2.1 (D4). `BannedApiAnalyzers` stays excluded for it — unmeasured, and not this change's question.
Second, a guard reads, for every packable project, the arguments the C# compiler was handed by the
compilation the pull request's build ran — the one whose output `dotnet pack --no-build` then
packed. `Build Release` runs with `-p:ProvideCommandLineArgs=true -p:RecordCscArgs=true`: the first
is the SDK's own switch that makes the `Csc` task output its arguments as `@(CscCommandLineArgs)`
without changing the compile; the second arms a target in a new `Directory.Build.targets` that
writes them, after `CoreCompile`, to `$(IntermediateOutputPath)public-api.csc-args.txt` — one file
per project, in that project's own `obj/`, whichever node compiled it — and, in the same
condition, sets that compile's `ErrorLog` so csc keeps its diagnostic log beside the record
(below). Both are global, so no project can unset them.
`scripts/ci/check-public-api-guard-coverage.sh` reads each file and fails the pull request unless:
`/warnaserror+` is present; `/skipanalyzers+`, `/warn:0` and any `/ruleset:` are absent; `RS0016`
is in neither `/nowarn:` nor `/warnaserror-:`; an `/analyzer:` line ends in
`Microsoft.CodeAnalysis.PublicApiAnalyzers.dll`; every `/additionalfile:` named
`PublicAPI.Shipped.txt` or `PublicAPI.Unshipped.txt` is the project's own, and each is present.

The same file names every analyzer-config file the compiler read, and that list — every path on
it, wherever it lives — is the scope of the scan for the one thing neither the arguments nor the
compiler's log can show, because the analyzer then raises nothing or the compiler drops what it
raised before any logger sees it: an option addressed to the analyzer, or a severity that hides
the diagnostic. There is no exempt file and no exempt directory. The root `.editorconfig` was
measured 2026-09-26 (SDK 10.0.401, analyzer 5.6.0) to silence `RS0016` on an undeclared public
type while its line 71 stayed intact and every argument stayed clean:
`dotnet_public_api_analyzer.skip_namespaces` in the `[*.cs]` section for a namespace with no
record lines (one with record lines fails with `RS0017` instead); a later section setting the
severity to `suggestion`; a later line in the same section setting it to `none` or `silent`;
the key upper-cased; and the same key in a `.globalconfig` beside the project, which the SDK
adds unasked and the record lists. The SDK's own `analysislevel_*.globalconfig` presets — all
770 that ship with 10.0.401 — pass the rule below, so excluding the SDK directory and the NuGet
cache bought nothing and cost a trust-by-path; they are scanned like the rest. The rule, applied
to every non-comment, non-section `key = value` line with the key lowercased and the value cut
at an inline `#` or `;`: no key may begin with `dotnet_public_api_analyzer` — the prefix, not
the names: 5.6.0's DLL carries exactly two keys under it, `skip_namespaces` and
`require_api_files` (its UTF-16 strings, read from the DLL the record names), and a prefix ban
covers whatever a later version adds; no key may begin with `dotnet_analyzer_diagnostic`; the
key `dotnet_diagnostic.rs0016.severity` must have the value `warning` or `error`, every
occurrence; and `rs0016` may appear on no other key line — which fails
`dotnet_code_quality.RS0016.excluded_symbol_names` too, measured inert for `RS0016` and failed on
purpose, because a stray mention is a one-line fix and a silent one is this change's whole
subject. The root file must contain at least one such severity line; it is what keeps the bulk
settings inert. That one line is all the former exemption was protecting: the file's other 65
keys never mention the analyzer, and the rule passes today's root file, the compiler's generated
`*.GeneratedMSBuildEditorConfig.editorconfig` and every SDK preset at 0.06 s for 29 runs. Not
banned, measured inert: `generated_code = true` and the shared `dotnet_code_quality.*` keys
that do not carry the identifier. A `.globalconfig` cannot lower the severity past an
`.editorconfig` line even at `global_level = 1000`, but it can set `skip_namespaces` when no
`.editorconfig` sets it; it is on the record and the same rule catches it. A `.editorconfig`
above the checkout is inert while the root file says `root = true` and is on the record — and
scanned — when it does not.

The arguments also name the declared record the analyzer was given, and that is checked too,
measured 2026-09-26: a second `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt` pair from another
directory, added as `AdditionalFiles` and listing the undeclared symbols, builds green — no
`RS0016` on the console, none in the log — while the package's own record stays untouched; the
analyzer reads every additional file of those two exact names (a lower-cased pair is ignored
and the build fails) and unions them. The record shows it as
`/additionalfile:../extra/PublicAPI.Shipped.txt`, so every such line must resolve, relative to
the project directory and with symbolic links followed, to the project's own file of that name.
And after the build the two record files of every package must match the checkout — `git diff
--quiet` and no untracked record file — because a build step that writes the symbols into the
record on the runner leaves the analyzer nothing to report, and this is the one place it shows.

A suppression at the point the diagnostic is raised is **not** found by reading source text. The
compile list does not include what a source generator emitted — measured 2026-09-26 on
`src/Verbara.Sdk.Ami`, 467 source paths, the 3 generated ones all MSBuild's own
(`GlobalUsings.g.cs`, `AssemblyAttributes.cs`) and none from the generator its `/analyzer:` line
names, because a generator runs inside the compiler — and a generator that emits
`#pragma warning disable RS0016` above an undeclared public class builds green with the class in
the packed DLL. Nor does text show a `SuppressMessage` whose id is a `const` from another file
(`[SuppressMessage("ApiDesign", Ids.Api)]` with `Ids.Api = "RS" + "0016"`: green, zero files
flagged) or a `DiagnosticSuppressor` — an analyzer class that suppresses `RS0016`
programmatically: green, the class in the DLL, no text anywhere. All of them are in one place:
the compiler's SARIF log. `Directory.Build.targets` sets `ErrorLog` to
`$(IntermediateOutputPath)public-api.sarif,version=2` whenever `RecordCscArgs` is `true` — there
and not on the `ci.yml` command line, because a `-p:` value with a comma must be `%2C`-escaped
or MSBuild reads `version=2` as a second property and csc writes SARIF 1.0 (reproduced), and
the record shows the property's fate either way: `/errorlog:"obj/Release/net10.0/public-api.sarif,version=2"`,
whose directory is the record's own `/out:` directory, so the guard reads the file the record
names and no other. In that log every `RS0016` the analyzer raised is a `results[]` entry
whether or not the console showed it: a suppressed one carries
`"suppressions":[{"kind":"inSource","properties":{"suppressionType":"Pragma Directive"}}]`, or
`"SuppressMessageAttribute"`, or `"DiagnosticSuppressor { Suppression Id: …, Suppression
Justification: … }"`, with file and line — a generated file's as a `file://` uri under the
project's `obj/Release/<tfm>/<generator assembly>/<generator type>/<hint name>`, a path that
does not exist on disk — and its message names the symbol; a demoted one is there with no
suppression, at `"level":"warning"` under `TreatWarningsAsErrors=false` or
`/warnaserror-:RS0016`, at `"level":"note"` under an analyzer-config `suggestion`. The guard's
rule is therefore that a compile that produced a record has **no** `RS0016` result at all and
that `tool.driver.rules[]` lists `RS0016` — the analyzer ran in this compile.
`invocations[0].executionSuccessful` is not read: it is `true` on a failed compile too
(measured); the record's existence is the proof of success, because a failed `CoreCompile`
writes none. Measured 2026-09-26 on SDK 10.0.401 with analyzer 5.6.0, twice independently: a
hand-written pragma, a generated pragma, a bare `#pragma warning disable`, the const-id
attribute and the suppressor each log three suppressed `RS0016` results on a green build, and
`/nowarn:RS0016` logs none — that one is the arguments' to catch. What a generator emits is also
recoverable as text (`-p:EmitCompilerGeneratedFiles=true` puts `/generatedfilesout:` on the
record and csc writes each generated file under
`obj/Release/<tfm>/generated/<generator assembly>/<generator type>/`, byte-identical to what it
compiled after a 3-byte UTF-8 BOM), but the guard does not read it: csc rewrites those files on
a failed compile too, a file a generator stops emitting stays until something deletes it, a
`RemoveDir` before `CoreCompile` empties the folder on an up-to-date build while the DLL is
kept, and the tree's generated code already carries one bare `#pragma warning disable`
(`System.Text.RegularExpressions.Generator`, in `Verbara.Sdk.Sessions.Postgres`) that a text
rule would fail on and the log, correctly, does not. The log is 0.4–1.2 MB a project (22.6 MB
across 29, almost all the rule catalogue of every analyzer), lives under `obj/`, is a
`FileWrites` item, cost the 29-project `--no-incremental` build +0.4 s and +2.1 s in two paired
runs (7.9 s and 11.1 s without), and `jq` reads one in 3 ms.

Measured 2026-09-26 on SDK 10.0.401 with analyzer 5.6.0 against an undeclared public member:
`WarningLevel=0` shows as `/warn:0`; `NoWarn`
appended in the project body and inside a `BeforeTargets="CoreCompile"` target both put `RS0016`
in `/nowarn:`; an `<Analyzer Remove>` in such a target leaves no `/analyzer:` line for the package;
`RunAnalyzersDuringBuild=false` shows `/skipanalyzers+`; `TreatWarningsAsErrors=false` leaves no
`/warnaserror+`; a ruleset shows `/ruleset:`; and `skip_namespaces`, a silencer that changes no
argument at all, is a line in a file the arguments name. Recording the arguments costs nothing
measurable (0.54 s against 0.56 s for a three-project rebuild) and about 21 KB a project; the log
adds under 2.1 s and 22.6 MB across the 29; the guard is 29 record reads, 29 `jq` reads at 3 ms and
29 analyzer-config scans at 2 ms. It blocks the pull request that adds an opt-out.

What such a record holds was read on this tree before the recording target exists, by asking MSBuild
for `CscCommandLineArgs` with the compiler skipped — an inspection, not the guard's route, which is
the real compile's own output. On `src/Verbara.Sdk.Cluster.Primitives` it returns **250** arguments:
`skipanalyzers: 0`, 26 `/analyzer:` DLLs, `/warnaserror+`, both `PublicAPI.*.txt` as
`additionalfile` — and `/nowarn:CS1591,RS0016,RS0037,RS0041,1701,1702,8002`. This change's own
defect is one of those 250 lines, which is the point: the arguments state what the compiler was told
about `RS0016`, and today they state that it was told to ignore it.

*Why not evaluation:* `dotnet msbuild -getProperty/-getItem` reads the project before any target
runs. A target can set `RunAnalyzers`, append to `NoWarn` or remove the analyzer item after that
and before `CoreCompile`; the SDK itself decides whether analyzers run inside a target
(`_ComputeSkipAnalyzers` in `Microsoft.Managed.Core.targets`: `RunAnalyzers` wins,
`RunAnalyzersDuringBuild` counts only when `RunAnalyzers` is empty); and a NuGet analyzer is not an
`Analyzer` item at evaluation at all. The evaluation of such a project is byte-identical to a
guarded one (measured), and a list of properties to read is open by construction — this change's
first draft missed `RunAnalyzersDuringBuild`, `WarningLevel` and `CodeAnalysisRuleSet`. The
sibling `check-package-validation-coverage.sh` is the right shape for its own question and says in
its header what it cannot see, "anything decided after evaluation"; for this guard that blind spot
is the threat itself.

*What the compiler's SARIF log judges, and what it does not:* it is the judge of suppression and
demotion, because it is the only observation that sees the diagnostic after the analyzer raised
it and before the console failed to show it. It is not the judge of the compiler's options:
measured 2026-09-26, under `/nowarn:RS0016` the log holds no `RS0016` result at all, and
`/warn:0`, `/skipanalyzers+`, a missing `/analyzer:` line, a `/ruleset:` and a foreign
`/additionalfile:` are the arguments' to read, one line each. Neither sees `skip_namespaces` or
an analyzer-config `none` or `silent`: the analyzer raises nothing, or the compiler drops what
it raised before any logger sees it — that stays with the analyzer-config scan. Three
observations, one question each: the arguments judge the options and the record the analyzer
was given, the log judges what became of what the analyzer raised, the analyzer-config files
judge what the analyzer was told; source text is not read. What none of the three can judge is
whether the files they read are the files the compiler read — a build step that changes and
restores one is invisible to any reading after the fact, and the spec states that limit as a
requirement rather than this design hiding it in a paragraph.

*Why not a negative control on every package:* it observes the outcome, the strongest observation
for anything an argument or a file decides — but it is a second compile a target can tell apart
from the real one, it adds 20–28 s sequential (about 7 s with four workers) plus a design-time
read per project, it fails 29 builds by design and must stay incremental or a failed rebuild
leaves the next package's `--no-dependencies` verdict as `CS0006`, and it is blind to a silencer
measured to change no argument: `skip_namespaces` skips the package's own namespace while the
probe's foreign namespace still fires. D5 keeps that observation where it pays — one package,
every pull request — as the check that the toolchain still turns these arguments into a failure.

*Alternative rejected:* a Governance test that greps project files — rejected for the ADR-0055
reasons, which apply unchanged. What fails closed rather than open: a project that sets
`ImportDirectoryBuildTargets=false` or declares `RecordCscArgs` as `TreatAsLocalProperty` produces
no record, and no record is "not proven".

## Risks / Trade-offs

- **The guard lands and still does not fire** → the worst outcome, and the one this change exists to
  prevent, so it is bound by the spec's liveness requirement and D5's committed control rather than by
  care.
- **The writer is coupled to the analyzer's message text** → a future `PublicApiAnalyzers` that
  changes the format breaks `tools/declare-public-api.sh` loudly, because D2's step 4 rebuilds and the
  compiler refuses the lines. That is the acceptable direction; the ADR names the version the format
  was measured on.
- **A package opts out after this change** → D6's guard fails the pull request that adds the opt-out,
  naming the project and the argument or the file; what no argument shows is found in the files the
  same record names. What the guard cannot see, by construction: a step of the repository's own
  build that changes a file the compiler read and restores it before the guard runs. The ADR says
  the guard trusts the checkout not to rewrite its own configuration mid-build, and that such a step
  is a diff to a build file.
- **A member is declared that should never have been public** → D4 makes the assessment a requirement
  and the finding explicit, while deliberately leaving the removal to a change that can carry a
  breaking tier. The risk is accepted, not eliminated: declaring an accidental export does entrench
  it further until that change happens. The guard judges only whether a member was declared, never
  whether it should have been: a pull request that adds a public member and its `Unshipped` line in
  the same diff passes, by design, and the reviewer reading the record line is the only check on it.
- **`Unshipped` becomes misleading** → D3's consequence, stated in the ADR so the next reader is not
  the one who discovers it.
- **A future language version emits new synthesised members** → the build fails on the next upgrade
  until they are declared. That is the guard working, but it is a cost on every major C# bump, and the
  ADR should say so rather than let it arrive as a surprise.
- **A toolchain that stops logging suppressed diagnostics** → not a blind spot: 2.5's real-MSBuild
  pragma and suppressor cases read "not proven" the day it arrives, the same way D5 catches an
  analyzer that stops firing. The timing and size figures in D6 are one machine's, two paired runs
  each.

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
