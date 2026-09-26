# Tasks

Execution follows `rules.tasks`: **a fresh subagent per task, never inline in the main session.**
Phase A is batched, Phase B is one focused subagent per component, Phase C is batched.

**This is not a bug fix in the regression-test sense** — nothing misbehaves at runtime, so there is
no failing behaviour to capture first. The equivalent discipline is task 1.1: the negative control
that proves the guard is dead today, recorded before anything is changed, so the same control run at
the end proves it is alive.

**A dry run of tasks 2.2–2.4 was already executed on 2026-09-25**, before this list was amended, and
its results are quoted in the tasks they belong to. It established three things: the 783 entries the
route generates are accepted by the compiler (0 `RS0016`, 0 `RS0017`), the solution then builds 0
warnings 0 errors with the diagnostic live, and the guard fires on a re-added probe. It also found
one broken verification command, corrected in 2.3. The dry run was discarded; nothing from it is in
the tree.

**Coordination.** `a-published-surface-is-one-something-measures` is another session's change and
claims `Verbara.Sdk.Ari`'s audio surface, the audio metrics, and the functional suite's coverage
claims. This change writes 218 entries into `src/Verbara.Sdk.Ari/PublicAPI.Unshipped.txt` — the
record file, not the code — so the two do not collide, but the pull request must say so. Do not touch
`src/Verbara.Sdk.Ari/**/*.cs`, `Tests/Verbara.Sdk.Ari.Tests/**`,
`Tests/Verbara.Sdk.FunctionalTests/Verbara.Sdk.FunctionalTests.csproj` or `docs/guides/README.md`.

## 1. Phase A — foundation (batched)

- [ ] 1.1 Record the negative control that proves the guard is dead **today**, before any change.
      Add a `public` member that reads instance data to a shipped type (the measurement used
      `public int MainSessionProbe() => _sessions.Count + _byLinkedId.Count;` inside
      `src/Verbara.Sdk.Sessions/Manager/CallSessionManager.cs`), build
      `dotnet build src/Verbara.Sdk.Sessions/Verbara.Sdk.Sessions.csproj -c Release --no-incremental`,
      and paste the result verbatim into this file. Expected, measured 2026-09-25 on `6f94caae`:
      `0 Warning(s)` and `0 Error(s)`, no `RS00*` diagnostic at all. **The probe must read instance
      data** — a `public void` or a member that does not touch a field fails `CA1822` first and the
      control then proves nothing about `RS0016`. **Do not lift the suppression with a global
      property here** — the control has to show what the committed configuration does. Restore the
      file from a copy taken before the edit, never with `git checkout --` or `git restore`, and
      verify the restore with `sha256sum` before and after.

- [ ] 1.2 Reach the package the analyzer never reached, then establish the full inventory as a
      committed artefact, so Phase B is bounded by a list rather than by a build that has to be
      re-run. First, design D6: in `Directory.Build.props`, move `Microsoft.CodeAnalysis.PublicApiAnalyzers`
      and the two `PublicAPI.*.txt` `AdditionalFiles` out of the item group that excludes
      `SourceGenerators` by name, into an item group conditioned on `src` alone; leave
      `BannedApiAnalyzers` and `BannedSymbols.txt` in the excluded group, unmeasured and out of scope.
      This edit stays. Then build with the suppression lifted **as a global property, editing
      nothing**: `dotnet build Verbara.Sdk.slnx -c Release --no-incremental
      -p:NoWarn=CS1591%3BRS0037%3BRS0041 -p:TreatWarningsAsErrors=false` (`TreatWarningsAsErrors=false`
      is required, or the first failing project stops its dependents and the inventory is partial).
      Extract every `Symbol '<...>' is not part of the declared public API` line, dedupe per owning
      project — the `[…/X.csproj]` suffix, not the source path — and write the distinct symbols with
      their owning project into the change directory. Verify **783** distinct symbols across the 28
      packages that already had the analyzer and **12** on `Verbara.Sdk.Ami.SourceGenerators`
      (4 generator types × type, implicit constructor, `Initialize`; measured 2026-09-25 with the
      analyzer attached in isolation, no diagnostic other than `RS0016`). If either figure differs,
      stop and report the difference rather than adjusting the expectation — `main` has moved since
      the measurement and the delta is a finding. Nothing to restore.

