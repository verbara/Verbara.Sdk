# Propuesta: Call Flow Visualization para DashboardExample

## Contexto

**sngrep** muestra flujos SIP como diagramas de secuencia (ladder diagrams) capturando paquetes de red. Nosotros no tenemos acceso a paquetes SIP crudos, pero tenemos **222 eventos AMI** que nos dan visibilidad completa del ciclo de vida de cada llamada: desde `NewChannel` hasta `Hangup`, pasando por `Dial`, `Bridge`, `Hold`, `Transfer` y `DTMF`.

La ventaja sobre sngrep: podemos **correlacionar** llamadas con colas, agentes, conferencias y métricas en tiempo real, algo que sngrep no puede hacer.

---

## Propuestas

### Propuesta A: Call Flow Ladder Diagram (estilo sngrep)

**Concepto:** Diagrama de secuencia vertical donde cada columna es un endpoint (PJSIP/2001, PJSIP/3000, Queue/ventas) y las flechas muestran el flujo temporal de eventos.

```
  PJSIP/1000        Asterisk         Queue/ventas      PJSIP/2001
      │                │                  │                │
      │──INVITE───────>│                  │                │
      │                │──JoinQueue──────>│                │
      │<──180 Ring─────│                  │                │
      │                │                  │──Ring─────────>│
      │                │                  │<──200 OK───────│
      │                │<──AgentConnect───│                │
      │<══════════ BRIDGE ═══════════════════════════════>│
      │                │                  │                │
      │──DTMF '#'─────>│                  │                │
      │                │                  │                │
      │                │──HOLD───────────────────────────>│
      │                │──UNHOLD─────────────────────────>│
      │                │                  │                │
      │──BYE──────────>│                  │                │
      │                │──Hangup─────────────────────────>│
      │                │                  │                │
    ─────────────────────────────────────────────────────────
    00:00             00:02             00:05            00:35
```

**Qué se ve:**
- Columnas por cada endpoint participante (canales SIP, colas, conferencias)
- Flechas direccionales con el tipo de evento
- Bridges como barras dobles (`═══`) indicando audio bidireccional
- Hold/Unhold como segmentos especiales
- DTMF como anotaciones
- Timeline vertical con timestamps relativos
- Color-coded: verde=establecimiento, azul=bridge, naranja=hold, rojo=hangup

**Datos necesarios:** NewChannel, Dial, Bridge*, Hold/Unhold, DTMF, Transfer*, Hangup
**Complejidad:** Alta (renderizado SVG/Canvas dinámico)

---

### Propuesta B: Call Timeline + Agent Correlation

**Concepto:** Vista horizontal tipo Gantt donde cada fila es un canal activo y las barras muestran las fases de la llamada. Los agentes aparecen vinculados a sus canales.

```
┌─────────────────────────────────────────────────────────────────────┐
│ Call: PJSIP/1000-00000042 → Queue/ventas → PJSIP/2001-00000043    │
│ CallerID: +5491155551234  Duration: 03:25  Status: Active          │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│ PJSIP/1000  [████ Dialing ████|████████ Ringing ████|══════════ Connected ══════════|  │
│                                                                     │
│ Queue/ventas         [░░░ Waiting (12s) ░░░|                       │
│                                                                     │
│ PJSIP/2001                  [████ Ring ████|══════════ Connected ══════════|          │
│ Agent: María (2001)                        ▲ Answered               │
│                                            │                        │
│ Events: ── Dial ── QueueJoin ── AgentConnect ── Bridge ── DTMF(5) ──│
│                                                                     │
│ 0s        5s         12s        15s        20s       ...     3:25   │
└─────────────────────────────────────────────────────────────────────┘
```

**Qué se ve:**
- Barra horizontal por cada canal participante
- Segmentos coloreados por estado: Dialing, Ringing, Connected, Hold, Hangup
- Fila de cola mostrando tiempo de espera
- Agente vinculado al canal con nombre y extensión
- Línea de eventos debajo como marcadores
- Panel lateral con detalles: CallerID, duración, cola, agente, hangup cause

**Datos necesarios:** Channels + LinkedChannel + Queue events + Agent events
**Complejidad:** Media (CSS flex/grid + timer updates)

---

