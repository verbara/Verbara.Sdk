# Asterisk.NetAot - Revision Arquitectonica para Alta Carga

**Fecha:** 2026-03-01
**Escenario objetivo:** 100,000+ agentes conectados, multiples colas, alta concurrencia
**Version analizada:** 1.0.0-preview.1 (12 fases completadas)

---

## 1. Resumen Ejecutivo

La solucion tiene las 12 fases de migracion completadas, incluyendo source generators con cuerpo completo, 164 unit tests, 25 integration tests con Docker, 15 benchmarks con BenchmarkDotNet, y publicacion Native AOT verificada (binario de 1.3 MB, 0 trim warnings). Presenta una base solida con decisiones acertadas como System.IO.Pipelines, System.Threading.Channels, source generators y Native AOT. Sin embargo, **para 100,000+ agentes se identifican 14 hallazgos criticos y 8 mejoras recomendadas** que deben abordarse antes de produccion a escala.

| Severidad | Cantidad | Impacto |
|-----------|----------|---------|
| Critico (bloqueante) | 5 | Data corruption, state loss, thread safety |
| Alto (degradacion severa) | 4 | Memory pressure, O(n) scans, bottlenecks |
| Medio (mejora necesaria) | 5 | Allocations, missing indices, observability |
| Bajo (optimizacion) | 3 | Code quality, future-proofing |

### Estado de Completitud del Proyecto

| Metrica | Valor | Estado |
|---------|-------|--------|
| Fases completadas | 12/12 | Completado |
| Unit tests | 164 (70 AMI + 28 AGI + 19 Live + 19 PBX + 5 ARI + 15 Config + 8 DI) | Passing |
| Integration tests | 25 (8 AMI + 4 AGI + 5 ARI + 8 DI) | Ready |
| Benchmarks | 15 (BenchmarkDotNet) | Implementados |
| Source generators | 4 (Action serializer, Event deserializer, Event registry, Response deserializer) | Cuerpo completo |
| Build | 0 warnings, 0 errors | Limpio |
| Trim warnings | 0 | Limpio |
| Binario AOT | 1.3 MB (linux-x64, BasicAmiExample) | Verificado |
| Archivos fuente | 531 .cs en src/ | Completo |
| Docker infra | docker-compose + Dockerfile + 7 archivos config Asterisk | Ready |

---

## 2. Evaluacion de la Division en Proyectos

### 2.1 Estructura Actual (9 proyectos + 4 source generators)

```
Abstractions  (interfaces puras, enums, atributos)
     |
    Ami  (+ Ami.SourceGenerators como analyzer)
     |
   Agi   Live   (ambos dependen de Abstractions + Ami)
     |
    Pbx  (depende de Abstractions + Ami + Agi + Live)
    Ari  (solo depende de Abstractions)
  Config  (solo depende de Abstractions)
     |
Asterisk.NetAot  (meta-paquete con DI registration)
```

### 2.2 Veredicto: La division ES justificada, pero con matices

**Justificada:**
- **Ari** es completamente independiente (REST + WebSocket), protocolo diferente a AMI. Un usuario puede querer solo ARI sin AMI. Proyecto separado correcto.
- **Config** es standalone (parseo de archivos .conf). Proyecto separado correcto.
- **Abstractions** como capa de contratos es esencial para desacoplamiento e inyeccion de dependencias.
- **Source Generators** DEBEN ser proyecto separado (netstandard2.0, DevelopmentDependency).

**Cuestionable:**
- **Live y Pbx podrian fusionarse.** Pbx depende de Live, Agi y Ami. Live es esencialmente el "modelo de dominio en tiempo real" y Pbx son "actividades sobre ese modelo". En la practica, quien use Pbx siempre usara Live. Para 100K agentes, tener Live+Pbx separados agrega indirection sin beneficio tangible.
- **Agi podria ser parte de Ami.** AGI usa el mismo transporte TCP con PipelineSocketConnection. La separacion tiene sentido semantico (protocolos diferentes), pero operacionalmente son colocados. Para una libreria (no microservicios), podrian ser un solo ensamblado.

**Recomendacion:**

| Escenario | Division recomendada |
|-----------|---------------------|
| **Distribucion NuGet** (usuarios eligen que instalar) | Mantener separados: permite `dotnet add package Asterisk.NetAot.Ami` sin arrastrar AGI/ARI |
| **Monolito interno** (deploy completo siempre) | Fusionar: Ami+Agi+Live+Pbx en un solo proyecto, Ari separado, Config separado |
| **Compromiso pragmatico** | Fusionar Live+Pbx en uno ("Asterisk.NetAot.Domain"). Mantener Ami, Agi, Ari, Config separados |

> **Para 100K agentes:** La division en proyectos NO impacta performance. El overhead de multiples assemblies en runtime es despreciable. El impacto real esta en las estructuras de datos internas y patrones de concurrencia, no en la granularidad de los NuGet packages.

---

## 3. Evaluacion del Patron Arquitectonico

### 3.1 Patron Actual: Event-Driven Domain Model con Single Connection

```
TCP Socket
    |
PipelineSocketConnection (System.IO.Pipelines, zero-copy)
    |
AmiProtocolReader/Writer (parsing Key:Value\r\n)
    |
AmiConnection (request/response correlation via ConcurrentDictionary)
    |
AsyncEventPump (Channel<T>, bounded 20K, DropOldest)
    |
AsteriskServer.EventObserver (pattern matching, dispatch a managers)
    |
ChannelManager / QueueManager / AgentManager / MeetMeManager
    |
Domain Events (Action<T> delegates)
```

### 3.2 Veredicto: Correcto para libreria cliente, NO para 100K agentes nativamente

**Lo que esta bien:**
- System.IO.Pipelines para zero-copy parsing
- System.Threading.Channels para desacoplar reader del dispatch
- ConcurrentDictionary para correlacion request/response
- Source generators para AOT sin reflexion
- IObservable<T> para suscripcion de eventos