- [ ] 1.3 Split that inventory into the groups designs D4 and D6 treat differently, and commit the
      split: **675** synthesised — the 671 compiler `record` members (`PrintMembers`, `Equals(T?)`,
      `EqualityContract`, `GetHashCode`, `ToString`, `Deconstruct`, `<Clone>$`, `operator ==`/`!=`)
      and `System.Text.Json` source-generated context members, plus the **4** implicit parameterless
      constructors the compiler emits for the generator types (`…Generator.…Generator() -> void`) —
      and **120** hand-written: the 112 plus the **8** author-written entries of
      `Verbara.Sdk.Ami.SourceGenerators`, the four `…Generator` types and their four
      `Initialize(IncrementalGeneratorInitializationContext)` methods. Those 8 go to 2.1's assessment
      like the rest: spec requirement 4's "compiler-emitted" scenario covers what a compiler or a
      generator emits, and a generator's own source is written by an author. Do not record them as
      "public because Roslyn loads them" — measured 2026-09-26 on SDK 10.0.401, an `internal sealed
      class …Generator` marked `[Generator]` is discovered, instantiated and emits identically, so
      `public` there is a kept convention, which is exactly what the record is for. Verify
      675 + 120 = 795 = 1.2's total (783 + 12), and that the 120 list contains
      `Verbara.Sdk.Ari.Client.AriClientFactory`, `VerbaraTelemetry.ActivitySourceNames`,
      `NatsBridge.StopAsync`, `AmiConnectionOptionsValidator.Validate` and the four `…Generator`
      types. A classifier that puts a hand-written member in the synthesised bucket silently removes
      it from D4's review, so the boundary cases belong in the commit message.

- [ ] 1.4 Write `docs/decisions/0063-*.md` (Status: Proposed → Accepted at merge) carrying D1–D6 from
      `design.md`, the measured figures and the analyzer version the `RS0016` message format was
      measured on, D3's consequence that `Unshipped` will hold entries that are not new API, the note
      that a future C# version emitting new synthesised members will fail the build until they are
      declared, the sentence that the per-package guard judges files as they exist when it runs and
      trusts the checkout's own build not to rewrite what the compiler read (spec requirement 5),
      and the note that `Directory.Build.props`'s two `PublicAPI.*.txt` `AdditionalFiles`
      lines duplicate what the analyzer's own `buildTransitive` targets add — kept on purpose — the
      compiler receives each file twice either way (2.5's record shows both `/additionalfile:`
      lines), so removing them changes nothing and is not this change's question. Leave the D4
      assessment section as a placeholder for task 2.1 to fill. Verify
      `openspec validate --all --strict` passes and the file exists.

- [ ] 1.5 Land the ADR-count coupling in the same commit as 1.4: bump `README.md`'s `**N ADRs**`
      figure, update its row in `docs/claim-registry.md`, and add the catalog row in
      `docs/decisions/README.md`. Verify `dotnet test Tests/Verbara.Sdk.OpenTelemetry.Tests/` passes —
      `ThePublishedAdrCount_ShouldMatchTheDecisionsOnDisk` and `TheDecisionCatalog_ShouldListEveryAdrOnDisk`
      both fail if any of the three is missed, and the catalog guard is a set equality both ways.

## 2. Phase B — critical components (one focused subagent each)

- [ ] 2.1 Carry out design D4's assessment of the **120** hand-written members from 1.3's split,
      **before any of them is recorded** (spec requirement 4): for each, intended public API or not,
      written into ADR-0063's placeholder. Verify every one of the 120 is accounted for, and that any
      judged unintended is named explicitly with its package. **Remove nothing** — a removal is a
      `CP0002` break and belongs to its own change with its own release tier; this task's output is a
      list, and the ADR is where it lives. The guard is not needed for this: `RS0016` stays in
      `NoWarn` until 2.4, so the build is green for the duration. D4 says "a pass by a human eye" and
      `rules.tasks` says a fresh subagent: the subagent drafts, and the approver reads the 120
      findings and signs them in the ADR before 2.4 lands — the record must not land ahead of that
      signature.

      The 8 generator entries take one recorded finding, and this is the finding the measurement
      supports: intended — each type is the Roslyn host's entry point (`[Generator]` +
      `IIncrementalGenerator`), its only public members are the contract's `Initialize` and the
      implicit constructor, and the package ships no `lib/` (`IncludeBuildOutput=false`; the DLL packs
      under `analyzers/dotnet/cs` only), so no consumer can bind to these symbols through the package.
      Note there that a later `public → internal` on these types is **not** a `CP0002` break —
      package validation has no `lib/` to compare for this package — so the record file is the only
      thing that would notice; that is a different removal path from the other 112, not the same one.
      Any public member outside that set, on a generator type or elsewhere in that package, gets an
      ordinary finding of its own.

