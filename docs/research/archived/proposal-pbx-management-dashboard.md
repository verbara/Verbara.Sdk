# Propuesta: PBX Management Dashboard — Asterisk.Sdk

## Contexto

El DashboardExample actual es un **monitor en tiempo real**: muestra llamadas, agentes, colas y métricas. La propuesta es elevarlo a una **plataforma de gestión PBX completa** — esencialmente un "FreePBX moderno" construido sobre Asterisk.Sdk con arquitectura .NET 10 Native AOT.

### Capacidades actuales del SDK aprovechables

| Capa | Elementos | Relevancia |
|------|-----------|------------|
| **AMI Actions** | 111 acciones tipadas | `GetConfigAction`, `UpdateConfigAction`, `CommandAction`, `PJSipShowEndpointsAction`, `SipPeersAction`, `ConfbridgeListAction`, `MixMonitorAction`, `OriginateAction`, `ModuleLoadAction`, etc. |
| **AMI Events** | 222 eventos tipados | `PeerStatusEvent`, `ContactStatusEvent`, `DeviceStateChangeEvent`, `RegistryEntryEvent`, `ConfbridgeJoinEvent`, `MixMonitorStartEvent`, etc. |
| **Config** | Parsers de archivos `.conf` | `ConfigFileReader`, `ExtensionsConfigFileReader` |
| **ARI** | 7 resources REST + WebSocket | Channels, Bridges, Recordings, Endpoints, Playbacks, Applications, Sounds |
| **Live** | Estado en tiempo real | `ChannelManager`, `QueueManager`, `AgentManager`, `MeetMeManager` |

### Estrategia de gestión de configuración

Asterisk soporta tres mecanismos para gestionar configuración:

| Mecanismo | Pros | Contras | Uso |
|-----------|------|---------|-----|
| **AMI `UpdateConfigAction`** | Sin acceso a filesystem, nativo AMI | Sintaxis compleja, limitado a archivos `.conf` estándar | **Principal** — troncales, extensiones, colas |
| **AMI `CommandAction`** | Ejecuta cualquier CLI command | Output en texto plano, hay que parsear | **Complementario** — reloads, queries ad-hoc |
| **AstDB (`DbPut`/`DbGet`)** | Key-value persistente, accesible desde dialplan | No es config "real", limitado | **Auxiliar** — feature flags, DND state, CF state |
| **Realtime (DB-backed)** | Escalable, multi-server, cambios en caliente | Requiere ODBC/driver setup en Asterisk | **Futuro** — nuevo módulo `Asterisk.Sdk.Realtime` |

**Decisión arquitectónica:** Usar `UpdateConfigAction` + `GetConfigAction` como mecanismo principal. Esto permite gestionar la configuración **sin acceso SSH** al servidor Asterisk — solo necesitamos la conexión AMI que ya tenemos.

---

## Arquitectura Propuesta

### Nuevo servicio: `PbxConfigManager`

```
DashboardExample
├── Services/
│   ├── AsteriskMonitorService.cs        (existente — conexión + estado real-time)
│   ├── CallFlowTracker.cs               (existente — tracking de llamadas)
│   ├── EventLogService.cs               (existente — log de eventos)
│   └── PbxConfigManager.cs              (NUEVO — gestión de configuración)
│       ├── ReadConfig(filename, section?)     → Dictionary<string, string>
│       ├── UpdateConfig(filename, action, category, var, value)
│       ├── ReloadModule(module)
│       ├── ExecuteCommand(cliCommand)         → string
│       └── Secciones especializadas:
│           ├── Trunks    → pjsip.conf / sip.conf / iax.conf
│           ├── Routes    → extensions.conf (contexts de rutas)
│           ├── Extensions → pjsip.conf (endpoints) / extensions.conf
│           ├── Queues    → queues.conf
│           ├── IVR       → extensions.conf (contexts de IVR)
│           ├── MOH       → musiconhold.conf
│           ├── TimeConditions → extensions.conf (GotoIfTime)
│           ├── Recordings → mixmonitor settings
│           ├── Conferences → confbridge.conf
│           └── Features  → features.conf
```

### Patrón de interacción UI → AMI

```
Blazor UI → PbxConfigManager → AMI Connection
    │                              │
    │  1. GetConfigAction          │──→ Lee config actual
    │  2. Presenta formulario      │
    │  3. UpdateConfigAction       │──→ Modifica config
    │  4. CommandAction("reload")  │──→ Aplica cambios
    │  5. Verify via AMI query     │──→ Confirma estado
    │                              │
    └──────────────────────────────┘
```

---

## Módulos Propuestos

---

### Módulo 1: Gestión de Troncales (SIP/PJSIP/IAX)

**Ruta:** `/trunks`

**Objetivo:** CRUD completo de troncales con monitoreo de registro en tiempo real.

#### Vista principal: Lista de troncales

```
┌─────────────────────────────────────────────────────────────────────┐
│ Trunks                                              [+ New Trunk]  │
├─────────────────────────────────────────────────────────────────────┤
│ ┌──────────────────────────┐  ┌──────────────────────────────────┐ │
│ │ ● PJSIP/provider-trunk   │  │ ○ SIP/backup-trunk               │ │
│ │ Provider: VoIP Inc.       │  │ Provider: Telco Backup           │ │
│ │ Host: sip.voipinc.com     │  │ Host: sip.telcobackup.com        │ │
│ │ Status: Registered ✓      │  │ Status: Unreachable ✗            │ │
│ │ Codec: G.711a, G.729      │  │ Codec: G.711u                    │ │
│ │ Calls: 3/10               │  │ Calls: 0/5                       │ │
│ │ DIDs: +54 11 5555-xxxx    │  │ DIDs: +54 11 4444-xxxx           │ │
│ │ [Edit] [Disable] [Test]   │  │ [Edit] [Enable] [Test]           │ │
│ └──────────────────────────┘  └──────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────┘
```

#### Formulario de edición de troncal

**Campos por tecnología:**

| Campo | PJSIP | SIP | IAX2 |
|-------|-------|-----|------|
| Name/ID | ✓ | ✓ | ✓ |
| Host/Server | ✓ | ✓ | ✓ |
| Port | ✓ (5060) | ✓ (5060) | ✓ (4569) |
| Transport | UDP/TCP/TLS/WS | UDP/TCP/TLS | — |
| Username | ✓ | ✓ | ✓ |
| Secret/Password | ✓ | ✓ | ✓ |
| Auth Type | userpass/md5 | — | md5/plaintext/rsa |
| Context (inbound) | ✓ | ✓ | ✓ |
| Codecs (allow/disallow) | ✓ | ✓ | ✓ |
| DTMF Mode | rfc4733/inband/info/auto | rfc2833/inband/info/auto | — |
| NAT | force_rport, comedia | yes/no/force_rport | — |
| Registration | outbound registration | register string | register string |
| Max Channels | max_contacts | call-limit | maxcallno |
| Qualify | qualify_frequency | qualifyfreq | qualify |
| CallerID | ✓ | ✓ | ✓ |
| DIDs (notas) | campo libre | campo libre | campo libre |