### Propuesta C: Live Call Matrix (vista operativa)

**Concepto:** Matriz en tiempo real estilo "wall board" donde cada llamada activa es una tarjeta que muestra el flujo completo con los participantes conectados. Ideal para call centers.

```
┌──────────────────────────────────┐  ┌──────────────────────────────────┐
│ 📞 Call #42              00:03:25 │  │ 📞 Call #43              00:01:12 │
│ ┌────────┐    ┌──────────┐       │  │ ┌────────┐    ┌──────────┐       │
│ │ Caller │───>│  Agent   │       │  │ │ Caller │    │ Queue    │       │
│ │ +54911 │◄───│ María    │       │  │ │ +54911 │───>│ ventas   │       │
│ │ 1000   │ 🔊 │ 2001     │       │  │ │ 3000   │    │ 3 wait   │       │
│ └────────┘    └──────────┘       │  │ └────────┘    └──────────┘       │
│ Queue: ventas  Wait: 12s         │  │ Position: 4   Wait: 01:12        │
│ [Hold] [Transfer] [Hangup]       │  │ [Priority] [Remove]              │
└──────────────────────────────────┘  └──────────────────────────────────┘

┌──────────────────────────────────┐  ┌──────────────────────────────────┐
│ 📞 Call #44         🔇 ON HOLD   │  │ 🔀 Transfer #45         00:00:05 │
│ ┌────────┐    ┌──────────┐       │  │ ┌────────┐ ┌──────┐ ┌────────┐  │
│ │ Caller │    │  Agent   │       │  │ │ Caller │→│Agent1│→│ Agent2 │  │
│ │ +54911 │    │ Pedro    │       │  │ │ +54911 │ │María │ │ Pedro  │  │
│ │ 4000   │ 🎵│ 2002     │       │  │ │ 1000   │ │ 2001 │ │ 2002   │  │
│ └────────┘    └──────────┘       │  │ └────────┘ └──────┘ └────────┘  │
│ Hold: 00:45   Music: default     │  │ Type: Attended  Status: Ringing  │
│ [Unhold] [Transfer] [Hangup]     │  └──────────────────────────────────┘
└──────────────────────────────────┘
```

**Qué se ve:**
- Card por cada llamada activa con participantes como nodos
- Flechas de conexión entre caller ↔ agent
- Estados visuales: Connected (verde), Hold (naranja), Transfer (azul), Ringing (pulsante)
- Info de cola: tiempo de espera, posición
- Acciones rápidas: Hold, Transfer, Hangup (via AMI actions)
- Contador de duración en tiempo real

**Datos necesarios:** ActiveChannels + LinkedChannel + Queue members + Agents
**Complejidad:** Media-baja (reusa patrones existentes de cards)

---

### Propuesta D: Hybrid — Todo combinado (Recomendada)

**Concepto:** Combinar las tres propuestas en una sola página `/calls` con tres niveles de detalle:

1. **Vista general** (Propuesta C): Grid de llamadas activas como cards
2. **Click en una llamada** → **Timeline** (Propuesta B): Gantt horizontal con fases
3. **Click en "Flow"** → **Ladder Diagram** (Propuesta A): Diagrama de secuencia completo

```
/calls                          → Live Call Matrix (cards grid)
/calls/{uniqueId}               → Call Detail con Timeline
/calls/{uniqueId}?view=flow     → Ladder Diagram SVG
```

**Arquitectura adicional necesaria:**

```
CallFlowTracker (nuevo servicio)
├── Captura eventos ordenados por LinkedId/UniqueId
├── Mantiene historial de últimas N llamadas (configurable)
├── Estructura: Dictionary<string, CallFlow>
│   └── CallFlow
│       ├── CallId (LinkedId o primer UniqueId)
│       ├── StartTime, EndTime, Duration
│       ├── State (Dialing, Ringing, Connected, Hold, Completed)
│       ├── Caller (CallerID, Channel)
│       ├── Destination (Channel, Agent?, Queue?)
│       ├── List<CallFlowEvent> Events  ← timeline ordenada
│       │   └── CallFlowEvent { Timestamp, Type, Source, Target, Data }
│       ├── List<CallParticipant> Participants
│       │   └── CallParticipant { Channel, Role, JoinedAt, LeftAt }
│       └── HangupCause
└── Expone: GetActiveCalls(), GetCallById(), GetRecentCalls(n)
```

