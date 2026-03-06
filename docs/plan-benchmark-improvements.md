# Plan: Mejora de Benchmark Suite

> Resultado de la auditoria de benchmarks del SDK.
> Fecha: 2026-03-05 | Branch: `feature/rename-asterisk-sdk`

---

## Contexto

Suite original: 5 clases, 15 benchmarks. Cubrian AMI reader/writer, event pump, ARI JSON, y throughput mixto. Auditoria identifico 9 hot paths sin benchmarks y problemas de metodologia.

---

## Sprint 1 — Fixes de Metodologia + ARI Event Parsing ✅ COMPLETADO

### Tarea 1.1 — Fix benchmarks existentes ✅

- `AsyncEventPumpBenchmark`: pre-allocar eventos en `[GlobalSetup]`, usar `RunContinuationsAsynchronously`
- `AmiProtocolWriterBenchmark`: cambiar `[IterationSetup]` a `[GlobalSetup]`, pre-allocar fields array, drain pipe
- Agregado `[Baseline]` en todos los benchmark classes

### Tarea 1.2 — ARI ParseEvent benchmark ✅

- Creado `AriParseEventBenchmark.cs` con 3 benchmarks:
  - `ParseStasisStart` — evento complejo con channel nested (baseline)
  - `ParseChannelDtmf` — evento medio
  - `ParseUnknownEvent` — fallback path para tipos desconocidos

### Tarea 1.3 — AudioSocket frame parsing benchmark ✅

- Creado `AudioSocketBenchmark.cs` con 4 benchmarks:
  - `ParseSingleAudioFrame` — 640B slin16 frame (baseline)
  - `Parse100AudioFrames` — batch throughput
  - `ParseIncompleteFrame_ShouldRewind` — rewind logic
  - `WriteAudioFrame` — serialization

---

## Sprint 2 — Observer Dispatch + Event Pipeline ✅ COMPLETADO

### Tarea 2.1 — Observer dispatch benchmark ✅

- Creado `ObserverDispatchBenchmark.cs` con 3 benchmarks:
  - `Dispatch_1Observer` (baseline), `Dispatch_10Observers`, `Dispatch_100Observers`
  - Mide volatile array snapshot + foreach loop (zero-alloc pattern)

### Tarea 2.2 — Event pipeline benchmark ✅

- Creado `EventDeserializerBenchmark.cs` con 3 benchmarks:
  - `ParseNewchannel` (baseline, 15 fields), `ParseVarSet` (5 fields), `ParseQueueParams` (12 fields)
  - Mide full pipeline: wire bytes -> Pipe -> AmiProtocolReader -> AmiMessage

---

## Sprint 3 — Live Layer + Correlation ✅ COMPLETADO

### Tarea 3.1 — ChannelManager throughput benchmark ✅

- Creado `ChannelManagerBenchmark.cs` con 5 benchmarks:
  - `Create1000Channels` (baseline), `Update1000ChannelStates`
  - `LookupByUniqueId`, `LookupByName` (secondary index)
  - `EnumerateByState` (LINQ filter over 10K channels)

### Tarea 3.2 — Request/response correlation benchmark ✅

- Creado `ActionCorrelationBenchmark.cs` con 3 benchmarks:
  - `AddAndCorrelate1000Actions` (baseline) — full TCS lifecycle
  - `TryAdd1000Actions`, `TryRemove1000Actions` — isolated operations

---

## Resumen

| Sprint | Objetivo | Tareas | Estado | Commit |
|--------|----------|--------|--------|--------|
| **1** | Fixes + ARI + AudioSocket | 1.1-1.3 | ✅ | `pending` |
| **2** | Observer + Event Pipeline | 2.1-2.2 | ✅ | `pending` |
| **3** | Live Layer + Correlation | 3.1-3.2 | ✅ | `pending` |

## Resultado

| Metrica | Antes | Despues |
|---------|-------|---------|
| Clases de benchmark | 5 | **10** |
| Benchmarks totales | 15 | **30** |
| Hot paths cubiertos | 5/9 | **9/9** |
| Benchmarks con `[Baseline]` | 0 | **10** |
| Metodologia correcta | Parcial | **Todos corregidos** |