**AMI Actions utilizadas:**
- `GetConfigAction("pjsip.conf")` → lee secciones de troncales
- `UpdateConfigAction("pjsip.conf", ...)` → crea/modifica/elimina troncales
- `PJSipShowEndpointsAction` → estado actual de endpoints PJSIP
- `PJSipShowContactsAction` → estado de registros PJSIP
- `SipPeersAction` → estado de peers SIP
- `SipShowRegistryAction` → estado de registros SIP
- `IaxPeerListAction` → estado de peers IAX2
- `CommandAction("pjsip reload")` / `CommandAction("sip reload")` → aplicar cambios

**Eventos monitoreados (tiempo real):**
- `PeerStatusEvent` → cambios de estado SIP/IAX
- `ContactStatusEvent` → cambios de contacto PJSIP
- `RegistryEntryEvent` → estado de registro

#### Tarea 1.1: `PbxConfigManager` — servicio base

```csharp
public sealed class PbxConfigManager
{
    private readonly AsteriskMonitorService _monitor;

    /// Lee una sección completa de un archivo de configuración.
    public async Task<Dictionary<string, string>> GetConfigSectionAsync(
        string serverId, string filename, string section, CancellationToken ct = default);

    /// Lee todas las secciones (categorías) de un archivo.
    public async Task<List<string>> GetConfigCategoriesAsync(
        string serverId, string filename, CancellationToken ct = default);

    /// Crea una nueva sección con variables.
    public async Task<bool> CreateSectionAsync(
        string serverId, string filename, string section,
        Dictionary<string, string> variables, CancellationToken ct = default);

    /// Actualiza una variable en una sección existente.
    public async Task<bool> UpdateVariableAsync(
        string serverId, string filename, string section,
        string variable, string value, CancellationToken ct = default);

    /// Elimina una sección completa.
    public async Task<bool> DeleteSectionAsync(
        string serverId, string filename, string section, CancellationToken ct = default);

    /// Ejecuta un comando CLI de Asterisk.
    public async Task<string> ExecuteCommandAsync(
        string serverId, string command, CancellationToken ct = default);

    /// Recarga un módulo específico.
    public async Task<bool> ReloadModuleAsync(
        string serverId, string module, CancellationToken ct = default);
}
```

#### Tarea 1.2: Modelo `TrunkConfig`

```csharp
public sealed class TrunkConfig
{
    public string Name { get; set; } = "";
    public TrunkTechnology Technology { get; set; }    // PJSIP, SIP, IAX2
    public string Host { get; set; } = "";
    public int Port { get; set; } = 5060;
    public string? Transport { get; set; }              // udp, tcp, tls, ws
    public string? Username { get; set; }
    public string? Secret { get; set; }
    public string Context { get; set; } = "from-trunk";
    public List<string> Codecs { get; set; } = ["ulaw", "alaw"];
    public string DtmfMode { get; set; } = "rfc4733";
    public bool NatEnabled { get; set; }
    public string? CallerIdNum { get; set; }
    public string? CallerIdName { get; set; }
    public int MaxChannels { get; set; }
    public int QualifyFrequency { get; set; } = 60;
    public bool RegistrationEnabled { get; set; }
    public string? Notes { get; set; }

    // Conversión a/desde pjsip.conf sections
    public Dictionary<string, string> ToPjsipEndpoint();
    public Dictionary<string, string> ToPjsipAuth();
    public Dictionary<string, string> ToPjsipAor();
    public Dictionary<string, string> ToPjsipRegistration();
    public static TrunkConfig FromPjsipSections(...);
}
```

#### Tarea 1.3: Páginas Blazor

- `Trunks.razor` — lista con cards y estado real-time
- `TrunkEdit.razor` — formulario de creación/edición
- `TrunkDetail.razor` — detalle con gráficas de uso, logs de registro

#### Tarea 1.4: Tests

- Tests de conversión `TrunkConfig` ↔ `pjsip.conf` sections
- Tests de `PbxConfigManager` con mock de AMI connection
- Tests de parsing de respuestas `GetConfigAction`

---

### Módulo 2: Rutas Entrantes y Salientes

**Ruta:** `/routes`

**Objetivo:** Gestión de rutas de entrada (por DID) y salida (por prefijo/patrón) con LCR básico.

#### Vista principal

```
┌────────────────────────────────────────────────────────────────────────┐
│ Routes                                                                 │
├───────────────────────────┬────────────────────────────────────────────┤
│ Inbound Routes            │ Outbound Routes                            │
│                           │                                            │
│ DID: +5411555501xx        │ Pattern: _9NXXXXXXX (Local)               │
│  → Queue: ventas          │  → Trunk: provider-trunk (pri 1)          │
│  Time: Lun-Vie 9-18       │  → Trunk: backup-trunk  (pri 2)          │
│  After hours → VM(100)    │  Prefix strip: 1 digit                    │
│                           │  Prepend: +5411                            │
│ DID: +5411555502xx        │                                            │
│  → IVR: menu-principal    │ Pattern: _00. (International)              │
│                           │  → Trunk: intl-trunk                       │
│ DID: _X. (catch-all)      │  Require PIN: yes                         │
│  → Extension: 100         │  Max duration: 30min                       │
│                           │                                            │
│ [+ Add Inbound]           │ [+ Add Outbound]                          │
└───────────────────────────┴────────────────────────────────────────────┘
```

#### Modelo de datos

```csharp
public sealed class InboundRoute
{
    public string Name { get; set; } = "";
    public string DidPattern { get; set; } = "";        // _X., +5411XXXXXXXX, etc.
    public string? CallerIdPattern { get; set; }         // Filtro por CallerID
    public string Destination { get; set; } = "";        // extension, queue, ivr, voicemail
    public string DestinationDetail { get; set; } = "";  // 100, ventas, menu-principal
    public string? TimeCondition { get; set; }           // Referencia a time condition
    public string? AfterHoursDestination { get; set; }
    public int Priority { get; set; } = 1;
}

public sealed class OutboundRoute
{
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";            // _9NXXXXXXX, _00., etc.
    public List<TrunkPriority> Trunks { get; set; } = [];  // LCR: lista ordenada
    public int StripDigits { get; set; }
    public string? PrependDigits { get; set; }
    public bool RequirePin { get; set; }
    public string? Pin { get; set; }
    public int? MaxDurationSecs { get; set; }
    public string? CallerIdOverride { get; set; }
    public int Priority { get; set; } = 1;
}

public sealed class TrunkPriority
{
    public string TrunkName { get; set; } = "";
    public int Priority { get; set; }                    // 1 = primero, failover automático
}
```

#### Implementación en dialplan

Las rutas se implementan como contexts en `extensions.conf`:

```ini
; Ruta entrante
[from-trunk]
exten => _+5411555501XX,1,GotoIfTime(09:00-18:00,mon-fri,*,*?ventas-horario,${EXTEN},1)
same => n,VoiceMail(100@default,u)

[ventas-horario]
exten => _X.,1,Queue(ventas,t,,,60)

; Ruta saliente
[outbound-routes]
exten => _9NXXXXXXX,1,Set(CALLERID(num)=${CALLERID(num)})
same => n,Set(dialstr=${EXTEN:1})                  ; strip 9
same => n,Set(dialstr=+5411${dialstr})              ; prepend
same => n,Dial(PJSIP/${dialstr}@provider-trunk,60)
same => n,Dial(PJSIP/${dialstr}@backup-trunk,60)   ; failover
same => n,Hangup()
```

