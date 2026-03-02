# Plan: Blazor Server Dashboard — Monitoreo Completo de Asterisk en Tiempo Real

## Objetivo

Crear un ejemplo funcional `Examples/DashboardExample/` que sea una **vitrina completa del SDK**, demostrando:

- Conexión multi-servidor vía AMI (`AsteriskServerPool`)
- Dashboard de KPIs globales en tiempo real
- Monitoreo de colas con agentes en colores por estado
- Monitoreo de canales activos con estado y duración
- Salas de conferencia (MeetMe/ConfBridge) en tiempo real
- Acciones de control: Originate, QueuePause/Unpause, QueueAdd/Remove
- Métricas de conexión AMI (events/s, roundtrip, drops)
- Estado de conexión con indicador de reconexión
- Event log en vivo (últimos N eventos AMI)
- Sin autenticación (fase inicial), multi-usuario

---

## Arquitectura

```
[Asterisk PBX 1] ──AMI──┐
[Asterisk PBX 2] ──AMI──┤  AsteriskServerPool (singleton)
                         │         │
                         │  AsteriskMonitorService (IHostedService)
                         │  EventLogService (circular buffer últimos 200 eventos)
                         │         │
                         │  Blazor Server (SignalR built-in)
                         │         │
                         └──► N browsers (1-2s refresh, 0 conexiones AMI extra)
```

### Features del SDK demostrados

| Feature del SDK | Página del Dashboard |
|-----------------|---------------------|
| `AsteriskServerPool` (multi-server) | Todas — selector de servidor |
| `QueueManager` (colas, miembros, callers) | `/queues`, `/queue/{id}/{name}` |
| `AgentManager` (estado, login/logoff) | `/agents` |
| `ChannelManager` (canales activos, linked) | `/channels` |
| `MeetMeManager` (conferencias) | `/conferences` |
| `AmiMetrics` (counters, histograms) | `/metrics` |
| `LiveMetrics` (observable gauges) | Dashboard KPIs |
| `OriginateAsync()` | Modal en `/channels` |
| `QueuePauseAction` / `QueueAddAction` | Botones en `/queue/{id}/{name}` |
| `IAmiConnection.Subscribe()` (IObservable) | `/events` — event log en vivo |
| `ConnectionLost` event | Header — indicador de estado |
| `AmiConnectionState` enum | Header — dot rojo/verde/amarillo |
| `QueueMemberState` enum (9 estados) | Colores de agente |
| `ChannelState` enum (11 estados) | Colores de canal |
| `HangupCause` enum (48 causas) | Tooltip en canales colgados |
| `AsteriskQueueEntry.JoinedAt` | Timer de espera en cola |
| `AsteriskChannel.CreatedAt` | Duración de llamada |
| `AsteriskChannel.LinkedChannel` | Par de canales bridgeados |
| `GetQueuesForMember()` (reverse index O(1)) | "En qué colas está este agente" |
| `GetChannelsByState()` (lazy filter) | Filtro por estado en `/channels` |
| `GetAgentsByState()` (lazy filter) | Filtro por estado en `/agents` |

---

## Estructura de Archivos (17 archivos nuevos)

```
Examples/DashboardExample/
├── DashboardExample.csproj
├── Program.cs
├── appsettings.json
├── Services/
│   ├── AsteriskMonitorService.cs
│   └── EventLogService.cs
├── Components/
│   ├── App.razor
│   ├── Routes.razor
│   ├── Layout/
│   │   └── MainLayout.razor
│   ├── Pages/
│   │   ├── Home.razor                 ← KPIs globales
│   │   ├── Queues.razor               ← Todas las colas
│   │   ├── QueueDetail.razor          ← Agentes + callers de una cola
│   │   ├── Channels.razor             ← Canales activos
│   │   ├── Agents.razor               ← Todos los agentes
│   │   ├── Conferences.razor          ← MeetMe/ConfBridge
│   │   ├── Metrics.razor              ← AMI + Live metrics
│   │   └── Events.razor               ← Event log en vivo
│   └── Shared/
│       └── ServerSelector.razor       ← Componente reutilizable
└── wwwroot/
    └── css/
        └── dashboard.css
```

**Modificar:** `Asterisk.Sdk.slnx` — agregar el proyecto.

