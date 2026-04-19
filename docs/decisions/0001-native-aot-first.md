# ADR-0001: Target Native AOT from day one

- **Status:** Accepted
- **Date:** 2026-03-16 (retrospective)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0003 (source generators), ADR-0004 (central package management)

## Context

The SDK was ported from [asterisk-java](https://github.com/asterisk-java/asterisk-java) with the explicit goal of being usable from telephony platforms that need sub-millisecond startup and predictable memory footprint — think VoIP gateways, AGI handlers, and edge containers where JIT warm-up is visible to callers. Traditional .NET reflection-heavy patterns (Newtonsoft.Json, `Type.GetMethod`, runtime expression trees) are incompatible with [Native AOT](https://learn.microsoft.com/dotnet/core/deploying/native-aot/).

Constraints in play:

- Target platform: `net10.0` with a pinned SDK version (`global.json`).
- Must ship as NuGet packages that library consumers can AOT-publish without trim warnings.
- Every dependency chosen needed to be AOT-clean or have a documented workaround.

## Decision

We will target **.NET Native AOT with zero runtime reflection**. Every public API must compile clean under `PublishAot=true` in a downstream consumer project. All serialization (AMI, ARI, JSON), dispatch (observer pattern), and options binding uses source generators or compile-time dispatch — never runtime type introspection.

## Consequences

- **Positive:** Consumer apps get instant startup (no JIT), smaller published binaries (trimmer removes dead code), and predictable memory. The trim warnings analyzer catches reflection regressions at build time. Benchmarks show 1.53M AMI events/sec on a Ryzen 9 9900X — the perf floor is high because the hot paths are zero-alloc.
- **Negative:** Some ergonomic patterns from the asterisk-java port needed rewriting (e.g. reflection-based event dispatch → source-generated `EventDeserializer` with 278 event types). Third-party libraries that don't ship AOT-safe builds are excluded from dependency candidates (e.g. some legacy Redis and MongoDB drivers).
- **Trade-off:** We accept slightly more boilerplate (explicit `[JsonSerializable(typeof(T))]` attributes, `[OptionsValidator]` partial classes) in exchange for a no-magic runtime that works in any host — including containers, Lambda, and desktop AOT apps.

## Alternatives considered

- **Reflection + JIT-only .NET** — rejected because we would lose ~60% of the SDK's performance differentiator vs asterisk-java. Zero-reflection is load-bearing in the `docs/research/benchmark-analysis.md` numbers and in the "batteries-included" promise of the SDK.
- **Trimming without AOT** (publish trimmed, but JIT at startup) — rejected because we wanted the full AOT benefit (no JIT warm-up, no P-stubs) and because trim warnings + AOT warnings are essentially the same discipline; we'd rather have the stricter analyzer.
- **Port to Go or Rust** — rejected because the SDK is a direct port of asterisk-java; Java → C# is a structurally close move, Java → Go/Rust is a rewrite. See the (local-only) cross-language analysis in `docs/superpowers/research/`.