**AMI Actions:**
- `UpdateConfigAction("extensions.conf", ...)` → crear/modificar contexts y extensiones
- `ShowDialplanAction` → verificar dialplan actual
- `CommandAction("dialplan reload")` → aplicar cambios

#### Tareas

- Tarea 2.1: Modelo `InboundRoute` / `OutboundRoute` con conversión a dialplan
- Tarea 2.2: Generador de dialplan (Route → extensions.conf syntax)
- Tarea 2.3: `Routes.razor` — vista dual inbound/outbound
- Tarea 2.4: `RouteEdit.razor` — formulario con validación de patterns
- Tarea 2.5: Tests de generación de dialplan

---

### Módulo 3: Extensiones y Dispositivos

**Ruta:** `/extensions`

**Objetivo:** CRUD de extensiones/endpoints con estado real-time, templates, BLF/hints.

#### Vista principal

```
┌──────────────────────────────────────────────────────────────────────────┐
│ Extensions                                            [+ New Extension] │
├──────────────────────────────────────────────────────────────────────────┤
│ Search: [________] Filter: [All ▾] [PJSIP ▾] [Online ▾]               │
│                                                                          │
│ ┌─────────────────────┐ ┌─────────────────────┐ ┌─────────────────────┐ │
│ │ ● 2001              │ │ ● 2002              │ │ ○ 2003              │ │
│ │ Alice Johnson        │ │ Bob Smith            │ │ Carol White          │ │
│ │ PJSIP/2001           │ │ PJSIP/2002           │ │ PJSIP/2003           │ │
│ │ State: In Use 📞      │ │ State: Not In Use    │ │ State: Unavailable   │ │
│ │ IP: 192.168.1.50     │ │ IP: 192.168.1.51     │ │ IP: —                │ │
│ │ Codecs: ulaw,alaw    │ │ Codecs: opus,ulaw    │ │ Codecs: ulaw         │ │
│ │ VM: 3 new / 5 old    │ │ VM: 0 new            │ │ VM: 1 new            │ │
│ │ Queues: ventas        │ │ Queues: soporte       │ │ Queues: ventas       │ │
│ │ [Edit] [Call] [VM]   │ │ [Edit] [Call] [VM]   │ │ [Edit] [Call] [VM]   │ │
│ └─────────────────────┘ └─────────────────────┘ └─────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────┘
```

#### Formulario de extensión

**Secciones del formulario:**

1. **General**
   - Extension Number (2001)
   - Display Name (Alice Johnson)
   - Technology (PJSIP / SIP)
   - Template (seleccionar de templates existentes)
   - CallerID Name / Number

2. **Autenticación**
   - Username (auto = extension number)
   - Password (auto-generate o manual)
   - Auth Type (userpass / md5)

3. **Transporte & NAT**
   - Transport (UDP / TCP / TLS / WSS)
   - NAT: force_rport, comedia
   - Direct Media (yes/no)
   - Qualify Frequency

4. **Codecs**
   - Drag-and-drop para ordenar preferencia
   - Opciones: opus, g722, ulaw, alaw, g729, gsm, ilbc

5. **Voicemail**
   - Enabled (yes/no)
   - Password (PIN)
   - Email address
   - Attach recording to email

6. **Features**
   - Call Waiting (yes/no)
   - DND (Do Not Disturb)
   - Call Forward: Unconditional / Busy / No Answer
   - Follow Me (ring strategy, timeout)

7. **BLF/Hints** (auto-generado)
   ```ini
   exten => 2001,hint,PJSIP/2001
   ```

**Secciones PJSIP generadas** (por cada extensión):
```ini
[2001](template-internal)           ; endpoint
type=endpoint
callerid="Alice Johnson" <2001>
auth=auth-2001
aors=2001

[auth-2001]                          ; auth
type=auth
auth_type=userpass
username=2001
password=SecureP@ss123

[2001]                               ; aor
type=aor
max_contacts=3
qualify_frequency=30
```

**AMI Actions:**
- `GetConfigAction("pjsip.conf")` → leer endpoints existentes
- `UpdateConfigAction("pjsip.conf", ...)` → CRUD de secciones
- `PJSipShowEndpointsAction` → estado actual
- `PJSipShowEndpointAction(endpoint)` → detalle
- `ExtensionStateAction(exten, context)` → BLF state
- `MailboxStatusAction` / `MailboxCountAction` → voicemail
- `CommandAction("pjsip reload")` → aplicar
- `DbPutAction("CF", "2001", "2099")` → call forward
- `DbPutAction("DND", "2001", "yes")` → DND

**Eventos monitoreados:**
- `DeviceStateChangeEvent` → cambio de estado de extensión
- `ContactStatusEvent` → registro/des-registro
- `ExtensionStatusEvent` → hint state changes
- `PeerStatusEvent` → SIP peer status

#### Tareas

- Tarea 3.1: Modelo `ExtensionConfig` con conversión a pjsip.conf sections
- Tarea 3.2: Template engine para extensiones (herencia de secciones PJSIP)
- Tarea 3.3: `Extensions.razor` — grid con estado real-time
- Tarea 3.4: `ExtensionEdit.razor` — formulario multi-tab
- Tarea 3.5: Panel de Voicemail integrado
- Tarea 3.6: Panel de BLF/Hints con estado visual
- Tarea 3.7: Tests unitarios

---

### Módulo 4: Gestión Avanzada de Colas

**Ruta:** `/queues` (extender existente) + `/queues/{server}/{name}/config`

**Objetivo:** Gestión completa de colas — configuración, estrategias, horarios, anuncios, penalties.

#### Vista de configuración de cola

```
┌─────────────────────────────────────────────────────────────────────┐
│ Queue: ventas                                        [Save] [Test] │
├─────────────────────────────────────────────────────────────────────┤
│ [General] [Strategy] [Members] [Timers] [Announcements] [Advanced]│
│                                                                     │
│ ── General ──                                                       │
│ Name:        [ventas          ]                                     │
│ Music Class: [default         ▾]                                    │
│ Max Callers: [0 (unlimited)   ]                                     │
│ Join Empty:  [yes ▾] (strict, yes, no, paused, penalty, inuse)     │
│ Leave Empty: [yes ▾]                                                │
│                                                                     │
│ ── Strategy ──                                                      │
│ Strategy:    [● ringall  ○ leastrecent  ○ fewestcalls              │
│               ○ random   ○ rrmemory     ○ rrordered                │
│               ○ linear   ○ wrandom                     ]           │
│                                                                     │
│ ── Timers ──                                                        │
│ Ring Timeout:   [15    ] sec   (ring each agent)                    │
│ Retry:          [5     ] sec   (wait between attempts)              │
│ Timeout:        [300   ] sec   (max wait in queue)                  │
│ Wrapup Time:    [10    ] sec   (post-call cooldown)                 │
│ Service Level:  [60    ] sec   (SLA target)                         │
│                                                                     │
│ ── Announcements ──                                                 │
│ Join Announce:     [queue-thankyou    ▾]                             │
│ Periodic Announce: [queue-periodic   ▾]  every [30] sec            │
│ Hold Time Announce: [yes ▾]                                         │
│ Position Announce:  [yes ▾]  every [15] sec                        │
│                                                                     │
│ ── Members (Static) ──                                              │
│ ┌──────────────────────────────────────┐                            │
│ │ PJSIP/2001  Penalty: 1  RingInUse: no  │ [↑] [↓] [✕]           │
│ │ PJSIP/2002  Penalty: 2  RingInUse: no  │ [↑] [↓] [✕]           │
│ │ PJSIP/3001  Penalty: 5  RingInUse: yes │ [↑] [↓] [✕]           │
│ └──────────────────────────────────────┘                            │
│ [+ Add Static Member]                                               │
│                                                                     │
│ ── Advanced ──                                                      │
│ Autopause:     [yes ▾] (yes, no, all)                              │
│ Autopausedelay: [0   ] sec                                          │
│ Ring In Use:    [no  ▾]                                             │
│ Penaltymemberslimit: [0]                                            │
│ Monitor Format: [wav ▾] (wav, wav49, gsm, none)                    │
│ Monitor Type:   [MixMonitor ▾]                                      │
│ Weight:         [0   ]                                               │
└─────────────────────────────────────────────────────────────────────┘
```