---

## Sprint 1: Proyecto Base + Infraestructura (5 archivos)

### 1A. `DashboardExample.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <ProjectReference Include="..\..\src\Asterisk.Sdk.Hosting\Asterisk.Sdk.Hosting.csproj" />
  </ItemGroup>
</Project>
```

> `Microsoft.NET.Sdk.Web` — Blazor Server. Sin `<PublishAot>` (Blazor usa reflection para Razor).

### 1B. `Program.cs`

- `AddRazorComponents().AddInteractiveServerComponents()`
- `AddAsteriskMultiServer()`
- `AddHostedService<AsteriskMonitorService>()`
- `AddSingleton<EventLogService>()`

### 1C. `appsettings.json`

```json
{
  "Asterisk": {
    "Servers": [
      {
        "Id": "pbx-main",
        "Hostname": "localhost",
        "Port": 5038,
        "Username": "admin",
        "Password": "secret"
      }
    ]
  }
}
```

### 1D. `Services/AsteriskMonitorService.cs`

`IHostedService` que:
1. Lee `Asterisk:Servers` de configuración
2. Conecta cada PBX via `pool.AddServerAsync()`
3. Suscribe un `IObserver<ManagerEvent>` por servidor para alimentar `EventLogService`
4. Registra `server.ConnectionLost` para logging

### 1E. `Services/EventLogService.cs`

Circular buffer thread-safe de los últimos 200 eventos AMI:

```csharp
public sealed class EventLogService
{
    private readonly ConcurrentQueue<EventLogEntry> _entries = new();
    private const int MaxEntries = 200;

    public void Add(string serverId, ManagerEvent evt) { ... }
    public IReadOnlyList<EventLogEntry> GetRecent(int count = 50) { ... }
}

public sealed record EventLogEntry(
    DateTimeOffset Timestamp,
    string ServerId,
    string EventType,
    string? UniqueId,
    string? Channel);
```

---

## Sprint 2: Shell + Layout + Navegación (3 archivos)

### 2A. `Components/App.razor`

HTML root con `<HeadOutlet>`, `<Routes>`, link a `dashboard.css`.

### 2B. `Components/Routes.razor`

```razor
<Router AppAssembly="typeof(Program).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="routeData" DefaultLayout="typeof(MainLayout)" />
    </Found>