- [ ] 2.2 Commit the route design D2 records as `tools/declare-public-api.sh`, and re-verify it on
      `Verbara.Sdk.Cluster.Primitives` alone before 2.3 touches 28 more packages: take that project's
      symbols from 1.2's inventory, append them to its `PublicAPI.Unshipped.txt` (assert the
      `#nullable enable` header and a trailing LF first; sort only the appended block, ordinal; leave
      existing lines alone), rebuild the project with `-p:NoWarn=CS1591%3BRS0037%3BRS0041` and
      `TreatWarningsAsErrors` at its default, and verify 0 warnings, 0 errors and exactly **19** lines
      added (the 2026-09-25 measurement; a different count means `main` moved and is a finding, not a
      reason to adjust). No route search and no `dotnet format` diagnosis: if the compiler disagrees
      with the script, report it — that is the decision point D2 used to describe, and it is now a
      narrow one.

- [ ] 2.3 Apply 2.2's script to the remaining 28 packages — `Verbara.Sdk.Ami.SourceGenerators`
      included, now that 1.2 attached the analyzer — writing every entry to `PublicAPI.Unshipped.txt`
      and leaving `PublicAPI.Shipped.txt` untouched (design D3). Verify
      `git diff --stat -- '*PublicAPI.Shipped.txt'` is empty, that the added line count per package
      matches 1.2's inventory, and that each **`src/` project alone** rebuilds with
      `-p:NoWarn=CS1591%3BRS0037%3BRS0041` and `TreatWarningsAsErrors` at its default reporting
      **0 warnings, 0 errors** — `RS0016` here is a missing entry, `RS0017` a mistyped one, and
      either is this task's failure.
      **Do NOT run that command across the solution.** Measured 2026-09-25: a global `-p:NoWarn`
      replaces the value every project inherits, so it also discards the per-project appends, and
      there are **six**, not the one an earlier draft of this task claimed — `CA1707` at
      `Directory.Build.props:101` for every test project, `CA1707` again in three test `.csproj`
      files that *replace* rather than append, `CA1822` in `Verbara.Sdk.Benchmarks`, and `NU5104` in
      `Verbara.Sdk.OpenTelemetry`. Solution-wide the command produced **3055 `CA1707` errors and
      zero `RS0016`**, which is a broken check, not a finding. The solution-wide proof belongs to
      2.4, where the suppression is removed from the file and every `$(NoWarn)` append still
      resolves.

- [ ] 2.4 Turn the guard on, and **this task carries the solution-wide proof** 2.3 cannot. Remove
      `RS0016` from `Directory.Build.props`'s `<NoWarn>` — editing the file, not overriding it, so
      every per-project `$(NoWarn)` append still resolves — then verify
      `dotnet build Verbara.Sdk.slnx -c Release --no-incremental` reports **0 warnings, 0 errors**
      with no `-p:` override at all. Both halves are already measured on a dry run of 2.2–2.4
      (2026-09-25): with all 783 entries written the solution built 0/0 with `RS0016` live, and the
      1.1 probe re-added then failed that project with
      `error RS0016: Symbol 'Verbara.Sdk.Sessions.Manager.CallSessionManager.MainSessionProbe() -> int'
      is not part of the declared public API`. A different result means `main` moved and is a finding.
      Then fix the two comments that describe the old arrangement: line 13 claims the diagnostic comes from
      source-generated code in `obj/`, which the measurement contradicts — most of the 671 are the C#
      compiler's own `record` synthesis for types declared in `src/` — and line 16 claims severity is
      controlled in `.editorconfig`, which `NoWarn` overrode. Leave the two `PublicAPI.*.txt`
      `AdditionalFiles` lines in place (1.4 records why). Verify
      `dotnet build Verbara.Sdk.slnx -c Release --no-incremental` succeeds with **0 warnings and 0
      errors**, with `TreatWarningsAsErrors` left at its repo default and no global property on the
      command line.

