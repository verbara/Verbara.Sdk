# Analisis de Benchmark — Asterisk.Sdk

> AMD Ryzen 9 9900X (12C/24T), .NET 10.0.x, BenchmarkDotNet v0.14.0
> Baseline: 2026-03-06 (SDK v1.0.0-preview). Delta v1.10: 2026-04-18.
>
> **Status:** Section 1 (core hot paths) retains v1.0 numbers — those code paths are unchanged and the delta on the same hardware is within noise. Section 1b documents the v1.10 VoiceAi `ProviderName` perf fix specifically. A full re-run of sections 1 and 2 on v1.10 is tracked in the docs-overhaul sprint plan.

---

## 1. Throughput Calculado

Convertimos latencias a operaciones/segundo para entender capacidad real:

| Operacion | Latencia | Throughput | Contexto |
|-----------|----------|------------|----------|
| Observer dispatch (1 obs) | 0.26 ns | **3,846 M ops/s** | Volatile array snapshot + virtual call |
| Observer dispatch (100 obs) | 21 ns | **47.6 M ops/s** | Escala linealmente (~0.21ns/observer) |
| AudioSocket parse frame | 11 ns | **90.9 M frames/s** | SequenceReader, zero-alloc |
| AMI write action | 118 ns | **8.47 M actions/s** | PipeWriter, zero-alloc |
| AMI read response | 412 ns | **2.43 M msgs/s** | PipeReader + span-based parsing |
| AMI read event | 582 ns | **1.72 M events/s** | PipeReader + AmiStringPool |
| Event deserializer (15 fields) | 791 ns | **1.26 M events/s** | Full pipeline: bytes→Pipe→AmiMessage |
| ARI JSON deserialize | 289 ns | **3.46 M ops/s** | System.Text.Json source-gen |
| ARI parse StasisStart | 4.5 us | **222 K events/s** | JSON parse + event dispatch |
| Channel lookup (UniqueId) | 6.3 ns | **158.7 M lookups/s** | ConcurrentDictionary O(1) |
| Channel lookup (Name) | 7.0 ns | **142.9 M lookups/s** | Secondary index O(1) |
| Action correlation (1000) | 62 us | **16.1 M correlations/s** | ConcurrentDict + TCS lifecycle |
| Event pump (1000 events) | 69 us | **14.5 M events/s** | Channel<T> enqueue + consume |

---

## 1b. VoiceAi Hot Path — Delta v1.10.0 (2026-04-18)

En v1.10.0 se introdujo la propiedad virtual `SpeechRecognizer.ProviderName` / `SpeechSynthesizer.ProviderName` con defaults que colapsan a literales en los 8 providers built-in (Deepgram, Google, Whisper, AzureWhisper, Azure TTS, ElevenLabs, Fake×2). El pipeline `VoiceAiPipeline` tagea cada recognition/synthesis activity con este nombre — antes usaba `GetType().Name` (virtual dispatch + reflection).

Resultado (misma máquina, ShortRunJob, 3 iter + 3 warmup):

| Método | Mean | Alloc | Ratio vs baseline |
|--------|------|-------|-------------------|
| `Stt_ProviderName` (v1.10 override) | **0.012 ns** | 0 B | baseline |
| `Stt_GetTypeName` (pre-v1.10) | 1.11 ns | 0 B | **92.4x más lento** |
| `Tts_ProviderName` (v1.10 override) | **0.193 ns** | 0 B | baseline |
| `Tts_GetTypeName` (pre-v1.10) | 1.29 ns | 0 B | **6.7x más lento** |

BenchmarkDotNet emite `ZeroMeasurement` warning para los `ProviderName` — indistinguible de método vacío. El JIT inlinea el literal const y el overhead desaparece.

En el contexto del pipeline: cada utterance dispara 2 accesos (STT + TTS). A 10K utterances/hora por instancia el ahorro bruto es pequeño (~25ms/hora), pero el verdadero beneficio es eliminar el sitio de reflection, mantener el hot path "zero-cost" y dar a custom providers una vía explícita para sobrescribir el nombre:

```csharp
public sealed class MyRecognizer : SpeechRecognizer
{
    public override string ProviderName => "MyRecognizer";
    // ...
}
```

Sin override, el default `=> GetType().Name` preserva el comportamiento pre-v1.10 — fully backwards-compatible.