</Router>
```

### 2C. `Components/Layout/MainLayout.razor`

Layout con sidebar de navegación y header con estado de conexión:

**Header:**
- Título "Asterisk Dashboard"
- Dot de estado por servidor: verde (`Connected`), amarillo (`Reconnecting`), rojo (`Disconnected`)
- Contador global: `{pool.ServerCount} servers | {totalChannels} calls | {totalAgents} agents`

**Sidebar (nav):**
- Home (KPIs)
- Queues
- Agents
- Channels
- Conferences
- Metrics
- Events

**Componente `ServerSelector.razor`** — dropdown para filtrar por servidor o "All Servers".

---

## Sprint 3: Home — KPIs Globales (1 archivo)

### 3A. `Components/Pages/Home.razor` (`@page "/"`)

Dashboard de KPIs con tarjetas numéricas grandes:

**Fila 1 — Resumen global:**

| KPI | Fuente del SDK | Color |
|-----|---------------|-------|
| Llamadas Activas | `server.Channels.ChannelCount` | Azul |
| Colas Configuradas | `server.Queues.QueueCount` | Morado |
| Callers en Espera | `sum(queue.EntryCount)` | Rojo si > 0 |
| Agentes Disponibles | `server.Agents.GetAgentsByState(Available).Count()` | Verde |
| Agentes En Llamada | `server.Agents.GetAgentsByState(OnCall).Count()` | Rojo |
| Agentes Pausados | `server.Agents.GetAgentsByState(Paused).Count()` | Amarillo |

**Fila 2 — Resumen por cola (tabla compacta):**

| Cola | Miembros | En Espera | Completadas | Abandonadas | T. Espera Prom | T. Habla Prom |
|------|----------|-----------|-------------|-------------|----------------|---------------|
| sales | 5 | 2 | 142 | 8 | 45s | 3m12s |
| support | 8 | 0 | 89 | 3 | 22s | 5m45s |

**Refresh:** Timer 2s.

---

## Sprint 4: Queues — Todas las Colas (2 archivos)

### 4A. `Components/Pages/Queues.razor` (`@page "/queues"`)

Grid responsivo de tarjetas de cola, agrupadas por servidor:

**Cada tarjeta muestra:**
- Nombre de la cola
- Estrategia (`ringall`, `roundrobin`, etc.)
- Miembros: `{activos}/{total}` con mini-bar de estados
- Callers en espera (badge rojo pulsante si > 0)
- Completadas / Abandonadas
- Hold time / Talk time promedio

**Indicador de salud (borde izquierdo):**
- Verde: 0 callers en espera
- Amarillo: 1-3 callers
- Rojo: 4+ callers
- Gris: 0 miembros (cola sin atención)

**Click** → navega a `/queue/{serverId}/{queueName}`

### 4B. `Components/Pages/QueueDetail.razor` (`@page "/queue/{ServerId}/{QueueName}"`)

Vista detallada de una cola específica con 3 secciones:

**Sección 1 — Stats Bar:**
- Max, Strategy, Calls, HoldTime, TalkTime, Completed, Abandoned
- Fuente: `AsteriskQueue` properties

**Sección 2 — Grid de Agentes (miembros de la cola):**

Cada tarjeta de agente muestra:
- Nombre (`MemberName`) o interfaz
- Interfaz (ej: `PJSIP/2000`)
- Estado con dot de color y texto
- Llamadas tomadas (`CallsTaken`)
- Penalidad (`Penalty`)
- Si está pausado: badge amarillo con razón (`PausedReason`)
- **Botones de acción:**
  - Pausar/Despausar → `QueuePauseAction` via `SendActionAsync`
  - Remover de cola → `QueueRemoveAction` via `SendActionAsync`

**Mapeo de colores (`QueueMemberState` + `Paused`):**

| Estado | Valor | Color | Icono |
|--------|-------|-------|-------|
| Paused (override) | — | `#eab308` amarillo | `⏸` |
| DeviceNotInUse | 1 | `#22c55e` verde | `✓` |
| DeviceInUse | 2 | `#ef4444` rojo | `📞` |
| DeviceBusy | 3 | `#dc2626` rojo oscuro | `⛔` |
| DeviceRinging | 6 | `#3b82f6` azul | `🔔` |
| DeviceRingInUse | 7 | `#3b82f6` azul | `🔔` |
| DeviceOnHold | 8 | `#f97316` naranja | `⏳` |
| DeviceUnavailable | 5 | `#9ca3af` gris | `✕` |
| DeviceUnknown | 0 | `#d1d5db` gris claro | `?` |
| DeviceInvalid | 4 | `#d1d5db` gris claro | `!` |

**Sección 3 — Callers en Espera:**
- Lista de `AsteriskQueueEntry` con:
  - Caller ID
  - Posición en cola
  - **Tiempo de espera en vivo** (calculado desde `JoinedAt` hasta `DateTimeOffset.UtcNow`, actualizado cada 1s)
  - Canal

**Sección 4 — Agregar Miembro (formulario inline):**
- Input: Interface (ej: `PJSIP/3000`), MemberName, Penalty
- Botón "Add" → `QueueAddAction` via `SendActionAsync`

**Refresh:** Timer 1s.

---

## Sprint 5: Channels — Canales Activos (1 archivo)

### 5A. `Components/Pages/Channels.razor` (`@page "/channels"`)

Tabla de canales activos con filtros y acciones:

**Filtros (chips clickeables):**
- All | Dialing | Ringing | Up | Busy | OnHold
- Fuente: `GetChannelsByState(state)` — lazy, zero-alloc

**Columnas de la tabla:**

| UniqueId | Canal | Estado | CallerID | Contexto | Extensión | Duración | Linked |
|----------|-------|--------|----------|----------|-----------|----------|--------|
| 123.1 | PJSIP/2000-001 | 🟢 Up | 5551234 | default | 100 | 2m34s | PJSIP/3000-002 |

- **Estado:** `ChannelState` con color (verde=Up, azul=Ringing, amarillo=Dialing, rojo=Busy)
- **Duración:** calculada desde `CreatedAt` hasta ahora, actualizada cada 1s
- **Linked:** muestra el canal bridgeado (`LinkedChannel?.Name`), indicando la otra pata de la llamada