- [ ] 2.5 Assert the guard across every shipped package (design D6, spec requirement 2's second
      scenario) from the arguments of the compile that produced each package — not from
      evaluation, not from a second compile, and, for what the arguments cannot show, from the
      compiler's SARIF log of that same compile and the analyzer-config files it names (D6 measures
      what each sees). Three pieces; the guard prints what it read and the sentence that it read it
      after the fact (spec requirement 5).
      **(1) The record.** Add `Directory.Build.targets` at the repository root (there is none
      today) with one target, armed only by a global property:
      `<Target Name="RecordPublicApiCompilerArgs" AfterTargets="CoreCompile" Condition="'$(RecordCscArgs)' == 'true'">`
      holding
      `<WriteLinesToFile File="$(IntermediateOutputPath)public-api.csc-args.txt" Lines="@(CscCommandLineArgs)" Overwrite="true" Condition="'@(CscCommandLineArgs)' != ''" />`
      and an `ItemGroup` with `<FileWrites Include="$(IntermediateOutputPath)public-api.csc-args.txt" />`.
      Also a `<PropertyGroup Condition="'$(RecordCscArgs)' == 'true'">` holding
      `<ErrorLog>$(IntermediateOutputPath)public-api.sarif,version=2</ErrorLog>` — the comma is
      literal in a property; on a `-p:` command line it needs `%2C` or csc writes SARIF 1.0
      (measured: the record then reads `/errorlog:obj/public-api.sarif` and the file says
      `"version":"1.0.0"`) — and a second
      `<FileWrites Include="$(IntermediateOutputPath)public-api.sarif" />`.
      `Directory.Build.targets` is imported after the project body, so a project-body `ErrorLog`
      loses; one set in a target changes the record's `/errorlog:` line, which (2) checks against
      `/out:`. Measured 2026-09-26 on SDK 10.0.401: the record carries
      `/errorlog:"obj/Release/net10.0/public-api.sarif,version=2"`, csc writes the file relative to
      the project directory on failed compiles too, the two properties on `Build Release` stay two,
      and the log costs the 29-project build under 2.1 s and 22.6 MB under `obj/`.
      In `.github/workflows/ci.yml`, `Pack Warnings Gate`'s `Build Release` step becomes
      `dotnet build Verbara.Sdk.slnx -c Release -p:ProvideCommandLineArgs=true -p:RecordCscArgs=true`.
      Measured 2026-09-26 on SDK 10.0.401: one file per project in that project's own
      `obj/Release/<tfm>/`, under `-m:1` and `-m:3` alike; about 21 KB and 210 lines a project; no
      measurable cost for the record itself, the log's as above. A failed `CoreCompile` writes
      nothing — neither the record nor its `FileWrites` entry — and an up-to-date one leaves
      the previous file — the record of the compile that produced the DLL still in `obj/`. CI's
      fresh checkout compiles every project; a local run builds `--no-incremental` with the two
      properties first.
      **(2) The guard.** Add `scripts/ci/check-public-api-guard-coverage.sh`: for every project
      under `src/` that evaluates as `IsPackable=true` (evaluation is fine for choosing the
      projects; it is not fine for judging them), read
      `<project dir>/obj/Release/*/public-api.csc-args.txt` and fail, naming the project and the
      line, unless all of these hold: the file exists (missing is "not proven", never a pass);
      `/warnaserror+` is a line; no line is `/skipanalyzers+`; no line is `/warn:0`; no `/nowarn:`
      and no `/warnaserror-:` line contains `RS0016` (case-insensitive); an `/analyzer:` line ends
      in `Microsoft.CodeAnalysis.PublicApiAnalyzers.dll`; every `/additionalfile:` line whose file
      name is `PublicAPI.Shipped.txt` or `PublicAPI.Unshipped.txt` resolves, relative to the project
      directory and with symbolic links followed, to that project's own file of that name, and each
      of the two names is present at least once (twice today, from `Directory.Build.props` and the
      analyzer's `buildTransitive` targets) — measured 2026-09-26: a second pair from another
      directory, added as `AdditionalFiles` and listing the undeclared symbols, builds green with
      0 `RS0016` on the console and none in the log while the package's own record stays untouched,
      and the record shows it as `/additionalfile:../extra/PublicAPI.Shipped.txt` (the analyzer
      matches the two names exactly; a lower-cased pair is ignored and the build fails); no line
      starts with `/ruleset:` (measured: a ruleset with `RS0016` at `None` silences it even with the
      root `.editorconfig` line at `warning`).
      Then read the files that same record names, for what no argument shows.
      **Analyzer-config files.** Every `/analyzerconfig:` path on the record is scanned — wherever
      it lives, the root `.editorconfig` included, with no exempt file and no exempt directory: the
      SDK's own `analysislevel_*.globalconfig` presets pass the rule (all 770 shipped with
      10.0.401, measured), so nothing is trusted by path; a relative path resolves against the
      project directory, and a path that does not exist at scan time is "not proven". One rule,
      applied to every non-comment, non-section `key = value` line (key lowercased; value cut at an
      inline `#` or `;`; a `:` separator accepted as the compiler accepts it; trailing `\r`
      stripped): fail if the key begins with `dotnet_public_api_analyzer` — the prefix, not the
      names: 5.6.0's DLL carries exactly two keys under it, `skip_namespaces` and
      `require_api_files`, and a prefix ban covers whatever a later version adds; fail if the key
      begins with `dotnet_analyzer_diagnostic`; if the key is `dotnet_diagnostic.rs0016.severity`
      the value must be `warning` or `error` — every occurrence, in every section, because a later
      line or a later section wins over line 71 (measured 2026-09-26: line 71 intact plus
      `dotnet_diagnostic.rs0016.severity = none` two lines below, or `= silent`, or a later section
      with `suggestion`, each builds green with an undeclared public type; so does
      `DOTNET_PUBLIC_API_ANALYZER.SKIP_NAMESPACES` upper-cased, and so does the same key in a
      `.globalconfig` beside the project, which the SDK picks up unasked and the record lists); and
      fail if `rs0016` appears anywhere else on a key line — which fails
      `dotnet_code_quality.RS0016.excluded_symbol_names` too, measured inert and failed on purpose.
      The file at the repository root must contain at least one such severity line. A comment
      mentioning `RS0016` is legal. Measured: 0.06 s for 29 runs, and today's root file, the
      compiler's generated `*.GeneratedMSBuildEditorConfig.editorconfig` and every SDK preset pass.
      **The compiler's log.** The record must carry exactly one `/errorlog:` line; its value (quotes
      stripped) must be the directory of the record's own `/out:` line followed by
      `public-api.sarif,version=2`, so the file the guard reads is the one the record names and a
      project that redirects `ErrorLog` in a target shows it there; that file, relative to the
      project directory, must exist and parse (`jq`, which `check-release-provenance.sh` already
      requires); `.version` must be `"2.1.0"`; `.runs[0].tool.driver.rules[]` must contain
      `{"id":"RS0016"}` — the analyzer ran in this compile, which is stronger than the `/analyzer:`
      line; and `[.runs[0].results[] | select(.ruleId == "RS0016")]` must be empty. A compile that
      produced a record built green, so any `RS0016` result in its log is one that something
      suppressed or demoted, and each fails: print
      `.locations[0].physicalLocation.artifactLocation.uri`, `.region.startLine`, `.message.text`
      (it names the symbol) and `.suppressions[0].properties.suppressionType` — `Pragma Directive`,
      `SuppressMessageAttribute` or
      `DiagnosticSuppressor { Suppression Id: …, Suppression Justification: … }` — or, with no
      `suppressions`, its `.level` (`warning` under `TreatWarningsAsErrors=false`, `note` under an
      analyzer-config `suggestion`). A generator's file has a `file://` uri under the project's
      `obj/Release/<tfm>/<generator assembly>/<generator type>/<hint name>` that does not exist on
      disk; the message tells the reader to rebuild that project with
      `-p:EmitCompilerGeneratedFiles=true` and read `obj/Release/<tfm>/generated/…`. Do not read
      `invocations[0].executionSuccessful`: it is `true` on a failed compile too (measured); the
      record's existence is what proves the compile succeeded, because a failed `CoreCompile` writes
      none. Source text is not scanned at all: the compile list does not include what a generator
      emitted, and text cannot see a `SuppressMessage` whose id is a `const` from another file or a
      suppressor (D6). Missing or unparseable log, wrong version, a rule list without `RS0016`:
      "not proven", never a pass.
      **The record files.** After `Build Release`,
      `git diff --quiet -- '*PublicAPI.Shipped.txt' '*PublicAPI.Unshipped.txt'` must exit 0 and
      `git status --porcelain=v1 --untracked-files=all -- '*PublicAPI.*.txt'` must print nothing — a
      build step that writes the symbols into the record on the runner leaves the analyzer nothing
      to report, and this is the one place it shows.
      On CI, also require one `./artifacts/<project name>.*.nupkg` per selected project and as many
      `.nupkg` files as selected projects, so the set judged is the set packed. Run it in
      `Pack Warnings Gate` immediately after the package-validation guard — no new job and no new
      check-run name (ADR-0042 D3). Cost: 29 record reads, 29 `jq` reads at 3 ms, 29 analyzer-config
      scans at 2 ms and one `git diff`, under 2 s all told, plus about 7 s of evaluation for the
      selection; no new tool — `jq`, `git` and `awk` are all already required.
      **What a proven package prints:** the three things read — the argument record, the log and the
      analyzer-config paths — and one sentence that they were read as they existed when the guard
      ran (spec requirement 5).
      **(3) The harness and the invariant.** `scripts/tests/test_public_api_guard_coverage.sh`
      runs in `Coverage Script Tests` against fixture record files and a stand-in `dotnet` for the
      selection: clean → 0; each forbidden line above; a missing file; an `/additionalfile:` pair
      from another directory; a record with no `/errorlog:` line, one whose `/errorlog:` directory
      differs from `/out:`, one whose log is missing, a log at `"version":"1.0.0"`, a log whose
      rules omit `RS0016`, and a log with one suppressed `RS0016` result (the fixture logs are the
      JSON real builds wrote, trimmed to `version`, `runs[0].tool.driver.rules` and
      `runs[0].results`); an analyzer-config fixture with `skip_namespaces` — once in a nested file,
      once in a `.globalconfig`, once in the root file with the key upper-cased — a root fixture
      whose line 71 is intact and which sets `dotnet_diagnostic.rs0016.severity = none` later in the
      same section, another with `silent`, another that appends a section with `suggestion`, a root
      fixture with no severity line at all, and a modified record file in the stand-in checkout → 1
      naming the file, line and cause; a root fixture with the line at `error`, a comment line
      mentioning `RS0016`, a `dotnet_code_quality.excluded_symbol_names` line, and the repository's
      own root `.editorconfig` as it stands → 0.
      Its real-MSBuild cases run in `Pack Warnings Gate` the way `PKV_MSBUILD_CASES=1` does:
      a scratch project built with the two properties, once clean (proven) and once each with
      `WarningLevel=0`; `RunAnalyzersDuringBuild=false` with `RunAnalyzers` unset; `RS0016`
      appended to `NoWarn` inside a `BeforeTargets="CoreCompile"` target; an `<Analyzer Remove>`
      in such a target; `TreatWarningsAsErrors=false`; `CodeAnalysisRuleSet` at a ruleset with
      `RS0016` at `None`; `dotnet_public_api_analyzer.skip_namespaces` in its root `.editorconfig`
      with the root's `dotnet_diagnostic.RS0016.severity = warning` line left in place, for a
      namespace with no record lines (one with record lines fails with `RS0017` instead, measured);
      a second `PublicAPI.*.txt` pair from another directory listing the probe;
      `#pragma warning disable RS0016` in a hand-written file;
      `[SuppressMessage("ApiDesign", Ids.Api)]` with `Ids.Api` a `const` in another file; and a
      second scratch project — `netstandard2.0`, `Microsoft.CodeAnalysis.CSharp` and `.Analyzers`
      at the pinned versions, referenced with `OutputItemType="Analyzer"` and
      `ReferenceOutputAssembly="false"` the way `Verbara.Sdk.Ami` references its generator —
      carrying one `IIncrementalGenerator` that emits an undeclared public class under
      `#pragma warning disable RS0016` and one `DiagnosticSuppressor` for `RS0016`, run once with
      each — every one measured 2026-09-26 to build green with an undeclared public member (the
      generator pair in about 3 s `--no-incremental`), and each must read "not proven".
      Do **not** pin "both `PublicAPI.*.txt` deleted": with neither file 5.6.0
      reports every public symbol, with exactly one it reports `RS0048`, and both fail the build
      on their own (measured). In `Tests/Verbara.Sdk.Governance.Tests`, one test applying (2)'s
      analyzer-config rule to the root `.editorconfig` directly, so the unit lane fails before any
      record exists: every occurrence of `dotnet_diagnostic.rs0016.severity` (case-insensitive) is
      `warning` or `error`, at least one exists, and no key begins with
      `dotnet_public_api_analyzer` or `dotnet_analyzer_diagnostic` — with a failure message that
      says why: a specific severity there is what keeps `dotnet_analyzer_diagnostic.severity = none`
      and the `category-ApiDesign` demotion inert (measured both ways), and a later line or a later
      section overrides it silently — `none`, `silent` and `suggestion` each measured.
      Verify: after
      `dotnet build Verbara.Sdk.slnx -c Release --no-incremental -p:ProvideCommandLineArgs=true -p:RecordCscArgs=true`
      on the integrated branch the guard exits 0 — which it can only do because 1.2 attached the
      analyzer to `SourceGenerators`; had it not, that project's record would carry no `/analyzer:`
      line for the package, and this is where an exemption would have had to be written — exits 1
      on each fixture above, the harness is green, and the Governance test passes.