---

## Comparativa

| Criterio | A: Ladder | B: Timeline | C: Matrix | D: Hybrid |
|----------|-----------|-------------|-----------|-----------|
| Similitud a sngrep | ⭐⭐⭐⭐⭐ | ⭐⭐ | ⭐ | ⭐⭐⭐⭐⭐ |
| Utilidad operativa | ⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| Correlación con agentes | ⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| Complejidad implementación | Alta | Media | Media-baja | Alta |
| Valor para call center | ⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| Sprints estimados | 2 | 1.5 | 1 | 3-4 |

---

## Recomendación

**Propuesta D (Hybrid)** implementada de forma incremental: Sprint 0 extiende el modelo de agente con estadisticas operativas (contadores, timers), Sprint 1 agrega la Matrix, Sprint 2 el Timeline, Sprint 3-4 el Ladder y la integracion completa. Cada sprint entrega valor funcional independiente.

---

## Plan de Trabajo

### Sprint 0: Agent Statistics — Contadores y Timers por Agente

**Objetivo:** Extender `AsteriskAgent` en la capa Live con metricas operativas: llamadas atendidas, tiempo disponible, tiempo en llamada, ultimo cambio de estado. Mostrar en el dashboard.

**Justificacion:** Actualmente `AsteriskAgent` solo tiene `State`, `Channel`, `TalkingTo` y `LoggedInAt`. No hay contadores ni forma de saber cuanto tiempo lleva un agente en su estado actual. Los eventos `AgentConnectEvent` (tiene `HoldTime`, `Ringtime`) y `AgentCompleteEvent` (tiene `TalkTime`, `HoldTime`) ya traen estos datos pero se descartan.

#### Tarea 0.1: Extender modelo `AsteriskAgent`
**Archivo a modificar:**
- `src/Asterisk.Sdk.Live/Agents/AgentManager.cs`

**Nuevas propiedades en `AsteriskAgent`:**
```csharp
public sealed class AsteriskAgent : LiveObjectBase
{
    // ... propiedades existentes ...

    /// <summary>Timestamp of the last state transition (Available, OnCall, Paused).</summary>
    public DateTimeOffset? LastStateChangeAt { get; set; }

    /// <summary>Total calls answered by this agent since login.</summary>
    public int CallsTaken { get; set; }

    /// <summary>Total talk time in seconds accumulated since login.</summary>
    public long TotalTalkTimeSecs { get; set; }

    /// <summary>Total hold time in seconds accumulated since login.</summary>
    public long TotalHoldTimeSecs { get; set; }

    /// <summary>Talk time of the last completed call in seconds.</summary>
    public long LastCallTalkTimeSecs { get; set; }

    /// <summary>Duration the agent has been in the current state.</summary>
    public TimeSpan StateElapsed => LastStateChangeAt.HasValue
        ? DateTimeOffset.UtcNow - LastStateChangeAt.Value
        : TimeSpan.Zero;

    /// <summary>Average talk time per call in seconds (0 if no calls taken).</summary>
    public double AvgTalkTimeSecs => CallsTaken > 0
        ? (double)TotalTalkTimeSecs / CallsTaken
        : 0;
}
```

#### Tarea 0.2: Actualizar `AgentManager` para trackear stats
**Archivo a modificar:**
- `src/Asterisk.Sdk.Live/Agents/AgentManager.cs`

**Cambios en los metodos existentes:**

