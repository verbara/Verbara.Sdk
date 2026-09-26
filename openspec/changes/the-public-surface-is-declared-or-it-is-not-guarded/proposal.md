---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Every consumer of the 29 published packages, and the reviewers of every future PR — the guard that should make a public API addition visible in a diff has never run
decision_ref: Sdk/ADR-0063
---

# Proposal: the-public-surface-is-declared-or-it-is-not-guarded

## Why

This repository believes it guards public API additions. It does not, and real public API has
accumulated undeclared for as long as the guard has been off.

`Directory.Build.props:15` reads `<NoWarn>$(NoWarn);CS1591;RS0016;RS0037;RS0041</NoWarn>`, and the
comment on the next line says *"RS0016 severity controlled in .editorconfig for user-authored code
(warning level)"*. `.editorconfig:71` does set `dotnet_diagnostic.RS0016.severity = warning`. But
`NoWarn` wins over `.editorconfig`, so RS0016 never fires anywhere.

**Negative control, measured 2026-09-25 on `main` at `6f94caae`:** a
`public int MainSessionProbe() => _sessions.Count + _byLinkedId.Count;` added inside the shipped
`CallSessionManager` builds `Release --no-incremental` with **0 warnings and 0 errors**. Nothing else
catches it: no file under `Tests/`, `tools/`, `scripts/` or `.github/` reads
`PublicAPI.Unshipped.txt`, and package validation cannot help because an *addition* is not a `CP0002`
break.

**What the suppression has been hiding**, measured by removing `RS0016` from `NoWarn` and building
the solution with `--no-incremental`:

| | Count | What they are |
|---|---|---|
| Total symbols firing RS0016 | **783** | |
| Synthesised | 671 | C# record members (`PrintMembers`, `Equals(T?)`, `EqualityContract`, `GetHashCode`, `ToString`, `Deconstruct`, `<Clone>$`, `operator ==`/`!=`) and `System.Text.Json` source-generated context members |
| **Hand-written and never declared** | **112** | real public API |

The 112 are not incidental. They include a whole public type, `Verbara.Sdk.Ari.Client.AriClientFactory`;
17 members on `Verbara.Sdk.Sessions.AgentSession` and 16 on `QueueSession`;
`VerbaraTelemetry.ActivitySourceNames` and `.MeterNames`; `LiveActivitySource.Source` and
`PushActivitySource.Source`; `NatsBridge.StopAsync`; and `AmiConnectionOptionsValidator.Validate`.
Across the 29 packages the declared records total **9568 lines** — 8635 in `PublicAPI.Shipped.txt`
and 933 in `PublicAPI.Unshipped.txt`, with 8 packages carrying an empty `Unshipped`. A first draft of
this proposal cited `Verbara.Sdk.Cluster.Primitives`'s one-line `Shipped` file as evidence the package
was undeclared; that was wrong, and checking it is what found the real shape. Its API is fully
declared — in `Unshipped`, 66 lines — and was simply never promoted to `Shipped`. Promotion is a
separate question this change does not decide, and the 783 figure above is measured from the build,
not from reading the files, so it is unaffected.

