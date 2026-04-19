# ADR-0024: `BannedSymbols.txt` as AOT policy (build-time enforced)

- **Status:** Accepted
- **Date:** 2026-03-26 (retrospective — decision made during the v1.5.1 quality-tooling layer)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0001 (Native AOT first), ADR-0003 (source generators over reflection), ADR-0023 (PublicAPI tracker adoption)

## Context

ADR-0001 establishes Native AOT as a non-negotiable SDK invariant: `PublishAot=true` must succeed with zero trim warnings on every shipping package. ADR-0003 establishes source generators as the chosen alternative to reflection. Both decisions only hold up if the SDK's codebase actually does not call reflection APIs — and "actually does not" is a property the SDK has to enforce continuously, because the offending APIs (`Type.GetMethod`, `Activator.CreateInstance`, `System.Reflection.Assembly.Load`) are all available from every BCL namespace and will compile without warning by default.

Similar problems exist outside reflection. `DateTime.Now` and `DateTimeOffset.Now` read the local-machine clock, which surfaces as latent bugs when the SDK runs in containers configured to UTC while a developer's machine reads local time. `DateTime.UtcNow` is the correct call; `DateTime.Now` is never correct for the SDK's use cases but is indistinguishable to a compiler from the right one. The same applies in a softer form to `Thread.Sleep` (should be `Task.Delay`), `Task.Result` and `Task.Wait()` (should be awaited), `.GetAwaiter().GetResult()` (same), and a handful of other API shapes that compile fine and are almost always wrong in the SDK's context.

A style guide that says "do not use reflection, use `DateTime.UtcNow`" covers this set but depends on human reviewers to enforce it, and humans miss. A Roslyn analyzer that flags these APIs as errors covers the same set but enforces it at build. The latter is build-time policy. When the policy is encoded in a file the build can read, it becomes part of the product's invariants in the same way type signatures do, and it cannot drift during routine code review.

`Microsoft.CodeAnalysis.BannedApiAnalyzers` is the canonical Roslyn tool for this. It reads a `BannedSymbols.txt` file at the repo root; every symbol listed there becomes a compilation error at every callsite. The analyzer runs in every shipping project; the error cannot be suppressed without touching the file. The policy is both discoverable (anyone can read `BannedSymbols.txt` and learn what the SDK does not permit) and enforced (the build fails if the policy is violated).

The decision in v1.5.1 was to adopt the analyzer and seed `BannedSymbols.txt` with the APIs that break AOT (`System.Reflection.*`, `Activator.CreateInstance`, `Type.InvokeMember`, `Type.GetMethod`/`GetProperty`/`GetField`/`GetMembers`) and the APIs that are almost always wrong (`DateTime.Now`, `DateTimeOffset.Now`). The list has stayed stable across releases; one or two additions per quarter is the normal cadence.

## Decision

`BannedSymbols.txt` at the repo root encodes the SDK's AOT and code-quality policy as a list of banned symbols. `Microsoft.CodeAnalysis.BannedApiAnalyzers` is referenced in `Directory.Packages.props` and enabled on every shipping project. Violations are build errors, not warnings, and cannot be suppressed without modifying the shared file (which is a tracked change visible in code review). The file is the canonical record of what the SDK does not permit and why.

## Consequences

- **Positive:**
  - Reflection entry points — `System.Reflection.Assembly`, `Type.GetMethod`, `Type.InvokeMember`, `Activator.CreateInstance` — are compilation errors everywhere in the SDK. The AOT invariant of ADR-0001 is mechanically enforced rather than culturally requested.
  - Clock APIs that break containerized deployments (`DateTime.Now`, `DateTimeOffset.Now`) are mechanically blocked; only `DateTime.UtcNow` passes.
  - A contributor working on a new package inherits the policy automatically because `BannedSymbols.txt` is at the repo root and picked up by every project via the analyzer's conventions.
  - The policy is legible: anyone can open `BannedSymbols.txt` and read exactly what is banned and, with the accompanying error messages, why.
- **Negative:**
  - The analyzer can produce false positives in the rare case where an otherwise-banned API has a legitimate use (e.g. a test project might need `Activator.CreateInstance` to exercise a type). Those cases are handled by scoping the analyzer reference to shipping projects only — tests are not constrained — but the scoping has to be kept correct.
  - A contributor hitting a banned-symbol error for the first time sees an unfamiliar error code (`RS0030`) and may not immediately know how to respond. Documentation must point clearly at the file and at the rationale for each entry.
- **Trade-off:** We trade the flexibility of "a contributor could use reflection in a pinch" for a cast-iron guarantee that no callsite in the SDK uses reflection. The flexibility is a feature nobody needs; the guarantee is the product invariant the whole SDK is built on. The audit in [`docs/research/2026-04-19-product-alignment-audit.md`](../research/2026-04-19-product-alignment-audit.md) §4 item #12 flagged this as a load-bearing policy whose disablement during a dependency update "temporarily" would silently regress the AOT contract.

## Alternatives considered

- **Style-guide or review-time enforcement** — rejected because humans miss; the symbols the file bans are exactly the ones that compile without warning and look innocuous in context. At 23 packages and multiple committers, any policy that depends on every reviewer catching every occurrence will eventually fail.
- **Selective adoption (only `src/Asterisk.Sdk.Ami`, `src/Asterisk.Sdk.Ari`, …)** — rejected because the AOT guarantee is package-wide; a consumer AOT-publishing their app links every SDK package they reference, and one reflection call in one package is enough to break the publish. Uniform adoption is the only shape that preserves the guarantee.
- **Warning-level enforcement (let builds pass)** — rejected because `TreatWarningsAsErrors=true` (ADR-0004) would promote the warnings to errors anyway, and because a warning that is always an error is better expressed as an error. Error-level enforcement is also more discoverable when a contributor trips it.
- **Runtime checks (assert no reflection at startup)** — rejected because AOT publishing happens at compile time; by the time runtime checks could fire, the publish has already succeeded or failed for other reasons. Compile-time enforcement matches the shape of the invariant being protected.