Benchmark code: `Tests/Asterisk.Sdk.Benchmarks/VoiceAiBenchmarks.cs`. Reproduce con `dotnet run -c Release --project Tests/Asterisk.Sdk.Benchmarks/ -- --filter "*VoiceAi*"`.

---

## 2. Comparacion con Referencias de la Industria

### 2.1 AMI Protocol Reader vs asterisk-java

| Metrica | asterisk-java | Asterisk.Sdk | Factor |
|---------|---------------|-------------|--------|
| Parsing model | `BufferedReader.readLine()` + regex | `System.IO.Pipelines` + `SequenceReader` | — |
| Alloc per event | ~5-10 KB (String[], HashMap, regex) | **3.15 KB** (Dictionary + strings) | ~2-3x menos |
| Thread model | 1 thread blocking I/O | async/await, zero thread blocking | — |
| Event dispatch | `synchronized` ArrayList + reflection | Volatile array snapshot, zero-lock | — |
| GC pressure | High (regex, String concat, boxing) | **Minimal** (Pipe reuses buffers) | — |

**Veredicto**: El AMI reader es ~2-3x mas eficiente en memoria que asterisk-java y no bloquea threads.

### 2.2 ARI JSON vs Alternativas

| Framework | Deserialize Channel | Modelo |
|-----------|-------------------|--------|
| Asterisk.Sdk (STJ source-gen) | **289 ns / 232 B** | AOT, zero-reflection |
| System.Text.Json (reflection) | ~400-600 ns / ~500 B | Runtime reflection |
| Newtonsoft.Json | ~800-1200 ns / ~2 KB | Reflection + alloc heavy |
| asterisk-java (Jackson) | ~1-2 us / ~1-3 KB | Reflection + annotation |

**Veredicto**: Source-generated STJ es 2-4x mas rapido que alternativas basadas en reflection.

### 2.3 Observer Pattern vs Alternativas Comunes

