# ADR-0013: `ISessionHandler` as the VoiceAi dispatch seam

- **Status:** Accepted
- **Date:** 2026-03-19 (retrospective — decision made during Sprint 24)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0001 (Native AOT first), ADR-0003 (source generators over reflection)

## Context

Voice AI on a PBX has two fundamentally different inference patterns that consumers want to pick from at deployment time, not at compile time. The first is the classic turn-based flow: voice-activity detection gates a speech-to-text pass, the transcript goes to an LLM handler (`IConversationHandler`), and the reply is synthesized back to audio. This pattern is composable — STT, LLM, and TTS providers are independently swappable, latency is dominated by the slowest step, and the whole chain is controllable (you can log every turn, inject safety filters, replay a conversation offline). The second is the streaming pattern that the OpenAI Realtime API exposes: a single persistent WebSocket carries both audio frames and model events, voice activity is detected server-side, and the model replies as audio tokens arrive. End-to-end latency drops, but the call is monolithic — you either use the provider's full loop or nothing.

A consumer building a contact center typically wants to start with the turn-based pipeline (cheaper, observable, easy to swap providers per tenant) and migrate selected tenants to the streaming bridge later — without rewriting their AudioSocket acceptor, their business logic, or their session dispatch. The forces in play are: both patterns must be Native AOT-clean (no runtime reflection, no dynamic dispatch beyond a single virtual call — see ADR-0001 and ADR-0003); both must plug into the same `AudioSocketServer` acceptor so we test the accept path once; and both must carry full IOptions/logger/provider wiring via DI, because the concrete implementations have very different dependency graphs (five or six services for the pipeline, one WebSocket client for the bridge).

## Decision

Introduce [`ISessionHandler`](../../src/Verbara.Sdk.VoiceAi/ISessionHandler.cs) — a single-method interface with `ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default)` — as the sole dispatch seam from the AudioSocket acceptor to the AI layer. Both patterns implement it: [`VoiceAiPipeline`](../../src/Verbara.Sdk.VoiceAi/Pipeline/VoiceAiPipeline.cs) for turn-based STT+LLM+TTS, and [`OpenAiRealtimeBridge`](../../src/Verbara.Sdk.VoiceAi.OpenAiRealtime/OpenAiRealtimeBridge.cs) for streaming. Consumers choose by DI registration alone — `AddVoiceAiPipeline(...)` or `AddOpenAiRealtimeBridge(...)` — and the acceptor resolves the registered `ISessionHandler` from the container with no compile-time branch in consumer code.

## Consequences

- **Positive:**
  - Consumers swap patterns by changing one DI call; acceptor code and business logic are untouched. A contact center can prototype with the pipeline and cut selected tenants over to the bridge per environment.
  - Future implementations (Azure AI Voice Live API, local `whisper.cpp`, experimental multi-agent orchestrators) plug in without a breaking-change cycle — they register as `ISessionHandler` and inherit the same acceptor, the same DI surface, and the same tests.
  - Acceptor code stays provider-agnostic; the accept-and-dispatch path is covered by a single test suite instead of two.
  - AOT-friendly: one virtual call per session (not per audio frame or per event) is unmeasurable in BenchmarkDotNet next to the WebSocket and STT costs that dominate.
- **Negative:**
  - Pattern-specific optimizations (pipeline-local provider warmup, bridge-local WebSocket pooling, per-turn metrics shapes) cannot be expressed through the interface. Each implementation exposes them as its own concrete API surface (`VoiceAiPipeline.Events`, `OpenAiRealtimeBridge.Events`), and consumers that want them must depend on the concrete type for those hooks.
- **Trade-off:** The interface is deliberately minimal — one method, no lifecycle events, no warmup hook, no per-session progress callback. Richer observability belongs on each concrete implementation (`VoiceAiPipeline` exposes a `Subject<VoiceAiPipelineEvent>`, `OpenAiRealtimeBridge` exposes a `Subject<RealtimeEvent>`, and both publish through ActivitySources + Meters). This keeps the seam narrow enough to refactor freely, but means a consumer that runs both patterns in the same host writes two wiring blocks — one per concrete handler — rather than one generic block. That is the price we pay to avoid a leaky common base that would force streaming semantics onto the turn-based path or vice versa.

## Alternatives considered

- **Monolithic `VoiceAiPipeline` that branches internally on provider type** — rejected because it couples the turn-based and streaming flows into one class, forces every consumer to take the full dependency graph of both (the STT+LLM+TTS trio plus the WebSocket client plus the function-calling registry), and is AOT-hostile: the branch relies on runtime type checks and provider-kind switches that the AOT analyzer flags as unsafe, and the trimmer cannot drop the unused path.
- **Two separate top-level APIs (`IVoicePipeline` + `IStreamingBridge`)** — rejected because every consumer acceptor has to pick one at compile time. Moving a tenant from turn-based to streaming becomes a coordinated consumer-side refactor instead of a DI-config change, and the AudioSocket acceptor has to be written twice (once per seam). Plugin ergonomics collapse, and the "contact center prototype → production migration" story we sell in the VoiceAi guide stops being true.
- **Delegate-based handler (`Func<AudioSocketSession, CancellationToken, ValueTask>`)** — rejected because the handler must be a registered service (scoped or singleton) so DI can wire in `IOptions`, providers, loggers, the scope factory, and activity sources. A bare delegate loses that structural integration; the wiring would end up in the consumer's `Program.cs` as captured locals, which breaks lifetime management, defeats `[OptionsValidator]` discovery, and makes the code untestable against a fake provider.
- **Abstract base class (`VoiceSessionHandlerBase`)** — rejected for the audit report reasons documented in `docs/research/2026-04-19-product-alignment-audit.md` §4 item #3: an abstract base would force shared fields (logger, options envelope, metrics shape) that the two implementations genuinely do not share. `VoiceAiPipeline` carries five injected services; `OpenAiRealtimeBridge` carries three. Any common base would either be empty (pointless) or leak implementation-specific assumptions into both subclasses.