```csharp
// OnAgentLogin — resetear contadores, registrar timestamp
public void OnAgentLogin(string agentId, string? channel = null)
{
    var agent = _agents.GetOrAdd(agentId, _ => new AsteriskAgent { AgentId = agentId });
    lock (agent.SyncRoot)
    {
        agent.State = AgentState.Available;
        agent.Channel = channel;
        agent.LoggedInAt = DateTimeOffset.UtcNow;
        agent.LastStateChangeAt = DateTimeOffset.UtcNow;
        agent.CallsTaken = 0;           // reset on login
        agent.TotalTalkTimeSecs = 0;
        agent.TotalHoldTimeSecs = 0;
        agent.LastCallTalkTimeSecs = 0;
    }
    AgentLoggedIn?.Invoke(agent);
}

// OnAgentConnect — registrar transicion a OnCall
public void OnAgentConnect(string agentId, string? talkingTo = null)
{
    if (_agents.TryGetValue(agentId, out var agent))
    {
        lock (agent.SyncRoot)
        {
            agent.State = AgentState.OnCall;
            agent.TalkingTo = talkingTo;
            agent.LastStateChangeAt = DateTimeOffset.UtcNow;
        }
        AgentStateChanged?.Invoke(agent);
    }
}

// OnAgentComplete — incrementar contador, acumular TalkTime, transicion a Available
public void OnAgentComplete(string agentId, long talkTimeSecs = 0, long holdTimeSecs = 0)
{
    if (_agents.TryGetValue(agentId, out var agent))
    {
        lock (agent.SyncRoot)
        {
            agent.State = AgentState.Available;
            agent.TalkingTo = null;
            agent.LastStateChangeAt = DateTimeOffset.UtcNow;
            agent.CallsTaken++;
            agent.LastCallTalkTimeSecs = talkTimeSecs;
            agent.TotalTalkTimeSecs += talkTimeSecs;
            agent.TotalHoldTimeSecs += holdTimeSecs;
        }
        AgentStateChanged?.Invoke(agent);
    }
}

// OnAgentPaused — registrar transicion
public void OnAgentPaused(string agentId, bool paused)
{
    if (_agents.TryGetValue(agentId, out var agent))
    {
        lock (agent.SyncRoot)
        {
            agent.State = paused ? AgentState.Paused : AgentState.Available;
            agent.LastStateChangeAt = DateTimeOffset.UtcNow;
        }
        AgentStateChanged?.Invoke(agent);
    }
}

// OnAgentLogoff — registrar transicion (NO resetear contadores, queremos ver el historico)
public void OnAgentLogoff(string agentId)
{
    if (_agents.TryGetValue(agentId, out var agent))
    {
        lock (agent.SyncRoot)
        {
            agent.State = AgentState.LoggedOff;
            agent.Channel = null;
            agent.LastStateChangeAt = DateTimeOffset.UtcNow;
        }
        AgentLoggedOff?.Invoke(agent);
    }
}
```

#### Tarea 0.3: Pasar TalkTime/HoldTime desde el EventObserver
**Archivo a modificar:**
- `src/Asterisk.Sdk.Live/Server/AsteriskServer.cs`

**Cambio en el case `AgentCompleteEvent`:**
```csharp
// Antes:
case AgentCompleteEvent acoe:
    server.Agents.OnAgentComplete(acoe.Agent ?? "");
    break;

// Despues:
case AgentCompleteEvent acoe:
    server.Agents.OnAgentComplete(
        acoe.Agent ?? "",
        acoe.TalkTime ?? 0,
        acoe.HoldTime ?? 0);
    break;
```

#### Tarea 0.4: Mostrar stats en `Agents.razor`
**Archivo a modificar:**
- `Examples/DashboardExample/Components/Pages/Agents.razor`

**Cambios en el card del agente:**
```html
<div class="agent-card-body">
    <div>State: @agent.State
        @if (agent.LastStateChangeAt.HasValue)
        {
            <span class="text-muted">(@FormatDuration(agent.StateElapsed))</span>
        }
    </div>
    @if (agent.State != AgentState.LoggedOff)
    {
        <div>Calls: @agent.CallsTaken
            @if (agent.CallsTaken > 0)
            {
                <span class="text-muted">
                    (avg @FormatSeconds((int)agent.AvgTalkTimeSecs))
                </span>
            }
        </div>
    }
    @if (agent.State == AgentState.OnCall && agent.TalkingTo is not null)
    {
        <div>Talking to: @agent.TalkingTo</div>
    }
    @if (agent.LoggedInAt.HasValue)
    {
        <div>Logged in: @FormatTimeAgo(agent.LoggedInAt.Value)</div>
    }
    <!-- ... queues existente ... -->
</div>
```

**Nuevos KPIs en la barra superior:**
```html
<div class="kpi-card kpi-blue">
    <div class="kpi-value">@TotalCallsTaken()</div>
    <div class="kpi-label">Calls Taken</div>
</div>
```

