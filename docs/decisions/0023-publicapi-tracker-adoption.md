# ADR-0023: PublicAPI tracker adoption across all packages

- **Status:** Accepted
- **Date:** 2026-03-26 (retrospective — decision made during the v1.5.1 quality-tooling layer)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0001 (Native AOT first), ADR-0003 (source generators over reflection), ADR-0004 (central package management)

## Context

The SDK ships 23 packages to nuget.org as of v1.11.1. Every public type, method, property, constructor overload, and attribute in every one of those packages is an API surface that consumers code against. Breaking any of them without a major-version bump is a promise the SDK should be making intentionally, not accidentally during a refactor.

The naïve approach is to trust SemVer discipline in commit messages and PR reviews. That approach fails silently in predictable ways: a renamed parameter in a public method is a breaking change that does not look like one in a diff; a removed overload is a breaking change that a reviewer may not notice among the surrounding signal; adding `required` to a constructor parameter is breaking in C# 11+ and rarely surfaces in code review. At 23 packages and multiple committers, human review cannot scale as the only guardrail.

`Microsoft.CodeAnalysis.PublicApiAnalyzers` is Roslyn's solution to this problem. Every package carries two files: `PublicAPI.Shipped.txt` (APIs promised to consumers in a released version, immutable) and `PublicAPI.Unshipped.txt` (APIs added since the last ship, promoted to Shipped on release). The analyzer runs on every build. If a public API appears in neither file, the build fails with `RS0016`. If a public API is removed from the Shipped file without being explicitly marked removed, the build fails. The enforcement is mechanical, not cultural.

The cost side is low but real. Every new public type means one new line to add to `PublicAPI.Unshipped.txt`. Every signature change means two lines (the old line removed, the new line added). CI sees the diff. Reviewers see the diff. It becomes impossible to ship a breaking change without someone noticing the Shipped file changing.

The decision in v1.5.1 was to adopt the tracker across every shipping package — not a subset, not the "big" packages, every one. The reasoning is that the discipline only works if it is uniform; a consumer does not know which packages are "tracked" and which are not, and the SDK should not require that knowledge.

## Decision

Every package that ships to nuget.org carries `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` files at the project root. `Microsoft.CodeAnalysis.PublicApiAnalyzers` is referenced in `Directory.Packages.props` and enabled on every shipping project. Every release promotes `PublicAPI.Unshipped.txt` content into `PublicAPI.Shipped.txt` and empties the Unshipped file. API diffs show up in PR reviews as explicit textual additions and removals, independent of the implementation diff.

## Consequences

- **Positive:**
  - Breaking API changes cannot merge silently; the PR diff surfaces them as explicit line changes in the Shipped file.
  - New public APIs require a deliberate line in `PublicAPI.Unshipped.txt`; accidental public exposure of internal types is caught at build.
  - Release process is mechanical: `move unshipped → shipped` on every release tag, reset unshipped to empty. The `v1.10.0` release (commit `daa7ba0`) moved six VoiceAi packages' Unshipped entries to Shipped as a single tracked promotion.
  - Consumers reading the `PublicAPI.Shipped.txt` file in a given package version get a canonical, always-up-to-date list of everything they can depend on. Documentation never lies about what is public.
- **Negative:**
  - Every PR that touches a public type must update the tracker files, adding friction to contribution. Contributors forgetting the update get `RS0016` build errors that can be confusing the first time.
  - The tracker file format is line-oriented and merge-conflict-prone when two branches add different public APIs to the same package. Conflicts are easy to resolve but annoying.
  - 23 packages × 2 files = 46 tracker files to keep in sync at release time. The release checklist has to include the promotion step for each.
- **Trade-off:** We trade per-PR friction and release-step ceremony for an SDK-wide guarantee that no breaking change ships without being seen. At 23 packages and many committers, the alternative is that breaking changes eventually ship silently, which consumers discover on upgrade and the maintainers discover from bug reports. Mechanical enforcement is worth the ceremony. The audit in [`docs/research/2026-04-19-product-alignment-audit.md`](../research/2026-04-19-product-alignment-audit.md) §4 item #11 flagged this as a load-bearing discipline whose rationale is not visible from the tracker files themselves.

## Alternatives considered

- **SemVer discipline in commit messages alone** — rejected because it depends entirely on human reviewers noticing breaking changes in arbitrary-sized diffs. At 23 packages and multiple committers, this is not scalable; v1.1.0 and v1.3.0 both shipped with a minor accidental public-surface exposure before the tracker went in that would have been caught mechanically.
- **Manual API-diff audits at release time** — rejected because the cost is concentrated at release, when the pressure is highest to ship, and because a manual audit of 23 packages is exactly the kind of tedium human reviewers skim through. Continuous mechanical enforcement catches the issue when the context is freshest.
- **Tracker on "public" packages only** — rejected because the public/internal split is fuzzy: `Asterisk.Sdk.Ami.SourceGenerators` is consumed by every other AMI package but its external surface is largely MSBuild metadata; `Asterisk.Sdk.VoiceAi.Testing` looks internal but is consumed by downstream test projects. Uniform adoption is simpler than any line-drawing exercise.
- **`[PublicApi]` attribute-based marking without external tracker files** — rejected because there is no first-class Roslyn analyzer for this shape today, and the tracker-file pattern has the benefit that `PublicAPI.Shipped.txt` can be diffed independently of source changes — a reviewer reviewing API stability reviews the `.txt` file directly, not every source file.
