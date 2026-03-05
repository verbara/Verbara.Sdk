# Plan: SDK Hardening & Quality Improvements

> Resultado de la auditoria de calidad del SDK.
> Fecha: 2026-03-05 | Branch: `feature/rename-asterisk-sdk`

---

## Contexto

Con la cobertura de eventos al 100% (AMI 261 eventos, ARI 46 eventos), el siguiente paso es endurecer el SDK para produccion. La auditoria identifico 10 areas de mejora organizadas en 4 sprints por prioridad.

---

## Sprint 1 — Fixes Criticos de Produccion (est. ~1.5h)

### Tarea 1.1 — Limitar reconexion infinita en AriClient

**Problema:** El loop de reconexion en `AriClient.cs` nunca verifica `MaxReconnectAttempts`, causando reintentos infinitos que pueden saturar Asterisk.

**Archivos:**
- `src/Asterisk.Sdk.Ari/Client/AriClient.cs` — agregar check de `MaxReconnectAttempts` en el loop de reconexion

**Criterio:** Despues de N intentos fallidos, dejar de reconectar y notificar via log + evento.

**Estado:** 🔲 Pendiente

### Tarea 1.2 — Limitar reconexion infinita en AmiConnection

**Problema:** Similar al ARI — `AmiConnection.cs` define `MaxReconnectAttempts` en options pero no lo verifica en el loop.

**Archivos:**
- `src/Asterisk.Sdk.Ami/Connection/AmiConnection.cs` — verificar enforcement del limite

**Estado:** 🔲 Pendiente

### Tarea 1.3 — Corregir exception swallowing en FastAgiServer

**Problema:** `catch (Exception)` en linea ~130 de `FastAgiServer.cs` traga todas las excepciones sin logging, haciendo imposible debuggear fallos en produccion.

**Archivos:**
- `src/Asterisk.Sdk.Agi/Server/FastAgiServer.cs` — agregar logging con `ILogger`

**Estado:** 🔲 Pendiente

### Tarea 1.4 — Completar metadata NuGet

**Problema:** 5 de 9 proyectos publicables carecen de `<Description>` en su `.csproj`, bloqueando publicacion correcta en NuGet.

**Archivos:**
- `src/Asterisk.Sdk.Agi/Asterisk.Sdk.Agi.csproj`
- `src/Asterisk.Sdk.Ari/Asterisk.Sdk.Ari.csproj`
- `src/Asterisk.Sdk.Live/Asterisk.Sdk.Live.csproj`
- `src/Asterisk.Sdk.Activities/Asterisk.Sdk.Activities.csproj`
- `src/Asterisk.Sdk.Config/Asterisk.Sdk.Config.csproj`

**Estado:** 🔲 Pendiente

### Tarea 1.5 — Tests para fixes de reconexion

- Test unitario: `AriClient` deja de reconectar despues de `MaxReconnectAttempts`
- Test unitario: `AmiConnection` respeta el limite
- Test: `FastAgiServer` loguea excepciones

**Estado:** 🔲 Pendiente

**Criterio Sprint 1:** 0 loops infinitos, 0 exceptions tragadas, metadata NuGet completa, build 0 warnings.

---

## Sprint 2 — ARI REST Completo + Documentacion Sizing (est. ~3h)

### Tarea 2.1 — Agregar AriDeviceStatesResource

**Problema:** Unico recurso REST faltante del ARI spec. Necesario para monitoreo de estado BLF (Busy Lamp Field).

**Archivos a crear:**
- `src/Asterisk.Sdk.Ari/Resources/AriDeviceStatesResource.cs`
- `src/Asterisk.Sdk/IAriClient.cs` — agregar `IAriDeviceStatesResource DeviceStates { get; }`

**Operaciones:**
```
GET    /deviceStates           — listar todos los estados
GET    /deviceStates/{name}    — obtener estado de un dispositivo
PUT    /deviceStates/{name}    — cambiar estado
DELETE /deviceStates/{name}    — eliminar estado custom
```

**Estado:** 🔲 Pendiente

### Tarea 2.2 — Tests para DeviceStatesResource

- Tests unitarios con HttpClient mockeado
- Test de ParseEvent para DeviceStateChanged (ya existe)

**Estado:** 🔲 Pendiente

### Tarea 2.3 — Documentar sizing de EventPump para alta carga

**Problema:** Sin guia de capacidad para `EventPumpCapacity` en escenarios 100K+ agentes. Riesgo de OOM.

**Archivo a crear:**
- `docs/high-load-tuning.md` — guia de configuracion para alta carga

**Contenido:**
- Tabla de `EventPumpCapacity` recomendado por escala (100, 1K, 10K, 100K agentes)
- Metricas a monitorear (`ami.event_pump.pending`, `ami.events_dropped`)
- Configuracion de backpressure en `PipelineSocketConnection`
- Ejemplos de `appsettings.json` para cada escala

**Estado:** 🔲 Pendiente

**Criterio Sprint 2:** ARI REST 100% (8/8 resources), guia de sizing documentada, build 0 warnings.

---

## Sprint 3 — Cobertura de Tests (est. ~4h)

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

## Sprint 4 — Optimizaciones y DX (est. ~2h)

### Tarea 4.1 — Fix micro-allocation en observer unsubscribe

**Problema:** `_observers.Where(o => o != observer).ToArray()` en `AmiConnection.cs:628` crea array nuevo en cada unsubscribe.

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

| Sprint | Objetivo | Tareas | Estado |
|--------|----------|--------|--------|
| **1** | Fixes criticos produccion | 1.1-1.5 | 🔲 Pendiente |
| **2** | ARI REST 100% + sizing docs | 2.1-2.3 | 🔲 Pendiente |
| **3** | Cobertura de tests | 3.1-3.3 | 🔲 Pendiente |
| **4** | Optimizaciones y DX | 4.1-4.3 | 🔲 Pendiente |

## Resultado Esperado

| Metrica | Antes | Despues |
|---------|-------|---------|
| Reconnection bounds | Infinito | Configurable con limite |
| Exception visibility | Tragadas | Logueadas |
| NuGet metadata | 4/9 | **9/9** |
| ARI REST resources | 7/8 | **8/8** |
| High-load docs | Ninguna | Guia completa |
| Server pool tests | 0 | **3+ escenarios** |
| Config parser tests | 0 | **5+ escenarios** |
| Observer allocations | ToArray() | Zero-alloc |
