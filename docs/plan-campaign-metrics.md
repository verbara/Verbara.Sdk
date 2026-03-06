# Plan: Campaign Metrics — Contact Center Real

> Fecha: 2026-03-06 | Branch: `feature/rename-asterisk-sdk`

---

## Contexto

La pagina "Campaign Metrics" anterior fue renombrada a **Traffic Analytics** porque solo mostraba un resumen de trafico del servidor (buffer de 500 llamadas / 5 min). Se necesita una funcionalidad real de campanas de contact center.

### Que tiene el SDK hoy

| Componente | Disponible | Limitacion |
|------------|-----------|------------|
| `OriginateAction` | Si | Solo 1 llamada a la vez, sin batch |
| `CdrEvent` | Si | Fires en cada llamada, pero no se persiste |
| `CelEvent` | Si | Channel Event Logging, no se persiste |
| `AgentCompleteEvent` | Si | holdTime, talkTime, reason por agente |
| `QueueCallerAbandonEvent` | Si | holdTime, originalPosition |
| `AsteriskQueue` (Live) | Si | Completed, Abandoned, HoldTime, TalkTime |
| `CallFlowTracker` | Si | Buffer 500 calls / 5 min (no persistencia) |
| `DbConfigProvider` (Dapper + Npgsql) | Si | Solo para config, no para CDR/campanas |
| PostgreSQL (Docker) | Si | DB `asterisk` ya configurada |
| `HangupCause` enum | Si | 26 causas Q.931 mapeadas |
| `Disposition` enum | Si | NoAnswer, Failed, Busy, Answered, Congestion |

### Que falta

| Feature | Estado | Impacto |
|---------|--------|---------|
| Modelo de campana (entidad) | No existe | Critico |
| Persistencia de CDR | No existe | Critico |
| Batch origination con rate limiting | No existe | Alto |
| Service Level % | No calculado | Alto |
| Inbound vs Outbound distinction | No explicito | Medio |
| DNC (Do Not Call) list | No existe | Medio |
| Retry/redial logic | No existe | Medio |
| Call recording tracking | No existe | Bajo |

---

## Arquitectura Propuesta

### Modelo de Datos

```
┌─────────────────┐     ┌──────────────────┐     ┌─────────────────┐
│   campaigns     │     │  campaign_calls   │     │  campaign_cdrs  │
├─────────────────┤     ├──────────────────┤     ├─────────────────┤
│ id (PK)         │────<│ campaign_id (FK)  │     │ id (PK)         │
│ name            │     │ id (PK)           │────<│ call_id (FK)    │
│ type            │     │ phone_number      │     │ disposition     │
│ server_id       │     │ caller_name       │     │ hangup_cause    │
│ queue_name      │     │ status            │     │ duration_secs   │
│ context         │     │ disposition       │     │ billable_secs   │
│ caller_id       │     │ attempts          │     │ talk_time_secs  │
│ max_concurrent  │     │ max_attempts      │     │ hold_time_secs  │
│ calls_per_sec   │     │ last_attempt_at   │     │ agent_name      │
│ status          │     │ answered_at       │     │ agent_interface │
│ created_at      │     │ hangup_cause      │     │ queue_name      │
│ started_at      │     │ agent_name        │     │ start_time      │
│ stopped_at      │     │ talk_duration     │     │ answer_time     │
│ total_contacts  │     │ hold_duration     │     │ end_time        │
│ schedule_cron   │     │ recording_file    │     │ caller_id       │
│ retry_delay_min │     │ notes             │     │ destination     │
│ dnc_enabled     │     │ created_at        │     │ account_code    │
└─────────────────┘     │ updated_at        │     │ user_field      │
                        └──────────────────┘     │ linked_id       │
                                                  │ unique_id       │
┌─────────────────┐                               │ server_id       │
│  dnc_numbers    │                               │ created_at      │
├─────────────────┤                               └─────────────────┘
│ phone_number PK │
│ reason          │
│ created_at      │
└─────────────────┘
```

### Tipos de Campana

| Tipo | Descripcion | Flujo |
|------|-------------|-------|
| **Outbound** | Marcacion automatica desde lista | Originate → Queue/Extension → Agent |
| **Inbound** | Llamadas entrantes a queue | Caller → Queue → Agent (tag con campaign_id) |
| **Blended** | Agentes manejan in+out | Combina ambos flujos |
| **Preview** | Agente ve datos antes de marcar | UI muestra contacto → agente acepta → Originate |
| **Progressive** | Auto-dial cuando agente libre | Monitor agentes disponibles → Originate automatico |

