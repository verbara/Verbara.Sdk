# Plan: Optimizacion de String Allocation en AMI Parsing

> Fecha: 2026-03-06 | Branch: `feature/rename-asterisk-sdk`
> Baseline: 743 ns / 3.15 KB por evento AMI (benchmark `ParseSingleEvent`)

---

## Problema

El AMI reader alloca strings nuevos para cada key y value de cada evento:

```csharp
// AmiProtocolReader.cs — linea 66, 85-89
var lineStr = GetString(line);              // alloc #1: linea completa
var key = lineStr[..colonIndex].Trim();     // alloc #2: substring key
var value = lineStr[(colonIndex + 1)..].Trim(); // alloc #3: substring value
fields[key] = value;                        // alloc #4: Dictionary entry
```

Para un evento Newchannel (15 campos): **~45 string allocations + 1 Dictionary**.

### Distribucion de Allocations (3.15 KB por evento)

| Fuente | Bytes estimados | % |
|--------|----------------|---|
| Dictionary overhead (15 entries) | ~600 B | 19% |
| String keys (15 × ~20 chars avg) | ~600 B | 19% |
| String values (15 × ~30 chars avg) | ~900 B | 29% |
| String lineas completas (intermedias) | ~1,050 B | 33% |

---

## Opciones de Optimizacion

### Opcion A: Key Interning con Static Pool

**Concepto**: Las keys AMI son un set finito (~80 keys distintas). Internar las mas comunes elimina allocations de keys.

```csharp
// Antes:
var key = lineStr[..colonIndex].Trim();  // alloc nuevo string cada vez

// Despues:
var key = AmiKeyPool.GetOrCreate(line.Slice(0, colonIndex));  // retorna interned string
```

**Keys candidatas para pool (top 20 por frecuencia)**:

| Key | Aparece en | Eventos estimados/sec |
|-----|-----------|----------------------|
| `Event` | 100% eventos | Todos |
| `Channel` | ~70% eventos (ChannelEventBase + 5 bases mas) | Muy alto |
| `Privilege` | 100% eventos | Todos |
| `Uniqueid` | ~80% eventos (ManagerEvent base) | Muy alto |
| `Linkedid` | ~60% eventos (ChannelEventBase) | Alto |
| `Context` | ~50% eventos | Alto |
| `Exten` | ~50% eventos | Alto |
| `Priority` | ~50% eventos | Alto |
| `CallerIDNum` | ~40% eventos | Alto |
| `CallerIDName` | ~40% eventos | Alto |
| `ConnectedLineNum` | ~40% eventos | Medio |
| `ConnectedLineName` | ~40% eventos | Medio |
| `ChannelState` | ~40% eventos | Medio |
| `ChannelStateDesc` | ~40% eventos | Medio |
| `AccountCode` | ~40% eventos | Medio |
| `Language` | ~40% eventos | Medio |
| `Response` | 100% responses | Medio |
| `ActionID` | 100% responses | Medio |
| `Message` | ~60% responses | Medio |
| `Queue` | ~15% eventos (queue family) | Variable |

```
Impacto: Elimina ~19% de allocations (600 B → ~0 B para keys)
Complejidad: Baja
Riesgo: Ninguno
AOT compatible: Si (static readonly, no reflection)
```

---

### Opcion B: Avoid Intermediate Line String

**Concepto**: Parsear key:value directamente de los bytes, sin materializar la linea completa.

```csharp
// Antes:
var lineStr = GetString(line);           // alloc linea completa "Channel: SIP/2000"
var colonIndex = lineStr.IndexOf(':');
var key = lineStr[..colonIndex].Trim();  // alloc "Channel"
var value = lineStr[(colonIndex + 1)..].Trim(); // alloc "SIP/2000"

// Despues:
if (TryParseField(line, out var key, out var value))
{
    // key viene del pool (0 alloc), value es string directo de bytes
    fields[key] = value;
}
```

