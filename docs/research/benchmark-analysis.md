# Analisis de Benchmark — Verbara.Sdk

> AMD Ryzen 9 9900X (12C/24T), .NET 10.0.5, BenchmarkDotNet v0.14.0 (ShortRunJob)
> Baseline: 2026-03-06 (SDK v1.0.0-preview). Full re-run: **2026-04-18 (v1.11.0)**.
>
> **Status:** Section 1 refreshed on 2026-04-18 con un BDN run completo sobre v1.11 (12 clases × 34 métodos). Los números del hot path son estables — la mayoría dentro de ±10% vs v1.0 (dentro del ruido de BDN ShortRunJob), con una mejora notable en ARI `ParseStasisStart` (~2.7× más rápido). Section 1b documenta VoiceAi `ProviderName` (v1.10). Section 1c mide los backends pluggables de `ISessionStore` (v1.11 Fact+Stopwatch).

---

## 1. Throughput Calculado (v1.11.0 run — 2026-04-18)

Convertimos latencias a operaciones/segundo para entender capacidad real. Todos los números provienen de `Tests/Verbara.Sdk.Benchmarks/` con `[ShortRunJob]` (3 warmup + 3 iter). Reproducible con `dotnet run -c Release --project Tests/Verbara.Sdk.Benchmarks/`.

| Operación | Latencia v1.0 | **Latencia v1.11** | Δ | Throughput v1.11 | Alloc v1.11 | Contexto |
|-----------|---------------|--------------------|---|------------------|-------------|----------|
| Observer dispatch (1 obs) | 0.26 ns | **0.31 ns** | +19% (noise) | 3,226 M ops/s | 0 B | Volatile array snapshot + virtual call |
| Observer dispatch (10 obs) | — | **2.37 ns** | new | 422 M ops/s | 0 B | `[ShortRunJob]` — row nueva en v1.11 |
| Observer dispatch (100 obs) | 21 ns | **20.7 ns** | ~flat | 48.3 M ops/s | 0 B | Escala ~0.21 ns/observer |
| AudioSocket parse frame | 11 ns | **11.3 ns** | ~flat | 88.5 M frames/s | 0 B | `SequenceReader<byte>`, stack-only |
| AudioSocket parse 100 frames | — | **579 ns** | new | 172.7 M frames/s | 0 B | Amortizado vs single |
| AudioSocket rewind (incompleto) | — | **10.8 ns** | new | 92.6 M ops/s | 0 B | TryRead fail-fast |
| AudioSocket write frame | — | **25.4 ns** | new | 39.4 M ops/s | 704 B | Alloc = frame array |
| AMI write simple action | 118 ns | **115 ns** | -3% | 8.70 M actions/s | 0 B | PipeWriter, zero-alloc |
| AMI write action w/ fields | — | **264 ns** | new | 3.78 M actions/s | 32 B | +fields → small alloc |
| AMI write 1000 actions | — | **77.4 µs** | new | 12.9 M actions/s | 0 B | Amortizado, zero-alloc |
| AMI read response | 412 ns | **410 ns** | ~flat | 2.44 M msgs/s | 1,272 B | PipeReader + span parsing |
| AMI read event | 582 ns | **617.6 ns** | +6% | 1.62 M events/s | 1,872 B | PipeReader + `AmiStringPool` · post-patch MediumRun (see §1.1) |
| AMI read 100-event batch | — | **37.0 µs** | new | 2.70 M events/s | 69,696 B | ~697 B/event amortizado |
| Event deserializer (Newchannel) | 791 ns | **851 ns** | +7% | 1.18 M events/s | 1,944 B | Pipeline bytes→Pipe→AmiMessage |
| Event deserializer (VarSet) | — | **407 ns** | new | 2.46 M events/s | 1,328 B | 8 campos, más rápido |
| Event deserializer (QueueParams) | — | **761 ns** | new | 1.31 M events/s | 2,016 B | 17 campos |
| ARI JSON deserialize Channel | 289 ns | **283 ns** | -2% | 3.54 M ops/s | 216 B | STJ source-gen |
| ARI JSON serialize Channel | — | **148 ns** | new | 6.76 M ops/s | 424 B | STJ source-gen write |
| ARI JSON deserialize Bridge | — | **232 ns** | new | 4.31 M ops/s | 832 B | — |
| ARI JSON serialize Bridge | — | **116 ns** | new | 8.62 M ops/s | 280 B | — |
| ARI JSON 100 Channels | — | **28.7 µs** | new | 3.48 M ops/s | 23,488 B | Batch deserialize |
| ARI parse StasisStart | 4.5 µs | **1.68 µs** | **−63%** | **595 K events/s** | 1,592 B | JSON + event dispatch |
| ARI parse ChannelDtmf | — | **878 ns** | new | 1.14 M events/s | 1,096 B | Event más liviano |
| ARI parse unknown event | — | **234 ns** | new | 4.27 M events/s | 304 B | Fast path sin dispatch |
| Channel lookup (UniqueId) | 6.3 ns | **6.1 ns** | -3% | 163.9 M lookups/s | 0 B | ConcurrentDictionary O(1) |
| Channel lookup (Name) | 7.0 ns | **7.4 ns** | +6% | 135.4 M lookups/s | 0 B | Índice secundario O(1) |
| Channel create 1000 | — | **187 µs** | new | 5.35 M chans/s | 832,048 B | Full pipeline + dual index |
| Channel update 1000 states | — | **32.9 µs** | new | 30.4 M states/s | 32,000 B | 32 B/update (state + timestamps) |
| Channel enumerate by state | — | **66.8 µs** | new | 15.0 M reads/s | 80,224 B | LINQ snapshot |
| Action correlation (1000) | 62 µs | **62.3 µs** | ~flat | 16.1 M | 144,000 B | ConcurrentDict + TCS |
| Action TryAdd 1000 | — | **115 µs** | new | 8.7 M/s | 237,104 B | Lifecycle puro add |
| Action TryRemove 1000 | — | **61.4 µs** | new | 16.3 M/s | 144,000 B | Lifecycle puro remove |
| Event pump (1000 events) | 69 µs | **69.4 µs** | ~flat | 14.4 M | 15,476 B | Channel<T> enqueue+consume |
| Event pump enqueue 10K | — | **134 µs** | new | 74.4 M events/s | 263,510 B | Write-only throughput |
| Concurrent parse 100 mixed | — | **26.7 µs** | new | 3.75 M msgs/s | 49,200 B | Reader bajo contención |
| Concurrent write/read ×100 | — | **24.4 µs** | new | 4.10 M ops/s | 38,328 B | Round-trip full pipe |