### Estados de Campana

```
Draft → Scheduled → Running → Paused → Completed
                      ↑          │
                      └──────────┘
```

### Estados de Contacto (campaign_calls)

```
Pending → Dialing → Answered → Completed
    │         │         │
    │         ├→ NoAnswer ──→ Retry (si attempts < max)
    │         ├→ Busy ──────→ Retry
    │         ├→ Failed ────→ Retry
    │         └→ Congestion → Retry
    │
    └→ DNC (si numero en lista)
```

---

## Sprint Plan

### Sprint 1 — Persistencia CDR + Modelo Base

**Objetivo**: CDR en PostgreSQL + entidad Campaign basica.

#### Tarea 1.1 — Schema SQL para campaigns + CDR

Crear migracion `003-campaign-schema.sql`:

```sql
-- Tabla de CDRs persistentes (alimentada por CdrEvent)
CREATE TABLE campaign_cdrs (
    id              BIGSERIAL PRIMARY KEY,
    unique_id       TEXT NOT NULL,
    linked_id       TEXT,
    server_id       TEXT NOT NULL,
    campaign_id     INTEGER REFERENCES campaigns(id),
    caller_id       TEXT,
    destination     TEXT,
    context         TEXT,
    disposition     TEXT NOT NULL,  -- ANSWERED, NO ANSWER, BUSY, FAILED, CONGESTION
    hangup_cause    INTEGER,
    duration_secs   INTEGER NOT NULL DEFAULT 0,
    billable_secs   INTEGER NOT NULL DEFAULT 0,
    talk_time_secs  INTEGER,
    hold_time_secs  INTEGER,
    agent_name      TEXT,
    agent_interface TEXT,
    queue_name      TEXT,
    account_code    TEXT,
    user_field      TEXT,
    start_time      TIMESTAMPTZ NOT NULL,
    answer_time     TIMESTAMPTZ,
    end_time        TIMESTAMPTZ NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_cdrs_server_start ON campaign_cdrs(server_id, start_time DESC);
CREATE INDEX idx_cdrs_campaign ON campaign_cdrs(campaign_id) WHERE campaign_id IS NOT NULL;
CREATE INDEX idx_cdrs_disposition ON campaign_cdrs(disposition, start_time DESC);
CREATE INDEX idx_cdrs_agent ON campaign_cdrs(agent_name, start_time DESC);

-- Campanas
CREATE TABLE campaigns (
    id              SERIAL PRIMARY KEY,
    name            TEXT NOT NULL,
    type            TEXT NOT NULL DEFAULT 'outbound',  -- outbound, inbound, blended, preview, progressive
    server_id       TEXT NOT NULL,
    queue_name      TEXT,
    context         TEXT NOT NULL DEFAULT 'default',
    caller_id       TEXT,
    max_concurrent  INTEGER NOT NULL DEFAULT 1,
    calls_per_sec   NUMERIC(4,2) NOT NULL DEFAULT 1.0,
    max_attempts    INTEGER NOT NULL DEFAULT 3,
    retry_delay_min INTEGER NOT NULL DEFAULT 30,
    dnc_enabled     BOOLEAN NOT NULL DEFAULT FALSE,
    status          TEXT NOT NULL DEFAULT 'draft',  -- draft, scheduled, running, paused, completed
    schedule_cron   TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    started_at      TIMESTAMPTZ,
    stopped_at      TIMESTAMPTZ
);

-- Contactos de campana
CREATE TABLE campaign_calls (
    id              BIGSERIAL PRIMARY KEY,
    campaign_id     INTEGER NOT NULL REFERENCES campaigns(id) ON DELETE CASCADE,
    phone_number    TEXT NOT NULL,
    caller_name     TEXT,
    status          TEXT NOT NULL DEFAULT 'pending',  -- pending, dialing, answered, completed, no_answer, busy, failed, dnc, retry
    disposition     TEXT,
    hangup_cause    INTEGER,
    attempts        INTEGER NOT NULL DEFAULT 0,
    max_attempts    INTEGER NOT NULL DEFAULT 3,
    agent_name      TEXT,
    talk_duration   INTEGER,  -- seconds
    hold_duration   INTEGER,  -- seconds
    recording_file  TEXT,
    notes           TEXT,
    last_attempt_at TIMESTAMPTZ,
    answered_at     TIMESTAMPTZ,
    completed_at    TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_calls_campaign_status ON campaign_calls(campaign_id, status);
CREATE INDEX idx_calls_phone ON campaign_calls(phone_number);

-- Lista DNC
CREATE TABLE dnc_numbers (
    phone_number    TEXT PRIMARY KEY,
    reason          TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

**Complejidad**: Baja | **Riesgo**: Ninguno

#### Tarea 1.2 — CdrPersistenceService

Servicio que escucha `CdrEvent` via AMI observer y persiste en `campaign_cdrs`:

```csharp
public sealed class CdrPersistenceService : IObserver<ManagerEvent>
{
    // Escucha CdrEvent → INSERT INTO campaign_cdrs via Dapper
    // Batch insert cada 1 segundo o cada 100 CDRs (lo que ocurra primero)
    // Asocia campaign_id si el AccountCode/UserField contiene tag de campana
}
```

**Complejidad**: Media | **Riesgo**: Bajo

#### Tarea 1.3 — CampaignRepository (Dapper)

CRUD basico de campanas y contactos:

```csharp
public sealed class CampaignRepository
{
    Task<List<Campaign>> GetAllAsync(string serverId);
    Task<Campaign?> GetByIdAsync(int id);
    Task<int> CreateAsync(Campaign campaign);
    Task UpdateStatusAsync(int id, string status);
    Task<int> ImportContactsAsync(int campaignId, List<CampaignContact> contacts);
    Task<CampaignStats> GetStatsAsync(int campaignId);
    Task<List<CdrRecord>> GetCdrsAsync(string serverId, DateTimeOffset from, DateTimeOffset to);
}
```

**Complejidad**: Media | **Riesgo**: Bajo

---

### Sprint 2 — Campaign Engine + Origination

**Objetivo**: Motor de marcacion automatica con rate limiting.

#### Tarea 2.1 — CampaignDialerService

`IHostedService` que ejecuta campanas activas:

```csharp
public sealed class CampaignDialerService : BackgroundService
{
    // Loop principal:
    // 1. Cargar campanas con status = 'running'
    // 2. Para cada campana:
    //    a. Verificar max_concurrent (no superar llamadas simultaneas)
    //    b. Verificar calls_per_sec (rate limiting con SemaphoreSlim + Timer)
    //    c. Obtener siguiente contacto con status = 'pending'
    //    d. Verificar DNC si dnc_enabled
    //    e. Originate via AsteriskServer.OriginateAsync()
    //    f. Actualizar status del contacto a 'dialing'
    //    g. Escuchar eventos para actualizar disposition
    // 3. Sleep hasta siguiente ciclo
}
```

Rate limiting por campana:
```
max_concurrent=5, calls_per_sec=2.0
→ Maximo 5 llamadas simultaneas
→ Originate cada 500ms (2/sec)
→ SemaphoreSlim(5) + PeriodicTimer(500ms)
```

**Complejidad**: Alta | **Riesgo**: Medio (necesita testing con Asterisk real)

#### Tarea 2.2 — CampaignCallTracker

Observer que correlaciona eventos AMI con contactos de campana:

```csharp
// Al originar, se tagea la llamada con:
//   AccountCode = $"CAMP-{campaignId}-{contactId}"
//   Variable = "CAMPAIGN_ID={campaignId}"
//
// Al recibir CdrEvent:
//   1. Extraer campaign_id de AccountCode
//   2. Actualizar campaign_calls.disposition
//   3. Si no-answer/busy/failed y attempts < max_attempts → status='retry'
//   4. Si answered → status='answered', guardar agent_name, talk_duration
//   5. Actualizar campaign_cdrs con campaign_id
```

**Complejidad**: Media | **Riesgo**: Medio

#### Tarea 2.3 — DNC Service

```csharp
public sealed class DncService
{
    Task<bool> IsBlockedAsync(string phoneNumber);
    Task AddAsync(string phoneNumber, string reason);
    Task RemoveAsync(string phoneNumber);
    Task<int> ImportAsync(List<string> numbers, string reason);
    Task<List<DncEntry>> ListAsync(int limit = 100);
}
```

**Complejidad**: Baja | **Riesgo**: Ninguno

---

### Sprint 3 — Dashboard Pages

**Objetivo**: UI de gestion de campanas y metricas.

#### Tarea 3.1 — Campaigns List Page (`/campaigns`)

- Lista de campanas con status, progreso, KPIs rapidos
- Botones: Crear, Start, Pause, Stop
- Indicador visual de progreso (% contactos procesados)

#### Tarea 3.2 — Campaign Detail Page (`/campaigns/{id}`)

KPIs de campana:

| KPI | Formula | Fuente |
|-----|---------|--------|
| **Contact Rate** | Answered / Total Originated | campaign_calls |
| **Conversion Rate** | Completed / Answered | campaign_calls |
| **Abandonment Rate** | (NoAnswer + Busy + Failed) / Total | campaign_calls |
| **Avg Talk Time** | AVG(talk_duration) WHERE answered | campaign_calls |
| **Avg Wait Time** | AVG(hold_duration) WHERE answered | campaign_calls |
| **Service Level** | % answered within X seconds | campaign_cdrs |
| **Calls/Hour** | COUNT per hour window | campaign_cdrs |
| **Agent Utilization** | talk_time / (talk_time + idle_time) | campaign_cdrs |

Secciones:
- KPI cards (8 metricas principales)
- Disposition breakdown (pie chart conceptual en tabla)
- Top agents por calls handled y avg talk time
- Contactos recientes con status, disposition, agent
- Timeline de actividad (calls/hora)

#### Tarea 3.3 — Campaign Create/Edit Page (`/campaigns/new`, `/campaigns/{id}/edit`)

Form:
- Nombre, tipo (outbound/inbound/blended/preview/progressive)
- Servidor, queue, contexto, caller ID
- Rate: max_concurrent, calls_per_sec
- Retry: max_attempts, retry_delay_min
- DNC habilitado
- Importar contactos (textarea o CSV)

#### Tarea 3.4 — CDR History Page (`/cdr`)

- Tabla de CDRs con filtros: server, fecha, disposition, agent, queue
- Busqueda por caller ID / destination
- Export conceptual (tabla paginada)

**Complejidad por tarea**: Media | **Riesgo**: Bajo

---

### Sprint 4 — Inbound + Service Level

**Objetivo**: Soporte inbound, SLA, y campanas blended.

#### Tarea 4.1 — Inbound Campaign Tracking

- Campana inbound = tag con AccountCode en queue config
- Asterisk `accountcode` en `queues.conf` se propaga a CdrEvent
- Mapear CdrEvent.AccountCode → campaign_id

#### Tarea 4.2 — Service Level Calculation

```sql
-- Service Level = % llamadas respondidas dentro de umbral (ej: 20 seg)
SELECT
    COUNT(*) FILTER (WHERE hold_time_secs <= 20 AND disposition = 'ANSWERED')::float
    / NULLIF(COUNT(*), 0) * 100
    AS service_level_pct
