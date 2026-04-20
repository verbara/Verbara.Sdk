# Post-ADR-0029 Roadmap — SDK scope (2026-04-20)

> **Nota:** esta es la vista **SDK-only sanitizada** del roadmap cross-repo. El plan completo (que incluye version pins de consumers comerciales, coordinaciones downstream, y referencias a repos privados) vive en otro lado por política del repositorio. Este archivo lista solo los items que impactan el SDK MIT.

## Context

Tras shipear v1.14.0 (ADR-0029 `Asterisk.Sdk.Resilience` MIT migration, 2026-04-20), se hizo inventario abierto del backlog y se priorizaron 3 releases SDK-lado (R1 + R1.5 paralelo + R2). Fuentes oficiales verificadas:

- [Asterisk 22 LTS](https://docs.asterisk.org/About-the-Project/Asterisk-Versions/) — security hasta 2028-10, EOL 2029-10 (target actual).
- [Asterisk 23 Standard](https://docs.asterisk.org/About-the-Project/Asterisk-Versions/) — released 2025-10-15, security hasta 2026-10, EOL 2027-10.
- Asterisk 24 LTS — aún no existe (expected octubre 2026).

**Principios:**
1. Preparar antes de reconstruir (namespace/shape collisions resueltas antes de introducir nuevos primitives).
2. Fundación MIT antes que consumers externos (Event Model v2 define contratos, luego consumers adoptan).
3. LTS-first en soporte Asterisk (22 LTS continuo, 23 Standard agregado como dual matrix, 24 LTS cuando salga).
4. AOT-first zero-warning strict en todo el scope.

**Orden:** R1 → R1.5 (paralelo) → R2.

---

## R1 — SDK v1.15.0 "Pre-v2 Foundation" (~2.5 semanas)

**Target:** Asterisk.Sdk **v1.15.0**.

### Alcance SDK

**A. Resolver `RemotePushEvent` collision downstream** (desbloquea consumers con namespace colisionante):
- `Asterisk.Sdk.Push.Events.RemotePushEvent` (shape `record(string OriginalEventType, string? SourceNodeId, byte[] RawPayload)` shipped en v1.13.0) queda como canonical.
- Auditar `src/Asterisk.Sdk.Push/Events/PushEventJsonContext.cs` — agregar `[JsonSerializable(typeof(RemotePushEvent))]` si falta.
- SDK no introduce breaking change; el trabajo de migración es downstream (fuera de SDK scope).

**B. Nuevo paquete `Asterisk.Sdk.Cluster.Primitives`** (MIT):
- Abstractions: `INodeRegistry`, `IMembershipProvider`, `IClusterTransport`, `ClusterEvent`.
- Reference impl in-memory para tests.
- Habilita downstream cluster implementations MIT o comerciales sobre contrato estable.

**C. `AudioSocketServer` rename** (naming consistency pre-v2):
- VoiceAi `AudioSocketServer` → `VoiceAiAudioSocketServer`.
- Ari mantiene `AriAudioSocketListener`.
- `TypeForwardedTo` window 1 release (v1.15 → v2.0).

**D. Deferred ADR-0029: Webhook per-URL circuit breaker** (piggyback):
- `WebhookDeliveryService` usa `ResiliencePolicy` per-subscription con circuit dict keyed por URL.
- ~150-200 LOC + 5-8 tests (open/half-open/closed transitions).
- Completa el feature deferral documentado en ADR-0029.

**E. ADR-0028 cadence commitment docs** (piggyback):
- Nuevo `docs/decisions/0028-cadence-commitment.md`.
- Narrativa pública; sin código.

**F. AOT validation suite** (tier A product hardening):
- GH Actions workflow `.github/workflows/aot-validate.yml`.
- `dotnet publish -r linux-x64 -c Release --self-contained` para cada paquete + smoke test binario.
- Verifica: 0 warnings AOT, 0 trimming issues, binario arranca.

**G. Telemetry dashboards prefab** (tier A product hardening):
- `docs/operations/dashboards/` con Grafana JSONs base (≥3): SDK overall, Webhook delivery, Resilience.
- Jaeger query examples `docs/operations/jaeger-queries.md` para 9 ActivitySources.

**H. Asterisk 23 Standard support** (dual matrix):
- `docker/Dockerfile.asterisk-23` + `docker/docker-compose.test-23.yml` (paralelo a 22).
- Test matrix CI: Functional + Integration tests contra 22 y 23.
- Detecta break-changes AMI/ARI/PJSIP entre 22↔23; documenta en `docs/guides/asterisk-version-matrix.md`.

### Criterios de éxito
- ✅ SDK v1.15.0: 26 packages (Cluster.Primitives nuevo), 0 warnings, tests green.
- ✅ Webhook circuit breaker con 5+ tests.
- ✅ AOT publish workflow green (linux-x64 + win-x64 + osx-arm64).
- ✅ 3 Grafana dashboards JSON válidos (importables).
- ✅ Asterisk 23 test matrix green (o documentado gap con follow-up).

---

## R1.5 — SDK v1.15.1 "VoiceAi Refresh" (paralelo a R1, ~1 semana)

**Target:** Asterisk.Sdk **v1.15.1** (patch, additive features).

### Alcance

**A. ElevenLabs Flash 2.5 TTS** — modelo nuevo <150ms TTFA:
- Extender `ElevenLabsTtsProvider` con opción `Model.Flash_v2_5`.
- Tests latency en `Tests/Asterisk.Sdk.VoiceAi.Tests/ElevenLabs/`.

**B. Deepgram Aura 2 TTS** — refresh de modelo:
- Extender `DeepgramTtsProvider` con `Model.Aura2`.

**C. OpenAI Whisper V3 local** — nuevo provider STT air-gapped:
- `src/Asterisk.Sdk.VoiceAi.Providers.WhisperLocal/` (nuevo paquete o extendido con flag `UseLocalModel`).
- `docs/guides/whisper-local-airgap.md`.

**D. ADR:** `docs/decisions/0035-voiceai-model-upgrades-2026-q2.md` con criterios provider selection.

### Criterios de éxito
- ✅ 3 modelos/providers nuevos con fluent API.
- ✅ Tests E2E con fake audio ejecutan green.
- ✅ Ejemplo en `Examples/VoiceAi.Providers.Showcase/`.

---

## R2 — SDK v2.0.0-preview1 "Event Model v2" (~3 semanas)

**Target:** Asterisk.Sdk **v2.0.0-preview1**.

### Alcance

**A. ADR-0030 CloudEvents v1.0 adoption:**
- Envelope canónico `CloudEvent<T>` con UUIDv7 + domain extensions.
- Wire bindings: NATS, HTTP, Webhooks, SSE.
- `AsteriskSemanticConventions` para `asterisk.*` extensions.
- Backward-compat helper `PushEvent<T>` ↔ `CloudEvent` durante preview window.

**B. ADR-0031 Domain vs Integration events:**
- Convention `asterisk.domain.*` (breakable minors pre-v2) vs `asterisk.integration.*` (semver-strict 6m deprecation).
- CI analyzer check.

**C. ADR-0032 Events ≠ Commands:**
- `ICommandDispatcher` separado del event bus.
- `Result<T>` vs `IObservable<T>` separation.

**D. ADR-0033 IEventLog vs IEventStore split:**
- `Asterisk.Sdk.EventLog` nuevo paquete MIT: `IEventLog` (append + read).
- In-memory + Postgres adapter reference (MIT).

**E. ADR-0034 ISessionInterceptor:**
- Contract público en `Asterisk.Sdk.Sessions`.
- Elimina `InternalsVisibleTo` leaks hacia downstream consumers.

**F. Load/latency benchmark suite** (tier A):
- `Tests/Asterisk.Sdk.Benchmarks/` extender con Push/NATS roundtrip, VoiceAi STT→TTS pipeline, Sessions roundtrip, CloudEvent serialization.
- NBomber para load tests (1K/10K concurrent).
- `docs/performance/slo-claims.md` con resultados reproducibles.

**G. SDK v2.0.0-preview1 release narrative:**
- ADR-0026 rebrand "Runtime for .NET" (10-point checklist).
- README reescrito.
- Compat matrix v1.x vs v2.x.
- Migration guide `docs/migrations/v1-to-v2.md` completa.
- Preview notice (no LTS claims).

### Criterios de éxito
- ✅ SDK v2.0.0-preview1: 27+ packages (EventLog nuevo), 0 warnings.
- ✅ Backward-compat: consumer v1.15 helper funciona.
- ✅ CloudEvents interop validado (ejemplo EventGrid/NATS).
- ✅ Benchmarks publicados con p50/p99 para 4 scenarios.
- ✅ Public API diff review documentado.

---

## Archivos críticos (SDK-side)

### R1
- `src/Asterisk.Sdk.Push/Events/PushEventJsonContext.cs` (auditar `RemotePushEvent` registration)
- `src/Asterisk.Sdk.Cluster.Primitives/**` (nuevo paquete MIT)
- `src/Asterisk.Sdk.VoiceAi/*AudioSocketServer*.cs` (rename → `VoiceAiAudioSocketServer`)
- `src/Asterisk.Sdk.Push.Webhooks/WebhookDeliveryService.cs` (circuit breaker per-URL)
- `docs/decisions/0028-cadence-commitment.md`
- `docs/operations/dashboards/*.json` (Grafana prefab)
- `docs/operations/jaeger-queries.md`
- `docs/guides/asterisk-version-matrix.md`
- `.github/workflows/aot-validate.yml`
- `docker/Dockerfile.asterisk-23` + `docker-compose.test-23.yml`
- `docs/migrations/v1-to-v2.md` (skeleton)

### R1.5
- `src/Asterisk.Sdk.VoiceAi.Providers.ElevenLabs/**` (Flash 2.5 model enum)
- `src/Asterisk.Sdk.VoiceAi.Providers.Deepgram/**` (Aura 2 model enum)
- `src/Asterisk.Sdk.VoiceAi.Providers.WhisperLocal/**` (nuevo o extendido)
- `docs/decisions/0035-voiceai-model-upgrades-2026-q2.md`
- `docs/guides/whisper-local-airgap.md`
- `Examples/VoiceAi.Providers.Showcase/`

### R2
- `src/Asterisk.Sdk.Push/CloudEvent.cs`
- `src/Asterisk.Sdk.Push/Bindings/{Nats,Http,Webhooks,Sse}Binding.cs`
- `src/Asterisk.Sdk.EventLog/**` (nuevo paquete)
- `src/Asterisk.Sdk.Commands/ICommandDispatcher.cs`
- `src/Asterisk.Sdk.Sessions/ISessionInterceptor.cs`
- `docs/decisions/0030-cloudevents.md`, `0031-domain-integration-events.md`, `0032-events-vs-commands.md`, `0033-eventlog-eventstore-split.md`, `0034-session-interceptor.md`
- `Tests/Asterisk.Sdk.Benchmarks/Scenarios/**`
- `docs/performance/slo-claims.md`
- `README.md` (rebrand)

---

## Verification SDK-side

```sh
# R1
dotnet build Asterisk.Sdk.slnx --nologo /warnaserror
dotnet test Asterisk.Sdk.slnx --filter "Category!=Functional&Category!=Integration"
dotnet pack Asterisk.Sdk.slnx -c Release -o /tmp/pack-test/
# AOT validation
dotnet publish Examples/Ami.QuickStart/ -r linux-x64 -c Release --self-contained
# Asterisk 23 matrix
docker compose -f docker/docker-compose.test-23.yml up --build

# R1.5
dotnet test Tests/Asterisk.Sdk.VoiceAi.Tests/

# R2
dotnet test Tests/Asterisk.Sdk.EventLog.Tests/
dotnet run -c Release --project Tests/Asterisk.Sdk.Benchmarks/
```

---

## Post-R2 (items de consumer coordination fuera de scope público)

Items de coordinación cross-repo con consumers downstream no se detallan aquí. Para contexto público:

- **Asterisk 24 LTS readiness** — release dedicado cuando Asterisk 24 LTS sea GA (expected octubre 2026). Branch cut: 2nd Wed agosto 2026.
- **Renovate cross-repo automation** — sustainability de release cadence.
- **OTel SIP semantic conventions upstream** — submit proposal a `open-telemetry/semantic-conventions` issue #2517 tras 2 semanas production field validation.
- **Kafka/RabbitMQ Push bridges** — demand-driven.
- **Tutorial/docs modernization** — DevEx improvements.
