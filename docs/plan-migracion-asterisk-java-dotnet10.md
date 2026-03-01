# Plan de Migracion: asterisk-java a .NET 10

## Estrategia Hibrida Completa

> **Fecha:** 2026-03-01
> **Origen:** asterisk-java 3.42.0-SNAPSHOT (Java 1.8)
> **Destino:** .NET 10 LTS (C# 14, Native AOT)
> **Nombre proyecto destino:** `Asterisk.NetAot`
> **Repositorio:** `git@github.com:Harol-Reina/Asterisk.NetAot.git`
> **Ruta local:** `/home/harol/Repositories/Sources/Asterisk.NetAot/`

---

## Estado de Progreso

> **Ultima actualizacion:** 2026-03-01

| Fase | Nombre | Estado | Commit | Archivos |
|------|--------|--------|--------|----------|
| 1 | Fundacion y Transporte | **COMPLETADA** | `6ba6569` | 7 archivos, 11 tests |
| 2 | Protocolo AMI Core | **COMPLETADA** | `4ce4c54` | 6 archivos, 13 tests |
| 3 | AMI Actions | **COMPLETADA** | `97cb59e` | 111 Actions generadas |
| 4 | AMI Events | **COMPLETADA** | `97cb59e` | 214 Events + 8 bases |
| 5 | AMI Responses e Internals | **COMPLETADA** | `42ae36a` | 17 Responses + 3 internals, 9 tests |
| 6 | FastAGI Server | **COMPLETADA** | `a5b02ec` | 54 commands + server + mapping, 17 tests |
| 7 | Live API | **COMPLETADA** | `5dc717f` | Channels/Queues/Agents/MeetMe managers, 19 tests |
| 8 | PBX Activities | **COMPLETADA** | `2b452a8` | 11 activities + modelos + state machine, 19 tests |
| 9 | Configuracion y Utilidades | **COMPLETADA** | `61e10da` | ConfigFileReader + ExtensionsConfig, 15 tests |
| 10 | ARI Client (nuevo) | **COMPLETADA** | `3ad08b1` | AriClient + Resources + source-gen JSON, 5 tests |
| 11 | Testing e Integracion | Pendiente | — | — |
| 12 | Native AOT y Optimizacion | Pendiente | — | — |

### Metricas actuales

| Metrica | Valor |
|---------|-------|
| Archivos .cs en Ami project | 364 |
| Archivos .cs en Agi project | 68 |
| Archivos .cs en Live project | 8 |
| Archivos .cs en Pbx project | 19 |
| Archivos .cs en Config project | 2 |
| Archivos .cs en Ari project | 7 |
| Tests unitarios | 108 (33 AMI + 17 AGI + 19 Live + 19 PBX + 15 Config + 5 ARI) |
| Build | 0 warnings, 0 errors |
| Commits | 10 |

---

## Tabla de Contenido

1. [Resumen Ejecutivo](#1-resumen-ejecutivo)
2. [Arquitectura Destino](#2-arquitectura-destino)
3. [Fases del Proyecto](#3-fases-del-proyecto)
4. [Fase 1 - Fundacion y Transporte](#fase-1---fundacion-y-transporte)
5. [Fase 2 - Protocolo AMI Core](#fase-2---protocolo-ami-core)
6. [Fase 3 - AMI Actions (115 clases)](#fase-3---ami-actions-115-clases)
7. [Fase 4 - AMI Events (235 clases)](#fase-4---ami-events-235-clases)
8. [Fase 5 - AMI Responses e Internals](#fase-5---ami-responses-e-internals)
9. [Fase 6 - FastAGI Server](#fase-6---fastagi-server)
10. [Fase 7 - Live API (Domain Objects)](#fase-7---live-api-domain-objects)
11. [Fase 8 - PBX Activities](#fase-8---pbx-activities)
12. [Fase 9 - Configuracion y Utilidades](#fase-9---configuracion-y-utilidades)
13. [Fase 10 - ARI Client (nueva funcionalidad)](#fase-10---ari-client-nueva-funcionalidad)
14. [Fase 11 - Integracion, Testing y Documentacion](#fase-11---integracion-testing-y-documentacion)
15. [Fase 12 - Native AOT y Optimizacion](#fase-12---native-aot-y-optimizacion)
16. [Mapeo de Tecnologias](#mapeo-de-tecnologias)
17. [Estructura del Proyecto .NET](#estructura-del-proyecto-net)
18. [Riesgos y Mitigaciones](#riesgos-y-mitigaciones)
19. [Criterios de Aceptacion](#criterios-de-aceptacion)

---

## 1. Resumen Ejecutivo

Migracion completa de la libreria asterisk-java (790+ clases Java) a .NET 10, aprovechando:

- **System.IO.Pipelines** para parsing TCP zero-copy del protocolo AMI/AGI
- **Source Generators** para reemplazar la reflexion en serializacion/deserializacion (AOT-compatible)
- **async/await** nativo en toda la capa de I/O y eventos
- **System.Threading.Channels** para el event pump asincronico
- **Native AOT** para deploy en contenedores Docker ultra-livianos (< 5 MB)
- **ARI (Asterisk REST Interface)** como funcionalidad nueva no presente en asterisk-java

### Metricas del proyecto

| Metrica | Valor |
|---------|-------|
| Clases Java origen | ~790 |
| AMI Actions | 115 (+ 27 abstractas/interfaces) |
| AMI Events | 235 (+ 98 abstractas/interfaces) |
| AMI Responses | 18 |
| AMI Internals | 21 |
| FastAGI | 95 |
| Live API | 55 |
| PBX Activities | 82 |
| Config/Util | 41 |
| Fases | 12 |

---

## 2. Arquitectura Destino

```
Asterisk.NetAot/
├── Asterisk.NetAot.sln
├── Directory.Build.props
├── Directory.Packages.props
│
├── src/                                          <── Todo el codigo fuente
│   ├── Asterisk.NetAot.Abstractions/             <-- Interfaces, contratos, enums
│   ├── Asterisk.NetAot.Ami/                      <-- Protocolo AMI completo
│   │   ├── Actions/                              <-- 115 action classes
│   │   ├── Events/                               <-- 235 event classes
│   │   ├── Responses/                            <-- 18 response classes
│   │   ├── Internal/                             <-- Parser, Writer, Dispatcher
│   │   ├── Connection/                           <-- ManagerConnection, reconnect
│   │   └── Transport/                            <-- Socket Pipelines
│   ├── Asterisk.NetAot.Ami.SourceGenerators/     <-- Generadores compile-time
│   ├── Asterisk.NetAot.Agi/                      <-- FastAGI server + AsyncAGI
│   │   ├── Commands/                             <-- 54+ AGI commands
│   │   ├── Mapping/                              <-- Script mapping strategies
│   │   └── Server/                               <-- TCP server async
│   ├── Asterisk.NetAot.Live/                     <-- Domain objects con estado
│   │   ├── Channels/                             <-- AsteriskChannel tracking
│   │   ├── Queues/                               <-- AsteriskQueue tracking
│   │   ├── Agents/                               <-- AsteriskAgent tracking
│   │   ├── MeetMe/                               <-- Conferencias
│   │   └── Server/                               <-- AsteriskServer aggregate root
│   ├── Asterisk.NetAot.Pbx/                      <-- Actividades de alto nivel
│   │   ├── Activities/                           <-- Dial, Hold, Transfer, Park...
│   │   ├── Agi/                                  <-- AGI channel activities
│   │   └── Models/                               <-- Call, Channel, EndPoint, Tech
│   ├── Asterisk.NetAot.Ari/                      <-- REST + WebSocket (NUEVO)
│   │   ├── Client/                               <-- HTTP client
│   │   ├── Events/                               <-- WebSocket event stream
│   │   └── Models/                               <-- ARI resource models
│   ├── Asterisk.NetAot.Config/                   <-- Parsing de archivos Asterisk
│   └── Asterisk.NetAot/                          <-- Meta-package
│
├── Tests/                                        <── Tests en raiz del proyecto
│   ├── Asterisk.NetAot.Ami.Tests/
│   ├── Asterisk.NetAot.Agi.Tests/
│   ├── Asterisk.NetAot.Live.Tests/
│   ├── Asterisk.NetAot.Pbx.Tests/
│   ├── Asterisk.NetAot.Ari.Tests/
│   ├── Asterisk.NetAot.Config.Tests/
│   ├── Asterisk.NetAot.IntegrationTests/
│   └── Asterisk.NetAot.Benchmarks/
│
├── Examples/                                     <── Ejemplos en raiz del proyecto
│   ├── BasicAmiExample/
│   ├── FastAgiServerExample/
│   ├── LiveApiExample/
│   ├── PbxActivitiesExample/
│   └── AriStasisExample/
│
└── docker/
    ├── docker-compose.test.yml
    └── test-config/
```

### Principios de diseno

1. **Async-first**: Toda operacion de I/O es `async Task` o `ValueTask`
2. **AOT-compatible**: Zero reflexion en runtime; todo via source generators y attributes
3. **DI-friendly**: Todas las dependencias via `IServiceCollection` / constructor injection
4. **Observable**: Eventos via `IObservable<T>` para integracion con Reactive Extensions
5. **Cancellable**: Todos los metodos async aceptan `CancellationToken`
6. **Testable**: Interfaces para todas las dependencias externas (sockets, timers)

---

## 3. Fases del Proyecto

| Fase | Nombre | Dependencias | Clases |
|------|--------|-------------|--------|
| 1 | Fundacion y Transporte | Ninguna | ~15 |
| 2 | Protocolo AMI Core | Fase 1 | ~25 |
| 3 | AMI Actions | Fase 2 | 142 |
| 4 | AMI Events | Fase 2 | 235 |
| 5 | AMI Responses e Internals | Fases 3, 4 | 39 |
| 6 | FastAGI Server | Fase 1 | 95 |
| 7 | Live API | Fases 3, 4, 5 | 55 |
| 8 | PBX Activities | Fases 6, 7 | 82 |
| 9 | Configuracion y Utilidades | Fase 1 | 41 |
| 10 | ARI Client (nuevo) | Fase 1 | ~30 (nuevo) |
| 11 | Testing e Integracion | Todas | ~100 |
| 12 | Native AOT y Optimizacion | Todas | Transversal |

### Diagrama de dependencias

```
Fase 1 (Fundacion)
  |
  +---> Fase 2 (AMI Core)
  |       |
  |       +---> Fase 3 (Actions)  --+
  |       |                          |
  |       +---> Fase 4 (Events)   --+--> Fase 5 (Responses) --> Fase 7 (Live) --> Fase 8 (PBX)
  |
  +---> Fase 6 (FastAGI) ------------------------------------------> Fase 8 (PBX)
  |
  +---> Fase 9 (Config/Util)
  |
  +---> Fase 10 (ARI)
  |
  Todas ---> Fase 11 (Testing) ---> Fase 12 (AOT)
```

> **Nota:** Las fases 3 y 4 pueden ejecutarse en paralelo. Las fases 6, 9 y 10 pueden ejecutarse en paralelo con las fases 3-5.

---

## Fase 1 - Fundacion y Transporte ✅

### Objetivo
Crear la estructura de la solucion, definir abstracciones base y la capa de transporte TCP con `System.IO.Pipelines`.

### Tareas

- [x] Crear solucion `Asterisk.NetAot.sln` con todos los proyectos (22 proyectos)
- [x] Configurar `Directory.Build.props` con target `net10.0`, nullable, AOT hints
- [x] Definir interfaces de transporte en `Asterisk.NetAot.Abstractions`
- [x] Implementar `PipelineSocketConnection` con `System.IO.Pipelines` (pump bidireccional)
- [x] Implementar `AsyncServerSocket` para AGI server
- [x] Soporte SSL/TLS via `SslStream`
- [x] Definir attributes base: `[AsteriskMapping]`, `[AsteriskVersion]`
- [x] `ISocketConnectionFactory` + `PipelineSocketConnectionFactory`
- [x] `FromStream()` para wrappear streams existentes (AGI accepted connections)
- [x] 11 tests unitarios de transporte (todos pasando)

### Clases a crear (mapeo desde Java)

| Java (util/) | .NET | Notas |
|--------------|------|-------|
| `SocketConnectionFacade` | `ISocketConnection` | Interface |
| `SocketConnectionFacadeImpl` | `PipelineSocketConnection` | Impl con Pipelines |
| `ServerSocketFacade` | `IServerSocket` | Interface |
| `ServerSocketFacadeImpl` | `AsyncServerSocket` | Impl con TcpListener async |
| `DaemonThreadFactory` | No aplica | .NET Task scheduler maneja esto |
| `Log` / `LogFactory` | `ILogger<T>` | Microsoft.Extensions.Logging |
| `Log4JLogger` | No aplica | Usar Serilog o built-in |
| `Slf4JLogger` | No aplica | Usar `ILogger<T>` |
| `NullLog` | `NullLogger<T>` | Ya existe en .NET |
| `JavaLoggingLog` | No aplica | - |
| `FileTrace` | `ILogger` con file sink | Serilog.Sinks.File |
| `Base64` | `Convert.ToBase64String` | BCL built-in |
| `DateUtil` | `DateTimeOffset` helpers | Extension methods |
| `AstUtil` | `AsteriskUtilities` | Static helper class |
| `AstState` | `AsteriskDeviceState` | Enum |
| `MixMonitorDirection` | `MixMonitorDirection` | Enum |
| `ReflectionUtil` | No aplica | Reemplazado por source generators |
| `FastScanner` / variantes | No aplica | Reemplazado por `PipeReader` |

### Entregables
- [x] Solucion compilable con 22 proyectos (9 src, 8 tests, 5 examples)
- [x] Capa de transporte TCP funcional con 11 tests unitarios
- [ ] Benchmark de throughput vs `BufferedReader` Java (pendiente Fase 12)

---

## Fase 2 - Protocolo AMI Core ✅

### Objetivo
Implementar el parser/writer del protocolo AMI texto plano y el sistema de dispatch de eventos.

### Protocolo AMI (referencia)

```
-- Accion (cliente -> Asterisk) --
Action: Originate\r\n
Channel: SIP/2000\r\n
Context: default\r\n
Exten: 1234\r\n
Priority: 1\r\n
ActionID: abc123\r\n
\r\n

-- Respuesta (Asterisk -> cliente) --
Response: Success\r\n
ActionID: abc123\r\n
Message: Originate successfully queued\r\n
\r\n

-- Evento (Asterisk -> cliente) --
Event: Newchannel\r\n
Channel: SIP/2000-00000001\r\n
Uniqueid: 1234567890.1\r\n
\r\n
```

### Tareas

- [x] Implementar `AmiProtocolReader` usando `PipeReader` (zero-copy con `SequenceReader<byte>`)
- [x] Implementar `AmiProtocolWriter` usando `PipeWriter` (zero-copy con `GetSpan`/`Advance`)
- [ ] Crear `IEventBuilder` con source generator para mapeo Event -> clase C# (pendiente Fase 12)
- [ ] Crear `IActionBuilder` con source generator para mapeo clase C# -> texto AMI (pendiente Fase 12)
- [x] Implementar `AmiMessage` con soporte para Response, Event, ProtocolIdentifier, CommandOutput
- [x] Implementar `AsyncEventPump` con `Channel<ManagerEvent>` (capacity: 20,000)
- [x] Implementar `ResponseEventCollector` para event-generating actions
- [x] Implementar `AmiConnection` con maquina de estados async (Initial->Connecting->Connected->Reconnecting->Disconnected)
- [x] Autenticacion MD5 challenge-response
- [x] Reconexion automatica con backoff exponencial (50ms x10, luego 5s)
- [x] Deteccion de version de Asterisk via CoreSettings + fallback CLI
- [x] Action/Response correlation via `ConcurrentDictionary<actionId, TCS>`
- [x] Event streaming via `IObservable<ManagerEvent>` + `event Func<ManagerEvent, ValueTask>`
- [x] 13 tests unitarios (8 reader + 5 writer)

### Clases a crear (mapeo desde Java)

| Java (manager/internal/) | .NET | Notas |
|--------------------------|------|-------|
| `ManagerConnectionImpl` | `AmiConnection` | Async state machine |
| `ManagerReaderImpl` | `AmiProtocolReader` | `PipeReader` based |
| `ManagerWriterImpl` | `AmiProtocolWriter` | `PipeWriter` based |
| `EventBuilderImpl` | `EventDeserializer` | Source generated |
| `ActionBuilderImpl` | `ActionSerializer` | Source generated |
| `ResponseBuilderImpl` | `ResponseDeserializer` | Source generated |
| `AsyncEventPump` | `AsyncEventPump<T>` | `Channel<T>` based |
| `Dispatcher` | `IEventDispatcher` | Interface |
| `ManagerUtil` | `AmiUtilities` | ActionId helpers |
| `ProtocolIdentifierWrapper` | `ProtocolIdentifier` | Record struct |
| `ResponseEventsImpl` | `ResponseEventCollector` | Async collection |
| `BackwardsCompatibilityForManagerEvents` | `LegacyEventAdapter` | Compat shims |
| `BridgeState` | `BridgeState` | Enum/record |
| `BridgesActive` | `ActiveBridgeTracker` | Concurrent state |
| `BridgeEnterEventComparator` | `BridgeEnterComparer` | IComparer<T> |
| `MeetmeCompatibility` | `MeetmeCompatibility` | Compat shims |

### Interfaces publicas principales

```csharp
// Conexion AMI
public interface IAmiConnection : IAsyncDisposable
{
    AmiConnectionState State { get; }
    ValueTask ConnectAsync(CancellationToken ct = default);
    ValueTask<ManagerResponse> SendActionAsync(ManagerAction action, CancellationToken ct = default);
    ValueTask<T> SendActionAsync<T>(ManagerAction action, CancellationToken ct = default) where T : ManagerResponse;
    IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(EventGeneratingAction action, CancellationToken ct = default);
    IDisposable Subscribe(IObserver<ManagerEvent> observer);
    event Func<ManagerEvent, ValueTask>? OnEvent;
    ValueTask DisconnectAsync(CancellationToken ct = default);
}

// Builder (source-generated)
[AttributeUsage(AttributeTargets.Class)]
public class AsteriskMappingAttribute : Attribute
{
    public string Name { get; }
    public string? SinceVersion { get; }
}
```

### Source Generator: `AmiSerializerGenerator`

```csharp
// Input: clase decorada con attributes
[AsteriskMapping("Originate")]
public sealed class OriginateAction : ManagerAction
{
    [AsteriskMapping("Channel")]
    public string? Channel { get; set; }

    [AsteriskMapping("Context")]
    public string? Context { get; set; }

    [AsteriskMapping("Exten")]
    public string? Exten { get; set; }
}

// Output (generado en compile-time):
// - Serializer: OriginateAction -> texto AMI
// - Deserializer: texto AMI -> OriginateAction (para UserEventAction responses)
// - Registry: Dictionary<string, Func<ManagerEvent>> sin reflexion
```

### Entregables
- [x] Conexion AMI funcional con login/logoff (MD5 challenge-response)
- [x] Envio de acciones y recepcion de respuestas (correlacion por ActionId)
- [x] Event pump funcionando con `Channel<T>` (AsyncEventPump)
- [ ] Source generator base para Actions/Events (pendiente Fase 12)
- [x] Tests: 13 tests de protocolo reader/writer (todos pasando)

---

## Fase 3 - AMI Actions (115 clases) ✅

### Objetivo
Portar todas las 115 clases de acciones AMI como POCOs con `[AsteriskMapping]` attributes.

### Jerarquia de clases

```
ManagerAction (abstracta)
├── AbstractManagerAction (base concreta)
├── EventGeneratingAction (interface/marker)
└── 115 acciones concretas
```

### Inventario completo de Actions

#### Acciones abstractas/base

| Clase Java | Clase .NET | Descripcion |
|------------|-----------|-------------|
| `ManagerAction` | `ManagerAction` | Interface/base contract |
| `AbstractManagerAction` | `ManagerAction` (abstract class) | Base con ActionId, variables |
| `EventGeneratingAction` | `IEventGeneratingAction` | Marker interface |
| `VariableInheritance` | `VariableInheritance` | Enum |

#### Call Control (Originate, Hangup, Redirect)

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 1 | `OriginateAction` | `OriginateAction` | Iniciar llamada saliente |
| 2 | `HangupAction` | `HangupAction` | Colgar canal (soporta regex) |
| 3 | `RedirectAction` | `RedirectAction` | Redirigir canal a otro contexto/extension |
| 4 | `AtxferAction` | `AttendedTransferAction` | Transferencia atendida |
| 5 | `BridgeAction` | `BridgeAction` | Puentear dos canales |
| 6 | `ParkAction` | `ParkAction` | Estacionar llamada |
| 7 | `PlayDtmfAction` | `PlayDtmfAction` | Enviar DTMF a canal |
| 8 | `AbsoluteTimeoutAction` | `AbsoluteTimeoutAction` | Timeout absoluto en canal |
| 9 | `LocalOptimizeAwayAction` | `LocalOptimizeAwayAction` | Optimizar canales Local |
| 10 | `SendTextAction` | `SendTextAction` | Enviar texto a canal |
| 11 | `MuteAudioAction` | `MuteAudioAction` | Silenciar audio de canal |
| 12 | `ExecAction` | `ExecAction` | Ejecutar aplicacion de dialplan |

#### Queue Management

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 13 | `QueueAddAction` | `QueueAddAction` | Agregar miembro a cola |
| 14 | `QueueRemoveAction` | `QueueRemoveAction` | Remover miembro de cola |
| 15 | `QueuePauseAction` | `QueuePauseAction` | Pausar/despausar miembro |
| 16 | `QueueStatusAction` | `QueueStatusAction` | Estado de colas (event-generating) |
| 17 | `QueueSummaryAction` | `QueueSummaryAction` | Resumen de colas (event-generating) |
| 18 | `QueuePenaltyAction` | `QueuePenaltyAction` | Cambiar penalidad de miembro |
| 19 | `QueueResetAction` | `QueueResetAction` | Resetear estadisticas |
| 20 | `QueueLogAction` | `QueueLogAction` | Escribir en queue_log |
| 21 | `QueueMemberRingInUseAction` | `QueueMemberRingInUseAction` | Configurar ring-in-use |
| 22 | `QueueChangePriorityCallerAction` | `QueueChangePriorityCallerAction` | Cambiar prioridad de llamante |

#### Agent Management

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 23 | `AgentCallbackLoginAction` | `AgentCallbackLoginAction` | Login agente con callback |
| 24 | `AgentLogoffAction` | `AgentLogoffAction` | Logoff de agente |
| 25 | `AgentsAction` | `AgentsAction` | Listar agentes (event-generating) |

#### Monitoring / Recording

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 26 | `MonitorAction` | `MonitorAction` | Iniciar grabacion |
| 27 | `ChangeMonitorAction` | `ChangeMonitorAction` | Cambiar archivo de grabacion |
| 28 | `StopMonitorAction` | `StopMonitorAction` | Detener grabacion |
| 29 | `PauseMonitorAction` | `PauseMonitorAction` | Pausar grabacion |
| 30 | `UnpauseMonitorAction` | `UnpauseMonitorAction` | Reanudar grabacion |
| 31 | `MixMonitorAction` | `MixMonitorAction` | Iniciar MixMonitor |
| 32 | `MixMonitorMuteAction` | `MixMonitorMuteAction` | Silenciar MixMonitor |
| 33 | `PauseMixMonitorAction` | `PauseMixMonitorAction` | Pausar MixMonitor |
| 34 | `StopMixMonitorAction` | `StopMixMonitorAction` | Detener MixMonitor |

#### Authentication / Session

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 35 | `LoginAction` | `LoginAction` | Login al AMI |
| 36 | `LogoffAction` | `LogoffAction` | Logoff del AMI |
| 37 | `ChallengeAction` | `ChallengeAction` | Solicitar challenge MD5 |
| 38 | `PingAction` | `PingAction` | Ping keepalive |
| 39 | `EventsAction` | `EventsAction` | Filtrar eventos recibidos |
| 40 | `FilterAction` | `FilterAction` | Filtro avanzado de eventos |

#### Server / Core

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 41 | `CoreSettingsAction` | `CoreSettingsAction` | Configuracion del core |
| 42 | `CoreStatusAction` | `CoreStatusAction` | Estado del core |
| 43 | `CoreShowChannelsAction` | `CoreShowChannelsAction` | Listar canales (event-generating) |
| 44 | `StatusAction` | `StatusAction` | Estado de canal (event-generating) |
| 45 | `CommandAction` | `CommandAction` | Ejecutar comando CLI |
| 46 | `ListCommandsAction` | `ListCommandsAction` | Listar comandos disponibles |
| 47 | `ModuleCheckAction` | `ModuleCheckAction` | Verificar modulo cargado |
| 48 | `ModuleLoadAction` | `ModuleLoadAction` | Cargar/recargar modulo |

#### Variables

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 49 | `GetVarAction` | `GetVarAction` | Obtener variable de canal |
| 50 | `SetVarAction` | `SetVarAction` | Establecer variable de canal |
| 51 | `GetConfigAction` | `GetConfigAction` | Leer archivo de configuracion |
| 52 | `UpdateConfigAction` | `UpdateConfigAction` | Modificar configuracion |

#### Database (AstDB)

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 53 | `DbGetAction` | `DbGetAction` | Leer valor de AstDB |
| 54 | `DbPutAction` | `DbPutAction` | Escribir valor en AstDB |
| 55 | `DbDelAction` | `DbDelAction` | Eliminar clave de AstDB |
| 56 | `DbDelTreeAction` | `DbDelTreeAction` | Eliminar familia de AstDB |

#### ConfBridge (Conferencias)

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 57 | `ConfbridgeListAction` | `ConfbridgeListAction` | Listar participantes (event-gen) |
| 58 | `ConfbridgeListRoomsAction` | `ConfbridgeListRoomsAction` | Listar salas (event-gen) |
| 59 | `ConfbridgeKickAction` | `ConfbridgeKickAction` | Expulsar participante |
| 60 | `ConfbridgeLockAction` | `ConfbridgeLockAction` | Bloquear sala |
| 61 | `ConfbridgeUnlockAction` | `ConfbridgeUnlockAction` | Desbloquear sala |
| 62 | `ConfbridgeMuteAction` | `ConfbridgeMuteAction` | Silenciar participante |
| 63 | `ConfbridgeUnmuteAction` | `ConfbridgeUnmuteAction` | Des-silenciar participante |
| 64 | `ConfbridgeSetSingleVideoSrcAction` | `ConfbridgeSetSingleVideoSrcAction` | Fuente de video unica |
| 65 | `ConfbridgeStartRecordAction` | `ConfbridgeStartRecordAction` | Iniciar grabacion de sala |
| 66 | `ConfbridgeStopRecordAction` | `ConfbridgeStopRecordAction` | Detener grabacion de sala |

#### MeetMe (Conferencias legacy)

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 67 | `MeetMeMuteAction` | `MeetMeMuteAction` | Silenciar en MeetMe |
| 68 | `MeetMeUnmuteAction` | `MeetMeUnmuteAction` | Des-silenciar en MeetMe |
| 69 | `AbstractMeetMeMuteAction` | `MeetMeMuteActionBase` | Base abstracta |

#### SIP / PJSIP

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 70 | `SipPeersAction` | `SipPeersAction` | Listar peers SIP (event-gen) |
| 71 | `SipShowPeerAction` | `SipShowPeerAction` | Detalle de peer SIP |
| 72 | `SipShowRegistryAction` | `SipShowRegistryAction` | Registros SIP |
| 73 | `SipNotifyAction` | `SipNotifyAction` | Enviar SIP NOTIFY |
| 74 | `PJSipShowEndpointsAction` | `PjSipShowEndpointsAction` | Listar endpoints PJSIP |
| 75 | `PJSipShowEndpointAction` | `PjSipShowEndpointAction` | Detalle de endpoint PJSIP |
| 76 | `PJSipShowContactsAction` | `PjSipShowContactsAction` | Contactos PJSIP |
| 77 | `PJSIPNotifyAction` | `PjSipNotifyAction` | PJSIP NOTIFY |

#### IAX

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 78 | `IaxPeerListAction` | `IaxPeerListAction` | Listar peers IAX (event-gen) |

#### Mailbox / Voicemail

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 79 | `MailboxCountAction` | `MailboxCountAction` | Contar mensajes |
| 80 | `MailboxStatusAction` | `MailboxStatusAction` | Estado de buzon |
| 81 | `VoicemailUsersListAction` | `VoicemailUsersListAction` | Listar usuarios voicemail |
| 82 | `MWIUpdateAction` | `MwiUpdateAction` | Actualizar indicador MWI |
| 83 | `MWIDeleteAction` | `MwiDeleteAction` | Eliminar MWI |

#### FAX

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 84 | `FaxLicenseListAction` | `FaxLicenseListAction` | Listar licencias FAX |
| 85 | `FaxLicenseStatusAction` | `FaxLicenseStatusAction` | Estado de licencia FAX |

#### CDR

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 86 | `SetCdrUserFieldAction` | `SetCdrUserFieldAction` | Campo personalizado CDR |

#### AGI

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 87 | `AgiAction` | `AgiAction` | Ejecutar comando AGI en canal |

#### Dialplan

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 88 | `ShowDialplanAction` | `ShowDialplanAction` | Mostrar dialplan (event-gen) |
| 89 | `ExtensionStateAction` | `ExtensionStateAction` | Estado de extension |

#### Messaging

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 90 | `MessageSendAction` | `MessageSendAction` | Enviar mensaje SIP/PJSIP |
| 91 | `JabberSendAction` | `JabberSendAction` | Enviar mensaje XMPP |

#### User Events

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 92 | `UserEventAction` | `UserEventAction` | Disparar evento personalizado |

#### Parked Calls

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 93 | `ParkedCallsAction` | `ParkedCallsAction` | Listar llamadas estacionadas |

#### DAHDI (Hardware)

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 94 | `DahdiShowChannelsAction` | `DahdiShowChannelsAction` | Listar canales DAHDI |

#### Zap (Legacy Hardware)

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 95 | `ZapShowChannelsAction` | `ZapShowChannelsAction` | Listar canales Zap |
| 96 | `ZapDialOffhookAction` | `ZapDialOffhookAction` | Marcar offhook Zap |
| 97 | `ZapHangupAction` | `ZapHangupAction` | Colgar canal Zap |
| 98 | `ZapTransferAction` | `ZapTransferAction` | Transferir Zap |
| 99 | `ZapRestartAction` | `ZapRestartAction` | Reiniciar Zap |
| 100 | `ZapDndOnAction` | `ZapDndOnAction` | DND on Zap |
| 101 | `ZapDndOffAction` | `ZapDndOffAction` | DND off Zap |

#### Dongle (GSM)

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 102 | `DongleSendSMSAction` | `DongleSendSmsAction` | Enviar SMS via dongle |
| 103 | `DongleShowDevicesAction` | `DongleShowDevicesAction` | Listar dongles GSM |

#### Skype (Legacy Plugin)

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 104 | `SkypeAccountPropertyAction` | `SkypeAccountPropertyAction` | Propiedad de cuenta Skype |
| 105 | `SkypeAddBuddyAction` | `SkypeAddBuddyAction` | Agregar contacto Skype |
| 106 | `SkypeRemoveBuddyAction` | `SkypeRemoveBuddyAction` | Remover contacto Skype |
| 107 | `SkypeBuddiesAction` | `SkypeBuddiesAction` | Listar contactos Skype |
| 108 | `SkypeBuddyAction` | `SkypeBuddyAction` | Detalle contacto Skype |
| 109 | `SkypeChatSendAction` | `SkypeChatSendAction` | Enviar chat Skype |
| 110 | `SkypeLicenseListAction` | `SkypeLicenseListAction` | Licencias Skype |
| 111 | `SkypeLicenseStatusAction` | `SkypeLicenseStatusAction` | Estado licencia Skype |

### Entregables
- [x] 111 clases Action generadas automaticamente desde asterisk-java source
- [x] `IEventGeneratingAction` marker interface
- [x] Script generador: `tools/generate-pocos.sh`
- [ ] Source generator generando serializers para cada Action (pendiente Fase 12)
- [ ] Tests unitarios de serializacion para cada categoria (pendiente Fase 11)

---

## Fase 4 - AMI Events (235 clases) ✅

### Objetivo
Portar todas las 235 clases de eventos AMI como POCOs con `[AsteriskMapping]` attributes y source-generated deserializers.

### Jerarquia de clases

```
ManagerEvent (abstracta)
├── AbstractChannelEvent
│   ├── AbstractChannelStateEvent
│   ├── AbstractChannelTalkingEvent
│   ├── AbstractHoldEvent
│   ├── AbstractMonitorEvent
│   ├── AbstractMixMonitorEvent
│   └── ... (muchos eventos de canal)
├── AbstractBridgeEvent
├── AbstractConfbridgeEvent
├── AbstractMeetMeEvent
├── AbstractQueueMemberEvent
├── AbstractAgentEvent
├── AbstractParkedCallEvent
├── AbstractUnParkedEvent
├── AbstractRtcpEvent
├── AbstractRtpStatEvent
├── AbstractSecurityEvent
├── AbstractFaxEvent
├── ResponseEvent
└── UserEvent
```

### Inventario completo de Events

#### Eventos abstractos/base

| Clase Java | Clase .NET | Descripcion |
|------------|-----------|-------------|
| `ManagerEvent` | `ManagerEvent` | Base de todos los eventos |
| `ResponseEvent` | `ResponseEvent` | Base para eventos que responden a acciones |
| `UserEvent` | `UserEvent` | Base para eventos personalizados |
| `AbstractAgentEvent` | `AgentEventBase` | Base eventos de agente |
| `AbstractBridgeEvent` | `BridgeEventBase` | Base eventos de bridge |
| `AbstractChannelEvent` | `ChannelEventBase` | Base eventos de canal |
| `AbstractChannelStateEvent` | `ChannelStateEventBase` | Base estados de canal |
| `AbstractChannelTalkingEvent` | `ChannelTalkingEventBase` | Base talking |
| `AbstractConfbridgeEvent` | `ConfbridgeEventBase` | Base ConfBridge |
| `AbstractFaxEvent` | `FaxEventBase` | Base FAX |
| `AbstractHoldEvent` | `HoldEventBase` | Base hold/unhold |
| `AbstractMeetMeEvent` | `MeetMeEventBase` | Base MeetMe |
| `AbstractMixMonitorEvent` | `MixMonitorEventBase` | Base MixMonitor |
| `AbstractMonitorEvent` | `MonitorEventBase` | Base Monitor |
| `AbstractParkedCallEvent` | `ParkedCallEventBase` | Base parking |
| `AbstractQueueMemberEvent` | `QueueMemberEventBase` | Base miembros de cola |
| `AbstractRtcpEvent` | `RtcpEventBase` | Base RTCP |
| `AbstractRtpStatEvent` | `RtpStatEventBase` | Base RTP stats |
| `AbstractSecurityEvent` | `SecurityEventBase` | Base seguridad |
| `AbstractUnParkedEvent` | `UnParkedEventBase` | Base unpark |

#### Channel Events (ciclo de vida de canales)

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 1 | `NewChannelEvent` | `NewChannelEvent` |
| 2 | `NewStateEvent` | `NewStateEvent` |
| 3 | `NewExtenEvent` | `NewExtenEvent` |
| 4 | `NewCallerIdEvent` | `NewCallerIdEvent` |
| 5 | `NewConnectedLineEvent` | `NewConnectedLineEvent` |
| 6 | `NewAccountCodeEvent` | `NewAccountCodeEvent` |
| 7 | `HangupEvent` | `HangupEvent` |
| 8 | `HangupRequestEvent` | `HangupRequestEvent` |
| 9 | `SoftHangupRequestEvent` | `SoftHangupRequestEvent` |
| 10 | `HangupHandlerPushEvent` | `HangupHandlerPushEvent` |
| 11 | `HangupHandlerRunEvent` | `HangupHandlerRunEvent` |
| 12 | `ChannelHungupEvent` | `ChannelHungupEvent` |
| 13 | `ChannelsHungupListComplete` | `ChannelsHungupListCompleteEvent` |
| 14 | `ChannelReloadEvent` | `ChannelReloadEvent` |
| 15 | `ChannelUpdateEvent` | `ChannelUpdateEvent` |
| 16 | `ChannelTalkingStartEvent` | `ChannelTalkingStartEvent` |
| 17 | `ChannelTalkingStopEvent` | `ChannelTalkingStopEvent` |
| 18 | `RenameEvent` | `RenameEvent` |
| 19 | `MasqueradeEvent` | `MasqueradeEvent` |
| 20 | `VarSetEvent` | `VarSetEvent` |

#### Dial Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 21 | `DialEvent` | `DialEvent` |
| 22 | `DialBeginEvent` | `DialBeginEvent` |
| 23 | `DialEndEvent` | `DialEndEvent` |
| 24 | `DialStateEvent` | `DialStateEvent` |

#### Bridge Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 25 | `BridgeEvent` | `BridgeEvent` |
| 26 | `BridgeCreateEvent` | `BridgeCreateEvent` |
| 27 | `BridgeDestroyEvent` | `BridgeDestroyEvent` |
| 28 | `BridgeEnterEvent` | `BridgeEnterEvent` |
| 29 | `BridgeLeaveEvent` | `BridgeLeaveEvent` |
| 30 | `BridgeMergeEvent` | `BridgeMergeEvent` |
| 31 | `BridgeExecEvent` | `BridgeExecEvent` |
| 32 | `BridgeVideoSourceUpdateEvent` | `BridgeVideoSourceUpdateEvent` |
| 33 | `LinkEvent` | `LinkEvent` |
| 34 | `UnlinkEvent` | `UnlinkEvent` |
| 35 | `LocalBridgeEvent` | `LocalBridgeEvent` |
| 36 | `LocalOptimizationBeginEvent` | `LocalOptimizationBeginEvent` |
| 37 | `LocalOptimizationEndEvent` | `LocalOptimizationEndEvent` |

#### Transfer Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 38 | `TransferEvent` | `TransferEvent` |
| 39 | `AttendedTransferEvent` | `AttendedTransferEvent` |
| 40 | `BlindTransferEvent` | `BlindTransferEvent` |

#### Queue Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 41 | `QueueEvent` | `QueueEvent` |
| 42 | `QueueCallerJoinEvent` | `QueueCallerJoinEvent` |
| 43 | `QueueCallerLeaveEvent` | `QueueCallerLeaveEvent` |
| 44 | `QueueCallerAbandonEvent` | `QueueCallerAbandonEvent` |
| 45 | `QueueEntryEvent` | `QueueEntryEvent` |
| 46 | `QueueParamsEvent` | `QueueParamsEvent` |
| 47 | `QueueMemberEvent` | `QueueMemberEvent` |
| 48 | `QueueMemberAddedEvent` | `QueueMemberAddedEvent` |
| 49 | `QueueMemberRemovedEvent` | `QueueMemberRemovedEvent` |
| 50 | `QueueMemberStatusEvent` | `QueueMemberStatusEvent` |
| 51 | `QueueMemberPauseEvent` | `QueueMemberPauseEvent` |
| 52 | `QueueMemberPausedEvent` | `QueueMemberPausedEvent` |
| 53 | `QueueMemberPenaltyEvent` | `QueueMemberPenaltyEvent` |
| 54 | `QueueMemberRingInUseEvent` | `QueueMemberRingInUseEvent` |
| 55 | `QueueStatusCompleteEvent` | `QueueStatusCompleteEvent` |
| 56 | `QueueSummaryEvent` | `QueueSummaryEvent` |
| 57 | `QueueSummaryCompleteEvent` | `QueueSummaryCompleteEvent` |
| 58 | `JoinEvent` | `JoinEvent` |
| 59 | `LeaveEvent` | `LeaveEvent` |

#### Agent Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 60 | `AgentLoginEvent` | `AgentLoginEvent` |
| 61 | `AgentLogoffEvent` | `AgentLogoffEvent` |
| 62 | `AgentCallbackLoginEvent` | `AgentCallbackLoginEvent` |
| 63 | `AgentCallbackLogoffEvent` | `AgentCallbackLogoffEvent` |
| 64 | `AgentCalledEvent` | `AgentCalledEvent` |
| 65 | `AgentConnectEvent` | `AgentConnectEvent` |
| 66 | `AgentCompleteEvent` | `AgentCompleteEvent` |
| 67 | `AgentDumpEvent` | `AgentDumpEvent` |
| 68 | `AgentRingNoAnswerEvent` | `AgentRingNoAnswerEvent` |
| 69 | `AgentsEvent` | `AgentsEvent` |
| 70 | `AgentsCompleteEvent` | `AgentsCompleteEvent` |

#### Hold Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 71 | `HoldEvent` | `HoldEvent` |
| 72 | `HoldedCallEvent` | `HoldedCallEvent` |
| 73 | `UnholdEvent` | `UnholdEvent` |

#### Music On Hold

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 74 | `MusicOnHoldEvent` | `MusicOnHoldEvent` |
| 75 | `MusicOnHoldStartEvent` | `MusicOnHoldStartEvent` |
| 76 | `MusicOnHoldStopEvent` | `MusicOnHoldStopEvent` |

#### ConfBridge Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 77 | `ConfbridgeStartEvent` | `ConfbridgeStartEvent` |
| 78 | `ConfbridgeEndEvent` | `ConfbridgeEndEvent` |
| 79 | `ConfbridgeJoinEvent` | `ConfbridgeJoinEvent` |
| 80 | `ConfbridgeLeaveEvent` | `ConfbridgeLeaveEvent` |
| 81 | `ConfbridgeTalkingEvent` | `ConfbridgeTalkingEvent` |
| 82 | `ConfbridgeListEvent` | `ConfbridgeListEvent` |
| 83 | `ConfbridgeListCompleteEvent` | `ConfbridgeListCompleteEvent` |
| 84 | `ConfbridgeListRoomsEvent` | `ConfbridgeListRoomsEvent` |
| 85 | `ConfbridgeListRoomsCompleteEvent` | `ConfbridgeListRoomsCompleteEvent` |

#### MeetMe Events (Legacy)

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 86 | `MeetMeJoinEvent` | `MeetMeJoinEvent` |
| 87 | `MeetMeLeaveEvent` | `MeetMeLeaveEvent` |
| 88 | `MeetMeEndEvent` | `MeetMeEndEvent` |
| 89 | `MeetMeTalkingEvent` | `MeetMeTalkingEvent` |
| 90 | `MeetMeStopTalkingEvent` | `MeetMeStopTalkingEvent` |
| 91 | `MeetMeTalkingRequestEvent` | `MeetMeTalkingRequestEvent` |
| 92 | `MeetMeMuteEvent` | `MeetMeMuteEvent` |

#### Parking Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 93 | `ParkedCallEvent` | `ParkedCallEvent` |
| 94 | `ParkedCallGiveUpEvent` | `ParkedCallGiveUpEvent` |
| 95 | `ParkedCallTimeOutEvent` | `ParkedCallTimeOutEvent` |
| 96 | `ParkedCallsCompleteEvent` | `ParkedCallsCompleteEvent` |
| 97 | `UnparkedCallEvent` | `UnparkedCallEvent` |
| 98 | `PickupEvent` | `PickupEvent` |

#### DTMF Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 99 | `DtmfEvent` | `DtmfEvent` |
| 100 | `DtmfBeginEvent` | `DtmfBeginEvent` |
| 101 | `DtmfEndEvent` | `DtmfEndEvent` |

#### Originate Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 102 | `OriginateResponseEvent` | `OriginateResponseEvent` |
| 103 | `OriginateSuccessEvent` | `OriginateSuccessEvent` |
| 104 | `OriginateFailureEvent` | `OriginateFailureEvent` |

#### Monitor / Recording Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 105 | `MonitorStartEvent` | `MonitorStartEvent` |
| 106 | `MonitorStopEvent` | `MonitorStopEvent` |
| 107 | `MixMonitorStartEvent` | `MixMonitorStartEvent` |
| 108 | `MixMonitorStopEvent` | `MixMonitorStopEvent` |

#### CDR / CEL Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 109 | `CdrEvent` | `CdrEvent` |
| 110 | `CelEvent` | `CelEvent` |

#### Peer / Registration Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 111 | `PeerStatusEvent` | `PeerStatusEvent` |
| 112 | `PeerEntryEvent` | `PeerEntryEvent` |
| 113 | `PeerlistCompleteEvent` | `PeerlistCompleteEvent` |
| 114 | `PeersEvent` | `PeersEvent` |
| 115 | `RegistryEvent` | `RegistryEvent` |
| 116 | `RegistryEntryEvent` | `RegistryEntryEvent` |
| 117 | `RegistrationsCompleteEvent` | `RegistrationsCompleteEvent` |

#### PJSIP Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 118 | `EndpointDetail` | `PjSipEndpointDetailEvent` |
| 119 | `EndpointDetailComplete` | `PjSipEndpointDetailCompleteEvent` |
| 120 | `EndpointList` | `PjSipEndpointListEvent` |
| 121 | `EndpointListComplete` | `PjSipEndpointListCompleteEvent` |
| 122 | `ContactList` | `PjSipContactListEvent` |
| 123 | `ContactListComplete` | `PjSipContactListCompleteEvent` |
| 124 | `ContactStatusDetail` | `PjSipContactStatusDetailEvent` |
| 125 | `ContactStatusEvent` | `PjSipContactStatusEvent` |
| 126 | `ContactStatusEnum` | `PjSipContactStatus` (enum) |
| 127 | `AuthDetail` | `PjSipAuthDetailEvent` |
| 128 | `AorDetail` | `PjSipAorDetailEvent` |
| 129 | `TransportDetail` | `PjSipTransportDetailEvent` |

#### Device / Extension State

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 130 | `ExtensionStatusEvent` | `ExtensionStatusEvent` |
| 131 | `DeviceStateChangeEvent` | `DeviceStateChangeEvent` |
| 132 | `DndStateEvent` | `DndStateEvent` |

#### AGI Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 133 | `AgiExecEvent` | `AgiExecEvent` |
| 134 | `AgiExecStartEvent` | `AgiExecStartEvent` |
| 135 | `AgiExecEndEvent` | `AgiExecEndEvent` |
| 136 | `AsyncAgiEvent` | `AsyncAgiEvent` |
| 137 | `AsyncAgiStartEvent` | `AsyncAgiStartEvent` |
| 138 | `AsyncAgiExecEvent` | `AsyncAgiExecEvent` |
| 139 | `AsyncAgiEndEvent` | `AsyncAgiEndEvent` |

#### Spy Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 140 | `ChanSpyStartEvent` | `ChanSpyStartEvent` |
| 141 | `ChanSpyStopEvent` | `ChanSpyStopEvent` |

#### FAX Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 142 | `FaxReceivedEvent` | `FaxReceivedEvent` |
| 143 | `FaxStatusEvent` | `FaxStatusEvent` |
| 144 | `FaxDocumentStatusEvent` | `FaxDocumentStatusEvent` |
| 145 | `FaxLicenseEvent` | `FaxLicenseEvent` |
| 146 | `FaxLicenseListCompleteEvent` | `FaxLicenseListCompleteEvent` |
| 147 | `ReceiveFaxEvent` | `ReceiveFaxEvent` |
| 148 | `SendFaxEvent` | `SendFaxEvent` |
| 149 | `SendFaxStatusEvent` | `SendFaxStatusEvent` |
| 150 | `T38FaxStatusEvent` | `T38FaxStatusEvent` |

#### Security Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 151 | `SuccessfulAuthEvent` | `SuccessfulAuthEvent` |
| 152 | `AuthMethodNotAllowedEvent` | `AuthMethodNotAllowedEvent` |
| 153 | `ChallengeResponseFailedEvent` | `ChallengeResponseFailedEvent` |
| 154 | `ChallengeSentEvent` | `ChallengeSentEvent` |
| 155 | `FailedACLEvent` | `FailedAclEvent` |
| 156 | `InvalidAccountId` | `InvalidAccountIdEvent` |
| 157 | `InvalidPasswordEvent` | `InvalidPasswordEvent` |
| 158 | `InvalidTransportEvent` | `InvalidTransportEvent` |
| 159 | `RequestBadFormatEvent` | `RequestBadFormatEvent` |
| 160 | `RequestNotAllowedEvent` | `RequestNotAllowedEvent` |
| 161 | `RequestNotSupportedEvent` | `RequestNotSupportedEvent` |
| 162 | `SessionLimitEvent` | `SessionLimitEvent` |
| 163 | `UnexpectedAddressEvent` | `UnexpectedAddressEvent` |
| 164 | `MemoryLimitEvent` | `MemoryLimitEvent` |
| 165 | `LoadAverageLimitEvent` | `LoadAverageLimitEvent` |

#### RTP / RTCP Stats

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 166 | `RtcpReceivedEvent` | `RtcpReceivedEvent` |
| 167 | `RtcpSentEvent` | `RtcpSentEvent` |
| 168 | `RtpReceiverStatEvent` | `RtpReceiverStatEvent` |
| 169 | `RtpSenderStatEvent` | `RtpSenderStatEvent` |
| 170 | `JitterBufStatsEvent` | `JitterBufStatsEvent` |

#### System Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 171 | `ConnectEvent` | `ConnectEvent` |
| 172 | `DisconnectEvent` | `DisconnectEvent` |
| 173 | `FullyBootedEvent` | `FullyBootedEvent` |
| 174 | `ShutdownEvent` | `ShutdownEvent` |
| 175 | `ReloadEvent` | `ReloadEvent` |
| 176 | `ModuleLoadReportEvent` | `ModuleLoadReportEvent` |
| 177 | `LogChannelEvent` | `LogChannelEvent` |
| 178 | `AlarmEvent` | `AlarmEvent` |
| 179 | `AlarmClearEvent` | `AlarmClearEvent` |
| 180 | `ProtocolIdentifierReceivedEvent` | `ProtocolIdentifierReceivedEvent` |

#### Core Show Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 181 | `CoreShowChannelEvent` | `CoreShowChannelEvent` |
| 182 | `CoreShowChannelsCompleteEvent` | `CoreShowChannelsCompleteEvent` |

#### DAHDI Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 183 | `DAHDIChannelEvent` | `DahdiChannelEvent` |
| 184 | `DahdiShowChannelsEvent` | `DahdiShowChannelsEvent` |
| 185 | `DahdiShowChannelsCompleteEvent` | `DahdiShowChannelsCompleteEvent` |

#### Zap Events (Legacy)

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 186 | `ZapShowChannelsEvent` | `ZapShowChannelsEvent` |
| 187 | `ZapShowChannelsCompleteEvent` | `ZapShowChannelsCompleteEvent` |

#### Dialplan Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 188 | `ListDialplanEvent` | `ListDialplanEvent` |
| 189 | `ShowDialplanCompleteEvent` | `ShowDialplanCompleteEvent` |

#### Database Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 190 | `DbGetResponseEvent` | `DbGetResponseEvent` |

#### Voicemail Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 191 | `MessageWaitingEvent` | `MessageWaitingEvent` |
| 192 | `VoicemailUserEntryEvent` | `VoicemailUserEntryEvent` |
| 193 | `VoicemailUserEntryCompleteEvent` | `VoicemailUserEntryCompleteEvent` |

#### Dongle GSM Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 194 | `DongleNewSMSEvent` | `DongleNewSmsEvent` |
| 195 | `DongleNewSMSBase64Event` | `DongleNewSmsBase64Event` |
| 196 | `DongleNewCMGREvent` | `DongleNewCmgrEvent` |
| 197 | `DongleStatusEvent` | `DongleStatusEvent` |
| 198 | `DongleCENDEvent` | `DongleCendEvent` |
| 199 | `DongleCallStateChangeEvent` | `DongleCallStateChangeEvent` |
| 200 | `DongleDeviceEntryEvent` | `DongleDeviceEntryEvent` |
| 201 | `DongleShowDevicesCompleteEvent` | `DongleShowDevicesCompleteEvent` |

#### Skype Events (Legacy)

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 202 | `SkypeAccountStatusEvent` | `SkypeAccountStatusEvent` |
| 203 | `SkypeBuddyEntryEvent` | `SkypeBuddyEntryEvent` |
| 204 | `SkypeBuddyListCompleteEvent` | `SkypeBuddyListCompleteEvent` |
| 205 | `SkypeBuddyStatusEvent` | `SkypeBuddyStatusEvent` |
| 206 | `SkypeChatMessageEvent` | `SkypeChatMessageEvent` |
| 207 | `SkypeLicenseEvent` | `SkypeLicenseEvent` |
| 208 | `SkypeLicenseListCompleteEvent` | `SkypeLicenseListCompleteEvent` |

#### Jabber Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 209 | `JabberEventEvent` | `JabberEventEvent` |

#### Misc Events

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 210 | `StatusEvent` | `StatusEvent` |
| 211 | `StatusCompleteEvent` | `StatusCompleteEvent` |
| 212 | `PausedEvent` | `PausedEvent` |
| 213 | `UnpausedEvent` | `UnpausedEvent` |
| 214 | `PriEventEvent` | `PriEventEvent` |
| 215 | `AntennaLevelEvent` | `AntennaLevelEvent` |

### Entregables
- [x] 214 clases Event generadas automaticamente desde asterisk-java source
- [x] 8 clases base manuales: ChannelEventBase, BridgeEventBase, QueueMemberEventBase, AgentEventBase, ConfbridgeEventBase, MeetMeEventBase, SecurityEventBase, FaxEventBase
- [x] `ResponseEvent` base class
- [x] Script generador: `tools/generate-pocos.sh` (lee Java getters, genera C# POCOs)
- [ ] Source generator generando deserializers + registry `FrozenDictionary<string, Func<ManagerEvent>>` (pendiente Fase 12)
- [ ] Tests unitarios de deserializacion para cada categoria (pendiente Fase 11)
- [ ] Test de integracion: parsear dump real de eventos AMI (pendiente Fase 11)

---

## Fase 5 - AMI Responses e Internals ✅

### Objetivo
Portar las 18 clases de respuesta y completar los 21 componentes internos del protocolo AMI.

### Response Classes

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 1 | `ManagerResponse` | `ManagerResponse` | Respuesta base |
| 2 | `ManagerError` | `ManagerError` | Respuesta de error |
| 3 | `ChallengeResponse` | `ChallengeResponse` | Challenge MD5 |
| 4 | `CommandResponse` | `CommandResponse` | Resultado de CommandAction |
| 5 | `CoreSettingsResponse` | `CoreSettingsResponse` | Config del core |
| 6 | `CoreStatusResponse` | `CoreStatusResponse` | Estado del core |
| 7 | `ExtensionStateResponse` | `ExtensionStateResponse` | Estado de extension |
| 8 | `FaxLicenseStatusResponse` | `FaxLicenseStatusResponse` | Licencia FAX |
| 9 | `GetConfigResponse` | `GetConfigResponse` | Archivo de config |
| 10 | `GetVarResponse` | `GetVarResponse` | Variable |
| 11 | `MailboxCountResponse` | `MailboxCountResponse` | Conteo de mensajes |
| 12 | `MailboxStatusResponse` | `MailboxStatusResponse` | Estado de buzon |
| 13 | `MixMonitorResponse` | `MixMonitorResponse` | Respuesta MixMonitor |
| 14 | `ModuleCheckResponse` | `ModuleCheckResponse` | Verificacion de modulo |
| 15 | `PingResponse` | `PingResponse` | Pong |
| 16 | `SipShowPeerResponse` | `SipShowPeerResponse` | Detalle de peer SIP |
| 17 | `SkypeBuddyResponse` | `SkypeBuddyResponse` | Contacto Skype |
| 18 | `SkypeLicenseStatusResponse` | `SkypeLicenseStatusResponse` | Licencia Skype |

### Internal Components (ya definidos en Fase 2 como interfaces)

Completar implementaciones pendientes:
- [x] `LegacyEventAdapter` — shims DialBegin to Dial, BridgeEnter to Link, BridgeLeave to Unlink
- [x] `ActiveBridgeTracker` — tracking de bridges activos con ConcurrentDictionary
- [x] `MeetmeCompatibility` — compat ConfBridge to MeetMe event mapping
- [x] `ResponseEventCollector` — coleccion async de response events (ya en Fase 2)
- [x] Fix AmiProtocolReader: parsing correcto de Response: Follows (headers antes de command output)

### Entregables
- [x] 17 clases Response generadas desde asterisk-java
- [x] 3 componentes internos de compatibilidad (LegacyEventAdapter, ActiveBridgeTracker, MeetmeCompatibility)
- [x] 9 tests: 5 flujos AMI completos + 2 legacy adapter + 1 bridge tracker + 1 meetme compat

---

## Fase 6 - FastAGI Server ✅

### Objetivo
Implementar servidor FastAGI completamente async con `System.IO.Pipelines`.

### Clases a portar

#### Server Core

| # | Clase Java | Clase .NET | Notas |
|---|-----------|-----------|-------|
| 1 | `AgiServer` | `IAgiServer` | Interface |
| 2 | `DefaultAgiServer` | `FastAgiServer` | TCP listener async |
| 3 | `AbstractAgiServer` | `AgiServerBase` | Base compartida |
| 4 | `AgiServerThread` | No aplica | Reemplazado por Task async |
| 5 | `AgiConnectionHandler` | `IAgiConnectionHandler` | Interface |
| 6 | `FastAgiConnectionHandler` | `FastAgiConnectionHandler` | Impl async |
| 7 | `AsyncAgiConnectionHandler` | `AsyncAgiConnectionHandler` | AsyncAGI |
| 8 | `AgiChannelFactory` | `IAgiChannelFactory` | Factory |
| 9 | `DefaultAgiChannelFactory` | `DefaultAgiChannelFactory` | Impl |

#### AGI Protocol I/O

| # | Clase Java | Clase .NET | Notas |
|---|-----------|-----------|-------|
| 10 | `AgiReader` | `IAgiReader` | Interface |
| 11 | `FastAgiReader` | `FastAgiReader` | PipeReader based |
| 12 | `AsyncAgiReader` | `AsyncAgiReader` | AMI-based AGI |
| 13 | `AgiWriter` | `IAgiWriter` | Interface |
| 14 | `FastAgiWriter` | `FastAgiWriter` | PipeWriter based |
| 15 | `AsyncAgiWriter` | `AsyncAgiWriter` | AMI-based AGI |

#### AGI Request/Channel/Script

| # | Clase Java | Clase .NET | Notas |
|---|-----------|-----------|-------|
| 16 | `AgiRequest` | `IAgiRequest` | Interface |
| 17 | `AgiRequestImpl` | `AgiRequest` | Impl |
| 18 | `AgiChannel` | `IAgiChannel` | Interface |
| 19 | `AgiChannelImpl` | `AgiChannel` | Impl |
| 20 | `AgiOperations` | `IAgiOperations` | Interface de comandos |
| 21 | `BaseAgiScript` | `AgiScriptBase` | Base para scripts |
| 22 | `AgiScript` | `IAgiScript` | Interface |
| 23 | `NamedAgiScript` | `NamedAgiScript` | Wrapper con nombre |
| 24 | `AgiReplyImpl` | `AgiReply` | Respuesta AGI |

#### Mapping Strategies

| # | Clase Java | Clase .NET | Notas |
|---|-----------|-----------|-------|
| 25 | `MappingStrategy` | `IMappingStrategy` | Interface |
| 26 | `AbstractMappingStrategy` | `MappingStrategyBase` | Base |
| 27 | `SimpleMappingStrategy` | `SimpleMappingStrategy` | Diccionario |
| 28 | `StaticMappingStrategy` | `StaticMappingStrategy` | Static config |
| 29 | `ClassNameMappingStrategy` | `TypeNameMappingStrategy` | Por nombre de tipo |
| 30 | `CompositeMappingStrategy` | `CompositeMappingStrategy` | Multiples estrategias |
| 31 | `ResourceBundleMappingStrategy` | `ConfigMappingStrategy` | Desde config file |
| 32 | `ScriptEngineMappingStrategy` | `ScriptEngineMappingStrategy` | Scripts dinamicos |

#### AGI Commands (54)

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 1 | `AnswerCommand` | `AnswerCommand` |
| 2 | `BridgeCommand` | `BridgeCommand` |
| 3 | `ChannelStatusCommand` | `ChannelStatusCommand` |
| 4 | `ConfbridgeCommand` | `ConfbridgeCommand` |
| 5 | `ControlStreamFileCommand` | `ControlStreamFileCommand` |
| 6 | `DatabaseDelCommand` | `DatabaseDelCommand` |
| 7 | `DatabaseDelTreeCommand` | `DatabaseDelTreeCommand` |
| 8 | `DatabaseGetCommand` | `DatabaseGetCommand` |
| 9 | `DatabasePutCommand` | `DatabasePutCommand` |
| 10 | `DialCommand` | `DialCommand` |
| 11 | `ExecuteCommand` | `ExecuteCommand` |
| 12 | `GetDataCommand` | `GetDataCommand` |
| 13 | `GetFullVariableCommand` | `GetFullVariableCommand` |
| 14 | `GetOptionCommand` | `GetOptionCommand` |
| 15 | `GetVariableCommand` | `GetVariableCommand` |
| 16 | `GosubCommand` | `GosubCommand` |
| 17 | `HangupCommand` | `HangupCommand` |
| 18 | `MeetmeCommand` | `MeetmeCommand` |
| 19 | `NoopCommand` | `NoopCommand` |
| 20 | `QueueCommand` | `QueueCommand` |
| 21 | `ReceiveCharCommand` | `ReceiveCharCommand` |
| 22 | `ReceiveTextCommand` | `ReceiveTextCommand` |
| 23 | `RecordFileCommand` | `RecordFileCommand` |
| 24 | `SayAlphaCommand` | `SayAlphaCommand` |
| 25 | `SayDateTimeCommand` | `SayDateTimeCommand` |
| 26 | `SayDigitsCommand` | `SayDigitsCommand` |
| 27 | `SayNumberCommand` | `SayNumberCommand` |
| 28 | `SayPhoneticCommand` | `SayPhoneticCommand` |
| 29 | `SayTimeCommand` | `SayTimeCommand` |
| 30 | `SendImageCommand` | `SendImageCommand` |
| 31 | `SendTextCommand` | `SendTextCommand` |
| 32 | `SetAutoHangupCommand` | `SetAutoHangupCommand` |
| 33 | `SetCallerIdCommand` | `SetCallerIdCommand` |
| 34 | `SetContextCommand` | `SetContextCommand` |
| 35 | `SetExtensionCommand` | `SetExtensionCommand` |
| 36 | `SetMusicOffCommand` | `SetMusicOffCommand` |
| 37 | `SetMusicOnCommand` | `SetMusicOnCommand` |
| 38 | `SetPriorityCommand` | `SetPriorityCommand` |
| 39 | `SetVariableCommand` | `SetVariableCommand` |
| 40 | `SpeechActivateGrammarCommand` | `SpeechActivateGrammarCommand` |
| 41 | `SpeechCreateCommand` | `SpeechCreateCommand` |
| 42 | `SpeechDeactivateGrammarCommand` | `SpeechDeactivateGrammarCommand` |
| 43 | `SpeechDestroyCommand` | `SpeechDestroyCommand` |
| 44 | `SpeechLoadGrammarCommand` | `SpeechLoadGrammarCommand` |
| 45 | `SpeechRecognizeCommand` | `SpeechRecognizeCommand` |
| 46 | `SpeechSetCommand` | `SpeechSetCommand` |
| 47 | `SpeechUnloadGrammarCommand` | `SpeechUnloadGrammarCommand` |
| 48 | `StreamFileCommand` | `StreamFileCommand` |
| 49 | `TddModeCommand` | `TddModeCommand` |
| 50 | `VerboseCommand` | `VerboseCommand` |
| 51 | `WaitForDigitCommand` | `WaitForDigitCommand` |
| 52 | `AsyncAgiBreakCommand` | `AsyncAgiBreakCommand` |
| 53 | `AbstractAgiCommand` | `AgiCommandBase` |
| 54 | `SpeechRecognitionResult` | `SpeechRecognitionResult` |

#### Exceptions

| # | Clase Java | Clase .NET |
|---|-----------|-----------|
| 55 | `AgiException` | `AgiException` |
| 56 | `AgiHangupException` | `AgiHangupException` |
| 57 | `AgiNetworkException` | `AgiNetworkException` |
| 58 | `AgiSpeechException` | `AgiSpeechException` |
| 59 | `InvalidCommandSyntaxException` | `InvalidCommandSyntaxException` |
| 60 | `InvalidOrUnknownCommandException` | `InvalidOrUnknownCommandException` |

### Entregables
- [x] FastAgiServer con accept loop async, connection handler, PipeReader/PipeWriter
- [x] 54 comandos AGI generados desde asterisk-java (Answer, Dial, StreamFile, etc.)
- [x] 3 mapping strategies: SimpleMappingStrategy, CompositeMappingStrategy, TypeNameMappingStrategy
- [x] AgiChannel, AgiRequest parser, AgiReply parser, FastAgiReader/Writer
- [x] AgiException, AgiHangupException, AgiNetworkException
- [ ] AsyncAGI (AGI sobre AMI) — estructura creada, pendiente implementacion completa
- [x] 17 tests: 5 reply, 2 request, 4 protocol roundtrip, 6 mapping strategy

---

## Fase 7 - Live API (Domain Objects) ✅

### Objetivo
Portar la capa de objetos de dominio con estado en tiempo real: canales, colas, agentes, conferencias.

### Clases a portar

#### Server (Aggregate Root)

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 1 | `AsteriskServer` | `IAsteriskServer` | Interface principal |
| 2 | `AsteriskServerImpl` | `AsteriskServer` | Implementacion |
| 3 | `DefaultAsteriskServer` | `AsteriskServerBuilder` | Factory/builder |
| 4 | `SecureAsteriskServer` | `SecureAsteriskServer` | SSL variant |
| 5 | `AsteriskServerListener` | `IAsteriskServerListener` | Observer |
| 6 | `AbstractAsteriskServerListener` | `AsteriskServerListenerBase` | Base vacia |
| 7 | `Constants` | `LiveConstants` | Constantes |

#### Channels

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 8 | `AsteriskChannel` | `IAsteriskChannel` | Interface |
| 9 | `AsteriskChannelImpl` | `AsteriskChannel` | Impl con estado |
| 10 | `ChannelManager` | `ChannelManager` | Tracking de canales |
| 11 | `ChannelState` | `ChannelState` | Enum de estados |
| 12 | `Extension` | `Extension` | Record |
| 13 | `ExtensionHistoryEntry` | `ExtensionHistoryEntry` | Historial |
| 14 | `LinkedChannelHistoryEntry` | `LinkedChannelHistoryEntry` | Canales enlazados |
| 15 | `CallDetailRecord` | `ICallDetailRecord` | Interface CDR |
| 16 | `CallDetailRecordImpl` | `CallDetailRecord` | Impl CDR |
| 17 | `HangupCause` | `HangupCause` | Enum causas |
| 18 | `Disposition` | `Disposition` | Enum disposicion |
| 19 | `AmaFlags` | `AmaFlags` | Enum AMA |

#### Queues

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 20 | `AsteriskQueue` | `IAsteriskQueue` | Interface |
| 21 | `AsteriskQueueImpl` | `AsteriskQueue` | Impl con estado |
| 22 | `AsteriskQueueEntry` | `IAsteriskQueueEntry` | Interface |
| 23 | `AsteriskQueueEntryImpl` | `AsteriskQueueEntry` | Impl |
| 24 | `AsteriskQueueMember` | `IAsteriskQueueMember` | Interface |
| 25 | `AsteriskQueueMemberImpl` | `AsteriskQueueMember` | Impl |
| 26 | `AsteriskQueueListener` | `IAsteriskQueueListener` | Observer |
| 27 | `QueueManager` | `QueueManager` | Tracking de colas |
| 28 | `QueueMemberState` | `QueueMemberState` | Enum estados |
| 29 | `QueueEntryState` | `QueueEntryState` | Enum estados |

#### Agents

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 30 | `AsteriskAgent` | `IAsteriskAgent` | Interface |
| 31 | `AsteriskAgentImpl` | `AsteriskAgent` | Impl |
| 32 | `AgentManager` | `AgentManager` | Tracking |

#### MeetMe / Conferencing

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 33 | `MeetMeRoom` | `IMeetMeRoom` | Interface |
| 34 | `MeetMeRoomImpl` | `MeetMeRoom` | Impl |
| 35 | `MeetMeUser` | `IMeetMeUser` | Interface |
| 36 | `MeetMeUserImpl` | `MeetMeUser` | Impl |
| 37 | `MeetMeUserState` | `MeetMeUserState` | Enum |
| 38 | `MeetMeManager` | `MeetMeManager` | Tracking |
| 39 | `MeetmeCompatibility` | `MeetmeCompatibility` | Compat layer |

#### Originate

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 40 | `OriginateCallback` | `IOriginateCallback` | Callback interface |
| 41 | `OriginateCallbackData` | `OriginateCallbackData` | Data holder |

#### Base / Support

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 42 | `LiveObject` | `ILiveObject` | Interface base |
| 43 | `AbstractLiveObject` | `LiveObjectBase` | Base con PropertyChanged |
| 44 | `LiveException` | `LiveException` | Excepcion |
| 45 | `ManagerCommunicationException` | `AmiCommunicationException` | Excepcion |
| 46 | `ManagerCommunicationExceptionMapper` | `ExceptionMapper` | Mapeo |
| 47 | `NoSuchChannelException` | `ChannelNotFoundException` | Excepcion |
| 48 | `NoSuchInterfaceException` | `InterfaceNotFoundException` | Excepcion |
| 49 | `InvalidPenaltyException` | `InvalidPenaltyException` | Excepcion |
| 50 | `Voicemailbox` | `Voicemailbox` | Record |
| 51 | `ConfigFileImpl` | `ConfigFile` | Config en memoria |
| 52 | `BackwardsCompatibilityForManagerEvents` | `LegacyEventAdapter` | Ya en Fase 2 |

### Patron de implementacion .NET

```csharp
// Ejemplo: IAsteriskChannel con INotifyPropertyChanged + IObservable
public interface IAsteriskChannel : ILiveObject
{
    string Name { get; }
    string UniqueId { get; }
    ChannelState State { get; }
    string CallerId { get; }
    IAsteriskChannel? LinkedChannel { get; }
    IReadOnlyList<Extension> ExtensionHistory { get; }
    IObservable<ChannelState> StateChanges { get; }
    IObservable<IAsteriskChannel> WhenHungUp { get; }
}
```

### Entregables
- [x] AsteriskServer aggregate root con tracking de canales/colas/agentes/conferencias
- [x] ChannelManager: OnNewChannel, OnNewState, OnHangup, OnRename, OnLink/OnUnlink + eventos
- [x] QueueManager: OnQueueParams, OnMemberAdded/Removed/Paused, OnCallerJoined/Left + eventos
- [x] AgentManager: OnAgentLogin/Logoff/Connect/Complete/Paused + eventos
- [x] MeetMeManager: OnUserJoined/Left con auto-cleanup de salas vacias
- [x] ILiveObject + LiveObjectBase con INotifyPropertyChanged
- [x] EventObserver interno que despacha AMI events a los managers correctos
- [x] Enums: AmaFlags, Disposition, QueueMemberState, QueueEntryState, MeetMeUserState, AgentState
- [x] Exceptions: LiveException, AmiCommunicationException, ChannelNotFoundException, etc.
- [ ] Originate async con callbacks (estructura creada, pendiente wiring completo)
- [ ] RequestInitialStateAsync (pendiente wiring con event-generating actions)
- [x] 19 tests: 7 channels + 6 queues + 6 agents
- [ ] Benchmarks de memory footprint vs Java (pendiente Fase 12)

---

## Fase 8 - PBX Activities ✅

### Objetivo
Portar el framework de actividades PBX de alto nivel: operaciones de telefonia como Dial, Hold, Transfer, Park, etc.

### Clases a portar

#### Core PBX

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 1 | `PBX` | `IPbx` | Interface principal |
| 2 | `PBXFactory` | `PbxFactory` | Factory |
| 3 | `PBXException` | `PbxException` | Excepcion base |
| 4 | `DuplicateScriptException` | `DuplicateScriptException` | Excepcion |

#### Models

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 5 | `Call` | `ICall` | Interface de llamada |
| 6 | `CallImpl` | `Call` | Impl |
| 7 | `CallDirection` | `CallDirection` | Enum |
| 8 | `Channel` | `IPbxChannel` | Interface (distinta de Live) |
| 9 | `ChannelFactory` | `PbxChannelFactory` | Factory |
| 10 | `ChannelState` | `PbxChannelState` | Enum |
| 11 | `CallerID` | `CallerId` | Record |
| 12 | `EndPoint` | `EndPoint` | Record |
| 13 | `PhoneNumber` | `PhoneNumber` | Value object |
| 14 | `Tech` | `Tech` | Tecnologia (SIP, PJSIP, etc) |
| 15 | `TechType` | `TechType` | Enum |
| 16 | `DTMFTone` | `DtmfTone` | Enum |
| 17 | `DialPlanExtension` | `DialPlanExtension` | Record |
| 18 | `InvalidChannelName` | `InvalidChannelNameException` | Excepcion |

#### Call State Machine

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 19 | `CallStateData` | `CallStateData` | Base de datos de estado |
| 20 | `CallStateAnswered` | `CallStateAnswered` | Estado: contestada |
| 21 | `CallStateDataNewInbound` | `CallStateDataNewInbound` | Estado: nueva entrante |
| 22 | `CallStateDataParked` | `CallStateDataParked` | Estado: estacionada |
| 23 | `CallStateDataTransfer` | `CallStateDataTransfer` | Estado: en transferencia |

#### Activities (operaciones de alto nivel)

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 24 | `Activity` | `IActivity` | Interface base |
| 25 | `ActivityStatusEnum` | `ActivityStatus` | Enum de estados |
| 26 | `ActivityCallback` | `IActivityCallback` | Callback pattern |
| 27 | `ActivityAgi` | `ActivityAgi` | Base AGI activity |
| 28 | `ActivityArrivalListener` | `IActivityArrivalListener` | Listener |
| 29 | `CompletionAdaptor` | `CompletionAdaptor` | Adaptor |
| 30 | `BlindTransferResultListener` | `IBlindTransferResultListener` | Resultado transfer |
| 31 | `CallHangupListener` | `ICallHangupListener` | Listener hangup |
| 32 | `NewChannelListener` | `INewChannelListener` | Listener nuevo canal |
| 33 | `NewExtensionListener` | `INewExtensionListener` | Listener nueva extension |
| 34 | `ChannelHangupListener` | `IChannelHangupListener` | Listener hangup canal |

#### AGI Channel Activities

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 35 | `AgiChannelActivityDial` | `DialActivity` | Marcar |
| 36 | `AgiChannelActivityHold` | `HoldActivity` | Poner en espera |
| 37 | `AgiChannelActivityHoldForBridge` | `HoldForBridgeActivity` | Espera para bridge |
| 38 | `AgiChannelActivityBridge` | `BridgeActivity` | Puentear canales |
| 39 | `AgiChannelActivityBlindTransfer` | `BlindTransferActivity` | Transfer ciega |
| 40 | `AgiChannelActivityHangup` | `HangupActivity` | Colgar |
| 41 | `AgiChannelActivityMeetme` | `MeetmeActivity` | Conferencia |
| 42 | `AgiChannelActivityPlayMessage` | `PlayMessageActivity` | Reproducir audio |
| 43 | `AgiChannelActivityQueue` | `QueueActivity` | Enviar a cola |
| 44 | `AgiChannelActivityTransientHoldSilence` | `TransientHoldSilenceActivity` | Espera silenciosa |
| 45 | `AgiChannelActivityVoicemail` | `VoicemailActivity` | Voicemail |

#### PBX Event Wrappers (re-exportados de manager)

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 46 | `ChannelEvent` | `PbxChannelEvent` | Wrapper |
| 47 | `ChannelEventHelper` | `ChannelEventHelper` | Utilidad |
| 48-82 | (Event wrappers) | Delegated | Wrappers de eventos AMI reutilizados |

#### AGI Configuration

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 83 | `AgiConfiguration` | `IAgiConfiguration` | Interface |
| 84 | `AgiMappingStragegy` | `AgiMappingStrategy` | Corregido typo |
| 85 | `ServiceAgiScript` | `IServiceAgiScript` | Interface |
| 86 | `ServiceAgiScriptImpl` | `ServiceAgiScript` | Impl |
| 87 | `AsteriskSettings` | `IAsteriskSettings` | Interface |
| 88 | `DefaultAsteriskSettings` | `AsteriskSettings` | Impl |

### Patron de implementacion .NET para Activities

```csharp
// Activities como async state machines
public interface IActivity : IAsyncDisposable
{
    ActivityStatus Status { get; }
    IObservable<ActivityStatus> StatusChanges { get; }
    ValueTask StartAsync(CancellationToken ct = default);
    ValueTask CancelAsync(CancellationToken ct = default);
}

public sealed class DialActivity : IActivity
{
    public required EndPoint Target { get; init; }
    public required IPbxChannel Channel { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    // ... async implementation
}
```

### Entregables
- [x] ActivityBase con IObservable de StatusChanges y lifecycle tracking
- [x] 11 actividades: Dial, Hold, Bridge, BlindTransfer, Hangup, Meetme, PlayMessage, Queue, Voicemail, Park
- [x] Modelos: Call, PbxChannel, EndPoint.Parse(), PhoneNumber, CallerId, DialPlanExtension
- [x] Call state machine: CallStateAnswered, CallStateNewInbound, CallStateParked, CallStateTransfer
- [x] Exceptions: PbxException, InvalidChannelNameException, ActivityFailedException
- [x] 19 tests: 8 modelos + 11 actividades con mock IAgiChannel
- [ ] Ejemplo: flujo completo de llamada saliente con transfer (pendiente Fase 11)

---

## Fase 9 - Configuracion y Utilidades ✅

### Objetivo
Portar el parser de archivos de configuracion de Asterisk y utilidades restantes.

### Clases a portar

#### Config Parser

| # | Clase Java | Clase .NET | Descripcion |
|---|-----------|-----------|-------------|
| 1 | `ConfigFile` | `IConfigFile` | Interface |
| 2 | `ConfigFileImpl` | `ConfigFile` | Impl |
| 3 | `ConfigFileReader` | `ConfigFileReader` | Parser de .conf |
| 4 | `ExtensionsConfigFile` | `ExtensionsConfigFile` | extensions.conf |
| 5 | `ExtensionsConfigFileReader` | `ExtensionsConfigFileReader` | Parser extensions |
| 6 | `Category` | `ConfigCategory` | Seccion [nombre] |
| 7 | `ConfigElement` | `ConfigElement` | Base |
| 8 | `ConfigVariable` | `ConfigVariable` | key=value |
| 9 | `ConfigDirective` | `ConfigDirective` | Base directiva |
| 10 | `ConfigExtension` | `ConfigExtension` | Extension de dialplan |
| 11 | `ConfigInclude` | `ConfigInclude` | #include |
| 12 | `ExecDirective` | `ExecDirective` | exec |
| 13 | `IncludeDirective` | `IncludeDirective` | include |
| 14 | `ConfigParseException` | `ConfigParseException` | Error de parsing |
| 15 | `MissingDirectiveParameterException` | `MissingDirectiveParameterException` | Error |
| 16 | `MissingEqualSignException` | `MissingEqualSignException` | Error |
| 17 | `UnknownDirectiveException` | `UnknownDirectiveException` | Error |

#### Utilidades restantes

| # | Clase Java | Clase .NET | Notas |
|---|-----------|-----------|-------|
| 18 | `AstState` | `AsteriskDeviceState` | Ya en Fase 1 |
| 19 | `AstUtil` | `AsteriskUtilities` | Ya en Fase 1 |
| 20 | `LocationAwareWrapper` | No aplica | Logging .NET lo maneja |
| 21 | `Trace` | No aplica | .NET DiagnosticSource |

### Entregables
- [x] ConfigFileReader: secciones, variables (= y =>), comentarios (;, //), inline comments, templates [name](template)
- [x] Directives: #include, #exec
- [x] ExtensionsConfigFileReader: exten =>, same =>, include =>, dialplan contexts
- [x] 15 tests: 10 config parser + 5 extensions parser (incluyendo real-world sip.conf y dialplan)

---

## Fase 10 - ARI Client (nueva funcionalidad) ✅

### Objetivo
Agregar soporte para Asterisk REST Interface (ARI), funcionalidad que **no existe** en asterisk-java.

### Componentes nuevos

| # | Clase .NET | Descripcion |
|---|-----------|-------------|
| 1 | `IAriClient` | Interface principal |
| 2 | `AriClient` | HttpClient + WebSocket |
| 3 | `AriConfiguration` | URL, user, password, app name |
| 4 | `AriWebSocketEventStream` | `WebSocketStream` de .NET 10 |
| 5 | `IAriEventHandler` | Handler de eventos Stasis |

#### ARI Resources (modelos generados desde Swagger spec de Asterisk)

| Recurso | Operaciones |
|---------|-------------|
| `Channels` | Create, Originate, Get, Hangup, Mute, Hold, Ring, SendDTMF, Play, Record, Snoop, Variable |
| `Bridges` | Create, Get, Destroy, AddChannel, RemoveChannel, Play, Record, StartMoh, StopMoh |
| `Endpoints` | List, Get, SendMessage |
| `Applications` | Get, Subscribe, Unsubscribe |
| `DeviceStates` | Get, Update, Delete |
| `Events` | WebSocket stream, UserEvent |
| `Sounds` | List, Get |
| `Playbacks` | Get, Stop, Control |
| `Recordings` | List, Get, Delete, Copy, Mute, Unmute, Pause, Resume, Stop |
| `Mailboxes` | List, Get, Update, Delete |

#### ARI Events (via WebSocket)

| Evento | Descripcion |
|--------|-------------|
| `StasisStart` | Canal entra a app Stasis |
| `StasisEnd` | Canal sale de app Stasis |
| `ChannelCreated` | Canal creado |
| `ChannelDestroyed` | Canal destruido |
| `ChannelStateChange` | Cambio de estado |
| `ChannelDtmfReceived` | DTMF recibido |
| `ChannelHangupRequest` | Solicitud de hangup |
| `ChannelDialplan` | Paso por dialplan |
| `ChannelTalkingStarted` | Inicio de audio |
| `ChannelTalkingFinished` | Fin de audio |
| `BridgeCreated` | Bridge creado |
| `BridgeDestroyed` | Bridge destruido |
| `BridgeMerged` | Bridge fusionado |
| `ChannelEnteredBridge` | Canal entra a bridge |
| `ChannelLeftBridge` | Canal sale de bridge |
| `PlaybackStarted` | Playback iniciado |
| `PlaybackFinished` | Playback terminado |
| `RecordingStarted` | Grabacion iniciada |
| `RecordingFinished` | Grabacion terminada |
| `EndpointStateChange` | Cambio de estado endpoint |
| `DeviceStateChanged` | Cambio de estado device |
| `Dial` | Marcacion |
| `ContactStatusChange` | Cambio de contacto |

### Estrategia de generacion

```bash
# Descargar Swagger spec de Asterisk
curl https://raw.githubusercontent.com/asterisk/asterisk/master/rest-api/api-docs/resources.json

# Generar modelos C# con NSwag o Kiota
# Los modelos ARI se generan automaticamente desde la spec
```

### Entregables
- [x] AriClient con HttpClient (Basic auth) + ClientWebSocket para event stream
- [x] AriChannelsResource: Create, Get, Hangup, Originate (REST)
- [x] AriBridgesResource: Create, Get, Destroy, AddChannel, RemoveChannel (REST)
- [x] AriJsonContext: source-generated JSON serialization (AOT-compatible, no reflexion)
- [x] 12 event types: StasisStart/End, ChannelStateChange, ChannelDtmfReceived, ChannelHangupRequest, BridgeCreated/Destroyed, ChannelEnteredBridge/LeftBridge, PlaybackStarted/Finished, Dial
- [x] AriPlayback model
- [x] 5 tests: serialization/deserialization de modelos ARI con source-gen JSON
- [ ] Modelos generados desde Swagger spec completo (pendiente: solo Channels/Bridges implementados)
- [ ] Tests con WireMock para HTTP simulado (pendiente Fase 11)
- [ ] Ejemplo: aplicacion Stasis basica (pendiente Fase 11)

---

## Fase 11 - Integracion, Testing y Documentacion

### Objetivo
Integrar todas las capas, completar la suite de tests y documentar la API publica.

### Tareas

#### Testing

- [ ] **Unit Tests** (por capa):
  - Transport: socket mock, pipeline parsing
  - AMI: serializacion/deserializacion de cada Action/Event/Response
  - AGI: cada comando, request parsing, reply building
  - Live: state tracking con eventos simulados
  - PBX: cada actividad con flujo completo
  - ARI: HTTP calls, WebSocket events
  - Config: parsing de archivos `.conf` reales

- [ ] **Integration Tests** (requieren Asterisk real o Docker):
  - Conexion AMI real: login, ping, core status
  - Originate: llamada entre 2 extensiones SIP
  - FastAGI: servidor recibe llamada, ejecuta script
  - ARI: crear canal, bridge, playback
  - Reconexion: simular desconexion y reconexion
  - Eventos: verificar recepcion de todos los eventos relevantes

- [ ] **Performance Tests**:
  - Benchmark: eventos por segundo vs asterisk-java
  - Memory: allocations por evento con BenchmarkDotNet
  - Latency: sendAction round-trip time
  - Throughput: concurrent sendAction calls

#### Integracion con DI

```csharp
// Extension method para IServiceCollection
services.AddDyalogoAsterisk(options =>
{
    options.Ami.Host = "192.168.1.100";
    options.Ami.Port = 5038;
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";
    options.Ami.AutoReconnect = true;

    options.Agi.Port = 4573;
    options.Agi.MappingStrategy = new SimpleMappingStrategy();

    options.Ari.BaseUrl = "http://192.168.1.100:8088";
    options.Ari.Username = "admin";
    options.Ari.Password = "secret";
    options.Ari.Application = "dyalogo";
});

// Inyeccion
public class MiServicio(IAmiConnection ami, IAgiServer agi, IAriClient ari)
{
    // ...
}
```

#### Docker para tests de integracion

```dockerfile
# docker-compose.test.yml
services:
  asterisk:
    image: andrius/asterisk:20-alpine
    ports:
      - "5038:5038"  # AMI
      - "4573:4573"  # AGI
      - "8088:8088"  # ARI
    volumes:
      - ./test-config:/etc/asterisk

  tests:
    build:
      context: .
      dockerfile: Dockerfile.test
    depends_on:
      - asterisk
    environment:
      - ASTERISK_HOST=asterisk
```

### Entregables
- Suite completa de tests (>80% coverage)
- Docker compose para tests de integracion
- Extension method para DI
- XML documentation en API publica
- README con ejemplos por capa

---

## Fase 12 - Native AOT y Optimizacion

### Objetivo
Optimizar para Native AOT, minimizar allocations, y preparar para produccion.

### Tareas

- [ ] **AOT Compatibility**:
  - Verificar que source generators cubren toda la reflexion
  - Agregar `[DynamicallyAccessedMembers]` donde sea necesario
  - Trimming analysis: `dotnet publish -c Release -r linux-x64 --self-contained`
  - Resolver warnings de trimming
  - Test: publicar como AOT y ejecutar toda la suite

- [ ] **Performance Optimization**:
  - `Span<byte>` para parsing del protocolo AMI (zero-copy)
  - `ArrayPool<byte>` para buffers temporales
  - `ValueTask` en hot paths (event dispatch)
  - `ReadOnlyMemory<char>` para strings del protocolo
  - Object pooling para eventos de alta frecuencia (`ObjectPool<T>`)
  - `FrozenDictionary<string, ...>` para registries de eventos/acciones (compile-time immutable)

- [ ] **Benchmarks finales**:
  - Comparar vs asterisk-java: throughput, latency, memory
  - Comparar AOT vs JIT: startup time, steady-state performance
  - Docker image size: AOT vs framework-dependent

- [ ] **NuGet Packaging**:
  - `Asterisk.NetAot.Ami` — AMI standalone
  - `Asterisk.NetAot.Agi` — AGI standalone
  - `Asterisk.NetAot.Live` — Live API (depende de Ami)
  - `Asterisk.NetAot.Pbx` — PBX Activities (depende de Live + Agi)
  - `Asterisk.NetAot.Ari` — ARI standalone
  - `Asterisk.NetAot.Config` — Config parser standalone
  - `Asterisk.NetAot` — Meta-paquete con todo

### Entregables
- Publicacion AOT funcional sin warnings
- Benchmarks documentados
- NuGet packages listos
- Docker image de ejemplo < 10 MB

---

## Mapeo de Tecnologias

| Java | .NET 10 | Notas |
|------|---------|-------|
| `Socket` / `BufferedReader` | `System.IO.Pipelines` | Zero-copy TCP parsing |
| `Reflections` library | Source Generators | Compile-time, AOT-safe |
| `@AsteriskMapping` annotation | `[AsteriskMapping]` attribute | Misma semantica |
| `Method.invoke()` | Source-generated delegate | Sin reflexion |
| `LinkedBlockingQueue<T>` | `Channel<T>` | Bounded/unbounded, async |
| `synchronized` | `lock` / `SemaphoreSlim` | Menos verbose |
| `ReentrantLock` | `SemaphoreSlim(1,1)` | Async-compatible |
| `CountDownLatch` | `TaskCompletionSource` | Mas idiomatico |
| `AtomicLong` | `Interlocked.Increment` | Mismo concepto |
| `volatile` | `volatile` / `Volatile.Read` | Mismo concepto |
| `ConcurrentHashMap` | `ConcurrentDictionary` | Equivalente directo |
| `Thread` (daemon) | `Task.Run` + `CancellationToken` | Mas eficiente |
| `MessageDigest` (MD5) | `MD5.HashData()` | One-liner |
| `ServerSocket` | `TcpListener` + `AcceptAsync` | Async nativo |
| `ManagerEventListener` | `IObservable<ManagerEvent>` | Reactive Extensions |
| `Logger` (Log4j) | `ILogger<T>` | Microsoft.Extensions.Logging |
| JUnit + Mockito | xUnit + NSubstitute | Equivalentes modernos |
| Maven | `dotnet` CLI + NuGet | Build + package |
| JAR | NuGet + Native AOT binary | Multiples targets |

---

## Estructura del Proyecto .NET

```
Asterisk.NetAot/
├── Asterisk.NetAot.sln
├── Directory.Build.props                          # Shared build config (net10.0, nullable, AOT)
├── Directory.Packages.props                       # Central package management
├── global.json                                    # SDK version pinning
├── .editorconfig                                  # Code style
│
├── src/                                           # ═══ Todo el codigo fuente ═══
│   │
│   ├── Asterisk.NetAot.Abstractions/
│   │   ├── Asterisk.NetAot.Abstractions.csproj
│   │   ├── IAmiConnection.cs
│   │   ├── IAgiServer.cs
│   │   ├── IAriClient.cs
│   │   ├── Attributes/
│   │   │   ├── AsteriskMappingAttribute.cs
│   │   │   └── AsteriskVersionAttribute.cs
│   │   └── Enums/
│   │       ├── AsteriskDeviceState.cs
│   │       ├── ChannelState.cs
│   │       ├── HangupCause.cs
│   │       └── ...
│   │
│   ├── Asterisk.NetAot.Ami/
│   │   ├── Asterisk.NetAot.Ami.csproj
│   │   ├── AmiConnection.cs
│   │   ├── AmiConnectionOptions.cs
│   │   ├── Actions/
│   │   │   ├── ManagerAction.cs                   # Base abstracta
│   │   │   ├── OriginateAction.cs
│   │   │   ├── HangupAction.cs
│   │   │   └── ... (115 archivos)
│   │   ├── Events/
│   │   │   ├── ManagerEvent.cs                    # Base abstracta
│   │   │   ├── NewChannelEvent.cs
│   │   │   ├── HangupEvent.cs
│   │   │   └── ... (235 archivos)
│   │   ├── Responses/
│   │   │   ├── ManagerResponse.cs
│   │   │   └── ... (18 archivos)
│   │   ├── Internal/
│   │   │   ├── AmiProtocolReader.cs
│   │   │   ├── AmiProtocolWriter.cs
│   │   │   ├── AsyncEventPump.cs
│   │   │   └── ...
│   │   └── Transport/
│   │       ├── PipelineSocketConnection.cs
│   │       └── AsyncServerSocket.cs
│   │
│   ├── Asterisk.NetAot.Ami.SourceGenerators/
│   │   ├── Asterisk.NetAot.Ami.SourceGenerators.csproj
│   │   ├── ActionSerializerGenerator.cs
│   │   ├── EventDeserializerGenerator.cs
│   │   └── EventRegistryGenerator.cs
│   │
│   ├── Asterisk.NetAot.Agi/
│   │   ├── Asterisk.NetAot.Agi.csproj
│   │   ├── FastAgiServer.cs
│   │   ├── Commands/
│   │   │   └── ... (54 archivos)
│   │   ├── Mapping/
│   │   │   └── ... (8 archivos)
│   │   └── Server/
│   │       └── ...
│   │
│   ├── Asterisk.NetAot.Live/
│   │   ├── Asterisk.NetAot.Live.csproj
│   │   ├── AsteriskServer.cs
│   │   ├── Channels/
│   │   ├── Queues/
│   │   ├── Agents/
│   │   └── MeetMe/
│   │
│   ├── Asterisk.NetAot.Pbx/
│   │   ├── Asterisk.NetAot.Pbx.csproj
│   │   ├── Activities/
│   │   │   ├── DialActivity.cs
│   │   │   ├── HoldActivity.cs
│   │   │   ├── BridgeActivity.cs
│   │   │   ├── BlindTransferActivity.cs
│   │   │   └── ...
│   │   ├── Models/
│   │   │   ├── Call.cs
│   │   │   ├── EndPoint.cs
│   │   │   └── ...
│   │   └── Agi/
│   │       └── ...
│   │
│   ├── Asterisk.NetAot.Ari/
│   │   ├── Asterisk.NetAot.Ari.csproj
│   │   ├── AriClient.cs
│   │   ├── Resources/
│   │   │   ├── ChannelsResource.cs
│   │   │   ├── BridgesResource.cs
│   │   │   └── ...
│   │   ├── Events/
│   │   │   └── ...
│   │   └── Models/
│   │       └── ...
│   │
│   ├── Asterisk.NetAot.Config/
│   │   ├── Asterisk.NetAot.Config.csproj
│   │   └── ...
│   │
│   └── Asterisk.NetAot/                           # Meta-package
│       └── Asterisk.NetAot.csproj
│
├── Tests/                                         # ═══ Tests en raiz del proyecto ═══
│   ├── Asterisk.NetAot.Ami.Tests/
│   │   ├── Asterisk.NetAot.Ami.Tests.csproj
│   │   ├── Actions/
│   │   ├── Events/
│   │   ├── Responses/
│   │   └── Internal/
│   ├── Asterisk.NetAot.Agi.Tests/
│   │   ├── Asterisk.NetAot.Agi.Tests.csproj
│   │   ├── Commands/
│   │   └── Server/
│   ├── Asterisk.NetAot.Live.Tests/
│   │   ├── Asterisk.NetAot.Live.Tests.csproj
│   │   ├── Channels/
│   │   ├── Queues/
│   │   └── Agents/
│   ├── Asterisk.NetAot.Pbx.Tests/
│   │   ├── Asterisk.NetAot.Pbx.Tests.csproj
│   │   └── Activities/
│   ├── Asterisk.NetAot.Ari.Tests/
│   │   └── Asterisk.NetAot.Ari.Tests.csproj
│   ├── Asterisk.NetAot.Config.Tests/
│   │   └── Asterisk.NetAot.Config.Tests.csproj
│   ├── Asterisk.NetAot.IntegrationTests/
│   │   ├── Asterisk.NetAot.IntegrationTests.csproj
│   │   └── docker-compose.test.yml
│   └── Asterisk.NetAot.Benchmarks/
│       ├── Asterisk.NetAot.Benchmarks.csproj
│       └── ...
│
├── Examples/                                      # ═══ Ejemplos en raiz del proyecto ═══
│   ├── BasicAmiExample/
│   │   ├── BasicAmiExample.csproj
│   │   └── Program.cs
│   ├── FastAgiServerExample/
│   │   ├── FastAgiServerExample.csproj
│   │   └── Program.cs
│   ├── LiveApiExample/
│   │   ├── LiveApiExample.csproj
│   │   └── Program.cs
│   ├── PbxActivitiesExample/
│   │   ├── PbxActivitiesExample.csproj
│   │   └── Program.cs
│   └── AriStasisExample/
│       ├── AriStasisExample.csproj
│       └── Program.cs
│
└── docker/
    ├── docker-compose.test.yml
    └── test-config/
        └── ... (archivos .conf de Asterisk para tests)
```

---

## Riesgos y Mitigaciones

| # | Riesgo | Impacto | Probabilidad | Mitigacion |
|---|--------|---------|-------------|------------|
| 1 | Source generators no cubren todos los casos de reflexion | Alto | Media | Auditar todos los usos de reflexion en Java antes de empezar; crear spike de source generator en Fase 2 |
| 2 | Protocolo AMI no tiene spec formal; asterisk-java es la "spec" | Alto | Baja | Usar dumps reales de trafico AMI como test fixtures; mantener compatibilidad de nombres de campos |
| 3 | Backward compatibility con Asterisk < 12 (bridging model change) | Medio | Media | Implementar LegacyEventAdapter igual que Java; testear con multiples versiones de Asterisk en Docker |
| 4 | PBX Activities tienen logica compleja de state machine | Alto | Alta | Portar con tests exhaustivos; usar diagramas de estado para documentar cada actividad |
| 5 | ARI Swagger spec puede cambiar entre versiones | Medio | Media | Versionado de spec; generar para Asterisk 18/20/22 |
| 6 | Performance regression vs Java en hot paths | Medio | Baja | BenchmarkDotNet desde Fase 2; comparar con asterisk-java en cada fase |
| 7 | Volumen de trabajo repetitivo (350+ POCOs) | Medio | Alta | Crear scripts de generacion de codigo desde las clases Java; T4 templates o scaffolding |

---

## Criterios de Aceptacion

### Por fase

| Fase | Criterio |
|------|----------|
| 1 | Socket TCP conecta a Asterisk, lee/escribe via Pipelines, tests pasan |
| 2 | Login AMI, sendAction, recepcion de eventos, reconnect automatico |
| 3 | Las 115 actions se serializan correctamente (comparar output con Java) |
| 4 | Los 235 events se deserializan correctamente desde dumps AMI reales |
| 5 | Flujo completo: Action -> Response -> ResponseEvents funciona |
| 6 | FastAGI server acepta conexion de Asterisk, ejecuta script, AGI commands funcionan |
| 7 | AsteriskServer trackea canales/colas/agentes con eventos en tiempo real |
| 8 | Flujo: Originate -> Dial -> Bridge -> Transfer -> Hangup via Activities |
| 9 | Parser lee `sip.conf`, `extensions.conf` reales sin errores |
| 10 | ARI: crear canal, bridge, playback, recibir eventos WebSocket |
| 11 | >80% code coverage, tests de integracion con Asterisk Docker pasan |
| 12 | Publicacion AOT sin warnings, Docker image < 10 MB, benchmarks documentados |

### Globales

- [ ] Todos los tests pasan en CI (GitHub Actions)
- [ ] Zero reflexion en runtime (verificable con ILSpy/dotnet-trace)
- [ ] API publica documentada con XML comments
- [ ] NuGet packages publicables
- [ ] Ejemplo funcional de cada capa
- [ ] Benchmark comparativo vs asterisk-java documentado
- [ ] Compatible con .NET 10 LTS (soporte hasta Nov 2028)

---

## Resumen de clases por fase

| Fase | Nuevas | Portadas | Total | Estado |
|------|--------|----------|-------|--------|
| 1 - Fundacion | 10 | 8 | 18 | ✅ Completada |
| 2 - AMI Core | 16 | 10 | 26 | ✅ Completada |
| 3 - Actions | 1 | 111 | 112 | ✅ Completada |
| 4 - Events | 9 | 214 | 223 | ✅ Completada |
| 5 - Responses | 3 | 17 | 20 | ✅ Completada |
| 6 - FastAGI | 7 | 57 | 64 | ✅ Completada |
| 7 - Live API | 8 | 0 | 8 | ✅ Completada |
| 8 - PBX | 15 | 4 | 19 | ✅ Completada |
| 9 - Config | 2 | 0 | 2 | ✅ Completada |
| 10 - ARI | 7 | 0 | 7 | ✅ Completada |
| 11 - Testing | ~100 | 0 | ~100 | Pendiente |
| 12 - AOT | 0 | 0 | Transversal | Pendiente |
| **TOTAL** | **~178** | **~625** | **~803** | **10/12 completadas** |