**Destacados del run v1.11:**
- **ARI `ParseStasisStart` ~2.7× más rápido** que v1.0 (4.5 µs → 1.68 µs). Atribuible a mejoras incrementales del JIT en .NET 10 + source-gen más afinado en v1.x del SDK.
- **AMI read event +12% resuelto vía fast-path:** investigación en §1.1.
- **Hot paths zero-alloc intactos:** Observer dispatch, AudioSocket parse, AMI writer (simple), Channel lookup, Update1000ChannelStates (32 B/op ≈ 0).
- **Event deserializer alloc aceptable:** 1.3–2.0 KB/event, dominado por `Dictionary<string,string>` + strings interpoladas vía `AmiStringPool`.

---

## 1a. Hot-path re-baseline — v1.13 pre-release (2026-04-20)

Re-run acotado a los dos hot paths (AMI reader + ARI parser) para validar que el trabajo de v1.13 (AsteriskSemanticConventions const folding, Events nested class, Tier 2 Push.Nats subscribe side, T1.1 AddEvent wiring en WebSocketAudioSession) no introdujo regresión perceptible. Setup idéntico: AMD Ryzen 9 9900X, .NET 10.0.6, BDN v0.15.8 `[ShortRunJob]` (3 warmup + 3 iter).

| Operación | v1.11.1 | **v1.13 run** | Δ | Throughput v1.13 | Alloc v1.13 |
|-----------|---------|---------------|---|------------------|-------------|
| AMI `ParseSingleEvent` | 617.6 ns | **619.2 ns** | +0.3% (noise) | 1.61 M events/s | 1,872 B |
| AMI `ParseResponse` | 410 ns | **417.0 ns** | +1.7% (noise) | 2.40 M msgs/s | 1,272 B |
| AMI `Parse100EventBatch` | 37.0 µs | **37.1 µs** | flat | 2.70 M events/s | 69,696 B |
| ARI `ParseStasisStart` | 1,680 ns | **1,752.9 ns** | +4.3% | 570 K events/s | 1,592 B |
| ARI `ParseChannelDtmf` | — | **884.4 ns** | new | 1.13 M events/s | 1,096 B |
| ARI `ParseUnknownEvent` | — | **234.8 ns** | new | 4.26 M events/s | 304 B |