#### Modelo de datos

```csharp
public sealed class QueueConfig
{
    // General
    public string Name { get; set; } = "";
    public string MusicClass { get; set; } = "default";
    public int MaxLen { get; set; }                     // 0 = unlimited
    public string JoinEmpty { get; set; } = "yes";
    public string LeaveWhenEmpty { get; set; } = "yes";

    // Strategy
    public string Strategy { get; set; } = "ringall";   // ringall|leastrecent|fewestcalls|random|rrmemory|rrordered|linear|wrandom

    // Timers
    public int Timeout { get; set; } = 15;              // ring timeout per member
    public int Retry { get; set; } = 5;
    public int MaxWait { get; set; } = 300;             // max wait time in queue
    public int WrapupTime { get; set; } = 0;
    public int ServiceLevel { get; set; } = 60;

    // Announcements
    public string? JoinAnnouncement { get; set; }
    public string? PeriodicAnnouncement { get; set; }
    public int PeriodicAnnounceFrequency { get; set; } = 30;
    public bool AnnounceHoldTime { get; set; }
    public bool AnnouncePosition { get; set; }
    public int AnnouncePositionFrequency { get; set; } = 15;

    // Advanced
    public string Autopause { get; set; } = "no";       // no|yes|all
    public int AutopauseDelay { get; set; }
    public bool RingInUse { get; set; }
    public string? MonitorFormat { get; set; }
    public string MonitorType { get; set; } = "MixMonitor";
    public int Weight { get; set; }

    // Static members
    public List<QueueStaticMember> StaticMembers { get; set; } = [];
}

public sealed class QueueStaticMember
{
    public string Interface { get; set; } = "";          // PJSIP/2001
    public string? MemberName { get; set; }
    public int Penalty { get; set; }
    public bool RingInUse { get; set; }
    public string? StateInterface { get; set; }
}
```

**AMI Actions:**
- `GetConfigAction("queues.conf")` → leer configuración
- `UpdateConfigAction("queues.conf", ...)` → CRUD
- `QueueStatusAction` → estado runtime
- `QueueSummaryAction` → estadísticas
- `QueueAddAction` / `QueueRemoveAction` → miembros dinámicos runtime
- `QueuePauseAction` → pausar miembros
- `QueuePenaltyAction` → cambiar penalty runtime
- `QueueMemberRingInUseAction` → cambiar ringinuse runtime
- `QueueResetAction` → resetear estadísticas
- `CommandAction("queue reload all")` → aplicar cambios config

#### Tareas

- Tarea 4.1: Modelo `QueueConfig` ↔ `queues.conf`
- Tarea 4.2: `QueueConfig.razor` — formulario multi-tab
- Tarea 4.3: Drag-and-drop para ordenar miembros estáticos por penalty
- Tarea 4.4: "Test Queue" — originar llamada de prueba con `OriginateAction`
- Tarea 4.5: Queue Schedule — integrar con Time Conditions (Módulo 7)

---

### Módulo 5: IVR Builder

**Ruta:** `/ivr`

**Objetivo:** Diseñador visual de IVR con generación automática de dialplan.

#### Vista: IVR Flow Designer

```
┌──────────────────────────────────────────────────────────────────────┐
│ IVR: menu-principal                              [Save] [Test] [▶]  │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌─────────────┐                                                     │
│  │  Answer      │                                                    │
│  │  Wait: 1s    │                                                    │
│  └──────┬───────┘                                                    │
│         ▼                                                            │
│  ┌──────────────────────────┐                                        │
│  │  Play: "bienvenido"       │                                       │
│  │  Press 1: Ventas          │──→ Queue(ventas)                      │
│  │  Press 2: Soporte         │──→ Queue(soporte)                     │
│  │  Press 3: Directorio      │──→ Directory(default)                 │
│  │  Press 0: Operadora       │──→ Extension(100)                     │
│  │  Timeout: 5s              │──→ (repeat)                           │
│  │  Invalid: 3 attempts      │──→ Hangup                            │
│  └──────────────────────────┘                                        │
│                                                                      │
│ ── Dialplan generado ──                                              │
│ [ivr-menu-principal]                                                 │
│ exten => s,1,Answer()                                                │
│ same => n,Wait(1)                                                    │
│ same => n,Set(ATTEMPTS=0)                                            │
│ same => n(start),Background(bienvenido)                              │
│ same => n,WaitExten(5)                                               │
│ same => n,Set(ATTEMPTS=$[${ATTEMPTS}+1])                             │
│ same => n,GotoIf($[${ATTEMPTS}>3]?hangup)                           │
│ same => n,Goto(start)                                                │
│ same => n(hangup),Playback(vm-goodbye)                               │
│ same => n,Hangup()                                                   │
│ exten => 1,1,Queue(ventas,t,,,60)                                    │
│ exten => 2,1,Queue(soporte,t,,,60)                                   │
│ exten => 3,1,Directory(default,from-internal)                        │
│ exten => 0,1,Goto(from-internal,100,1)                               │
│ exten => i,1,Playback(pbx-invalid)                                   │
│ same => n,Goto(s,start)                                              │
│ exten => t,1,Goto(s,start)                                           │
└──────────────────────────────────────────────────────────────────────┘
```

#### Modelo de datos

```csharp
public sealed class IvrMenu
{
    public string Name { get; set; } = "";
    public string? Greeting { get; set; }               // Sound file to play
    public int Timeout { get; set; } = 5;               // Seconds to wait for input
    public int MaxRetries { get; set; } = 3;
    public string InvalidPrompt { get; set; } = "pbx-invalid";
    public string TimeoutAction { get; set; } = "repeat"; // repeat, hangup, destination
    public string? TimeoutDestination { get; set; }
    public string InvalidAction { get; set; } = "repeat";
    public string? InvalidDestination { get; set; }
    public List<IvrOption> Options { get; set; } = [];
    public bool AnswerBeforePlay { get; set; } = true;
    public int WaitBeforePlay { get; set; } = 1;
}

public sealed class IvrOption
{
    public string Digits { get; set; } = "";             // "1", "2", "0", "*", "#"
    public string DestinationType { get; set; } = "";    // extension, queue, ivr, voicemail, hangup, directory
    public string DestinationTarget { get; set; } = "";  // 100, ventas, sub-menu
    public string? Label { get; set; }                   // "Ventas", "Soporte"
}
```

