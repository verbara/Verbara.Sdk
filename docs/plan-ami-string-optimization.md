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

### Sprint 1+2 — Balanced (E+D): Key/Value Interning + Span-Based Parsing ✅ COMPLETADO

> Implementado como un solo sprint combinando Opciones E y D.

#### Tarea 1.1 — AmiStringPool (key + value interning unificado) ✅

- Creado `src/Asterisk.Sdk.Ami/Internal/AmiStringPool.cs`
- Pool estatico con ~70 keys y ~35 valores comunes
- Lookup O(1) por longitud de bytes, luego scan lineal con `SequenceEqual` (SIMD-accelerated)
- Fallback: `Encoding.UTF8.GetString()` para keys/valores desconocidos

#### Tarea 2.1 — Refactor AmiProtocolReader con Span-Based Parsing ✅

- Reescrito `AmiProtocolReader.ReadMessageAsync` con fast path `TryParseFieldBytes`
- `TryParseFieldBytes` → `ParseFieldSpan`: busca colon byte en span, extrae key/value sin string intermedio
- Single-segment fast path + stackalloc fallback para multi-segment (<512 bytes)
- `StartsWithEndCommand` → `CheckEndCommandMultiSegment`: chequeo byte-based de --END COMMAND--

#### Tarea 2.2 — Benchmark validation ✅

Resultados (AMD Ryzen 9 9900X, .NET 10.0.3):

| Benchmark | Antes (Latencia) | Despues (Latencia) | Antes (Alloc) | Despues (Alloc) | Reduccion |
|-----------|------------------|--------------------|----------------|-----------------|-----------|
| ParseSingleEvent | 743 ns | **582 ns** (-22%) | 3.15 KB | **1.83 KB** (-42%) | ✅ |
| ParseResponse | 487 ns | **412 ns** (-15%) | 1.94 KB | **1.27 KB** (-35%) | ✅ |
| ParseNewchannel (15 fields) | 984 ns | **791 ns** (-20%) | 4.13 KB | **1.90 KB** (-54%) | ✅ |

Targets alcanzados:
- ✅ Sprint 1 target: <2.5 KB por evento → **1.83 KB** (single event), **1.90 KB** (15-field)
- ✅ Latencia mejorada: -15% a -22% en todos los benchmarks
- ✅ 0 breaking changes, 0 warnings, 473 tests passing

### Sprint 3 — Advanced (Descartado)

> No necesario. Sprint 1+2 supero los targets de reduccion de allocations.
> Dictionary pooling (C) descartado por riesgo de use-after-return bugs y beneficio marginal (600 B).
> FrozenDictionary (F) descartado porque AmiMessage fields se leen 1-2 veces (read-once dispatch pattern).

---

## Metricas de Exito

| Metrica | Antes | Target | Resultado | Estado |
|---------|-------|--------|-----------|--------|
| Alloc/evento (15 fields) | 4.13 KB | **<2.5 KB** | **1.90 KB** | ✅ |
| Alloc/evento (single) | 3.15 KB | **<2.5 KB** | **1.83 KB** | ✅ |
| Latencia/evento (single) | 743 ns | **<650 ns** | **582 ns** | ✅ |
| Gen0 GC/1000 ops | 0.19 | **<0.15** | **0.12** | ✅ |
| Breaking changes | — | 0 | **0** | ✅ |
| AOT compatible | Si | Si | **Si** | ✅ |
| Tests passing | 473 | 473 | **473** | ✅ |