**Colores de `ChannelState`:**

| Estado | Color | Texto |
|--------|-------|-------|
| Down | Gris | Desconectado |
| Dialing | Amarillo | Marcando |
| Ring | Azul claro | Ring (saliente) |
| Ringing | Azul | Sonando (entrante) |
| Up | Verde | Conectado |
| Busy | Rojo | Ocupado |
| OnHold | Naranja | En espera |
| Unknown | Gris claro | Desconocido |

**Acción — Originate Call (modal):**
- Campos: Channel, Context, Extension, CallerID, Timeout
- Botón "Originate" → `server.OriginateAsync(channel, context, extension, callerId, timeout)`
- Resultado: mostrar Success/Failure + ChannelId

**Refresh:** Timer 1s.

---

## Sprint 6: Agents — Todos los Agentes (1 archivo)

### 6A. `Components/Pages/Agents.razor` (`@page "/agents"`)

Grid de agentes agrupados por estado, con filtros:

**Filtros (chips):**
- All | Available | On Call | Paused | Logged Off
- Fuente: `GetAgentsByState(state)`

**Cada tarjeta de agente:**
- AgentId + Name
- Estado con color de fondo
- Canal actual (`Channel`)
- Hablando con (`TalkingTo`)
- Conectado desde (`LoggedInAt` → "hace 2h 15m")
- Colas asignadas: `GetQueuesForMember(agent.Channel)` — reverse index O(1)

**Colores de `AgentState`:**

| Estado | Color | Fondo |
|--------|-------|-------|
| Available | `#22c55e` | `#f0fdf4` |
| OnCall | `#ef4444` | `#fef2f2` |
| Paused | `#eab308` | `#fefce8` |
| LoggedOff | `#9ca3af` | `#f9fafb` |
| Unknown | `#d1d5db` | `#f3f4f6` |

**Contadores en header:**
- `Available: 12` | `On Call: 8` | `Paused: 3` | `Logged Off: 5`

**Refresh:** Timer 2s.

---

## Sprint 7: Conferences — MeetMe/ConfBridge (1 archivo)

### 7A. `Components/Pages/Conferences.razor` (`@page "/conferences"`)

Lista de salas de conferencia activas:

**Cada sala muestra:**
- Número de sala (`RoomNumber`)
- Cantidad de participantes (`UserCount`)
- Lista de participantes:
  - Canal (`Channel`)
  - Estado: `Joined`, `Talking`, `Left`
  - Color: verde=Talking, azul=Joined, gris=Left

**Fuente:** `server.MeetMe.Rooms`, `room.Users.Values`

**Refresh:** Timer 2s.

> Nota: MeetMe es legacy, pero muchas instalaciones de Asterisk aún lo usan.
> ConfBridge events también son soportados por el mismo MeetMeManager.

---

## Sprint 8: Metrics — Métricas AMI + Live (1 archivo)

### 8A. `Components/Pages/Metrics.razor` (`@page "/metrics"`)

Panel de métricas del SDK usando `System.Diagnostics.Metrics.MeterListener`:

**Sección 1 — AMI Connection Health:**

| Métrica | Fuente | Tipo |
|---------|--------|------|
| Events Received | `ami.events.received` | Counter |
| Events Dropped | `ami.events.dropped` | Counter (rojo si > 0) |
| Events Dispatched | `ami.events.dispatched` | Counter |
| Actions Sent | `ami.actions.sent` | Counter |
| Responses Received | `ami.responses.received` | Counter |
| Reconnection Attempts | `ami.reconnections` | Counter |
| Avg Roundtrip | `ami.action.roundtrip` | Histogram (ms) |
| Avg Event Dispatch | `ami.event.dispatch` | Histogram (ms) |

**Sección 2 — Live State Gauges:**

| Métrica | Fuente | Visualización |
|---------|--------|--------------|
| Active Channels | `live.channels.active` | Número grande |
| Queue Count | `live.queues.count` | Número |
| Total Agents | `live.agents.total` | Número |
| Available Agents | `live.agents.available` | Barra verde |
| On-Call Agents | `live.agents.on_call` | Barra roja |
| Paused Agents | `live.agents.paused` | Barra amarilla |

