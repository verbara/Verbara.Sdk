# ADR-0016: VoiceAi `ProviderName` virtual override

- **Status:** Accepted
- **Date:** 2026-04-17 (retrospective — decision made during the v1.10.0 release)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0001 (Native AOT first), ADR-0013 (`ISessionHandler` abstraction), ADR-0014 (raw HTTP/WebSocket providers)

## Context

VoiceAi telemetry tags every STT and TTS emission with the name of the provider that produced it. Every metric point, every ActivitySource span, every HealthCheck report and every structured log carries a `provider` dimension; at contact-center scale the identification runs on a hot path that fires per audio frame, per partial transcript, per synthesis chunk. Since the SDK ships 4 STT providers and 2 TTS providers today and targets a roadmap of more, the cost of identifying a provider must be effectively zero.

The natural .NET idiom is to call `GetType().Name` on the instance and use that as the provider identifier. It works, it is correct, and it is what a first-time contributor will reach for. What that pattern hides is the per-call cost: `GetType()` triggers a runtime type lookup and `Name` materializes a `string` from the reflection metadata cache on every call. Measured against the `ProviderName` override in `Tests/Asterisk.Sdk.Benchmarks/VoiceAiBenchmarks.cs`, the gap is 1.11 ns vs 0.012 ns per call — roughly 92×. At zero allocations either way the difference is pure cycle cost, but at the telemetry emission frequencies VoiceAi runs at (every frame, every token, every synthesizer chunk), those cycles compound into a measurable share of the VoiceAi cost envelope.

A second concern is AOT legibility. `GetType().Name` works under trimming today, but it is exactly the kind of reflection-adjacent idiom that `EnableTrimAnalyzer` could start flagging in a future .NET release as the trimmer becomes stricter. A `const string` override sidesteps the question permanently.

## Decision

The `SpeechRecognizer` and `SpeechSynthesizer` base classes expose a virtual `ProviderName { get; } = GetType().Name` as a safe default so unregistered providers still produce telemetry, and every shipped provider overrides the property with a `const string` (e.g. `"Deepgram"`, `"ElevenLabs"`). Telemetry emission reads `ProviderName` directly on the hot path, never `GetType().Name`.

## Consequences

- **Positive:**
  - ~92× speedup on the STT/TTS telemetry hot path (1.11 ns → 0.012 ns per call) with zero allocations either way.
  - AOT-clean: a `const string` is the simplest possible value the trimmer can analyze.
  - The default path still works, so an external contributor adding a new provider gets correct telemetry before they notice the override idiom — they lose the speedup, not the correctness.
  - `Examples/VoiceAiCustomProviderExample/` and the `Examples/TelemetryExample/` README document the override expectation for consumers building custom providers.
- **Negative:**
  - The optimization is invisible from the call site; a well-meaning contributor can delete the override during cleanup without anything failing in tests.
  - Benchmark coverage is the only guardrail against regression — a dedicated benchmark (`VoiceAiBenchmarks.cs`) is the sole automated signal.
- **Trade-off:** We accept a second, overridable declaration per provider to preserve a measurable telemetry-hot-path win. The alternative — trust `GetType().Name` everywhere — is simpler to write but silently regresses at the scale VoiceAi is built to serve. The audit in [`docs/research/2026-04-19-product-alignment-audit.md`](../research/2026-04-19-product-alignment-audit.md) §4 item #5 flagged this as a load-bearing invariant that must survive future refactors.

## Alternatives considered

- **Rely on `GetType().Name` on every telemetry emission** — rejected because benchmark data shows ~92× regression versus the override at zero extra allocation budget, and the idiom is the exact kind of reflection-adjacent pattern ADR-0001 exists to avoid as the .NET trimmer tightens over releases.
- **Require the override (no default)** — rejected because a custom provider that forgets to set `ProviderName` would emit no telemetry at all, which is a worse failure mode than emitting slower telemetry. The virtual-with-default pattern keeps the contract forgiving.
- **Source-generated `[ProviderName("...")]` attribute** — considered but rejected as disproportionate. A generator buys nothing a `const string` override does not already buy, and it would add a second generator to maintain for the VoiceAi packages.
- **Separate `IProviderNamed` interface** — rejected because every provider has a provider name by definition; a marker interface adds ceremony without constraint. The base-class virtual is the correct shape.