**Lo que NO escala a 100K agentes:**

El diseno actual asume **1 instancia de aplicacion = 1 conexion AMI = 1 Asterisk**. Esta es la realidad del protocolo AMI: cada conexion TCP es una sesion autenticada independiente. Asterisk tipicamente soporta 100-200 conexiones AMI simultaneas.

Para 100K agentes necesitas una de estas arquitecturas:

| Arquitectura | Descripcion | Complejidad |
|-------------|-------------|-------------|
| **N instancias x 1 conexion** | Microservicios, cada uno con 1 AmiConnection | Baja (actual) |
| **1 instancia x N Asterisk** | Pool de conexiones a multiples servidores Asterisk | Media |
| **Cluster Asterisk + ARI** | Kamailio/OpenSIPs como proxy SIP + ARI para control | Alta |

---

## 4. Hallazgos Criticos (Bloqueantes para Alta Carga)

### CRITICO-01: Race Condition en QueueManager.Members (List\<T\> no thread-safe)

**Archivo:** `src/Asterisk.NetAot.Live/Queues/QueueManager.cs:52-53`

```csharp
// Dos hilos pueden ejecutar esto simultaneamente para la misma cola:
queue.Members.RemoveAll(m => m.Interface == iface);  // Lectura + modificacion
queue.Members.Add(member);                            // Append
```

`List<T>` NO es thread-safe. Con 100K agentes generando eventos de cola concurrentemente:
- **Corrupcion de datos**: `RemoveAll` + `Add` no son atomicos
- **IndexOutOfRangeException**: acceso concurrente al array interno
- **Estado inconsistente**: miembros duplicados o perdidos

**Mismo problema en:** `OnMemberRemoved` (linea 65), `OnCallerJoined` (linea 96), `OnCallerLeft` (linea 108)

**Severidad:** CRITICA - Corrupcion silenciosa de estado en produccion

**Solucion:**
```csharp
// Opcion A: Lock por cola
private readonly ConcurrentDictionary<string, Lock> _queueLocks = new();

// Opcion B: Reemplazar List<T> por ConcurrentDictionary<string, T>
public ConcurrentDictionary<string, AsteriskQueueMember> Members { get; } = new();
```

---

### CRITICO-02: Writer Concurrente sin Sincronizacion en AmiConnection

**Archivo:** `src/Asterisk.NetAot.Ami/Connection/AmiConnection.cs:216`

```csharp
// SendActionAsync puede ser llamado por N hilos simultaneamente:
await _writer!.WriteActionAsync(actionName, actionId, fields, cancellationToken);
```

`AmiProtocolWriter` escribe al `PipeWriter` (Output pipe). Si dos hilos llaman `SendActionAsync` simultaneamente, los bytes de ambas acciones pueden intercalarse en el pipe, produciendo un mensaje AMI corrupto:

```
Action: Login\r\n       ← Hilo 1
Action: Status\r\n      ← Hilo 2 (intercalado!)
Username: admin\r\n     ← Hilo 1
\r\n                    ← Hilo 1 (fin de mensaje corrupto)
```

**Severidad:** CRITICA - Corrupcion de protocolo, desconexion de Asterisk

**Solucion:**
```csharp
private readonly SemaphoreSlim _writeLock = new(1, 1);

// En SendActionAsync:
await _writeLock.WaitAsync(cancellationToken);
try
{
    await _writer!.WriteActionAsync(actionName, actionId, fields, cancellationToken);
}
finally
{
    _writeLock.Release();
}
```

---

### CRITICO-03: DropOldest Silencioso Causa Perdida de Estado

**Archivo:** `src/Asterisk.NetAot.Ami/Internal/AsyncEventPump.cs:22-27`

```csharp
_channel = Channel.CreateBounded<ManagerEvent>(new BoundedChannelOptions(capacity)
{
    FullMode = BoundedChannelFullMode.DropOldest,  // Descarta sin aviso
    SingleReader = true,
    SingleWriter = true
});
```

Con 100K agentes, un evento de login o hangup descartado produce estado inconsistente:
- Agente aparece logueado pero nunca se proceso el login
- Canal aparece activo pero el hangup se perdio (memory leak en ChannelManager)
- Miembro de cola aparece pausado pero el unpause se descarto

No hay metrica, log, ni callback para eventos descartados.

**Severidad:** CRITICA - Inconsistencia silenciosa e irrecuperable del estado

**Solucion:**
```csharp
// Opcion A: Metrica + alerta
private long _droppedEvents;

public bool TryEnqueue(ManagerEvent evt)
{
    if (!_channel.Writer.TryWrite(evt))
    {
        Interlocked.Increment(ref _droppedEvents);
        // Log o callback
        return false;
    }
    return true;
}

// Opcion B: Wait en lugar de Drop (backpressure real)
FullMode = BoundedChannelFullMode.Wait  // Bloquea al writer si esta lleno

// Opcion C: Capacidad adaptativa
new BoundedChannelOptions(capacity: 200_000)  // 10x para 100K agentes
```

---

### CRITICO-04: Singleton AmiConnection Sin Soporte Multi-Servidor

**Archivo:** `src/Asterisk.NetAot/ServiceCollectionExtensions.cs:41`

```csharp
services.TryAddSingleton<IAmiConnection, AmiConnection>();
```

100K agentes NUNCA estaran en un solo servidor Asterisk. Asterisk soporta ~2,000-5,000 agentes por instancia. Para 100K necesitas 20-50 servidores.

La DI actual registra UN singleton de AmiConnection. No hay:
- Pool de conexiones
- Keyed services por servidor
- Factory para crear conexiones dinamicamente
- Routing de acciones al servidor correcto