The comment explaining the suppression is also wrong in its detail, which matters because it is what
a future reader would trust: line 13 says *"source-generated code in `obj/` produces undeclared API
(editorconfig can't suppress `obj/`)"*. Most of the 671 do not come from `obj/` at all — they are the
C# compiler's own record synthesis, emitted for types declared in `src/`.

**Now, rather than later,** because the cost is still bounded. Every release adds public members
while nothing records them, so the 112 only grows, and the day a reviewer needs the file to answer
"did this PR widen the surface?" it will answer wrongly.

This is the failure class the open change `a-published-surface-is-one-something-measures` names in
its own words — *"the repository publishes a claim, nothing connects the claim to the code, and the
test that should tell the difference would stay green if the code were deleted"* — on a fourth
surface: the build configuration itself. That change belongs to another session and is cited here,
not folded in.

## What Changes

- **RS0016 becomes a guard that actually fires.** It leaves `NoWarn` in `Directory.Build.props`, so
  an undeclared public member fails the build under `TreatWarningsAsErrors`. The two comments that
  describe the current arrangement are corrected or removed rather than left describing a mechanism
  that never worked.
- **The declared surface is completed.** Every public member the analyzer reports is written into the
  package's `PublicAPI.*.txt`, so the files describe the surface rather than a subset of it. How the
  671 synthesised members are handled is the design question (see `design.md`); what the requirement
  states is that after this change the files are complete and the build proves it.
- **The 120 hand-written members are reviewed, not just recorded** — the 112 plus the 8
  author-written entries of the source-generator package. Each is declared, and either recorded as
  intended or reported as one that should not have been public. Declaring an accidental
  export as intended is how an accident becomes a contract, so the review is a requirement rather
  than a suggestion — but **removing anything is out of scope here**: a removal is a `CP0002` break
  and belongs to its own change with its own release tier.
- **The guard is proven by a negative control, in the repository.** Re-adding a public member must
  fail the build, and that must be demonstrable by a committed, repeatable procedure rather than by
  one session's transcript. A guard that is merely re-enabled and not proven is precisely how this
  defect started.
- **The guard is proven on every package, not on one.** `Verbara.Sdk.Ami.SourceGenerators` never
  received the analyzer at all — `Directory.Build.props:69` excludes it by name — so "29 packages"
  is 28 today. It gets the analyzer (12 entries, measured), and a per-package check on every pull
  request, reading the arguments of the compile that produced each package, fails the one that lets
  any project opt out again; the negative control proves depth on one path, the check proves breadth
  on all of them.

- **Not in scope, deliberately:** removing any existing public member; changing `PackageValidation`,
  `PackageValidationBaselineVersion` or any `CompatibilitySuppressions.xml`; and `RS0037`/`RS0041`,
  which are suppressed for their own stated reasons and are not implicated by this measurement.

## Capabilities

### New Capabilities

- `public-api-declaration` — what this repository guarantees about its published surface: that every
  public member of a shipped package is declared, that an undeclared one fails the build rather than
  landing silently, and that the guard's liveness is itself demonstrable.

### Modified Capabilities

None. `claim-guards` governs quantitative claims published in living documents, and `ci-gating`
governs the shape of the CI workflow; neither states anything about the declared public surface or
the analyzers the build runs.

## Impact

- `Directory.Build.props` — the `NoWarn` list and the two comments describing it.
- `.editorconfig` — the `RS0016` severity line, which becomes load-bearing instead of decorative.
- `src/*/PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` across the 29 packages — populated to
  completeness. **How is decided in `design.md` D2:** the entries are the analyzer's
  own `RS0016` message text — measured to be byte-identical to the record line, implicit
  constructors and quoted constants included — written by a committed script and verified by
  rebuilding with the diagnostic live. `dotnet format analyzers --diagnostics RS0016` was tried and
  does not apply the fixer. A dry run on 2026-09-25 wrote all 783 entries across the **16** projects
  that have any, rebuilt the solution at 0 warnings and 0 errors with the diagnostic live, and
  confirmed the guard fires on a re-added probe.
- `docs/decisions/0063-*.md` — the durable decision. Adding an ADR also moves `README.md`'s `**N ADRs**`
  figure, its `docs/claim-registry.md` row and the `docs/decisions/README.md` catalog row, all in the
  same pull request.
- `Directory.Build.props` (again) — the analyzer item group stops excluding `SourceGenerators`;
  that package's `PublicAPI.Unshipped.txt` gains 12 lines.
- New: `Directory.Build.targets` (the recording target),
  `scripts/ci/check-public-api-guard-coverage.sh`, `scripts/ci/check-public-api-guard-fires.sh`,
  their harnesses under `scripts/tests/`, `tools/declare-public-api.sh`, and one Governance test.
  `.github/workflows/ci.yml`'s `Build Release` step gains two global properties and `Pack Warnings
  Gate` gains two steps, and `Coverage Script Tests` gains two; no new job and no new check-run
  name.
- **No behaviour changes for a consumer, and no package contents change.** `PublicAPI.*.txt` files are
  analyzer metadata; they are not shipped. `PackageValidation` compares against the published
  package, so the 2.6.0 baseline and the zero suppressions currently in the tree stay valid.
- Downstream: none. Sdk.Pro and Platform consume the packages, not these files.