FROM campaign_cdrs
WHERE campaign_id = @id AND start_time >= @from;
```

#### Tarea 4.3 — Real-time Campaign Dashboard Widgets

Agregar widget en Home page:
- Campanas activas con progreso
- Service level actual por queue

---

## Tabla Comparativa de Sprints

| Sprint | Objetivo | Tareas | Complejidad | Dependencia |
|--------|----------|--------|-------------|-------------|
| **1** | Persistencia CDR + Modelo | 1.1-1.3 | Media | PostgreSQL |
| **2** | Campaign Engine | 2.1-2.3 | Alta | Sprint 1 |
| **3** | Dashboard Pages | 3.1-3.4 | Media | Sprint 1 (parcial Sprint 2) |
| **4** | Inbound + SLA | 4.1-4.3 | Media | Sprint 1-3 |

---

## Prerequisitos

| Requisito | Estado | Notas |
|-----------|--------|-------|
| PostgreSQL en Docker | Ya configurado | `docker-compose.dashboard.yml` |
| Dapper + Npgsql | Ya en proyecto | Usado por `DbConfigProvider` |
| `CdrEvent` en SDK | Ya existe | 15+ campos disponibles |
| `OriginateAction` | Ya existe | Single-call, async soportado |
| `AgentCompleteEvent` | Ya existe | holdTime, talkTime, reason |
| `CallFlowTracker` | Ya existe | Necesita extension con campaign_id |
| `AsteriskServer.OriginateAsync()` | Ya existe | Retorna `OriginateResult` |

---

## Metricas de Exito

| Metrica | Target |
|---------|--------|
| CDRs persistidos | 100% de CdrEvents → PostgreSQL |
| Campana outbound funcional | Originate con rate limiting + disposition tracking |
| Campana inbound funcional | Queue-based con AccountCode tagging |
| Service Level calculo | % within threshold, actualizado cada 5 seg |
| Retencion de datos | Ilimitada (PostgreSQL vs 5 min actual) |
| Contactos procesados/hora | Depende de rate config (max: max_concurrent × 120/min) |
| Dashboard response time | < 500ms para queries de campana |