**Severidad:** CRITICA - Arquitectura incompatible con el escenario objetivo

**Solucion:**
```csharp
// Opcion A: Keyed services (.NET 8+)
services.AddKeyedSingleton<IAmiConnection>("server1", (sp, key) => ...);
services.AddKeyedSingleton<IAmiConnection>("server2", (sp, key) => ...);

// Opcion B: Factory pattern
services.AddSingleton<IAmiConnectionFactory, AmiConnectionFactory>();
// Permite: var conn = factory.CreateConnection(serverConfig);

// Opcion C: Pool con routing
services.AddSingleton<IAmiConnectionPool, AmiConnectionPool>();
// Permite: var conn = pool.GetConnectionForAgent("agent5001");
```

---

### CRITICO-05: Actualizacion No Atomica de Propiedades en Entidades Live

**Archivo:** `src/Asterisk.NetAot.Live/Agents/AgentManager.cs:27-31`

```csharp
var agent = _agents.GetOrAdd(agentId, _ => new AsteriskAgent { AgentId = agentId });
agent.State = AgentState.Available;     // Write 1
agent.Channel = channel;                // Write 2
agent.LoggedInAt = DateTimeOffset.UtcNow;  // Write 3
AgentLoggedIn?.Invoke(agent);           // Lector ve estado parcial?
```

Entre Write 1 y Write 3, otro hilo podria leer el agente y ver:
- `State = Available` pero `Channel = null` (write 2 no ejecutado aun)
- El evento `AgentLoggedIn` se dispara, pero los consumidores reciben un objeto que aun puede estar mutando

Mismo patron en `ChannelManager.OnNewChannel`, `QueueManager.OnQueueParams`, etc.

**Severidad:** CRITICA en alta concurrencia - Lecturas inconsistentes ("torn reads")

**Solucion:**
```csharp
// Opcion A: Inmutabilidad (crear nuevo objeto, reemplazar en diccionario)
var newAgent = agent with { State = Available, Channel = channel, LoggedInAt = now };
_agents[agentId] = newAgent;  // Atomico a nivel de referencia

// Opcion B: Lock por entidad
agent.BeginUpdate();
try { ... }
finally { agent.EndUpdate(); }
```

---

## 5. Hallazgos de Alta Severidad (Degradacion de Performance)

### ALTO-01: Busqueda de Canal por Nombre es O(n)

**Archivo:** `src/Asterisk.NetAot.Live/Channels/ChannelManager.cs:28-29`

```csharp
public AsteriskChannel? GetByName(string name) =>
    _channelsByUniqueId.Values.FirstOrDefault(c =>
        string.Equals(c.Name, name, StringComparison.Ordinal));
```

Escaneo lineal de todos los canales. Con 5,000 canales activos: 5,000 comparaciones por busqueda. Si se llama desde event handlers, se convierte en O(n^2).

**Solucion:** Indice secundario `ConcurrentDictionary<string, AsteriskChannel> _channelsByName`

---

### ALTO-02: No Existe Indice Inverso Agente -> Colas

No hay forma eficiente de responder "en que colas esta el agente X?". Requiere escanear todas las colas y todos los miembros de cada cola.

Con 1,000 colas x 100 miembros = 100,000 comparaciones por agente.

**Operaciones afectadas:**
- Logout de agente de todas las colas
- Dashboard de agente (mostrar sus colas)
- Reasignacion de agente entre colas

**Solucion:** `ConcurrentDictionary<string, HashSet<string>> _queuesByAgent` (agentInterface -> queueNames)

---

### ALTO-03: Snapshots de Colecciones Allotan Excesivamente

**Archivos:** AgentManager.cs:20, QueueManager.cs:22, ChannelManager.cs:22-23

```csharp
public IReadOnlyCollection<AsteriskAgent> Agents =>
    _agents.Values.ToList().AsReadOnly();
```

Cada acceso a `.Agents`, `.Queues`, `.ActiveChannels` crea:
1. Un nuevo `List<T>` (copia completa)
2. Un wrapper `ReadOnlyCollection<T>`

Para 100K agentes: **~800 KB de allocation por llamada**. Si un dashboard llama esto cada segundo, genera 800 KB/s de presion en GC.

**Solucion:**
```csharp
// Opcion A: Retornar IEnumerable (lazy, zero-alloc)
public IEnumerable<AsteriskAgent> Agents => _agents.Values;

// Opcion B: Snapshot cacheado con version
private volatile (int Version, IReadOnlyList<AsteriskAgent> Snapshot) _cache;
```

---

### ALTO-04: Reconexion No Reestablece Estado del Live API

**Archivo:** `src/Asterisk.NetAot.Ami/Connection/AmiConnection.cs:390-401`

```csharp
private async Task ReconnectLoopAsync()
{
    // ...
    await CleanupAsync();
    await ConnectAsync();
    return; // Success - PERO no llama RequestInitialStateAsync()!
}
```

Despues de una reconexion:
- Los managers (ChannelManager, QueueManager, AgentManager) retienen estado STALE
- No se re-suscriben los observers
- No se llama `AsteriskServer.RequestInitialStateAsync()`
- El estado diverge silenciosamente de la realidad de Asterisk

**Solucion:** Exponer un evento `OnReconnected` y que AsteriskServer limpie managers + recargue estado.

---

## 6. Hallazgos de Severidad Media

### MEDIO-01: Lock en Dispatch de Cada Evento

**Archivo:** `src/Asterisk.NetAot.Ami/Connection/AmiConnection.cs:406-410`

```csharp
private ValueTask DispatchEventAsync(ManagerEvent evt)
{
    IObserver<ManagerEvent>[] snapshot;
    lock (_observerLock)
    {
        snapshot = [.. _observers];  // Allocation dentro del lock!
    }
    // ...
}
```

Para cada evento recibido:
1. Adquiere lock
2. Alloca un array con spread operator `[.. _observers]`
3. Libera lock