```
Impacto: Elimina ~33% de allocations (1,050 B → 0 B para lineas intermedias)
Complejidad: Media
Riesgo: Bajo (mas parsing en bytes, edge cases con multi-segment buffers)
AOT compatible: Si
```

---

### Opcion C: Dictionary Pooling con ObjectPool

**Concepto**: Reusar instancias de Dictionary entre eventos via `ObjectPool<Dictionary<string, string>>`.

```csharp
// Antes:
var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

// Despues:
var fields = _dictionaryPool.Get();  // reusa Dictionary existente
fields.Clear();
// ... parsear campos ...
return new AmiMessage(fields, _dictionaryPool); // AmiMessage devuelve al pool en Dispose()
```

```
Impacto: Elimina ~19% de allocations (600 B → 0 B Dictionary overhead)
Complejidad: Media-Alta (AmiMessage necesita IDisposable o return-to-pool pattern)
Riesgo: Medio (lifetime management, potential use-after-return bugs)
AOT compatible: Si (ObjectPool<T> es AOT-safe)
```

---

### Opcion D: Value Interning para Valores Comunes

**Concepto**: Valores repetitivos como estados de canal, contextos comunes, etc.

```csharp
// Valores que se repiten constantemente:
"Down", "Ring", "Up", "Busy", "Ringing"     // ChannelStateDesc
"default", "internal", "external"            // Context
"0", "1", "2", "3", "4", "5", "6"          // ChannelState, Priority
"", " "                                      // Empty ConnectedLine fields
"call,all", "agent,all", "system,all"       // Privilege
```

```
Impacto: Elimina ~10-15% de allocations (valores cortos/repetitivos)
Complejidad: Baja
Riesgo: Bajo (FrozenSet lookup, solo valores exactos)
AOT compatible: Si
```

---

### Opcion E: Span-Based Parsing (Zero-Copy Keys)

**Concepto**: Usar `ReadOnlySpan<byte>` para buscar el colon sin materializar strings.

```csharp
// Parsear Key directamente de bytes usando Span
private static bool TryFindColon(ReadOnlySpan<byte> lineSpan, out int colonIndex)
{
    colonIndex = lineSpan.IndexOf((byte)':');
    return colonIndex > 0;
}

// Luego convertir key bytes → pool lookup, value bytes → string
var keySpan = lineSpan[..colonIndex];
var key = AmiKeyPool.GetOrCreate(keySpan);  // UTF8 span → interned string
var value = Encoding.UTF8.GetString(lineSpan[(colonIndex + 2)..]); // solo value alloc
```

```
Impacto: Combina A + B. Elimina ~52% de allocations
Complejidad: Media
Riesgo: Bajo
AOT compatible: Si
```

---

### Opcion F: FrozenDictionary para AmiMessage (read-heavy)

**Concepto**: `AmiMessage._fields` se lee muchas veces pero se escribe una sola vez. `FrozenDictionary` optimiza reads.

```csharp
// Antes:
public sealed class AmiMessage
{
    private readonly Dictionary<string, string> _fields;  // mutable, general-purpose

// Despues:
public sealed class AmiMessage
{
    private readonly FrozenDictionary<string, string> _fields;  // immutable, read-optimized
```

```
Impacto: 0% en allocations (mismo o ligeramente mas), pero ~30-50% faster reads
Complejidad: Baja
Riesgo: Bajo (requiere .NET 8+, tenemos .NET 10)
AOT compatible: Si (FrozenDictionary es AOT-safe)
Nota: Aumenta costo de creacion, reduce costo de lookup. Solo vale si AmiMessage se lee >3 veces.
```

---

## Tabla Comparativa