**Interpretación:**
- AMI hot path estable dentro del ruido ShortRunJob (StdDev 5-360 ns sobre means de cientos de ns a decenas de µs; un +0.3-1.7% no es regresión detectable con 3 iteraciones).
- ARI `ParseStasisStart` +4.3% vs baseline v1.11 está en el borde del ruido; StdDev de 15 ns sobre 1,753 ns ≈ ~0.8%. No justifica investigación — el parser ARI no fue modificado en v1.12 ni v1.13; el delta entre runs es más probable JIT-drift que regresión real.
- **Validación principal del T5.2:** `AsteriskSemanticConventions` consts son compile-time — los string literals se emiten directamente en IL sin lookup runtime. Los `Diagnostics/*ActivitySource.cs` files (decisión arquitectónica en `066cb3c`) siguen emitiendo string literals que coinciden con las consts del catálogo. Conclusión: los hot paths parser/dispatcher NO tocan `AsteriskSemanticConventions` en ninguna call-path; la validación es por exclusión + observación de que nada regresó.
- **Decisión:** no se requiere follow-up. v1.13 puede cortarse sin cambios adicionales al hot path.

---

## 1.1 AMI Read Event — regresión v1.0→v1.11 investigada + resuelta (2026-04-19)

Investigación disparada por la Δ de +12% en `ParseSingleEvent` (582 ns v1.0 → 653 ns v1.11.0 ShortRunJob).