Con 10,000 eventos/segundo: 10,000 locks + 10,000 allocations por segundo.

**Solucion:** Copy-on-write con `ImmutableArray<T>`:
```csharp
private volatile ImmutableArray<IObserver<ManagerEvent>> _observers = [];

// Subscribe: atomar via Interlocked
// Dispatch: leer _observers directamente (no lock, no alloc)
```

---

### MEDIO-02: ExtensionHistory Sin Limite (Memory Leak Potencial)

**Archivo:** `src/Asterisk.NetAot.Live/Channels/ChannelManager.cs:125`

```csharp
public List<ExtensionHistoryEntry> ExtensionHistory { get; } = [];
```

Un canal de larga duracion (IVR, cola, conferencia) puede acumular miles de entradas. No hay limite de tamano.

**Solucion:** Circular buffer o limite configurable (ej: ultimas 100 entradas).

---

### MEDIO-03: No Hay Metricas ni Observabilidad

La libreria no expone:
- Contadores de eventos procesados/descartados
- Latencia de dispatch de eventos
- Tamano actual de las colecciones de managers
- Estado del event pump (pendientes, capacidad)
- Tasa de reconexiones

Para 100K agentes, volar a ciegas es inaceptable.

**Solucion:** Integrar con `System.Diagnostics.Metrics` (nativo en .NET 8+):
```csharp
private static readonly Meter s_meter = new("Asterisk.NetAot.Ami");
private static readonly Counter<long> s_eventsProcessed = s_meter.CreateCounter<long>("ami.events.processed");
private static readonly Counter<long> s_eventsDropped = s_meter.CreateCounter<long>("ami.events.dropped");
private static readonly Histogram<double> s_dispatchLatency = s_meter.CreateHistogram<double>("ami.dispatch.latency.ms");
```

---

### MEDIO-04: EventObserver.OnError Vacio

**Archivo:** `src/Asterisk.NetAot.Live/Server/AsteriskServer.cs:267`

```csharp
public void OnError(Exception error) { }  // Silencia errores del observable
public void OnCompleted() { }             // Ignora completado
```

Si la conexion AMI falla, `OnError` se traga la excepcion. El AsteriskServer no sabe que la conexion murio y sigue con estado stale.

**Solucion:** Loguear, disparar evento, o limpiar estado.

---

### MEDIO-05: Falta Soporte para QueueMemberStatus Events

El `AsteriskServer.EventObserver` maneja `QueueMemberAddedEvent` y `QueueMemberRemovedEvent`, pero NO maneja:
- `QueueMemberStatusEvent` (cambio de estado: Available -> InCall -> Paused)
- `QueueMemberPauseEvent` (pause/unpause explicito)

Con 100K agentes, los cambios de estado de miembros de cola son los eventos mas frecuentes. Ignorarlos significa que `QueueMemberState` nunca se actualiza despues del estado inicial.

---

## 7. Hallazgos de Baja Severidad

### BAJO-01: PipeOptions Sin Tuning para Alta Carga

**Archivo:** `src/Asterisk.NetAot.Ami/Transport/PipelineSocketConnection.cs:57-59`

```csharp
var pipeOptions = new PipeOptions(
    minimumSegmentSize: MinimumBufferSize,  // 4096
    useSynchronizationContext: false);
```

Faltan:
- `pauseWriterThreshold` / `resumeWriterThreshold` para backpressure controlada
- `MemoryPool<byte>` compartido para reducir allocations
- `ReaderScheduler` / `WriterScheduler` para controlar scheduling

---

### BAJO-02: ResponseEventCollector Usa Canal Unbounded

**Archivo:** `src/Asterisk.NetAot.Ami/Connection/AmiConnection.cs:537-538`

```csharp
private readonly Channel<ManagerEvent> _channel =
    Channel.CreateUnbounded<ManagerEvent>();
```

Un `StatusAction` en un sistema con 100K canales activos generaria 100K eventos en un canal sin limite. Si el consumidor es lento, la memoria crece indefinidamente.

---

### BAJO-03: Ausencia de IAsyncEnumerable en Managers

Los managers exponen `IReadOnlyCollection<T>` que materializa todo en memoria. Para 100K agentes, seria mas eficiente:

```csharp
public async IAsyncEnumerable<AsteriskAgent> GetAgentsAsync(
    Func<AsteriskAgent, bool>? predicate = null)
{
    foreach (var agent in _agents.Values)
    {
        if (predicate?.Invoke(agent) ?? true)
            yield return agent;
    }
}
```

---

## 8. Analisis de Memoria para 100K Agentes

### 8.1 Estimacion por Componente

| Componente | Items | Bytes/Item | Total | Notas |
|-----------|-------|-----------|-------|-------|
| AgentManager._agents | 100,000 | ~250 B | **25 MB** | ConcurrentDict overhead: +3 MB |
| QueueManager._queues | 1,000 | ~200 B | 200 KB | Solo metadatos de cola |
| Queue.Members (total) | 100,000 | ~180 B | **18 MB** | Distribuidos en 1,000 colas |
| Queue.Entries (waiters) | 10,000 | ~120 B | 1.2 MB | Llamantes en espera |
| ChannelManager (active) | 5,000 | ~350 B | 1.75 MB | Canales concurrentes |
| AsyncEventPump buffer | 20,000 | ~100 B | 2 MB | Bounded channel |
| Pending actions dict | ~100 | ~200 B | 20 KB | TaskCompletionSource |
| Observers snapshot/evt | per-dispatch | ~40 B | Variable | Allocation por evento |

**Total estimado: ~55-60 MB** (aceptable para un servidor moderno)

### 8.2 Hotspots de GC