**Dialplan generation:**
- Cada IVR genera un context `[ivr-{name}]`
- `Background()` para reproducir prompts
- `WaitExten()` para capturar DTMF
- Extensiones por dígito con `Goto()` al destino
- `exten => i` para opción inválida
- `exten => t` para timeout
- Loop con contador de reintentos

**AMI Actions:**
- `UpdateConfigAction("extensions.conf", ...)` → generar contexts
- `CommandAction("dialplan reload")` → aplicar
- `OriginateAction` → test IVR (llamar y conectar al context)

#### Tareas

- Tarea 5.1: Modelo `IvrMenu` / `IvrOption` con generador de dialplan
- Tarea 5.2: `IvrList.razor` — lista de IVRs
- Tarea 5.3: `IvrEdit.razor` — diseñador visual con preview de dialplan
- Tarea 5.4: "Test IVR" — originar llamada de prueba
- Tarea 5.5: Listado de sound files disponibles (ARI `SoundsResource`)
- Tarea 5.6: Tests del generador de dialplan

---

### Módulo 6: Música en Espera (MOH)

**Ruta:** `/moh`

**Objetivo:** Gestión de clases de MOH, upload de archivos de audio.

#### Vista principal

```
┌────────────────────────────────────────────────────────────────────┐
│ Music on Hold                                    [+ New Class]     │
├────────────────────────────────────────────────────────────────────┤
│ ┌──────────────────────────┐  ┌──────────────────────────────────┐│
│ │ 🎵 default                │  │ 🎵 jazz                          ││
│ │ Mode: files               │  │ Mode: files                      ││
│ │ Directory: /var/lib/.../  │  │ Directory: /var/lib/.../jazz     ││
│ │ Files: 5                  │  │ Files: 3                         ││
│ │ Sort: random              │  │ Sort: alpha                      ││
│ │ [Edit] [▶ Preview]       │  │ [Edit] [▶ Preview]               ││
│ └──────────────────────────┘  └──────────────────────────────────┘│
│                                                                    │
│ ┌──────────────────────────┐                                       │
│ │ 🎵 custom-stream          │                                      │
│ │ Mode: custom              │                                      │
│ │ Application: mpg123       │                                      │
│ │ [Edit] [▶ Preview]       │                                      │
│ └──────────────────────────┘                                       │
└────────────────────────────────────────────────────────────────────┘
```

#### Modelo

```csharp
public sealed class MohClass
{
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "files";          // files, custom, mp3nb, quietmp3nb
    public string? Directory { get; set; }                // path to audio files
    public string? Application { get; set; }              // for custom mode
    public string Sort { get; set; } = "random";          // random, alpha, randstart
    public string? Format { get; set; }                   // force format (ulaw, alaw, gsm, wav)
}
```

**AMI Actions:**
- `GetConfigAction("musiconhold.conf")`
- `UpdateConfigAction("musiconhold.conf", ...)`
- `CommandAction("moh reload")`
- `CommandAction("moh show classes")` → listar clases activas

#### Tareas

- Tarea 6.1: Modelo `MohClass` ↔ `musiconhold.conf`
- Tarea 6.2: `MohList.razor` — vista con cards
- Tarea 6.3: `MohEdit.razor` — formulario
- Tarea 6.4: Preview via `OriginateAction` → `MusicOnHold(class)`

---

### Módulo 7: Time Conditions (Horarios y Festivos)

**Ruta:** `/time-conditions`

**Objetivo:** Gestión de condiciones temporales para rutas entrantes, colas e IVRs.

#### Vista principal

```
┌──────────────────────────────────────────────────────────────────────┐
│ Time Conditions                                    [+ New Condition] │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│ ┌──────────────────────────────────────────────┐                     │
│ │ 📅 Horario Oficina                            │                    │
│ │ Lun-Vie 09:00 - 18:00                         │ Currently: ● OPEN │
│ │ Sáb 09:00 - 13:00                             │                    │
│ │ Match  → Queue(ventas)                         │                    │
│ │ No Match → VM(100) + Announce(cerrado)         │                    │
│ │ Used by: Ruta "+5411555501xx"                  │                    │
│ │ [Edit] [Override: Force Open ▾]               │                    │
│ └──────────────────────────────────────────────┘                     │
│                                                                      │
│ ── Holidays ──                                                       │
│ ┌──────────────────────────────────────────────┐                     │
│ │ 📅 Festivos 2026                               │                   │
│ │ 01 Jan — Año Nuevo                             │                   │
│ │ 24 Mar — Día de la Memoria                     │                   │
│ │ 02 Apr — Día del Veterano                      │                   │
│ │ 25 May — Revolución de Mayo                    │                   │
│ │ ...                                            │                   │
│ │ Action → Playback(festivo) + VM(100)           │                   │
│ │ [Edit]                                         │                   │
│ └──────────────────────────────────────────────┘                     │
└──────────────────────────────────────────────────────────────────────┘
```

#### Modelo

```csharp
public sealed class TimeCondition
{
    public string Name { get; set; } = "";
    public List<TimeRange> Ranges { get; set; } = [];
    public List<HolidayDate> Holidays { get; set; } = [];
    public string MatchDestination { get; set; } = "";        // queue:ventas, ext:100, ivr:main
    public string NoMatchDestination { get; set; } = "";      // vm:100, hangup
    public string? OverrideState { get; set; }                // null=auto, "open", "closed" (manual override via AstDB)
}

public sealed class TimeRange
{
    public string Days { get; set; } = "mon-fri";             // mon-fri, sat, sun, mon&wed&fri
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
}

public sealed class HolidayDate
{
    public DateOnly Date { get; set; }
    public string Description { get; set; } = "";
}
```

**Implementación en dialplan:**
```ini
[time-check-oficina]
exten => s,1,GotoIfTime(09:00-18:00,mon-fri,*,*?open,s,1)
same => n,GotoIfTime(09:00-13:00,sat,*,*?open,s,1)
; Check holiday override in AstDB
same => n,Set(HOLIDAY=${DB(holidays/${STRFTIME(${EPOCH},,%m%d)})})
same => n,GotoIf($["${HOLIDAY}" != ""]?closed,s,1)
; Check manual override
same => n,Set(OVERRIDE=${DB(timeoverride/oficina)})
same => n,GotoIf($["${OVERRIDE}" = "open"]?open,s,1)
same => n,GotoIf($["${OVERRIDE}" = "closed"]?closed,s,1)
same => n,Goto(closed,s,1)

[open]
exten => s,1,Queue(ventas,t,,,60)

[closed]
exten => s,1,Playback(office-closed)
same => n,VoiceMail(100@default,u)
```

**AMI Actions:**
- `UpdateConfigAction("extensions.conf", ...)` → contexts de time check
- `DbPutAction("holidays", "0101", "Año Nuevo")` → festivos en AstDB
- `DbPutAction("timeoverride", "oficina", "open")` → override manual
- `DbGetAction("timeoverride", "oficina")` → estado actual del override

#### Tareas