#### Tarea 0.5: Mostrar stats en `QueueDetail.razor` (miembros de cola)
**Archivo a modificar:**
- `Examples/DashboardExample/Components/Pages/QueueDetail.razor`

**Cambio:** El `AsteriskQueueMember` ya tiene `CallsTaken` y `Status`. Mostrar el estado del agente con su timer si podemos correlacionar con el `AgentManager`.

#### Tarea 0.6: Tests unitarios
**Archivo nuevo:**
- `Tests/Asterisk.Sdk.Live.Tests/Agents/AgentStatisticsTests.cs`

**Tests:**
- `OnAgentLogin_ShouldResetCountersAndSetTimestamp`
- `OnAgentComplete_ShouldIncrementCallsTakenAndAccumulateTalkTime`
- `OnAgentComplete_ShouldCalculateCorrectAverage`
- `OnAgentConnect_ShouldUpdateLastStateChangeAt`
- `OnAgentPaused_ShouldUpdateLastStateChangeAt`
- `OnAgentLogoff_ShouldPreserveCounters`
- `StateElapsed_ShouldReturnCorrectDuration`

**Criterio de aceptacion:**
- Contadores se incrementan correctamente al completar llamadas
- `StateElapsed` refleja el tiempo real en el estado actual
- `AvgTalkTimeSecs` se calcula correctamente con division segura
- Los contadores se resetean al login, pero NO al logoff
- Thread-safe: todas las mutaciones dentro de `lock (agent.SyncRoot)`
- Build 0 warnings, todos los tests pasan

---

### Sprint 1: Call Flow Tracker + Live Call Matrix

**Objetivo:** Servicio de tracking de llamadas + vista de cards de llamadas activas.

#### Tarea 1.1: CallFlowTracker Service
**Archivos nuevos:**
- `Examples/DashboardExample/Services/CallFlowTracker.cs`

**Modelo de datos:**
```csharp
public sealed class CallFlow
{
    public string CallId { get; init; }           // LinkedId or first UniqueId
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; set; }
    public CallFlowState State { get; set; }       // Dialing, Ringing, Queued, Connected, Hold, Completed
    public CallParticipant Caller { get; init; }
    public CallParticipant? Destination { get; set; }
    public string? QueueName { get; set; }
    public string? AgentName { get; set; }
    public string? AgentInterface { get; set; }
    public HangupCause? HangupCause { get; set; }
    public List<CallFlowEvent> Events { get; } = [];
}

public sealed class CallParticipant
{
    public string Channel { get; init; }
    public string UniqueId { get; init; }
    public string? CallerIdNum { get; set; }
    public string? CallerIdName { get; set; }
    public ChannelState State { get; set; }
}

public sealed class CallFlowEvent
{
    public DateTimeOffset Timestamp { get; init; }
    public CallFlowEventType Type { get; init; }   // NewChannel, Dial, Ring, Answer, Bridge, Hold, Unhold, DTMF, Transfer, Hangup
    public string? Source { get; init; }
    public string? Target { get; init; }
    public string? Detail { get; init; }            // e.g., DTMF digit, HangupCause, Queue name
}
```

**Implementación:**
- Subscribirse como `IObserver<ManagerEvent>` en `AsteriskMonitorService`
- Capturar: NewChannel, Dial, NewState, BridgeEnter/Leave, Hold/Unhold, DtmfEnd, QueueCallerJoin/Leave, AgentConnect, BlindTransfer, AttendedTransfer, Hangup
- Agrupar por `LinkedId` (Asterisk vincula todos los canales de una llamada con el mismo LinkedId)
- Buffer circular: últimas 500 llamadas (configurable)
- Thread-safe con `ConcurrentDictionary<string, CallFlow>`

**Criterio de aceptación:**
- El servicio captura y correlaciona eventos correctamente
- Las llamadas completadas se mantienen en buffer por 5 minutos
- Thread-safe para acceso concurrente desde Blazor

#### Tarea 1.2: Página Live Call Matrix (`/calls`)
**Archivos nuevos:**
- `Examples/DashboardExample/Components/Pages/Calls.razor`