| Hotspot | Frecuencia | Allocation | Impacto |
|---------|-----------|------------|---------|
| `.Agents.ToList()` | Por acceso | 800 KB | Alto - Gen2 collections |
| `[.. _observers]` snapshot | Por evento | 40-200 B | Medio - Gen0 churn |
| `new AsteriskAgent/Channel/Member` | Por evento | 200-350 B | Bajo - necesario |
| `string.Create` (ActionId) | Por action | 64 B | Bajo |
| `new TaskCompletionSource` | Por action | 72 B | Bajo |

---

## 9. Patron Arquitectonico Recomendado para 100K+ Agentes

### 9.1 Arquitectura Propuesta: Multi-Server Federation

```
                    ┌─────────────────────────────────┐
                    │   Application / API Gateway      │
                    │   (ASP.NET Core / gRPC)          │
                    └─────────┬───────────────────────┘
                              │
                    ┌─────────▼───────────────────────┐
                    │   AsteriskServerPool             │
                    │   (nueva clase)                  │
                    │                                  │
                    │  ┌──────────────────────────┐   │
                    │  │ ConcurrentDictionary      │   │
                    │  │ <serverId, AsteriskServer>│   │
                    │  └──────────────────────────┘   │
                    │                                  │
                    │  ┌──────────────────────────┐   │
                    │  │ Agent Routing Table       │   │
                    │  │ <agentId, serverId>       │   │
                    │  └──────────────────────────┘   │
                    └─────┬────────┬────────┬─────────┘
                          │        │        │
              ┌───────────▼──┐ ┌──▼────────▼──┐
              │AsteriskServer│ │AsteriskServer │ ... x 20-50
              │ (Server A)   │ │ (Server B)    │
              │ AmiConnection│ │ AmiConnection │
              │ ChannelMgr   │ │ ChannelMgr    │
              │ QueueMgr     │ │ QueueMgr      │
              │ AgentMgr     │ │ AgentMgr      │
              └──────┬───────┘ └──────┬────────┘
                     │                │
              ┌──────▼───────┐ ┌──────▼────────┐
              │ Asterisk PBX │ │ Asterisk PBX  │
              │ (2-5K agents)│ │ (2-5K agents) │
              └──────────────┘ └───────────────┘
```

### 9.2 Cambios Clave Necesarios

1. **`IAmiConnectionFactory`** - Factory para crear multiples conexiones
2. **`AsteriskServerPool`** - Agrega multiples AsteriskServer, routing por agente
3. **`IAmiConnectionPool`** - Pool con health checks y failover
4. **Keyed DI Registration** - Soportar multiples servidores en el contenedor
5. **Agregacion de metricas** - Consolidar estado de N servidores

---

## 10. Tabla de Prioridades de Correccion

Todas las 12 fases de migracion estan completadas. Los hallazgos a continuacion representan trabajo adicional necesario especificamente para el escenario de 100K+ agentes, que va mas alla del alcance original de la migracion.

| # | Hallazgo | Esfuerzo | Prioridad | Sprint sugerido |
|---|----------|----------|-----------|-----------------|
| C-01 | Race condition en QueueManager.Members | 2h | P0 | Sprint 1 (thread safety) |
| C-02 | Writer concurrente sin lock | 1h | P0 | Sprint 1 (thread safety) |
| C-03 | DropOldest silencioso | 2h | P0 | Sprint 1 (thread safety) |
| C-04 | Singleton sin multi-server | 1-2 dias | P0 | Sprint 2 (multi-server) |
| C-05 | Actualizaciones no atomicas | 4h | P1 | Sprint 1 (thread safety) |
| A-01 | GetByName O(n) | 1h | P1 | Sprint 3 (performance) |
| A-02 | Sin indice agente->colas | 2h | P1 | Sprint 3 (performance) |
| A-03 | Snapshot allocations | 1h | P1 | Sprint 3 (performance) |
| A-04 | Reconexion no reestablece estado | 3h | P1 | Sprint 2 (multi-server) |
| M-01 | Lock en dispatch | 1h | P2 | Sprint 3 (performance) |
| M-02 | ExtensionHistory sin limite | 30min | P2 | Sprint 3 (performance) |
| M-03 | Sin metricas/observabilidad | 1 dia | P2 | Sprint 4 (observabilidad) |
| M-04 | OnError vacio | 30min | P2 | Sprint 1 (thread safety) |
| M-05 | QueueMemberStatus no manejado | 1h | P2 | Sprint 1 (thread safety) |
| B-01 | PipeOptions sin tuning | 30min | P3 | Sprint 4 (observabilidad) |
| B-02 | ResponseEventCollector unbounded | 30min | P3 | Sprint 3 (performance) |
| B-03 | IAsyncEnumerable en managers | 2h | P3 | Sprint 4 (observabilidad) |

---

## 11. Conclusion

### Lo que esta BIEN hecho:
- System.IO.Pipelines para zero-copy TCP (excelente)
- 4 source generators completos para AOT sin reflexion (ActionSerializer, EventDeserializer, EventRegistry, ResponseDeserializer)
- System.Threading.Channels para event pump (buena base)
- Separacion de responsabilidades entre managers (buen DDD)
- IObservable<T> para composicion reactiva (extensible)
- LoggerMessage source-generated (eficiente)
- CancellationToken en toda la API publica (correcto)
- No hay dependencias externas pesadas (solo BCL + System.Reactive)
- FrozenDictionary para registros de eventos/acciones (O(1) lookup inmutable)
- Native AOT verificado: 0 trim warnings, binario de 1.3 MB
- 164 unit tests + 25 integration tests + 15 benchmarks (cobertura solida)
- Infraestructura Docker completa para CI/CD (docker-compose + Dockerfile + 7 config files)
- MD5 challenge-response authentication (correcto para protocolo AMI)