**Implementación:**
- Usar `MeterListener` para capturar instrumentos del Meter `Asterisk.Sdk.Ami` y `Asterisk.Sdk.Live`
- Actualizar valores cada 2s con `RecordObservableInstruments()`

**Refresh:** Timer 2s.

---

## Sprint 9: Events — Log en Vivo (1 archivo)

### 9A. `Components/Pages/Events.razor` (`@page "/events"`)

Log de los últimos 50 eventos AMI en tiempo real:

**Tabla con auto-scroll:**

| Timestamp | Server | Event Type | UniqueId | Channel |
|-----------|--------|------------|----------|---------|
| 14:23:45 | pbx-main | NewChannel | 123.1 | PJSIP/2000-001 |
| 14:23:46 | pbx-main | Newstate | 123.1 | PJSIP/2000-001 |
| 14:23:47 | pbx-main | Hangup | 123.1 | PJSIP/2000-001 |

**Fuente:** `EventLogService.GetRecent(50)`

**Filtros opcionales:**
- Por servidor (dropdown)
- Por tipo de evento (chips: Channel, Queue, Agent, All)

**Refresh:** Timer 1s (eventos llegan rápido).

---

## Sprint 10: CSS + Responsive + ServerSelector (2 archivos)

### 10A. `wwwroot/css/dashboard.css`

**Design system:**

```
/* Paleta principal */
--color-available:   #22c55e   /* verde */
--color-incall:      #ef4444   /* rojo */
--color-busy:        #dc2626   /* rojo oscuro */
--color-ringing:     #3b82f6   /* azul */
--color-hold:        #f97316   /* naranja */
--color-paused:      #eab308   /* amarillo */
--color-offline:     #9ca3af   /* gris */
--color-unknown:     #d1d5db   /* gris claro */

/* Layout */
--sidebar-width:     220px
--header-height:     56px
```

**Componentes CSS:**
- `.layout` — sidebar + main flexbox
- `.sidebar` — fixed-width nav con links
- `.header` — sticky top con KPIs inline
- `.kpi-card` — tarjeta grande con número, label, color
- `.kpi-grid` — grid de KPI cards `repeat(auto-fill, minmax(160px, 1fr))`
- `.queue-card`, `.agent-card`, `.channel-row` — tarjetas con borde-color
- `.status-dot` — dot circular 10px con color
- `.badge` — small pill (ej: callers waiting)
- `.badge-pulse` — animación de pulso para alertas
- `.stats-bar` — barra horizontal de estadísticas
- `.caller-timer` — monospace font para timer de espera
- `.event-log` — tabla con `max-height`, `overflow-y: auto`, auto-scroll
- `.modal-overlay`, `.modal-content` — modal simple para Originate
- `.chip`, `.chip-active` — filtros de estado clickeables
- `.connection-dot` — 8px dot en header (verde/amarillo/rojo)
- Media queries: mobile (< 768px) colapsa sidebar a top-bar

### 10B. `Components/Shared/ServerSelector.razor`

Componente reutilizable (dropdown):

```razor
<select @bind="SelectedServerId" @bind:after="OnSelectionChanged">
    <option value="">All Servers</option>
    @foreach (var (id, _) in Pool.Servers)
    {
        <option value="@id">@id</option>
    }
</select>

@code {
    [Parameter] public string? SelectedServerId { get; set; }
    [Parameter] public EventCallback<string?> SelectedServerIdChanged { get; set; }
    [Inject] public AsteriskServerPool Pool { get; set; } = default!;

    private Task OnSelectionChanged() =>
        SelectedServerIdChanged.InvokeAsync(SelectedServerId);
}
```

Usado en Queues, Agents, Channels, Conferences, Events.

---

## Sprint 11: Integración y Verificación

### 11A. Modificar `Asterisk.Sdk.slnx`

```xml
<Project Path="Examples/DashboardExample/DashboardExample.csproj" />
```

### 11B. Verificación

```bash
dotnet build Examples/DashboardExample/
dotnet build Asterisk.Sdk.slnx         # 0 warnings

# Ejecutar (requiere Asterisk accesible)
dotnet run --project Examples/DashboardExample/
# Abrir http://localhost:5000
```

---

## Resumen de Archivos