**UI:**
- Grid de cards (reutilizando `.card-grid` existente)
- Cada card muestra: Caller ↔ Agent/Destination, estado, duración, cola
- Filtros: All / Active / Queued / On Hold / Completed
- Color-coded por estado (reutilizando CSS vars existentes)
- Auto-refresh cada 1 segundo (patrón existente con Timer)
- Click en card → navega a `/calls/{callId}`

**Criterio de aceptación:**
- Se ven todas las llamadas activas en tiempo real
- Los estados se actualizan automáticamente
- Las llamadas completadas desaparecen después de 30 segundos

#### Tarea 1.3: Agregar al sidebar de navegación
**Archivo a modificar:**
- `Examples/DashboardExample/Components/Layout/MainLayout.razor`

**Cambio:** Agregar link "Calls" en la navegación del sidebar.

#### Tarea 1.4: CSS para call cards
**Archivo a modificar:**
- `Examples/DashboardExample/wwwroot/css/dashboard.css`

**Nuevas clases:**
- `.call-card` — card base con borde izquierdo coloreado por estado
- `.call-participants` — layout flex para caller ↔ destination
- `.call-arrow` — flecha animada de conexión
- `.call-state-badge` — badge con el estado actual
- `.call-duration` — timer en la esquina

---

### Sprint 2: Call Detail + Timeline View

**Objetivo:** Vista detallada de una llamada individual con timeline Gantt horizontal.

#### Tarea 2.1: Página Call Detail (`/calls/{callId}`)
**Archivos nuevos:**
- `Examples/DashboardExample/Components/Pages/CallDetail.razor`

**UI — Panel superior (resumen):**
- Caller info (CallerID, Channel, estado)
- Destination info (Agent, Channel, estado)
- Cola (si aplica): nombre, tiempo de espera
- Duración total, estado actual
- HangupCause (si terminada)

**UI — Timeline Gantt:**
- Eje horizontal = tiempo (relativo al inicio de la llamada)
- Una fila por cada participante (canal)
- Segmentos coloreados por estado:
  - Gris claro = Dialing
  - Azul pulsante = Ringing
  - Verde = Connected/Bridged
  - Naranja = On Hold
  - Rojo = Hangup
- Marcadores de eventos sobre la timeline: DTMF, Transfer, QueueJoin, etc.
- Auto-scroll horizontal para llamadas en curso

**Criterio de aceptación:**
- Se ve el timeline actualizado en tiempo real
- Los segmentos de estado son correctos y proporcionales
- Los marcadores de eventos son clickeables con tooltip de detalle

#### Tarea 2.2: Panel de eventos (event log de la llamada)
**Parte de CallDetail.razor:**
- Lista cronológica de todos los `CallFlowEvent` de esta llamada
- Formato: `[00:05.2] Dial → PJSIP/2001 (Ring)`
- Scroll automático al último evento
- Iconos por tipo de evento

#### Tarea 2.3: CSS para timeline
**Archivo a modificar:**
- `Examples/DashboardExample/wwwroot/css/dashboard.css`

**Nuevas clases:**
- `.timeline-container` — contenedor con scroll horizontal
- `.timeline-row` — fila por participante
- `.timeline-segment` — segmento coloreado (proporcional al tiempo)
- `.timeline-marker` — marcador de evento con tooltip
- `.timeline-axis` — eje temporal con ticks

---

### Sprint 3: Ladder Diagram (estilo sngrep)

**Objetivo:** Diagrama de secuencia SVG/CSS renderizado desde los eventos capturados.

#### Tarea 3.1: Componente LadderDiagram
**Archivos nuevos:**
- `Examples/DashboardExample/Components/Shared/LadderDiagram.razor`

**Renderizado con CSS puro (sin dependencias JS):**
- Columnas fijas por participante (ancho proporcional)
- Líneas verticales punteadas (lifelines)
- Flechas horizontales SVG inline entre columnas
- Labels sobre las flechas (tipo de evento)
- Bloques de activación (barras verticales durante Bridge)
- Timestamps en el margen izquierdo

**Algoritmo de layout:**
1. Identificar participantes únicos → columnas
2. Ordenar eventos por timestamp
3. Para cada evento, dibujar flecha de source → target
4. Bridges = barras verticales gruesas entre las dos columnas
5. Hold = cambio de color en la barra de bridge