- Tarea 7.1: Modelo `TimeCondition` con generador de dialplan
- Tarea 7.2: `TimeConditions.razor` — lista con indicador de estado actual
- Tarea 7.3: `TimeConditionEdit.razor` — editor visual con timeline semanal
- Tarea 7.4: Holiday manager con calendario
- Tarea 7.5: Override button (Force Open/Close via AstDB)

---

### Módulo 8: Grabaciones y Retención

**Ruta:** `/recordings`

**Objetivo:** Gestión de grabaciones de llamadas — políticas, browse, playback, retención.

#### Vista principal

```
┌──────────────────────────────────────────────────────────────────────────┐
│ Recordings                                                               │
├──────────────────────────────────────────────────────────────────────────┤
│ ── Recording Policy ──                                                   │
│ ┌───────────────────────────────────────────────┐                        │
│ │ Default: Record all inbound queue calls        │                       │
│ │ Format: wav | Retention: 90 days               │                       │
│ │ Storage: /var/spool/asterisk/monitor/           │                       │
│ │ [Edit Policy]                                  │                       │
│ └───────────────────────────────────────────────┘                        │
│                                                                          │
│ ── Recent Recordings ──                                                  │
│ Search: [________] Date: [Today ▾]  Queue: [All ▾]  Agent: [All ▾]     │
│                                                                          │
│ ┌─────────┬──────────┬──────────┬────────┬────────┬────────┬─────────┐  │
│ │ Date     │ Caller   │ Agent    │ Queue  │ Duration│ Size  │ Actions │  │
│ ├─────────┼──────────┼──────────┼────────┼────────┼────────┼─────────┤  │
│ │ 10:30:15│ 5551234  │ María    │ ventas │ 03:25  │ 4.2MB │ ▶ ⬇ 🗑  │  │
│ │ 10:28:01│ 5559876  │ Pedro    │ soporte│ 01:12  │ 1.1MB │ ▶ ⬇ 🗑  │  │
│ │ 10:15:45│ 5554321  │ —        │ —      │ 00:45  │ 0.5MB │ ▶ ⬇ 🗑  │  │
│ └─────────┴──────────┴──────────┴────────┴────────┴────────┴─────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
```

#### Modelo

```csharp
public sealed class RecordingPolicy
{
    public string Name { get; set; } = "default";
    public RecordingScope Scope { get; set; }            // All, Inbound, Outbound, QueueOnly, OnDemand
    public string Format { get; set; } = "wav";          // wav, wav49, gsm
    public string StoragePath { get; set; } = "/var/spool/asterisk/monitor/";
    public int RetentionDays { get; set; } = 90;
    public bool MixOnCompletion { get; set; } = true;    // Mix both channels into one file
    public string? ExcludePattern { get; set; }           // Excluir extensiones/patrones
}

public enum RecordingScope { All, Inbound, Outbound, QueueOnly, OnDemand }
```

**AMI Actions:**
- `MixMonitorAction` → iniciar grabación
- `StopMixMonitorAction` → parar grabación
- `PauseMixMonitorAction` / `UnpauseMonitorAction` → pausa
- `CommandAction("mixmonitor list {channel}")` → listar grabaciones activas

**ARI Actions:**
- `AriRecordingsResource.GetLiveAsync()` → grabación activa
- `AriRecordingsResource.StopAsync()` → detener
- `AriRecordingsResource.DeleteStoredAsync()` → eliminar

**Eventos:**
- `MixMonitorStartEvent` → grabación iniciada
- `MixMonitorStopEvent` → grabación terminada

**Implementación en dialplan** (auto-record queue calls):
```ini
[macro-record-call]
exten => s,1,Set(MONITOR_FILENAME=${STRFTIME(${EPOCH},,%Y%m%d-%H%M%S)}-${CALLERID(num)}-${UNIQUEID})
same => n,MixMonitor(${MONITOR_FILENAME}.wav,b)
```

#### Tareas

- Tarea 8.1: Modelo `RecordingPolicy` con generador de dialplan
- Tarea 8.2: `Recordings.razor` — browse con filtros y paginación
- Tarea 8.3: Audio player inline (HTML5 `<audio>`)
- Tarea 8.4: API endpoint para servir archivos de audio
- Tarea 8.5: Retención automática (background service para cleanup)
- Tarea 8.6: On-demand recording button en call cards (via `MixMonitorAction`)

---

### Módulo 9: Conferencias (ConfBridge)

**Ruta:** `/conferences`

**Objetivo:** Gestión de salas de conferencia con control en tiempo real.

#### Vista principal

```
┌──────────────────────────────────────────────────────────────────────┐
│ Conferences                                      [+ New Room]        │
├──────────────────────────────────────────────────────────────────────┤
│ ── Active Conferences ──                                             │
│ ┌──────────────────────────────────────────────────────┐             │
│ │ 🔊 Room 800 — "Weekly Standup"            Duration: 00:15:30     │
│ │                                                                    │
│ │ Participants (4):                                                  │
│ │   ● Alice (2001) — Admin 🔊                [Mute] [Kick]         │
│ │   ● Bob (2002)           🔊                [Mute] [Kick]         │
│ │   ● Carol (2003)         🔇 MUTED          [Unmute] [Kick]       │
│ │   ● +5551234 (external)  🔊                [Mute] [Kick]         │
│ │                                                                    │
│ │ [🔒 Lock] [🔴 Record] [📞 Invite] [🚪 End Conference]           │
│ └──────────────────────────────────────────────────────┘             │
│                                                                      │
│ ── Configured Rooms ──                                               │
│ ┌─────────────────┐ ┌─────────────────┐ ┌─────────────────┐        │
│ │ Room 800         │ │ Room 801         │ │ Room 802         │       │
│ │ Max: 10          │ │ Max: 50          │ │ Max: 6           │       │
│ │ PIN: ****        │ │ PIN: none        │ │ PIN: ****        │       │
│ │ Record: auto     │ │ Record: no       │ │ Record: on-demand│       │
│ │ [Edit] [Invite]  │ │ [Edit] [Invite]  │ │ [Edit] [Invite]  │      │
│ └─────────────────┘ └─────────────────┘ └─────────────────┘        │
└──────────────────────────────────────────────────────────────────────┘
```

#### Modelo

```csharp
public sealed class ConferenceRoom
{
    public string Number { get; set; } = "";              // Extension number (800)
    public string Name { get; set; } = "";                // Descriptive name
    public int MaxMembers { get; set; } = 10;
    public string? AdminPin { get; set; }
    public string? UserPin { get; set; }
    public bool RecordConference { get; set; }
    public bool MuteOnEntry { get; set; }
    public bool AnnounceMemberJoin { get; set; } = true;
    public bool WaitForAdmin { get; set; }
    public string MusicClass { get; set; } = "default";
    public string BridgeProfile { get; set; } = "default_bridge";
    public string UserProfile { get; set; } = "default_user";
    public string AdminProfile { get; set; } = "admin_user";
}
```

