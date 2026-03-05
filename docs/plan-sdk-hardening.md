# Plan: SDK Hardening & Quality Improvements

> Resultado de la auditoria de calidad del SDK.
> Fecha: 2026-03-05 | Branch: `feature/rename-asterisk-sdk`

---

## Contexto

Con la cobertura de eventos al 100% (AMI 261 eventos, ARI 46 eventos), el siguiente paso es endurecer el SDK para produccion. La auditoria identifico areas de mejora organizadas en 4 sprints por prioridad.

---

## Sprint 1 — Fixes Criticos de Produccion ✅ COMPLETADO

**Commit:** `21a189e` fix(ari): add MaxReconnectAttempts to prevent infinite reconnection loop

### Hallazgos vs Realidad

| Tarea original | Resultado |
|----------------|-----------|
| 1.1 AriClient reconnection infinita | ✅ **Corregido** — Agregado `MaxReconnectAttempts` a `AriClientOptions` + bounds check + log `ReconnectGaveUp` |
| 1.2 AmiConnection reconnection | ✅ **Ya estaba implementado** — `MaxReconnectAttempts` verificado en linea 459 |
| 1.3 FastAgiServer exception swallowing | ✅ **Ya estaba implementado** — `ConnectionError` log en linea 127 |
| 1.4 NuGet metadata faltante | ✅ **Ya estaba completa** — Los 9 proyectos tienen `<Description>` |
| 1.5 Tests | N/A — solo 1 fix real, behavior verificable solo via integration test |

> La auditoria sobreestimo los problemas. Solo el AriClient tenia el bug real.

---

## Sprint 2 — ARI REST Completo + Documentacion Sizing ✅ COMPLETADO

**Commit:** `64a9133`

### Tarea 2.1 — Agregar AriDeviceStatesResource ✅

- Creado `AriDeviceStatesResource.cs` con List, Get, Update (PUT), Delete
- Agregado `IAriDeviceStatesResource` interface en `IAriClient.cs`
- Registrado `[JsonSerializable(typeof(AriDeviceState[]))]` en `AriJsonContext`
- Instanciado en `AriClient` constructor

### Tarea 2.2 — Tests para DeviceStatesResource ✅

- 4 tests unitarios con `FakeHttpHandler` mockeado: List, Get, Update, Delete
- Todos verifican HTTP method, URL encoding, y deserializacion
- Tests en `AriResourceTests.cs` siguiendo patron existente

### Tarea 2.3 — Documentar sizing de EventPump para alta carga ✅

- Creado `docs/high-load-tuning.md` con:
  - Tabla de `EventPumpCapacity` recomendado por escala (100 a 100K agentes)
  - Todas las metricas AMI y ARI con umbrales de alerta
  - Configuracion de `PipelineSocketConnection` backpressure
  - Ejemplos de `appsettings.json` para 10K y 100K agentes
  - Guia de GC tuning para escenarios de alta carga

**Criterio:** ARI REST 8/8 resources ✅, guia de sizing documentada ✅, build 0 warnings ✅.

---

## Sprint 3 — Cobertura de Tests ✅ COMPLETADO

**Commit:** `d08e268`

### Tarea 3.1 — Tests de failover multi-servidor ✅

- 6 nuevos tests agregados a `AsteriskServerPoolTests.cs` (total: 14):
  - `MultiServer_ShouldTrackMultipleServers` — pool con 2 servidores
  - `Servers_ShouldEnumerateAllServers` — enumeracion de 3 servidores
  - `RemoveServerAsync_ShouldBeIdempotent_WhenServerNotFound`
  - `AgentRouting_ShouldTrackAgentLogin` — routing positivo
  - `AgentRouting_ShouldRemoveOnLogoff` — cleanup en logoff
  - `AgentRouting_ShouldRouteToCorrectServer` — routing federado east/west

### Tarea 3.2 — Tests de ConfigFileReader ✅ (ya existian)

- Ya existian 10 tests en `ConfigFileReaderTests.cs` + 6 en `ExtensionsConfigTests.cs`
- Cobertura completa: secciones, comentarios, includes, templates, append, extensiones, same, prioridades

### Tarea 3.3 — Tests de AudioSocket ✅ (ya existian)

- Ya existian 19 tests en 3 archivos:
  - `AudioSocketProtocolTests.cs` (10 tests): frame parsing, serialization
  - `AudioSocketSessionTests.cs` (4 tests): session lifecycle, hangup, error
  - `WebSocketAudioServerTests.cs` (5 tests): upgrade handshake, options

**Criterio:** 479 unit tests passing, 0 failures. Cobertura de server pool, config, y audio socket verificada.

---

## Sprint 4 — Optimizaciones y DX ✅ COMPLETADO

**Commit:** `9c8f65e`

### Tarea 4.1 — Fix micro-allocation en observer unsubscribe ✅

- Reemplazado `.Where().ToArray()` con `Array.IndexOf` + `Array.Copy`
- Elimina allocaciones de closure LINQ, iterador, y buffer intermedio
- Copy-on-write volatile pattern mantiene hot path (dispatch) zero-alloc

### Tarea 4.2 — READMEs por proyecto ✅

- Creados 4 READMEs: Ami, Agi, Ari, Live
- Cada uno con: descripcion, features, quick start, links a docs

### Tarea 4.3 — Troubleshooting guide ✅

- Creado `docs/troubleshooting.md` con:
  - Problemas de conexion AMI/ARI (refused, auth, firewall)
  - Diagnostico de eventos perdidos (buffer sizing)
  - Reconexion (infinite loop, state loss)
  - AOT/trimming issues (JsonSerializer, source generators, trim warnings)
  - Configuracion de logging para debug

**Criterio:** Zero LINQ allocations en unsubscribe ✅, docs DX completa ✅, 479 tests passing ✅.

---

## Resumen

| Sprint | Objetivo | Tareas | Estado | Commit |
|--------|----------|--------|--------|--------|
| **1** | Fixes criticos produccion | 1.1 | ✅ | `21a189e` |
| **2** | ARI REST 100% + sizing docs | 2.1-2.3 | ✅ | `34fe206` |
| **3** | Cobertura de tests | 3.1-3.3 | ✅ | `d08e268` |
| **4** | Optimizaciones y DX | 4.1-4.3 | ✅ | `9c8f65e` |

## Resultado Esperado

| Metrica | Antes | Despues |
|---------|-------|---------|
| Reconnection bounds | ARI infinito | ✅ **Configurable** |
| ARI REST resources | 7/8 | **8/8** |
| High-load docs | Ninguna | Guia completa |
| Server pool tests | 0 | **3+ escenarios** |
| Config parser tests | 0 | **5+ escenarios** |
| Observer allocations | ToArray() | Zero-alloc |