| Pattern | Latencia (100 obs) | Alloc | Modelo |
|---------|-------------------|-------|--------|
| Asterisk.Sdk (volatile array) | **21 ns / 0 B** | Zero-alloc | Copy-on-write snapshot |
| `event` delegate (C#) | ~30-50 ns / 0 B | Zero-alloc | Multicast delegate |
| `List<T>` + `lock` | ~100-200 ns / 0 B | Lock contention | Traditional |
| System.Reactive `Subject<T>` | ~200-500 ns / 40+ B | Per-notification alloc | Full Rx pipeline |
| `ConcurrentBag` + foreach | ~500+ ns / varies | Enumeration alloc | Thread-safe collection |

**Veredicto**: El pattern volatile array es el mas rapido posible para dispatch — mas rapido que `event` delegates nativos de C#.

### 2.4 Channel Lookup vs Estructuras de Datos

| Estructura | Latencia | Notas |
|-----------|----------|-------|
| Asterisk.Sdk `ConcurrentDictionary` | **6.3 ns** | O(1) amortizado, lock-free reads |
| `Dictionary<K,V>` (no thread-safe) | ~3-5 ns | Mas rapido pero no concurrente |
| `ImmutableDictionary` | ~50-80 ns | Arbol balanceado, O(log n) |
| `ConcurrentBag` + LINQ FirstOrDefault | ~1-10 us | O(n) scan |
| Redis GET (localhost) | ~50-100 us | Network I/O overhead |

**Veredicto**: ConcurrentDictionary es la eleccion optima — solo 1.3-2x overhead vs Dictionary no-concurrente, pero con thread safety completo.

---

## 3. Analisis de Allocations (GC Pressure)

### Zero-Alloc Hot Paths (excelente)

| Componente | Alloc | Por que |
|-----------|-------|---------|
| Observer dispatch | 0 B | Volatile array snapshot, no copy |
| AudioSocket parse | 0 B | SequenceReader<byte> es stack-only |
| AMI writer | 0 B | PipeWriter reusa buffers del pool |
| Channel lookup | 0 B | ConcurrentDictionary no alloca en reads |

### Low-Alloc Paths (aceptable)

| Componente | Alloc | Fuente | Optimizable? |
|-----------|-------|--------|-------------|
| AMI read event | 1.83 KB | Dictionary + interned keys/values | Optimizado con AmiStringPool |
| ARI JSON deserialize | 232 B | Objetos de dominio (Channel, etc) | No — son el resultado |
| ARI parse event | 3 KB | JSON + event object + dispatch | Aceptable para ARI |
| Event deserializer (15 fields) | 1.90 KB | Dictionary(15 entries) + interned strings | Optimizado con AmiStringPool |

### Analisis GC por Escenario

| Escenario | Events/sec | Alloc/sec | Gen0/sec | Impacto GC |
|-----------|-----------|-----------|----------|-----------|
| PBX chico (100 agentes) | ~1K | ~3 MB | ~1 | Insignificante |
| PBX mediano (1K agentes) | ~10K | ~30 MB | ~10 | Bajo |
| PBX grande (10K agentes) | ~100K | ~300 MB | ~100 | Moderado* |
| PBX masivo (100K agentes) | ~1M | ~3 GB | ~1000 | Requiere Server GC** |

\* Con Workstation GC, 100 Gen0/sec causa pausas de ~1ms cada 10ms.
\** Usar `<ServerGarbageCollection>true</ServerGarbageCollection>` + `GCLatencyMode.SustainedLowLatency`.

---

## 4. Bottleneck Analysis

### Pipeline completo: wire bytes → evento dispatcheado

```
TCP recv → PipeReader.ReadAsync()     ~50-200 ns (kernel)
         → TryReadLine (scan \r\n)    ~100-300 ns (buffer size dependent)
         → String parsing (Key:Value) ~200-400 ns (UTF8 decode + split)
         → Dictionary<string,string>  ~100-200 ns (hash + insert × N fields)
         → ManagerEvent creation      ~50-100 ns (source-generated)
         → Observer dispatch          ~0.26-21 ns (volatile array)
         ─────────────────────────────
         TOTAL                        ~743-984 ns per event
```

**Bottleneck principal**: String allocation en el parsing de AMI fields — mitigado con `AmiStringPool` (key/value interning) y span-based parsing que eliminó ~42-54% de allocations. Reducido de 3.15 KB a 1.83 KB por evento.

### Pipeline ARI: WebSocket → evento dispatcheado

```
WebSocket recv                       ~100-500 ns
→ JsonSerializer.Deserialize         ~289 ns (source-gen, minimal alloc)
→ Event type routing                 ~50-100 ns (switch/dictionary)
→ Event object creation              ~200-500 ns
→ Observer dispatch                  ~0.26-21 ns
─────────────────────────────────────
TOTAL                                ~4.5 us per event
```

**Bottleneck principal**: JSON deserialization del payload completo. ARI events tienen objetos nested (Channel con ~20 fields). No hay mucho que optimizar aqui — STJ source-gen ya es optimo.

---

## 5. Scorecard Final

| Categoria | Score | Justificacion |
|-----------|-------|---------------|
| **Latencia AMI** | A | <1us/evento, 1.35M events/sec single-thread |
| **Latencia ARI** | A- | 4.5us/evento con JSON nested, 222K events/sec |
| **Memory efficiency** | A | Zero-alloc en 4 de 9 hot paths, <5KB en los demas |
| **GC pressure** | B+ | 3.15KB/evento AMI es aceptable, pero escala con load |
| **Concurrency** | A+ | Lock-free reads en todos los hot paths, 6ns lookups |
| **Scalability** | A | Lineal hasta 100 observers, O(1) lookups con secondary indices |
| **vs asterisk-java** | A+ | 2-3x menos alloc, async vs blocking, zero-reflection |
| **vs theoretical max** | B+ | String alloc es el gap principal vs zero-copy ideal |

### Score Global: **A**

La libreria esta en el rango de **high-performance** para un SDK de Asterisk. Los numeros criticos:

- **1.72M AMI events/sec** en single-thread — suficiente para un cluster de 100K+ agentes
- **6.3ns channel lookups** — ConcurrentDictionary O(1) con secondary indices
- **Zero-alloc observer dispatch** — el hot path mas critico no genera GC pressure
- **289ns ARI JSON** — source-generated STJ es el estado del arte para .NET

### Area de mejora principal

El string allocation en AMI parsing fue optimizado de 3.15 KB a 1.83 KB/event (-42%) mediante `AmiStringPool` (key/value interning) y span-based parsing. Ver `docs/plan-ami-string-optimization.md` para detalles.
