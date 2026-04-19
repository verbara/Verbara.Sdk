# ADR-0003: Use Roslyn source generators for protocol and config layers

- **Status:** Accepted
- **Date:** 2026-03-16 (retrospective)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0001 (AOT-first)

## Context

ADR-0001 commits us to zero runtime reflection. That affects three pervasive patterns in the asterisk-java port:

1. **AMI action/event marshaling.** asterisk-java used reflection to enumerate fields and build the wire format. We have 148 actions and 278 events; hand-written marshal code would be tedious and easy to drift out of sync with the Java source.
2. **ARI JSON (de)serialization.** 27 models + 46 events with nested objects. `System.Text.Json` has reflection-based and source-generated modes; only the latter is AOT-safe.
3. **Options binding and validation.** `IOptions<T>` binders typically use reflection against configuration sections; `[OptionsValidator]` generates the code.

We need a consistent story across all three.

## Decision

All compile-time codegen uses **Roslyn source generators**. Specifically:

- `Asterisk.Sdk.Ami.SourceGenerators` (internal package, not published): generates AMI `Action.ToWireFormat()` and `EventDeserializer` dispatch from C# POCO declarations.
- `System.Text.Json` source generators (via `[JsonSerializable(typeof(T))]` on a `JsonSerializerContext` partial class): one context per package (`AriJsonContext`, `SessionJsonContext`, …).
- `Microsoft.Extensions.Options.SourceGeneration` (via `[OptionsValidator]`): every `*Options` class has a generated validator.

Hand-written marshal code is acceptable when a generator would be over-engineered (e.g. small one-off payloads); the rule is "no reflection at runtime" not "always generate."

## Consequences

- **Positive:** Zero trim warnings, measured 283 ns for ARI Channel JSON deserialization, 115 ns for simple AMI action writes. AMI + ARI models stay in sync with the Java parent via `tools/generate-*.sh` scripts. Build is 8 s clean.
- **Negative:** Generators must be tested like any other code — `Asterisk.Sdk.Ami.SourceGenerators.Tests` exists. Debugging a generator is harder than debugging reflection-based code (you debug the emitted `*.g.cs` files). IDEs sometimes lag behind the generator on large renames.
- **Trade-off:** We accept the learning curve (Roslyn incremental generator APIs are not beginner-friendly) for a runtime that has no surprises.

## Alternatives considered

- **Manual hand-written marshal for everything** — rejected because of the 148 + 278 type count. It would work but becomes a maintenance liability on every Asterisk version bump.
- **T4 templates** — rejected because T4 runs at build time (not IDE time) and doesn't integrate with the IDE's IntelliSense / error list.
- **Expression trees at first-use** — rejected because expression trees involve runtime IL emission, which AOT prohibits.

## Notes

- AMI Actions and Events are **generated from Java source** via `tools/generate-ami-actions.sh` + `tools/generate-ami-events.sh`. Do not edit the generated C# — edit the generator template.
- Benchmark detail: see `docs/research/benchmark-analysis.md` §1 and §2.