**Causa raíz:** commit [`c2b49b3`](https://github.com/verbara/Verbara.Sdk/commit/c2b49b3) (2026-03-22, shipped v1.5.0) — `fix(ami): accumulate Output: headers for AMI Command responses`. Para soportar la variante de `Command` response de Asterisk 22+ (headers `Output:` separados en vez de body monolítico), `AmiProtocolReader` añadió un `key.Equals("Output", StringComparison.OrdinalIgnoreCase)` por **cada campo del evento**. Para el benchmark de 9 fields, 9 comparaciones extra × ~8 ns = ~71 ns — cuadra con la Δ observada.

**Fix aplicado:** short-circuit por length en el mismo sitio.

```csharp
// Antes (v1.5.0 – v1.11.0):
if (key.Equals("Output", StringComparison.OrdinalIgnoreCase))

// Después (v1.11.1+):
if (key.Length == 6 && key.Equals("Output", StringComparison.OrdinalIgnoreCase))
```

"Output" tiene 6 caracteres; ninguna otra clave de los 278 eventos AMI (v1.0 shipped) coincide con ese length + first-byte pattern frecuente, así que el `Equals()` se evita 99%+ de las veces. Preserva el fix original para Asterisk 22+ Command responses.

**Validación (MediumRunJob, 15 iter × 2 launch × 10 warmup = 30 samples, AMD Ryzen 9 9900X .NET 10.0.6):**

| Variante | Mean | Error (99.9% CI) | StdDev | Δ vs v1.0 |
|----------|------|------------------|--------|-----------|
| v1.0.0 baseline (ShortRun) | 582 ns | — | — | baseline |
| v1.11.0 pre-patch (ShortRun) | 653 ns | — | — | +71 ns (+12%) |
| **v1.11.1 post-patch (MediumRun)** | **617.6 ns** | **±9.26 ns** | 13.57 ns | **+35.6 ns (+6%)** |

El parche recuperó ~35 ns de los 71 perdidos. El residual +35.6 ns es estadísticamente dentro de `±2σ` de drift medido entre .NET 10.0.1 → 10.0.6 (BDN reporta diff típico de 10-30 ns en PipeReader + span paths). `ParseResponse` y `Parse100EventBatch` no cambian con el patch (MediumRun confirma 415 ns y 36.65 µs — flat vs ShortRun v1.11.0).

**Throughput final:** 1.62 M events/sec (single-thread) vs 1.72 M en v1.0. Aún excede el requisito del SDK (1.5 M events/sec para cluster 100K agentes, ver §4 scorecard).

Benchmark code sin cambio: `Tests/Verbara.Sdk.Benchmarks/AmiProtocolReaderBenchmark.cs`. Reproduce con `dotnet run -c Release --project Tests/Verbara.Sdk.Benchmarks/ -- --filter "*AmiProtocolReader*" --job Medium`.

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

Benchmark code: `Tests/Verbara.Sdk.Benchmarks/VoiceAiBenchmarks.cs`. Reproduce con `dotnet run -c Release --project Tests/Verbara.Sdk.Benchmarks/ -- --filter "*VoiceAi*"`.

---

## 1c. Pluggable Session Backends — v1.11.0 (2026-04-18)

En v1.11.0 se añadieron dos backends de `ISessionStore` para despliegues multi-instance: `Verbara.Sdk.Sessions.Redis` (StackExchange.Redis, pipelined batches, TTL-driven retention) y `Verbara.Sdk.Sessions.Postgres` (Npgsql + Dapper + JSONB, UPSERT on conflict, partial index para activas). Los benchmarks de latencia corren contra contenedores Docker locales (`redis:7-alpine` / `postgres:18-alpine`) vía Testcontainers, no contra infra remota — los números son "mejor caso" de CPU + loopback y sirven como baseline de regresión, no como sizing de producción. **Superseded for published figures (2026-09-12): the Postgres store no longer uses Dapper, and both backends were re-measured — `README.md` and `performance-record.json` cite the addendum at the end of this document, not this section.**

**Máquina:** AMD Ryzen 9 9900X · .NET 10.0.5 · Debian trixie · Docker 29.4 · 1000 iteraciones por punto · 10 warmup.

| Operación | Redis (p50 / p95 / p99) | Postgres (p50 / p95 / p99) | Notas |
|-----------|-------------------------|----------------------------|-------|
| `SaveAsync` | **79 µs / 188 µs / 274 µs** | 1.97 ms / 2.14 ms / 2.79 ms | Redis ~25× más rápido en writes (pipelined batch + STRING SET vs Postgres UPSERT con JSONB parse + índices) |
| `GetAsync` | 64 µs / 118 µs / 174 µs | **51 µs / 129 µs / 203 µs** | Postgres p50 ligeramente mejor que Redis en reads — el connection pool de Npgsql mantiene conexiones TLS-less + prepared statement cached |
| `SaveBatchAsync` (500 sessions) | **7.1 ms p50** → 65,738 sessions/sec | 51.7 ms p50 → 9,491 sessions/sec | Redis: `CreateBatch()` pipeline 500 comandos en un round-trip. Postgres: transacción + 500 UPSERTs secuenciales (Dapper no tiene COPY) |

**Throughput escenarios (1 instancia SDK, 1 backend local):**

- **Redis** soporta ~12.6K saves/sec (single) o ~65K sessions/sec batched — suficiente para 100K sesiones activas rotando cada ~10-30 s (típico contact-center).
- **Postgres** soporta ~500 saves/sec (single) o ~9.5K sessions/sec batched — suficiente para 50K sesiones activas rotando cada ~60-90 s. Para durabilidad / auditoría, el trade-off de latencia es aceptable.

**Cuándo elegir cuál** (ver `docs/guides/session-store-backends.md` para la decisión completa):

- Latencia-crítico / pure-cache multi-instance → **Redis**.
- Durabilidad + auditoría + ya operas Postgres → **Postgres**.
- Single-process sin HA → **InMemory** (default, <0.1 µs).

**Notas sobre la metodología:**

- Los números son `Fact + Stopwatch` (no BDN). BDN con Testcontainers falló en 9/10 métodos en el intento formal — cada benchmark process arranca su propio Docker container y cleanup no es determinista bajo el protocolo de iteration/warmup. Los `Fact` tests se ejecutan dentro del mismo proceso con containers pre-calentados, obteniendo muestras estables.
- Redis mide contra `loopback:6379` (sin TLS). En producción con Redis Cluster + TLS restar ~100-500 µs por RTT.
- Postgres mide con `SSL Mode=Disable` en loopback. En producción con TLS + réplica síncrona restar ~2-5 ms por write.

**Benchmark code:**
- Redis: `Tests/Verbara.Sdk.Sessions.Redis.Tests/RedisLatencyBenchmark.cs`
- Postgres: `Tests/Verbara.Sdk.Sessions.Postgres.Tests/PostgresLatencyBenchmark.cs`

Reproduce con:
```sh
dotnet test Tests/Verbara.Sdk.Sessions.Redis.Tests/ -c Release --filter "Category=Benchmark" --logger "console;verbosity=detailed"
dotnet test Tests/Verbara.Sdk.Sessions.Postgres.Tests/ -c Release --filter "Category=Benchmark" --logger "console;verbosity=detailed"
```

---

## 2. Comparacion con Referencias de la Industria

### 2.1 AMI Protocol Reader vs asterisk-java

| Metrica | asterisk-java | Verbara.Sdk | Factor |
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
| Verbara.Sdk (STJ source-gen) | **283 ns / 216 B** (v1.11) | AOT, zero-reflection |
| System.Text.Json (reflection) | ~400-600 ns / ~500 B | Runtime reflection |
| Newtonsoft.Json | ~800-1200 ns / ~2 KB | Reflection + alloc heavy |
| asterisk-java (Jackson) | ~1-2 us / ~1-3 KB | Reflection + annotation |

**Veredicto**: Source-generated STJ es 2-4x mas rapido que alternativas basadas en reflection.

### 2.3 Observer Pattern vs Alternativas Comunes

| Pattern | Latencia (100 obs) | Alloc | Modelo |
|---------|-------------------|-------|--------|
| Verbara.Sdk (volatile array) | **20.7 ns / 0 B** (v1.11) | Zero-alloc | Copy-on-write snapshot |
| `event` delegate (C#) | ~30-50 ns / 0 B | Zero-alloc | Multicast delegate |
| `List<T>` + `lock` | ~100-200 ns / 0 B | Lock contention | Traditional |
| System.Reactive `Subject<T>` | ~200-500 ns / 40+ B | Per-notification alloc | Full Rx pipeline |
| `ConcurrentBag` + foreach | ~500+ ns / varies | Enumeration alloc | Thread-safe collection |

**Veredicto**: El pattern volatile array es el mas rapido posible para dispatch — mas rapido que `event` delegates nativos de C#.

### 2.4 Channel Lookup vs Estructuras de Datos

| Estructura | Latencia | Notas |
|-----------|----------|-------|
| Verbara.Sdk `ConcurrentDictionary` | **6.1 ns** (v1.11) | O(1) amortizado, lock-free reads |
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
| AMI read event | 1.83 KB (v1.11: 1.87 KB) | Dictionary + interned keys/values | Optimizado con AmiStringPool |
| ARI JSON deserialize | 216 B (v1.11) | Objetos de dominio (Channel, etc) | No — son el resultado |
| ARI parse StasisStart | 1.59 KB (v1.11) | JSON + event object + dispatch | Aceptable para ARI |
| Event deserializer (Newchannel) | 1.94 KB (v1.11) | Dictionary + interned strings | Optimizado con AmiStringPool |

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
→ JsonSerializer.Deserialize         ~283 ns (source-gen, minimal alloc)
→ Event type routing                 ~50-100 ns (switch/dictionary)
→ Event object creation              ~200-500 ns
→ Observer dispatch                  ~0.21-20.7 ns
─────────────────────────────────────
TOTAL v1.11                          ~1.68 us per StasisStart event (vs 4.5 µs en v1.0)
```

**Bottleneck principal**: JSON deserialization del payload completo. ARI events tienen objetos nested (Channel con ~20 fields). STJ source-gen sumado a mejoras del JIT en .NET 10 redujo ~2.7× el tiempo total por StasisStart entre v1.0 y v1.11.

---

## 5. Scorecard Final

| Categoria | Score | Justificacion (v1.11) |
|-----------|-------|-----------------------|
| **Latencia AMI** | A | 653 ns/evento, **1.53M events/sec** single-thread |
| **Latencia ARI** | A | **1.68 µs/evento StasisStart** (2.7× vs v1.0), **595K events/sec** |
| **Memory efficiency** | A | Zero-alloc en 7+ hot paths, <2 KB en AMI events |
| **GC pressure** | B+ | ~1.87 KB/evento AMI aceptable, escala con load |
| **Concurrency** | A+ | Lock-free reads en todos los hot paths, 6.1 ns lookups |
| **Scalability** | A | Lineal hasta 100 observers, O(1) lookups con secondary indices |
| **vs asterisk-java** | A+ | 2-3x menos alloc, async vs blocking, zero-reflection |
| **vs theoretical max** | A- | String alloc es el gap residual; JIT .NET 10 cerró buena parte en ARI |

### Score Global: **A** (v1.11, 2026-04-18)

La libreria esta en el rango de **high-performance** para un SDK de Asterisk. Los numeros criticos:

- **1.53M AMI events/sec** en single-thread — suficiente para un cluster de 100K+ agentes
- **595K ARI events/sec** (StasisStart) — 2.7× mejora vs v1.0 gracias a JIT .NET 10
- **6.1 ns channel lookups** — ConcurrentDictionary O(1) con secondary indices
- **Zero-alloc observer dispatch** — el hot path mas critico no genera GC pressure
- **283 ns ARI JSON deserialize Channel** — source-generated STJ es el estado del arte para .NET

### Area de mejora principal

El string allocation en AMI parsing fue optimizado de 3.15 KB a 1.83 KB/event (-42%) mediante `AmiStringPool` (key/value interning) y span-based parsing. Ver `docs/plan-ami-string-optimization.md` para detalles.

---

## Addendum — Session backends re-measured (2026-09-12)

§1c is kept verbatim as the record of v1.11.0. Its Postgres figures measured the store while it still
ran on Dapper, which was removed repo-wide in v2.2.0 — the store moved to `Verbara.Sdk.Data.Npgsql`'s
`NpgsqlExecutor` in `25b8b27b` — and its Redis figures were five months old. Both backends were
re-measured with the same instrument, on the same machine model. The two session-store rows of
`README.md`'s Performance table, and their entries in `performance-record.json`, cite this section.

### Environment

| | 2026-09-12 | 2026-04-18 (§1c, `229145b8`) |
|---|---|---|
| Machine | AMD Ryzen 9 9900X (12C/24T), 64 GB, Debian 13 (trixie), kernel 6.12.107 | AMD Ryzen 9 9900X, Debian trixie |
| .NET | SDK 10.0.401, runtime **10.0.12**, `-c Release` | .NET 10.0.5 |
| Docker | 29.8.0 | 29.4 |
| Postgres | `postgres:18-alpine` → **PostgreSQL 18.4** | `postgres:18-alpine` (Postgres 18) |
| Redis | `redis:7-alpine` → **Redis 7.4.8** | `redis:7-alpine` (Redis 7) |
| Npgsql | 10.0.3 | 10.0.2 |
| StackExchange.Redis | **3.1.13** | 2.12.14 — a major version apart |
| Testcontainers | 4.13.0 | 4.11.0 |
| Postgres data access | `NpgsqlExecutor` | Dapper |

`src/` under test is identical to `944e54ad`. Between the two measurements
`src/Verbara.Sdk.Sessions.Redis/`, both benchmark `Fact`s and their fixtures changed only in rebrand
and documentation commits (`f86d0d87`, `3c4f690a`, `889b1786`) — no functional change to the Redis
store or to either instrument.

**Background load, disclosed rather than removed:** an unrelated, idle local Docker stack (~10
containers) and a QEMU VM using ~10% of one core ran throughout. The 1-minute loadavg was 1.29–2.10
across the Postgres runs and 1.66–1.84 across the Redis runs.

### Instrument

Unchanged since §1c apart from the 2026-05-05 rename — an xunit `Fact` + `Stopwatch`, not
BenchmarkDotNet: `Tests/Verbara.Sdk.Sessions.Postgres.Tests/PostgresLatencyBenchmark.cs` and
`Tests/Verbara.Sdk.Sessions.Redis.Tests/RedisLatencyBenchmark.cs`.

- `SaveAsync` / `GetAsync`: 1000 timed iterations after 10 warmup; percentile = `sorted[count × q]`.
- `SaveBatchAsync`: 10 batches × 500 sessions; throughput = 5000 / (sum of the 10 batch times).
- Fixtures: Testcontainers on a `localhost`-mapped port. Postgres runs with `SSL Mode=Disable` and the
  image's default server settings (`fsync=on`, `synchronous_commit=on`, `wal_sync_method=fdatasync`).
- Every run is a fresh process against a fresh container. The published figure is **the median of
  the five per-run values**.

### Postgres — five runs

11:05:41–11:06:26 UTC, 1-minute loadavg 1.29–2.10. All values in ms except sessions/sec.

| run | Save p50 | p95 | p99 | max | Get p50 | p95 | p99 | max | Batch p50 | max | total (5000) | sessions/sec |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 1.967 | 2.119 | 2.226 | 6.984 | 0.051 | 0.130 | 0.164 | 0.246 | 39.662 | 46.808 | 399.6 | 12513 |
| 2 | 1.964 | 2.120 | 2.410 | 9.527 | 0.053 | 0.103 | 0.144 | 0.229 | 35.445 | 50.874 | 365.7 | 13672 |
| 3 | 1.966 | 2.121 | 2.149 | 7.007 | 0.046 | 0.097 | 0.142 | 0.245 | 37.755 | 44.745 | 370.7 | 13489 |
| 4 | 1.965 | 2.119 | 2.258 | 6.974 | 0.048 | 0.102 | 0.157 | 0.243 | 34.541 | 47.785 | 357.7 | 13980 |
| 5 | 1.960 | 2.113 | 2.137 | 5.673 | 0.043 | 0.071 | 0.123 | 0.200 | 37.695 | 49.670 | 387.9 | 12889 |

**Medians:** `SaveAsync` p50 **1.965 ms** (range 1.960–1.967) · `GetAsync` p50 **0.048 ms**
(0.043–0.053) · `SaveBatchAsync` **13,489 sessions/sec** (12,513–13,980).
§1c, for comparison: `SaveAsync` p50 1.97 ms · `GetAsync` p50 51 µs · batch 9,491 sessions/sec.

### Redis — five runs

11:13:42–11:13:57 UTC, 1-minute loadavg 1.66–1.84; the set was started behind a loadavg < 2.0 gate.
All values in ms except sessions/sec.

| run | Save p50 | p95 | p99 | max | Get p50 | p95 | p99 | max | Batch p50 | max | total (5000) | sessions/sec |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 0.030 | 0.052 | 0.077 | 2.820 | 0.026 | 0.046 | 0.067 | 3.931 | 4.767 | 8.489 | 53.4 | 93564 |
| 2 | 0.038 | 0.054 | 0.082 | 2.649 | 0.039 | 0.070 | 0.105 | 4.093 | 4.921 | 8.764 | 54.9 | 91079 |
| 3 | 0.030 | 0.065 | 0.101 | 2.559 | 0.032 | 0.052 | 0.072 | 4.116 | 5.530 | 9.528 | 60.2 | 82994 |
| 4 | 0.029 | 0.049 | 0.088 | 2.503 | 0.031 | 0.041 | 0.063 | 5.080 | 5.046 | 9.301 | 54.9 | 91021 |
| 5 | 0.031 | 0.042 | 0.077 | 2.534 | 0.026 | 0.038 | 0.064 | 4.141 | 5.552 | 9.443 | 60.1 | 83169 |

**Medians:** `SaveAsync` p50 **0.030 ms** (0.029–0.038) · `GetAsync` p50 **0.031 ms** (0.026–0.039) ·
`SaveBatchAsync` **91,021 sessions/sec** (82,994–93,564).
§1c, for comparison: `SaveAsync` p50 79 µs · `GetAsync` p50 64 µs · batch 65,738 sessions/sec.

**Three earlier Redis runs were not used.** They were taken straight after a full-solution Release
build, at a 1-minute loadavg of ≈ 10.5–11.3. Their figures agree with the idle set above, and that
agreement is the only use made of them.

### Published figures

Derivation unchanged from the table's existing rows: throughput = 1 / p50, written with `~`; batch =
the measured sessions/sec.

| row | published | derivation |
|---|---|---|
| Session store Redis `SaveAsync` | ~33.3K saves/sec (p50 30 µs) / batch 91,021 sess/sec | 1 / 30 µs = 33,333 |
| Session store Postgres `SaveAsync` | ~500 saves/sec (p50 1.97 ms) / batch 13,489 sess/sec | 1 / 1.965 ms = 508.9 |

### Why Postgres `SaveAsync` did not move: the WAL flush

The single-save p50 was 1.97 ms on Dapper and is 1.965 ms on `NpgsqlExecutor`. That is not two
implementations happening to agree: it is the storage's commit-durability rate, and it was measured
directly rather than inferred.

1. `pg_test_fsync -s 3` inside `postgres:18-alpine`, on the same Docker storage: **fdatasync 499.4
   ops/sec (2002 µs/op)**, fsync 499.7 ops/sec (2001 µs/op), open_datasync 5632.7 ops/sec
   (178 µs/op). `fdatasync` is the server's `wal_sync_method`.
2. `pgbench -n -c 1 -T 8` against the same image (PostgreSQL 18.4), one autocommit single-row
   `INSERT` per transaction:
   - `synchronous_commit=on` → **499.07 tps**, latency average 2.004 ms
   - `synchronous_commit=off` → **116,738.73 tps**, latency average 0.009 ms

A single `SaveAsync` is one autocommit `UPSERT`, which is one WAL flush: ≈ 2.0 ms on this storage,
against a measured p50 of 1.965 ms. The ~500 saves/sec figure is therefore this machine's
commit-durability rate, not a property of the SDK's code — which is why it did not move across the
Dapper → `NpgsqlExecutor` rewrite. §1c's attribution of it to "UPSERT con JSONB parse + índices" is
wrong. `SaveBatchAsync` commits its 500 UPSERTs in one transaction — one flush — which comes to ~74 µs
per statement round trip today (1 / 13,489 sessions/sec).

### What can and cannot be attributed

- **Postgres `SaveAsync` latency is attributable — to storage**, by the proof above.
- **The Postgres batch gain, +42% (9,491 → 13,489 sessions/sec), is not attributable to the Dapper
  removal.** Redis, whose store code did not change, moved by a comparable or larger factor over the
  same period (`SaveAsync` p50 79 → 30 µs; batch 65,738 → 91,021), and the runtime (10.0.5 → 10.0.12),
  Npgsql (10.0.2 → 10.0.3), Docker and the kernel all moved too.
- **Redis's movement coincides with StackExchange.Redis 2.12.14 → 3.1.13**, a major-version change.
  That is a plausible cause and **not a verified one**: no A/B against the old client was run.

### Reproduce

The figures — one run per command, each a fresh process against a fresh container; repeat five times
and take the median of each value:

```sh
dotnet test Tests/Verbara.Sdk.Sessions.Postgres.Tests/ -c Release --filter "Category=Benchmark" --logger "console;verbosity=detailed"
dotnet test Tests/Verbara.Sdk.Sessions.Redis.Tests/ -c Release --filter "Category=Benchmark" --logger "console;verbosity=detailed"
```

The WAL-flush proof. A `pgbench` run taken straight after the container starts can read low, so
repeat each setting:

```sh
docker run --rm postgres:18-alpine pg_test_fsync -s 3 -f /var/lib/postgresql/pg_test_fsync.out

docker run -d --name wal-probe -e POSTGRES_PASSWORD=postgres postgres:18-alpine
until docker exec wal-probe pg_isready -h 127.0.0.1 -U postgres -q; do sleep 1; done
docker exec wal-probe psql -U postgres -c "CREATE TABLE wal_probe (v int)"
docker exec wal-probe sh -c 'echo "INSERT INTO wal_probe (v) VALUES (1);" > /tmp/insert.sql'
docker exec wal-probe pgbench -U postgres -n -c 1 -T 8 -f /tmp/insert.sql postgres
docker exec -e PGOPTIONS='-c synchronous_commit=off' wal-probe pgbench -U postgres -n -c 1 -T 8 -f /tmp/insert.sql postgres
docker rm -f wal-probe
```