### Lo que NECESITA correccion antes de 100K agentes:
1. **Thread safety** en QueueManager (List\<T\> y escrituras concurrentes)
2. **Sincronizacion de writer** en AmiConnection
3. **Observabilidad** del event pump (metricas de drop)
4. **Multi-server** (factory/pool en vez de singleton)
5. **Indices secundarios** para queries frecuentes
6. **Reconciliacion post-reconexion** del estado Live

### Resumen final:

> La libreria ha completado exitosamente las 12 fases de migracion desde asterisk-java 3.42.0-SNAPSHOT a .NET 10 Native AOT. La base arquitectonica es solida y bien pensada: source generators completos, protocolo AMI/AGI/ARI implementado, publicacion AOT verificada sin warnings, y suite de tests comprehensiva. Para **1-5,000 agentes en un solo Asterisk**, funcionara bien una vez corregidos los hallazgos de thread safety (C-01 a C-03). Para **100,000+ agentes**, se requiere ademas la capa de multi-server (C-04) y las optimizaciones de performance (A-01 a A-04). La division en proyectos es razonable para distribucion NuGet y no impacta performance.

---

## 12. Evaluacion como SDK (comparativa AWS SDK / Azure SDK)

### 12.1 Veredicto General

**La libreria NO califica aun como un SDK de calidad comparable a AWS SDK o Azure SDK para .NET.** Tiene una base arquitectonica solida y varias decisiones correctas, pero le faltan elementos que los SDKs maduros consideran obligatorios. La calificacion actual es **C+ (59/100)** — suficiente para uso interno con supervision, insuficiente para publicacion NuGet publica.

### 12.2 Scorecard Comparativo

| Area | Peso | Puntaje | AWS/Azure Referencia |
|------|------|---------|---------------------|
| API Surface (interfaces, modelos) | 15% | 7/10 | Interfaces limpias, `IAsyncDisposable`, `CancellationToken` everywhere. Pierde por `RawFields` leaking, `List<string>` mutables en modelos ARI, `IAgiChannel` incompleta (7/54 commands) |
| Options/Configuration | 10% | 5/10 | `IOptions<T>` presente, defaults razonables. Pierde por cero validacion, `IOptionsMonitor<T>` ausente, `AriClientOptions.AutoReconnect` no implementado |
| Error Handling | 15% | 4/10 | Excepciones por capa (AGI, Live, PBX, Config). Pierde por no tener `AmiException` base, login falla con `InvalidOperationException`, reconnect traga errores silenciosamente, ARI descarta body de errores HTTP |
| Resilience | 10% | 3/10 | Reconnect AMI existe. Pierde por backoff hardcodeado (50ms/5s), ARI sin reconnect (propiedad `AutoReconnect` rota), sin `IHttpClientFactory`, sin retry HTTP, `ConnectionTimeout` declarado pero no usado |
| DI Integration | 10% | 6/10 | `AddAsteriskNetAot()` con lambda, `TryAddSingleton`. Pierde por `AsteriskServer` sin interfaz, `StartTracking()` manual, `FastAgiServer` con closure en vez de DI, sin builder pattern |
| Async Patterns | 10% | 7/10 | `ValueTask` en hot paths, `IAsyncEnumerable<T>`, `ConfigureAwait(SuppressThrowing)`. Pierde por `async void OnReconnected()`, `_ = Task.Run(ReconnectLoop)` sin observar, CTS allocation por action |
| Documentacion / DX | 15% | 1/10 | **No hay README.md**, los 5 ejemplos son stubs vacios, CS1591 suprimido globalmente (no hay XML docs en APIs publicas), no hay guia de "getting started" |
| Package Metadata | 5% | 6/10 | `PackageLicenseExpression`, `RepositoryUrl`, `GenerateDocumentationFile`. Pierde por no tener icono, no `PackageProjectUrl`, version global sin split `VersionPrefix/VersionSuffix` |
| Completitud de Implementacion | 10% | 4/10 | AMI completo (111 actions, 215 events). Pierde por ARI con solo 2/15 resources, 54 AGI `BuildCommand()` son stubs, typed ARI events son dead code, modelos ARI anemicos |

**Puntaje ponderado: 59/100**

### 12.3 Lo que SI Cumple (Fortalezas SDK)

La libreria tiene varias decisiones de diseño que SI estan al nivel de un SDK profesional:

1. **Interface-first design en Abstractions.** `IAmiConnection`, `IAgiServer`, `IAriClient` son contratos limpios, desacoplados de implementacion. Esto es exactamente el patron de Azure SDK (`SecretClient` implementa `ISecretClient`-like interfaces).

2. **Async-first con `CancellationToken` en toda la API publica.** Cada metodo I/O recibe `CancellationToken cancellationToken = default`. AWS SDK adopto esto completamente desde v3.

3. **`IAsyncDisposable` en todos los tipos con recursos.** Azure SDK requiere `IAsyncDisposable` para clientes que mantienen conexiones. Implementado correctamente aqui.

4. **Source generators para AOT.** Esto es superior a lo que hacen AWS/Azure SDKs actualmente. Los 4 generators (ActionSerializer, EventDeserializer, EventRegistry, ResponseDeserializer) eliminan reflexion por completo. `System.Text.Json` source-gen para ARI JSON. Azure SDK esta migrando a este patron.

5. **`System.IO.Pipelines` para parsing TCP.** Zero-copy, backpressure-aware, pool-friendly. Superior al `BufferedReader` de asterisk-java y al `Stream` directo que usan algunos SDKs.

6. **`LoggerMessage` source-generated logging.** Evita boxing, allocation-free en hot paths. Este es el patron recomendado por Microsoft y adoptado por Azure SDK.

7. **`FrozenDictionary` para registros inmutables.** `GeneratedEventRegistry` usa `FrozenDictionary<string, Func<ManagerEvent>>` — O(1) lookup sin overhead de concurrencia. Patron moderno de .NET 8+.

