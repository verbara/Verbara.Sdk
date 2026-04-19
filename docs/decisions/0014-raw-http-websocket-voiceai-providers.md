# ADR-0014: Raw HTTP / `ClientWebSocket` for VoiceAi providers

- **Status:** Accepted
- **Date:** 2026-03-19 (retrospective — decision made during Sprint 23)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0001 (Native AOT first), ADR-0003 (source generators over reflection)

## Context

The VoiceAi stack has 4 STT providers and 2 TTS providers shipping today, with more candidates on the roadmap (Google TTS, OpenAI TTS, Speechmatics, AssemblyAI). Every major AI vendor ships an official .NET SDK (or a community one), and on the surface depending on those SDKs is the obvious move — less code, less wire-protocol parity work, vendor-maintained JSON shapes, and examples the user can copy from the vendor's docs.

We surveyed the candidate SDKs (`Azure.AI.OpenAI`, `Google.Cloud.Speech.V1`, `OpenAI.Official`, `Deepgram.Client`) before Sprint 23 and found the same pattern in each: reflection-based `System.Text.Json` polymorphic serialization (runtime `TypeInfoResolver`), `System.Reflection.Emit` serializer caches, logging adapters that pull in additional reflection surfaces, and transitive dependency graphs that a downstream consumer inherits. Each of these is a hard blocker under `PublishAot=true`, which ADR-0001 establishes as a non-negotiable SDK invariant.

A generic abstraction over those vendor SDKs was also considered — a single `IVoiceAiProvider` facade with vendor SDKs behind it — but the AOT problems live at the leaf, not at the abstraction. Wrapping a non-AOT dependency does not make it AOT-safe, and a vendor breaking change would cascade through the abstraction instead of staying isolated to a single file. The wire protocols in play are also heterogeneous enough (Deepgram's bidirectional WebSocket framing, Google Speech v1's HTTP streaming, Whisper's multipart REST, ElevenLabs' stream-oriented WebSocket, Azure's multi-shape REST) that a unified abstraction would either lose fidelity or become its own maintenance framework.

## Decision

Every VoiceAi provider ships as a hand-rolled implementation against the vendor's public REST or WebSocket API, using only `System.Net.Http.HttpClient` and `System.Net.WebSockets.ClientWebSocket`. JSON shapes are declared as plain records with `[JsonSerializable(typeof(T))]` entries on a per-provider `JsonSerializerContext`. **Zero vendor SDKs, zero generated clients, zero polymorphic serialization.**

## Consequences

- **Positive:**
  - `PublishAot=true` works downstream with zero trim warnings across all 6 providers.
  - Each provider is small enough to audit line-by-line per provider; a security review of the entire VoiceAi provider surface fits in an afternoon.
  - Vendor breaking changes surface as isolated patches in a single file, not framework-wide ripples.
  - Consumers pay only for the providers they register (no transitive vendor-SDK dependency graph).
  - Zero dependency version churn from vendor SDK releases.
- **Negative:**
  - When a vendor ships a new feature we want, we implement it ourselves instead of bumping a NuGet.
  - A contributor who knows a vendor SDK well cannot copy/paste examples — they have to understand the wire protocol.
- **Trade-off:** We carry the maintenance burden of wire-protocol parity for every provider. Given the SDK targets operators running AOT-published, high-throughput PBX fleets, AOT cleanliness and startup size matter more than vendor-SDK-assisted DX. The burden is bounded by the fact that each provider is small enough to fit in one file; we are not maintaining a framework, just a set of focused wrappers.

## Alternatives considered

- **Official vendor SDKs** (`Azure.AI.OpenAI`, `Google.Cloud.Speech.V1`, `OpenAI.Official`, `Deepgram.Client`) — rejected because every candidate surveyed introduces reflection-based serialization, `System.Reflection.Emit` serializer caches, or runtime logging adapters incompatible with Native AOT. ADR-0001 (AOT-first) forbids this.
- **OpenAPI-generated clients (NSwag, Kiota)** — rejected because many providers — especially streaming STT — use bespoke WebSocket framing that is not captured in OpenAPI specs, and generated clients typically emit reflection-based serializers that reintroduce the ADR-0003 problem.
- **Single unified abstraction over all vendor SDKs** — rejected because a vendor breaking change would cascade into the abstraction layer, AOT would still be broken at the leaf, and we would lose the per-provider audit surface.
- **Community wrapper libraries (e.g. `OpenAI-DotNet`)** — rejected because their maintenance cadence is volatile, they carry their own AOT issues, and any abandonment becomes a blocking upstream issue for the SDK.

## Evidence

Shipping providers implementing this pattern:

- [`src/Asterisk.Sdk.VoiceAi.Stt/Deepgram/DeepgramSpeechRecognizer.cs`](../../src/Asterisk.Sdk.VoiceAi.Stt/Deepgram/DeepgramSpeechRecognizer.cs) — streaming WebSocket (bidirectional, real-time partial transcripts).
- [`src/Asterisk.Sdk.VoiceAi.Stt/Google/GoogleSpeechRecognizer.cs`](../../src/Asterisk.Sdk.VoiceAi.Stt/Google/GoogleSpeechRecognizer.cs) — REST streaming to Google Speech-to-Text v1.
- [`src/Asterisk.Sdk.VoiceAi.Stt/Whisper/WhisperSpeechRecognizer.cs`](../../src/Asterisk.Sdk.VoiceAi.Stt/Whisper/WhisperSpeechRecognizer.cs) — REST batch to OpenAI Whisper.
- [`src/Asterisk.Sdk.VoiceAi.Stt/Whisper/AzureWhisperSpeechRecognizer.cs`](../../src/Asterisk.Sdk.VoiceAi.Stt/Whisper/AzureWhisperSpeechRecognizer.cs) — REST batch to Azure OpenAI Whisper deployment.
- [`src/Asterisk.Sdk.VoiceAi.Tts/ElevenLabs/ElevenLabsSpeechSynthesizer.cs`](../../src/Asterisk.Sdk.VoiceAi.Tts/ElevenLabs/ElevenLabsSpeechSynthesizer.cs) — WebSocket streaming (ultra-low-latency).
- [`src/Asterisk.Sdk.VoiceAi.Tts/Azure/AzureTtsSpeechSynthesizer.cs`](../../src/Asterisk.Sdk.VoiceAi.Tts/Azure/AzureTtsSpeechSynthesizer.cs) — REST to Azure Cognitive Services TTS.

Source: product-alignment audit, `docs/research/2026-04-19-product-alignment-audit.md` §4 item #4.