| Opcion | Reduccion Alloc | Latencia Esperada | Complejidad | Riesgo | Breaking Change |
|--------|----------------|-------------------|-------------|--------|-----------------|
| **A: Key Interning** | **-19% (-600 B)** | ~650 ns (-12%) | Baja | Ninguno | No |
| **B: No intermediate string** | **-33% (-1,050 B)** | ~580 ns (-22%) | Media | Bajo | No |
| **C: Dictionary Pool** | **-19% (-600 B)** | ~680 ns (-8%) | Media-Alta | Medio | Si* |
| **D: Value Interning** | **-10-15% (-400 B)** | ~700 ns (-6%) | Baja | Bajo | No |
| **E: Span-Based (A+B)** | **-52% (-1,650 B)** | ~500 ns (-33%) | Media | Bajo | No |
| **F: FrozenDictionary** | 0% | reads -40% | Baja | Bajo | No |

\* C es breaking si AmiMessage expone la Dictionary reference y alguien la retiene.

### Combinaciones Recomendadas

| Combo | Opciones | Reduccion Total | Latencia | Esfuerzo |
|-------|----------|----------------|----------|----------|
| **Quick Win** | A + D | -30% (~2.2 KB) | ~620 ns | 1 sprint |
| **Balanced** | E + D | -62% (~1.2 KB) | ~470 ns | 1-2 sprints |
| **Maximum** | E + D + C + F | -80% (~0.6 KB) | ~400 ns | 2-3 sprints |

---

## Plan de Ejecucion Propuesto

### Sprint 1 — Quick Wins (Key + Value Interning)

#### Tarea 1.1 — AmiKeyPool (static key interning)

- Crear `AmiKeyPool` con `FrozenDictionary<int, string>` (hash de UTF8 bytes → interned string)
- Pool de ~80 keys conocidas, generadas por source generator o hardcoded
- Fallback: `Encoding.UTF8.GetString()` para keys desconocidas
- Benchmark: comparar antes/despues con `EventDeserializerBenchmark`

#### Tarea 1.2 — AmiValuePool (common value interning)

- Crear `AmiValuePool` con valores comunes: estados, contextos, numeros cortos
- ~50 valores mas frecuentes
- Fallback: string normal para valores no-pooled

#### Tarea 1.3 — Benchmark validation

- Verificar reduccion de alloc en benchmarks existentes
- Target: <2.5 KB por evento (vs 3.15 KB actual)

### Sprint 2 — Span-Based Parsing

#### Tarea 2.1 — Refactor TryParseField con Span

- Parsear colon en bytes sin materializar linea completa
- Key lookup directo en `AmiKeyPool` desde `ReadOnlySpan<byte>`
- Value: `Encoding.UTF8.GetString()` solo del segmento value

#### Tarea 2.2 — Handle multi-segment buffers

- `ReadOnlySequence<byte>` puede tener multiples segmentos
- Optimizar single-segment fast path, fallback para multi-segment

#### Tarea 2.3 — Benchmark validation

- Target: <1.5 KB por evento, <550 ns latencia

### Sprint 3 — Advanced (Optional)

#### Tarea 3.1 — Dictionary pooling con ObjectPool

- Solo si Sprint 2 no alcanza el target
- Requiere analizar lifetime de AmiMessage en el codebase

#### Tarea 3.2 — FrozenDictionary para AmiMessage

- Solo si profiling muestra que reads de AmiMessage son bottleneck
- Medir overhead de `FrozenDictionary.ToFrozenDictionary()` vs beneficio en reads

---

## Metricas de Exito

| Metrica | Actual | Sprint 1 Target | Sprint 2 Target |
|---------|--------|-----------------|-----------------|
| Alloc/evento (15 fields) | 3.15 KB | **<2.5 KB** | **<1.5 KB** |
| Latencia/evento | 743 ns | **<650 ns** | **<550 ns** |
| Gen0 GC/1000 ops | 0.19 | **<0.15** | **<0.10** |
| Breaking changes | — | 0 | 0 |
| AOT compatible | Si | Si | Si |