**Criterio de aceptación:**
- Diagrama legible con hasta 5 participantes
- Scroll vertical para llamadas largas
- Flechas correctamente direccionadas
- Bridges visualmente conectados

#### Tarea 3.2: Botón "Flow" en CallDetail
**Archivo a modificar:**
- `Examples/DashboardExample/Components/Pages/CallDetail.razor`

**Cambio:** Toggle entre vista Timeline y vista Ladder con tabs.

#### Tarea 3.3: CSS para ladder diagram
**Archivo a modificar:**
- `Examples/DashboardExample/wwwroot/css/dashboard.css`

**Nuevas clases:**
- `.ladder-container` — grid con columnas por participante
- `.ladder-lifeline` — línea vertical punteada
- `.ladder-arrow` — flecha horizontal con label
- `.ladder-bridge` — barra vertical gruesa (activación)
- `.ladder-timestamp` — timestamp en margen izquierdo

---

### Sprint 4: Polish + Agent Integration

**Objetivo:** Integración profunda con agentes/colas y refinamientos.

#### Tarea 4.1: Agent Correlation Panel
**En CallDetail.razor:**
- Si la llamada pasó por una cola, mostrar:
  - Nombre de la cola, estrategia, posición del caller
  - Agente que contestó, tiempo de respuesta
  - Otros agentes que fueron ringueados pero no contestaron (si disponible via AgentRingNoAnswer events)
- Si el agente está en la vista de Agents.razor, link bidireccional

#### Tarea 4.2: Click desde Agents/Queues → Call Detail
**Archivos a modificar:**
- `Examples/DashboardExample/Components/Pages/Agents.razor`
- `Examples/DashboardExample/Components/Pages/QueueDetail.razor`

**Cambio:**
- Si un agente está OnCall, mostrar link a la llamada activa
- Si un caller está esperando en cola, mostrar link a su call flow
- En el card del agente, mostrar mini-timeline de la llamada actual

#### Tarea 4.3: Call History con búsqueda
**En `/calls`:**
- Tab "Recent" con las últimas 100 llamadas completadas
- Búsqueda por CallerID, agente, cola
- Filtros por duración, hangup cause, cola

#### Tarea 4.4: KPI de Call Flow en Home
**Archivo a modificar:**
- `Examples/DashboardExample/Components/Pages/Home.razor`

**Nuevos KPIs:**
- Avg Wait Time (colas)
- Avg Talk Time
- Abandonment Rate
- Calls/Hour

---

## Resumen de Archivos por Sprint

| Sprint | Archivos Nuevos | Archivos Modificados |
|--------|----------------|---------------------|
| **0** | `AgentStatisticsTests.cs` | `AgentManager.cs` (modelo + metodos), `AsteriskServer.cs` (EventObserver), `Agents.razor`, `QueueDetail.razor` |
| **1** | `CallFlowTracker.cs`, `CallFlow.cs` (modelos), `Calls.razor` | `MainLayout.razor`, `dashboard.css`, `AsteriskMonitorService.cs`, `Program.cs` |
| **2** | `CallDetail.razor` | `dashboard.css` |
| **3** | `LadderDiagram.razor` | `CallDetail.razor`, `dashboard.css` |
| **4** | — | `Agents.razor`, `QueueDetail.razor`, `Home.razor`, `Calls.razor` |

## Dependencias Externas

**Ninguna.** Todo se implementa con:
- Blazor Server SSR (ya configurado)
- CSS puro + SVG inline (para el ladder diagram)
- Patrones existentes del dashboard (Timer refresh, card grid, data tables)
- Eventos AMI ya disponibles en el SDK

## Riesgos

| Riesgo | Mitigación |
|--------|-----------|
| LinkedId no siempre está disponible en todos los eventos | Fallback a correlación por UniqueId + Channel name |
| Memoria: muchas llamadas simultáneas | Buffer circular configurable, eviction de llamadas completadas > 5min |
| Ladder diagram complejo con transfers | Limitar a 5 columnas, scroll horizontal para más |
| Blazor re-render performance con muchas cards | Virtualización (`Virtualize<>`) si > 50 llamadas simultáneas |
