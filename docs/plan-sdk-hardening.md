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

## Sprint 3 — Cobertura de Tests (est. ~4h) 🔲 PENDIENTE

### Tarea 3.1 — Tests de failover multi-servidor

**Problema:** `AsteriskServerPool` no tiene tests para escenarios de failover, reconexion y routing federado.

**Archivo a crear:**
- `Tests/Asterisk.Sdk.Live.Tests/Server/AsteriskServerPoolTests.cs`

**Escenarios:**
- Pool con 2+ servidores, uno se desconecta — verificar failover
- Reconexion de servidor — verificar reload de managers
- Routing de agente a servidor correcto

**Estado:** 🔲 Pendiente

### Tarea 3.2 — Tests de ConfigFileReader

**Problema:** `ConfigFileReader` y `ExtensionsConfigFileReader` sin tests unitarios dedicados.

**Archivo a crear:**
- `Tests/Asterisk.Sdk.Config.Tests/ConfigFileReaderTests.cs` (verificar si ya existe)

**Escenarios:**
- Parseo de archivo `.conf` con secciones, comentarios, includes
- Parseo de `extensions.conf` con contextos, extensiones, prioridades
- Edge cases: lineas vacias, comentarios inline, continuacion de linea

**Estado:** 🔲 Pendiente

### Tarea 3.3 — Tests de AudioSocket

**Problema:** Implementacion de AudioSocket con cobertura minima.

**Archivo a crear/expandir:**
- `Tests/Asterisk.Sdk.Ari.Tests/Audio/AudioSocketTests.cs`

**Estado:** 🔲 Pendiente

**Criterio Sprint 3:** Cobertura de tests para server pool, config parser, y audio socket.

---

## Sprint 4 — Optimizaciones y DX (est. ~2h) 🔲 PENDIENTE

### Tarea 4.1 — Fix micro-allocation en observer unsubscribe

**Problema:** `_observers.Where(o => o != observer).ToArray()` en `AmiConnection.cs` crea array nuevo en cada unsubscribe.

**Solucion:** Usar `ImmutableArray<T>` o `lock` + `List<T>.Remove()` para evitar allocacion.

**Estado:** 🔲 Pendiente

### Tarea 4.2 — READMEs por proyecto

**Archivos a crear:**
- `src/Asterisk.Sdk.Ami/README.md`
- `src/Asterisk.Sdk.Agi/README.md`
- `src/Asterisk.Sdk.Ari/README.md`
- `src/Asterisk.Sdk.Live/README.md`

**Contenido:** Descripcion breve, ejemplo de uso minimo, link a docs principales.

**Estado:** 🔲 Pendiente

### Tarea 4.3 — Troubleshooting guide

**Archivo a crear:**
- `docs/troubleshooting.md`

**Contenido:**
- Problemas comunes de conexion AMI/ARI (timeout, auth, firewall)
- Diagnostico de eventos perdidos (EventPump lleno)
- Errores de AOT trimming
- Configuracion de logging para debug

**Estado:** 🔲 Pendiente

**Criterio Sprint 4:** Zero allocations innecesarias en hot path, documentacion DX completa.

---

## Resumen

| Sprint | Objetivo | Tareas | Estado | Commit |
|--------|----------|--------|--------|--------|
| **1** | Fixes criticos produccion | 1.1 | ✅ | `21a189e` |
| **2** | ARI REST 100% + sizing docs | 2.1-2.3 | ✅ | `64a9133` |
| **3** | Cobertura de tests | 3.1-3.3 | 🔲 | — |
| **4** | Optimizaciones y DX | 4.1-4.3 | 🔲 | — |

## Resultado Esperado

| Metrica | Antes | Despues |
|---------|-------|---------|
| Reconnection bounds | ARI infinito | ✅ **Configurable** |
| ARI REST resources | 7/8 | **8/8** |
| High-load docs | Ninguna | Guia completa |
| Server pool tests | 0 | **3+ escenarios** |
| Config parser tests | 0 | **5+ escenarios** |
| Observer allocations | ToArray() | Zero-alloc |