## 3. Phase C — integration (batched)

- [ ] 3.1 Commit the negative control as `scripts/ci/check-public-api-guard-fires.sh` (design D5,
      spec requirement 3) — a script, not a documented sequence, because restoration has to run on
      every exit path. It adds an undeclared public member that reads instance data to one shipped
      source file (1.1's probe in `CallSessionManager.cs`), builds that project incrementally with the
      committed configuration — no `-p:NoWarn`, no `-p:TreatWarningsAsErrors` — and restores the file
      from a copy taken before the edit, using the pattern
      `scripts/ci/check-package-validation-coverage.sh:101-102` already uses:
      `orig="$(mktemp)"; cp -- "$file" "$orig"; trap 'cp -- "$orig" "$file"; rm -f -- "$orig"' EXIT`
      installed before the first edit. **Never restore with `git checkout --`, `git restore` or
      `git stash`** — on a tree with uncommitted work they replace the developer's file with HEAD,
      and archived task 6.6 of `2026-09-13-enforce-unguarded-public-claims` records that exact
      accident. Refuse to start if the probe is already present (`grep -q -- MainSessionProbe
      "$file"`): that is the leftover of an interrupted run, and the guard's own remedy — "declare
      it" — would otherwise turn it into declared API. **Exit 0 only if the build output contains
      `error RS0016` naming `MainSessionProbe`**; a non-zero build exit alone is not the assertion —
      `CA1822`, a stale `Unshipped` entry (`RS0017`) or a compile error all fail a build without
      proving anything, and each of those, like a green build, exits 1 with a message that says the
      guard was not proven. Add `scripts/tests/test_public_api_guard_fires.sh`, run in
      `Coverage Script Tests`, with a stand-in `dotnet` (the pattern
      `test_package_validation_coverage.sh` uses): stand-in prints `error RS0016 … MainSessionProbe`
      → exit 0 and the file restored; prints `error CA1822` → exit 1; prints nothing and exits 0 →
      exit 1; probe already present → refuses; stand-in killed mid-run → non-zero and the file
      restored. Then run the real script as the **last** step of `Pack Warnings Gate`, after the pack
      and both coverage guards, so the mutated checkout feeds nothing else; there the probe's project
      recompiles alone, not the four-project graph 1.1 rebuilds. Verify from outside the script,
      three times: on a clean tree, on a tree carrying an unrelated uncommitted edit to the same
      file, and once with the build interrupted by SIGINT. For each run capture `sha256sum --
      "$file"` and `git status --porcelain=v1 --untracked-files=all` before and after; both must be
      **the same, not empty** — an empty status after a wiped local edit is what a wrong restore looks
      like. Contrast the whole with 1.1's recorded output in the same place, so the pair reads as
      "dead before, alive after".

- [ ] 3.2 Turn every scenario in `specs/public-api-declaration/spec.md` into something that is
      actually checked. Requirement 1 is checked by the build itself; requirement 2 is **not** — a
      package that silences the diagnostic for itself builds exactly as green as one that declares
      everything, which is this change's own thesis — so it is checked by 2.5's guard and Governance
      scan on every pull request; requirement 3 by 3.1's script, its harness and its CI step;
      requirement 4 by 2.1's list and the approver's signature on it. Requirement 5 is checked in
      part: its first scenario by 2.5's `git diff` over the record files and that fixture, its
      second by the sentence the guard prints and by 1.4's note in the ADR; its third — an option
      under a prefix the rule does not know — is asserted by nothing that runs, and saying so is
      this task's job. Verify each scenario names what checks it, and say plainly which — if any —
      is asserted by nothing but a human reading a file, rather than rounding it up.

- [ ] 3.3 Write the `CHANGELOG.md` entry under `[Unreleased]`, labelled **`### Changed`** — not
      `BREAKING`. No consumer-visible behaviour changes, no package contents change, and
      `PublicAPI.*.txt` files are analyzer metadata that are not shipped. State what a consumer can
      now rely on: a public member added to a shipped package fails the build unless it is declared.
      **Pick an insertion anchor distinct from any other in-flight PR's and state it in the PR body.**

- [ ] 3.4 Confirm the change moves no published surface and no package contents:
      `git diff main...HEAD -- '*PublicAPI.Shipped.txt' '*CompatibilitySuppressions.xml'` is empty,
      `Directory.Build.props`'s `PackageValidationBaselineVersion` is unchanged, and
      `dotnet pack -c Release` succeeds for the solution. Note in the commit that a green `pack` is
      **not** evidence the ApiCompat gate ran — it is incremental, and the honest check is a negative
      control — but that this change does not touch the baseline, so the gate's state is unchanged.

- [ ] 3.5 **Verification.** On the integrated branch: `dotnet build Verbara.Sdk.slnx -c Release
      --no-incremental` with **0 warnings**; the full unit lane under the CI filter;
      `Tests/Verbara.Sdk.Governance.Tests` and `Tests/Verbara.Sdk.OpenTelemetry.Tests` (tree-scanning
      guards — green on touched projects is not green in CI); `openspec validate --all --strict`;
      `bash scripts/tests/test_public_api_guard_coverage.sh` and
      `bash scripts/tests/test_public_api_guard_fires.sh` (green);
      `dotnet build Verbara.Sdk.slnx -c Release --no-incremental -p:ProvideCommandLineArgs=true -p:RecordCscArgs=true`
      followed by `bash scripts/ci/check-public-api-guard-coverage.sh` (exit 0 — a record from a
      build without the two properties is "not proven"); and
      `bash scripts/ci/check-public-api-guard-fires.sh` (exit 0 — and it must exit 0 only because
      the build failed with `RS0016` naming the probe). Then read `.github/workflows/ci.yml` and run
      the remaining fast, deterministic, non-service steps it lists rather than recalling job names.

- [ ] 3.6 Do **not** bump `Directory.Build.props`'s `PackageVersion`. The version is cut at release
      time (ADR-0055). Verify `PackageVersion` is absent from this change's diff.

## 4. Measured elsewhere, or not yet owned

- **`Unshipped` is never promoted to `Shipped`.** The records total 8635 lines in `Shipped` and 933
  in `Unshipped`, and 8 of the 29 packages carry an empty `Unshipped` while others carry a populated
  one that predates several releases — `Verbara.Sdk.Cluster.Primitives` has a one-line `Shipped` and
  a 66-line `Unshipped`. Whatever promotes one to the other at release time either does not exist or
  does not run. Out of scope here (design D3), **no open change owns it**, and it is what made this
  proposal's first draft misread that package as undeclared.
- **`RS0037` and `RS0041`** are suppressed beside `RS0016` with their own one-line justifications,
  neither of which was measured during this work. Whether they are load-bearing or inherited is
  unknown, and nobody has asked.
- **`.editorconfig:72` demotes `RS0026` to `suggestion`**, labelled "Optional parameter overloads —
  existing API". It is a second severity demotion in the file this change makes load-bearing, it was
  not measured, and nothing establishes whether the "existing API" it was written for still exists.
  It belongs beside `RS0037`/`RS0041` above and **has no open change of its own.**
- **Three test projects replace `NoWarn` rather than appending to it** —
  `Tests/Verbara.Sdk.FunctionalTests`, `Tests/Verbara.Sdk.Push.Webhooks.IntegrationTests` and
  `Tests/Verbara.Sdk.DocSnippets.Tests` each write `<NoWarn>CA1707</NoWarn>` with no `$(NoWarn);`
  prefix, so they silently discard everything `Directory.Build.props` sets, `CS1591` included. Found
  while diagnosing why the global-property route failed solution-wide (task 2.3). They are test
  projects, so no public-API record is at stake, but the pattern is a live footgun for any future
  repository-wide diagnostic. Out of scope, **no open change owns it**.