**AMI Actions:**
- `ConfbridgeListRoomsAction` → conferencias activas
- `ConfbridgeListAction(conference)` → participantes
- `ConfbridgeMuteAction(conference, channel)` → silenciar
- `ConfbridgeUnmuteAction(conference, channel)` → des-silenciar
- `ConfbridgeKickAction(conference, channel)` → expulsar
- `ConfbridgeLockAction(conference)` → bloquear sala
- `ConfbridgeUnlockAction(conference)` → desbloquear
- `ConfbridgeStartRecordAction(conference)` → grabar
- `ConfbridgeStopRecordAction(conference)` → parar grabación
- `OriginateAction` → invitar participante externo
- `GetConfigAction("confbridge.conf")` → leer configuración
- `UpdateConfigAction("confbridge.conf", ...)` → CRUD

**Eventos:**
- `ConfbridgeStartEvent` → conferencia iniciada
- `ConfbridgeEndEvent` → conferencia terminada
- `ConfbridgeJoinEvent` → participante entró
- `ConfbridgeLeaveEvent` → participante salió
- `ConfbridgeTalkingEvent` → indicador de habla (VAD)

#### Tareas

- Tarea 9.1: Modelo `ConferenceRoom` ↔ `confbridge.conf`
- Tarea 9.2: `Conferences.razor` — lista activas + configuradas
- Tarea 9.3: `ConferenceRoom.razor` — control en tiempo real (mute/kick/lock/record)
- Tarea 9.4: "Invite" button con `OriginateAction`
- Tarea 9.5: Talking indicator via `ConfbridgeTalkingEvent`

---

### Módulo 10: Features (Transfer, Pickup, DND, etc.)

**Ruta:** `/features`

**Objetivo:** Configuración de features codes y star codes.

#### Vista principal

```
┌──────────────────────────────────────────────────────────────────┐
│ Feature Codes                                                    │
├──────────────────────────────────────────────────────────────────┤
│                                                                  │
│ ── Transfer ──                                                   │
│ Blind Transfer:     [##]    Enabled: [✓]                        │
│ Attended Transfer:  [*2]    Enabled: [✓]                        │
│ Transfer Timeout:   [5] sec                                      │
│                                                                  │
│ ── Call Pickup ──                                                │
│ Directed Pickup:    [*8]    Enabled: [✓]                        │
│ Group Pickup:       [#8]    Enabled: [✓]                        │
│ Pickup Groups: [Edit Groups...]                                  │
│                                                                  │
│ ── DND / CF ──                                                   │
│ DND Toggle:         [*78/*79]  Enabled: [✓]                    │
│ CF Unconditional:   [*72/*73]  Enabled: [✓]                    │
│ CF Busy:            [*90/*91]  Enabled: [✓]                    │
│ CF No Answer:       [*92/*93]  Enabled: [✓]                    │
│                                                                  │
│ ── Parking ──                                                    │
│ Park Extension:     [700]                                        │
│ Park Range:         [701-720]                                    │
│ Park Timeout:       [45] sec                                     │
│ Comeback to Origin: [✓]                                         │
│                                                                  │
│ ── Other ──                                                      │
│ One-Touch Record:   [*1]     Enabled: [✓]                      │
│ Disconnect Code:    [**]     Enabled: [✓]                      │
│ Auto-Monitor:       [*3]     Enabled: [✓]                      │
│                                                                  │
│ [Save] [Reset Defaults]                                          │
└──────────────────────────────────────────────────────────────────┘
```

#### Modelo

```csharp
public sealed class FeatureCodesConfig
{
    // Transfer
    public string BlindTransferCode { get; set; } = "##";
    public string AttendedTransferCode { get; set; } = "*2";
    public int TransferTimeout { get; set; } = 5;
    public bool BlindTransferEnabled { get; set; } = true;
    public bool AttendedTransferEnabled { get; set; } = true;

    // Pickup
    public string DirectedPickupCode { get; set; } = "*8";
    public string GroupPickupCode { get; set; } = "#8";
    public bool PickupEnabled { get; set; } = true;

    // DND / Call Forward
    public string DndOnCode { get; set; } = "*78";
    public string DndOffCode { get; set; } = "*79";
    public string CfUnconditionalOnCode { get; set; } = "*72";
    public string CfUnconditionalOffCode { get; set; } = "*73";
    public string CfBusyOnCode { get; set; } = "*90";
    public string CfBusyOffCode { get; set; } = "*91";
    public string CfNoAnswerOnCode { get; set; } = "*92";
    public string CfNoAnswerOffCode { get; set; } = "*93";

    // Parking
    public string ParkExtension { get; set; } = "700";
    public int ParkRangeStart { get; set; } = 701;
    public int ParkRangeEnd { get; set; } = 720;
    public int ParkTimeout { get; set; } = 45;
    public bool ParkComebackToOrigin { get; set; } = true;

    // Other
    public string OneTouchRecordCode { get; set; } = "*1";
    public string DisconnectCode { get; set; } = "**";
}
```

**AMI Actions:**
- `GetConfigAction("features.conf")` → leer features
- `UpdateConfigAction("features.conf", ...)` → modificar
- `GetConfigAction("res_parking.conf")` → parking config
- `UpdateConfigAction("res_parking.conf", ...)` → modificar parking
- `ParkedCallsAction` → calls actualmente parqueadas
- `CommandAction("features reload")` → aplicar

**Dialplan para star codes (generado en extensions.conf):**
```ini
[feature-codes]
; DND
exten => *78,1,Set(DB(DND/${CALLERID(num)})=yes)
same => n,Playback(activated)
same => n,Hangup()
exten => *79,1,DBdel(DND/${CALLERID(num)})
same => n,Playback(deactivated)
same => n,Hangup()

; Call Forward Unconditional
exten => *72,1,Read(FW_NUM,dial-pls-enter-num-aftr-tone,,,10)
same => n,Set(DB(CF/${CALLERID(num)})=${FW_NUM})
same => n,Playback(activated)
same => n,Hangup()
exten => *73,1,DBdel(CF/${CALLERID(num)})
same => n,Playback(deactivated)
same => n,Hangup()
```

#### Tareas

- Tarea 10.1: Modelo `FeatureCodesConfig` ↔ `features.conf` + dialplan
- Tarea 10.2: `Features.razor` — formulario con secciones colapsables
- Tarea 10.3: Generador de dialplan para star codes
- Tarea 10.4: Parking lot visualizer (calls parqueadas en tiempo real)

---

## Plan de Sprints

### Sprint 5: Infraestructura + Troncales (2-3 semanas)

| Tarea | Descripción | Complejidad |
|-------|-------------|-------------|
| 5.1 | `PbxConfigManager` — servicio base con `GetConfig`/`UpdateConfig`/`ExecuteCommand`/`ReloadModule` | Media |
| 5.2 | Modelo `TrunkConfig` con conversión PJSIP/SIP/IAX | Alta |
| 5.3 | `Trunks.razor` — lista con cards y estado real-time | Media |
| 5.4 | `TrunkEdit.razor` — formulario multi-tecnología | Alta |
| 5.5 | `TrunkDetail.razor` — detalle con registro y estadísticas | Media |
| 5.6 | Tests unitarios de PbxConfigManager y TrunkConfig | Media |

**Dependencias:** Ninguna — Sprint independiente

### Sprint 6: Extensiones y Dispositivos (2-3 semanas)