| # | Archivo | Sprint | Líneas aprox |
|---|---------|--------|-------------|
| 1 | `DashboardExample.csproj` | 1 | 10 |
| 2 | `Program.cs` | 1 | 30 |
| 3 | `appsettings.json` | 1 | 20 |
| 4 | `Services/AsteriskMonitorService.cs` | 1 | 70 |
| 5 | `Services/EventLogService.cs` | 1 | 45 |
| 6 | `Components/App.razor` | 2 | 18 |
| 7 | `Components/Routes.razor` | 2 | 10 |
| 8 | `Components/Layout/MainLayout.razor` | 2 | 80 |
| 9 | `Components/Pages/Home.razor` | 3 | 100 |
| 10 | `Components/Pages/Queues.razor` | 4 | 90 |
| 11 | `Components/Pages/QueueDetail.razor` | 4 | 200 |
| 12 | `Components/Pages/Channels.razor` | 5 | 150 |
| 13 | `Components/Pages/Agents.razor` | 6 | 120 |
| 14 | `Components/Pages/Conferences.razor` | 7 | 70 |
| 15 | `Components/Pages/Metrics.razor` | 8 | 130 |
| 16 | `Components/Pages/Events.razor` | 9 | 80 |
| 17 | `Components/Shared/ServerSelector.razor` | 10 | 25 |
| 18 | `wwwroot/css/dashboard.css` | 10 | 300 |

**Total:** 18 archivos nuevos, ~1,550 líneas, 1 modificado (`Asterisk.Sdk.slnx`).

---

## Mapa de Navegación

```
┌─────────────────────────────────────────────────┐
│  HEADER: Asterisk Dashboard  [●pbx1 ●pbx2]     │
│  📊 12 calls | 8 queues | 25 agents             │
├──────────┬──────────────────────────────────────┤
│ SIDEBAR  │                                      │
│          │                                      │
│ 🏠 Home  │   ┌─────┐ ┌─────┐ ┌─────┐ ┌─────┐  │
│ 📋 Queues│   │ 12  │ │  8  │ │  3  │ │ 15  │  │
│ 👤 Agents│   │calls│ │queue│ │wait │ │avail│  │
│ 📞 Chan. │   └─────┘ └─────┘ └─────┘ └─────┘  │
│ 🏢 Conf. │                                      │
│ 📊 Metr. │   Queue Summary Table ...            │
│ 📝 Events│                                      │
│          │                                      │
├──────────┴──────────────────────────────────────┤
│  [Server: All Servers ▼]                         │
└─────────────────────────────────────────────────┘
```

---

## Decisiones Técnicas

| Decisión | Elección | Razón |
|----------|----------|-------|
| Framework | Blazor Server (.NET 10) | SignalR built-in, C# full stack |
| Real-time | `Timer` + `StateHasChanged()` | Estado ya está en memoria singleton |
| Event log | `ConcurrentQueue` (circular 200) | Simple, thread-safe, sin DB |
| Metrics capture | `MeterListener` | API estándar .NET, no necesita OpenTelemetry |
| Acciones AMI | `SendActionAsync` directo | El `IAmiConnection` está en el pool |
| Originate | Modal HTML/CSS puro | Sin dependencia JS |
| Multi-server | `AsteriskServerPool` singleton | N browsers, 1 AMI por PBX |
| Config | `appsettings.json` | Estándar ASP.NET Core |
| CSS | Custom, sin framework | Autocontenido, ~300 líneas |
| Routing | 7 páginas `@page` | Sidebar nav simple |
| Timers | 1s (detail/events), 2s (dashboard/queues) | Balance UI vs CPU |
| Connection status | `AmiConnectionState` polling | Leído del pool cada 2s |

---

## Features del SDK NO demostrados (fuera de alcance)

| Feature | Razón |
|---------|-------|
| ARI (WebSocket + REST) | Dashboard usa AMI/Live, ARI es para control de llamadas Stasis |
| AGI (FastAGI server) | Script por llamada, no aplica a monitoreo |
| Source Generators | Internos al SDK, no visibles en app |
| `PipelineSocketConnection` | Transporte interno, no expuesto |
| `AsyncEventPump` | Interno de AmiConnection |
| Activities layer | Máquinas de estado de alto nivel, ejemplo separado |