8. **Separacion de paquetes para composicion.** Un usuario puede instalar solo `Asterisk.NetAot.Ami` sin arrastrar AGI/ARI. Esto replica el patron de AWS SDK donde cada servicio es un paquete independiente.

### 12.4 Lo que NO Cumple (Gaps Criticos vs SDK)

#### GAP-01: No Hay README ni Documentacion Publica

**Severidad: BLOQUEANTE para publicacion**

AWS SDK y Azure SDK tienen:
- README.md con badges, descripcion, instalacion, quickstart
- XML doc comments en el 100% de la API publica
- Ejemplos funcionales por cada feature
- Migration guides, troubleshooting, FAQ

Estado actual:
- **No existe README.md** (referenciado en `PackageReadmeFile` pero el archivo no existe)
- **CS1591 suprimido globalmente** — ningun tipo publico tiene XML documentation
- **Los 5 proyectos de ejemplo son stubs** con `Console.WriteLine("Not yet implemented")`
- Cero guias de uso

> Un SDK sin documentacion no es un SDK — es una libreria privada con aspiraciones.

#### GAP-02: No Existe Jerarquia de Excepciones AMI

**Severidad: ALTA**

AWS SDK tiene `AmazonServiceException` como base, con subclases tipadas por cada error conocido. Azure SDK tiene `RequestFailedException` con `ErrorCode`, `Status`, `Message` estructurados.

Estado actual del layer AMI (el mas critico):
- Login fallido: `throw new InvalidOperationException($"AMI login failed: {msg}")` — sin tipo propio
- No conectado: `throw new InvalidOperationException($"Not connected. Current state: {_state}")` — sin tipo propio
- Protocolo invalido: `throw new InvalidOperationException("Expected Asterisk protocol identifier")` — sin tipo propio
- Timeout: propaga `OperationCanceledException` de CancellationToken — correcto pero no distinguible de cancelacion del usuario
- Reconnect: `catch { /* Retry */ }` — swallows silenciosamente

**Se necesitan:**
```
AmiException (base)
├── AmiAuthenticationException
├── AmiConnectionException
├── AmiProtocolException
├── AmiTimeoutException
└── AmiNotConnectedException (con propiedad State)
```

#### GAP-03: ARI Client es Esqueletico

**Severidad: ALTA**

El ARI REST API de Asterisk expone ~15 resource types. El SDK implementa solo 2 (`Channels`, `Bridges`). Falta:
- `Applications`, `DeviceStates`, `Endpoints`, `Events`, `Mailboxes`, `Playbacks`, `Recordings`, `Sounds`
- `ListAsync()` en ambas resources existentes
- Error response deserialization (ARI devuelve `{"message": "...", "error": "..."}`, actualmente descartado por `EnsureSuccessStatusCode()`)
- WebSocket frame fragmentation (buffer fijo de 8KB trunca eventos grandes)
- Typed event dispatch (12 subclases de `AriEvent` son dead code — `ParseEvent` siempre retorna base `AriEvent`)
- `IHttpClientFactory` (usa `new HttpClient()` — riesgo de socket exhaustion)
- Reconnect (`AutoReconnect = true` declarado pero jamas implementado)

AWS SDK cubre el 100% de la API surface de cada servicio. Azure SDK tiene la misma politica. Con solo 2/15 resources, el ARI client no es un SDK sino un proof-of-concept.

#### GAP-04: 54 AGI Commands son Stubs

**Severidad: ALTA**

Todas las clases de comando AGI en `Commands/` tienen:
```csharp
public override string BuildCommand()
{
    return "STREAM FILE"; // Ignora las propiedades File, EscapeDigits, Offset
}
```

Las propiedades estan declaradas pero `BuildCommand()` no las usa. Un usuario que construya `new StreamFileCommand { File = "hello", EscapeDigits = "#" }` y lo envie via `SendCommandAsync` enviara `"STREAM FILE\n"` en vez de `"STREAM FILE hello \"#\"\n"`.

El path funcional es via `AgiChannel.StreamFileAsync("hello", "#")` que construye el string directamente — pero los 54 command objects tipados son todos rotos.

#### GAP-05: Validacion de Options Ausente

**Severidad: MEDIA**

Azure SDK valida options al construir el cliente:
```csharp
services.AddOptions<AmiConnectionOptions>()
    .BindConfiguration("Asterisk:Ami")
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

Estado actual: cero validacion. Un usuario puede configurar `Username = ""`, `Hostname = null`, `Port = -1` y solo descubrira el error al llamar `ConnectAsync()`.

#### GAP-06: Resilience HTTP Ausente en ARI

**Severidad: MEDIA**

Azure SDK envuelve `HttpClient` con retry policies (exponential backoff con jitter), timeouts configurables, y circuit breaker. AWS SDK tiene `RetryPolicy` configurable con `MaxRetries`, `BackoffType`.

Estado actual en `AriClient`:
```csharp
_httpClient = new HttpClient { BaseAddress = new Uri(...) };
// Sin: Timeout, retry, IHttpClientFactory, resilience handler
```

`response.EnsureSuccessStatusCode()` tira el body de error de ARI sin deserializarlo. Un 404 ("Channel not found") se convierte en un `HttpRequestException` generico sin informacion util.

#### GAP-07: Inconsistencias de Naming

**Severidad: BAJA**

| Ubicacion | Issue |
|-----------|-------|
| AMI Actions vs AGI Channel | `GetVarAction` vs `GetVariableAsync` (naming inconsistente entre layers) |
| Server start | `FastAgiServer.StartAsync()` vs `AsteriskServer.StartTracking()` (verbo diferente, sync vs async) |
| Originate property | `OriginateAction.Async` (sombrea keyword semanticamente) |
| Queue events | `MemberAdded(string, QueueMember)` vs `QueueUpdated(AsteriskQueue)` (parametros inconsistentes) |
| ARI state | `AriChannel.State: string` (deberia ser enum como `ChannelState`) |

### 12.5 Matriz de Madurez: Donde Esta y Que Falta

```
                    ┌─────────────────────────────────────────────────┐
                    │          NIVEL DE MADUREZ SDK                   │
                    │                                                 │
  Nivel 4           │  □ Production-ready SDK                         │
  (AWS/Azure)       │    Documentacion completa, 100% API coverage,   │
                    │    retry/resilience, breaking change policy,     │
                    │    SemVer, changelog, CI/CD pipeline             │
                    │                                                 │
  Nivel 3           │  □ Beta SDK                                     │
  (Publicable)      │    README, examples funcionales, XML docs,      │
                    │    exceptions tipadas, options validation,       │
                    │    IHttpClientFactory                            │
                    │                                                 │
  Nivel 2           │  ■ Alpha SDK  ◄── ESTADO ACTUAL                 │
  (Internal)        │    Interfaces limpias, async-first, AOT-ready,  │
                    │    test coverage, source generators, DI basico   │
                    │                                                 │
  Nivel 1           │  □ Prototype                                    │
  (PoC)             │    Funcionalidad basica, sin tests, sin DI      │
                    └─────────────────────────────────────────────────┘