| Tarea | Descripción | Complejidad |
|-------|-------------|-------------|
| 6.1 | Modelo `ExtensionConfig` con conversión PJSIP sections | Alta |
| 6.2 | Template engine para extensiones | Media |
| 6.3 | `Extensions.razor` — grid con estado real-time | Media |
| 6.4 | `ExtensionEdit.razor` — formulario multi-tab | Alta |
| 6.5 | Panel de voicemail (estado, mensajes) | Media |
| 6.6 | BLF/Hints panel con estado visual | Baja |
| 6.7 | Tests unitarios | Media |

**Dependencias:** Sprint 5 (PbxConfigManager)

### Sprint 7: Rutas + Time Conditions (2 semanas)

| Tarea | Descripción | Complejidad |
|-------|-------------|-------------|
| 7.1 | Modelo `InboundRoute`/`OutboundRoute` con generador de dialplan | Alta |
| 7.2 | `Routes.razor` — vista dual inbound/outbound | Media |
| 7.3 | `RouteEdit.razor` — formulario con validación de patterns | Media |
| 7.4 | Modelo `TimeCondition` con generador de dialplan | Media |
| 7.5 | `TimeConditions.razor` — lista con indicador de estado | Media |
| 7.6 | `TimeConditionEdit.razor` — editor visual + calendario festivos | Alta |
| 7.7 | Tests de generación de dialplan | Media |

**Dependencias:** Sprint 5 (PbxConfigManager), Sprint 6 (extensiones como destino)

### Sprint 8: Colas Avanzadas + IVR (2-3 semanas)

| Tarea | Descripción | Complejidad |
|-------|-------------|-------------|
| 8.1 | Modelo `QueueConfig` ↔ `queues.conf` completo | Media |
| 8.2 | `QueueConfig.razor` — formulario multi-tab | Alta |
| 8.3 | Drag-and-drop para miembros estáticos | Media |
| 8.4 | Modelo `IvrMenu`/`IvrOption` con generador de dialplan | Alta |
| 8.5 | `IvrList.razor` + `IvrEdit.razor` — diseñador visual | Alta |
| 8.6 | Sound file browser (via ARI `SoundsResource`) | Baja |
| 8.7 | "Test" buttons con `OriginateAction` | Baja |
| 8.8 | Tests del generador de dialplan IVR | Media |

**Dependencias:** Sprint 5 (PbxConfigManager), Sprint 7 (rutas como referencia)

### Sprint 9: Grabaciones + Conferencias (2 semanas)

| Tarea | Descripción | Complejidad |
|-------|-------------|-------------|
| 9.1 | Modelo `RecordingPolicy` con generador de dialplan | Media |
| 9.2 | `Recordings.razor` — browse con filtros y audio player | Alta |
| 9.3 | API endpoint para servir archivos de audio | Media |
| 9.4 | On-demand recording button via `MixMonitorAction` | Baja |
| 9.5 | Modelo `ConferenceRoom` ↔ `confbridge.conf` | Media |
| 9.6 | `Conferences.razor` — control en tiempo real | Alta |
| 9.7 | Invite/mute/kick/lock/record actions | Media |
| 9.8 | Talking indicator via `ConfbridgeTalkingEvent` | Baja |

**Dependencias:** Sprint 5 (PbxConfigManager)

### Sprint 10: Features + Polish (1-2 semanas)

| Tarea | Descripción | Complejidad |
|-------|-------------|-------------|
| 10.1 | Modelo `FeatureCodesConfig` ↔ `features.conf` | Baja |
| 10.2 | `Features.razor` — formulario | Media |
| 10.3 | Generador de dialplan para star codes | Media |
| 10.4 | Parking lot visualizer | Baja |
| 10.5 | MOH manager completo | Baja |
| 10.6 | Navigation sidebar actualizado con todos los módulos | Baja |
| 10.7 | Tests E2E con Docker compose | Alta |
| 10.8 | Documentación de usuario | Media |

**Dependencias:** Todos los sprints anteriores

---

## Diagrama de Dependencias entre Sprints

```
Sprint 5 (Infra + Troncales)
    │
    ├──→ Sprint 6 (Extensiones)
    │       │
    │       └──→ Sprint 7 (Rutas + Time Conditions)
    │               │
    │               └──→ Sprint 8 (Colas + IVR)
    │
    ├──→ Sprint 9 (Grabaciones + Conferencias)
    │
    └──→ Sprint 10 (Features + Polish)
```

## Resumen de Archivos Nuevos por Módulo

| Módulo | Archivos Nuevos | Config Files |
|--------|----------------|-------------|
| **Infraestructura** | `PbxConfigManager.cs` | — |
| **Troncales** | `TrunkConfig.cs`, `Trunks.razor`, `TrunkEdit.razor`, `TrunkDetail.razor` | `pjsip.conf`, `sip.conf`, `iax.conf` |
| **Extensiones** | `ExtensionConfig.cs`, `Extensions.razor`, `ExtensionEdit.razor` | `pjsip.conf`, `voicemail.conf` |
| **Rutas** | `InboundRoute.cs`, `OutboundRoute.cs`, `Routes.razor`, `RouteEdit.razor` | `extensions.conf` |
| **Time Conditions** | `TimeCondition.cs`, `TimeConditions.razor`, `TimeConditionEdit.razor` | `extensions.conf`, AstDB |
| **Colas** | `QueueConfig.cs`, `QueueConfigEdit.razor` | `queues.conf` |
| **IVR** | `IvrMenu.cs`, `IvrList.razor`, `IvrEdit.razor` | `extensions.conf` |
| **MOH** | `MohClass.cs`, `MohList.razor`, `MohEdit.razor` | `musiconhold.conf` |
| **Grabaciones** | `RecordingPolicy.cs`, `Recordings.razor`, `RecordingController.cs` | `extensions.conf` |
| **Conferencias** | `ConferenceRoom.cs`, `Conferences.razor`, `ConferenceControl.razor` | `confbridge.conf` |
| **Features** | `FeatureCodesConfig.cs`, `Features.razor` | `features.conf`, `res_parking.conf` |

## Riesgos y Mitigaciones

| Riesgo | Impacto | Mitigación |
|--------|---------|-----------|
| `UpdateConfigAction` no soporta todas las directivas | Alto | Fallback a `CommandAction("config reload")` + validar con `GetConfigAction` post-update |
| Concurrencia: dos usuarios editando misma config | Medio | Optimistic locking: leer versión antes, comparar al guardar |
| Asterisk restart necesario para algunos cambios | Medio | Indicar claramente en UI cuándo se requiere reload vs restart |
| Seguridad: passwords de troncales en config | Alto | Enmascarar en UI, audit log de cambios, permisos por rol |
| Config syntax errors pueden romper Asterisk | Crítico | Validar antes de escribir, backup automático de `.conf` via `GetConfigAction`, botón "Rollback" |
| Diferentes versiones de Asterisk | Medio | Feature detection via `CoreSettingsAction` (versión), deshabilitar features no soportados |
| Performance con muchas extensiones (>500) | Bajo | Paginación, virtualización en Blazor, carga lazy |

## Dependencias Externas

**Ninguna nueva.** Todo se implementa con:
- AMI Actions existentes en el SDK (111 acciones)
- AMI Events existentes (222 eventos)
- ARI Resources existentes (7 resources)
- Config parsers existentes
- Blazor Server SSR (patrón existente)
- CSS puro (sin dependencias JS adicionales)