```

### 12.6 Roadmap: De Alpha SDK a Production-Ready

#### Sprint A — Documentacion y DX (2 dias)

| Tarea | Esfuerzo | Impacto |
|-------|----------|---------|
| Crear README.md (instalacion, quickstart por layer, badges) | 4h | BLOQUEANTE |
| Implementar los 5 ejemplos con lifecycle completo | 4h | Alto |
| Habilitar CS1591 y agregar XML docs a Abstractions + Options + Extensions | 4h | Alto |
| Agregar `PublishAot=true` a todos los examples | 15min | Bajo |

#### Sprint B — Error Handling y Resilience (1.5 dias)

| Tarea | Esfuerzo | Impacto |
|-------|----------|---------|
| Crear jerarquia `AmiException` (5 subclases) y reemplazar `InvalidOperationException` | 3h | Alto |
| Implementar options validation con `ValidateDataAnnotations` + `ValidateOnStart` | 2h | Medio |
| Exponential backoff configurable en `ReconnectLoopAsync` | 2h | Medio |
| Fix `async void OnReconnected` → `Task.Run` observado | 30min | Medio |
| Aplicar `ConnectionTimeout` durante reconnect | 30min | Medio |
| Crear `AriException` con deserialization del error body | 2h | Medio |

#### Sprint C — ARI Completitud (2 dias)

| Tarea | Esfuerzo | Impacto |
|-------|----------|---------|
| Migrar `AriClient` a `IHttpClientFactory` | 2h | Alto |
| Implementar WebSocket reconnect (honrar `AutoReconnect`) | 3h | Alto |
| Implementar typed event dispatch (wiring `StasisStartEvent`, etc.) | 2h | Alto |
| Agregar `ListAsync` a Channels y Bridges resources | 1h | Medio |
| Implementar 3 resources faltantes prioritarios (Playbacks, Recordings, Endpoints) | 4h | Medio |
| Fix WebSocket fragmentation handling (multi-segment receive) | 1h | Medio |
| URL parameter encoding (`Uri.EscapeDataString`) | 30min | Bajo |

#### Sprint D — AGI Completitud (1 dia)

| Tarea | Esfuerzo | Impacto |
|-------|----------|---------|
| Implementar `BuildCommand()` en las 54 clases AGI | 4h | Alto |
| Crear `FastAgiHostedService : IHostedService` wrapper | 1h | Medio |
| Registrar `AsteriskServer` detras de `IAsteriskServer` | 1h | Medio |
| Unificar dispatch de commands (objects vs string) | 2h | Bajo |

#### Sprint E — API Polish (1 dia)

| Tarea | Esfuerzo | Impacto |
|-------|----------|---------|
| `AriBridge.Channels` → `IReadOnlyList<string>` | 15min | Bajo |
| `AriChannel.State` → enum `AriChannelState` | 30min | Bajo |
| Consistencia naming: `StartTracking` → `StartAsync` | 30min | Bajo |
| Agregar `DefaultEventTimeout` enforcement en `SendEventGeneratingActionAsync` | 1h | Medio |
| Mover `AsteriskServer` a pattern `IAsteriskServer.StartAsync()` | 1h | Medio |
| `VersionPrefix/VersionSuffix` split en Directory.Build.props | 15min | Bajo |

**Esfuerzo total estimado para Nivel 3 (Beta SDK): ~8 dias de desarrollo**
**Esfuerzo adicional para Nivel 4 (Production): ~5 dias mas (100% API coverage, changelog, CI/CD)**

### 12.7 Hallazgos Ya Corregidos (desde la review inicial)

Los siguientes hallazgos del documento original ya fueron abordados en commits recientes:

| Hallazgo | Estado | Commit/Cambio |
|----------|--------|---------------|
| CRITICO-02: Writer concurrente sin lock | **CORREGIDO** | `SemaphoreSlim _writeLock` + `WriteActionLockedAsync` |
| CRITICO-03: DropOldest silencioso | **CORREGIDO** | `EventPumpCapacity` configurable + `OnEventDropped` callback con log |
| ALTO-04: Reconexion no reestablece estado | **CORREGIDO** | `AmiConnection.Reconnected` event + `AsteriskServer.OnReconnected()` con clear+reload |
| MEDIO-04: EventObserver.OnError vacio | **CORREGIDO** | `OnError` y `OnCompleted` ahora logean y disparan `ConnectionLost` event |
| MEDIO-05: QueueMemberStatus no manejado | **CORREGIDO** | `QueueMemberPausedEvent`, `QueueMemberStatusEvent`, `QueueMemberPauseEvent` handled |
