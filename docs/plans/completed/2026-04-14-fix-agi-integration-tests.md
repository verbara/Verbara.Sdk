# Fix FastAGI Integration Tests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Hacer que los 4 `FastAgiIntegrationTests` pasen (actualmente todos fallan con `SocketException: Address already in use`) corrigiendo la arquitectura del test fixture.

**Architecture:** FastAGI es reverse-callback — Asterisk en el container llama OUT al `FastAgiServer` del test en el host. El bug raíz es que `AsteriskContainer` exponía el puerto 4573 via `.WithPortBinding(4573, true)`, haciendo que `docker-proxy` se apropiara de ese puerto antes de que el test pudiera bindearlo. El fix tiene 3 partes acopladas: (1) quitar el port binding erróneo y exponer el host vía `host.docker.internal`, (2) añadir la extensión 200 al dialplan de Asterisk para que llame al AGI server del test, (3) actualizar el test para bindear al puerto fijo `4573`.

**Tech Stack:** .NET 10, xUnit 2.9.3, Testcontainers.NET, Asterisk 22 dialplan, Docker Linux (`host-gateway`)

---

## Root Cause Summary

```
docker-proxy                    docker-proxy
 ↓ ya ocupa :XXXX                ↓ ya ocupa :4573 (después del fix: ELIMINADO)
AsteriskContainer                AsteriskContainer
  .WithPortBinding(4573, true)     (sin port binding 4573)
  AgiPort = GetMappedPublicPort    (sin AgiPort, AGI es reverse-callback)
        |                                 |
FastAgiServer(AgiPort, ...)       FastAgiServer(4573, ...)
  TcpListener.Start() → FALLA       TcpListener.Start() → OK
                                    Container dialea agi://host.docker.internal:4573/test-script → OK
```

`host.docker.internal` se resuelve al host en Linux Docker via `--add-host=host.docker.internal:host-gateway` (Testcontainers lo expone como `.WithExtraHost("host.docker.internal", "host-gateway")`).

---

## Files to Modify

| File | Cambio |
|------|--------|
| `Tests/Asterisk.Sdk.TestInfrastructure/Containers/AsteriskContainer.cs` | Quitar port binding 4573, quitar `AgiPort`, agregar `WithExtraHost` |
| `Tests/Asterisk.Sdk.IntegrationTests/Agi/FastAgiIntegrationTests.cs` | `_fixture.Asterisk.AgiPort` → `4573` |
| `docker/functional/asterisk-config/extensions.conf` | Agregar extensión `200` con `AGI(agi://host.docker.internal:4573/test-script)` |

**NO tocar:** ningún archivo bajo `src/` — el SDK es correcto.

---

## Task 1: Fix AsteriskContainer + FastAgiIntegrationTests

**Files:**
- Modify: `Tests/Asterisk.Sdk.TestInfrastructure/Containers/AsteriskContainer.cs`
- Modify: `Tests/Asterisk.Sdk.IntegrationTests/Agi/FastAgiIntegrationTests.cs`

> ⚠️ Estos dos archivos deben modificarse **en el mismo commit** porque eliminar `AgiPort` de `AsteriskContainer` rompe la compilación de `FastAgiIntegrationTests`.

- [ ] **Step 1: Confirmar fallos de base (pre-fix)**

```bash
cd /media/Data/Source/IPcom/Asterisk.Sdk
dotnet test Tests/Asterisk.Sdk.IntegrationTests/ --filter "FullyQualifiedName~FastAgiIntegrationTests" 2>&1 | tail -6
```

Esperado: `Failed: 4, Passed: 0` con `SocketException: Address already in use`.

- [ ] **Step 2: Modificar `AsteriskContainer.cs`**

Ruta completa: `Tests/Asterisk.Sdk.TestInfrastructure/Containers/AsteriskContainer.cs`

Estado actual (líneas 17-36):
```csharp
public string Host => _container.Hostname;
public int AmiPort => _container.GetMappedPublicPort(5038);
public int AriPort => _container.GetMappedPublicPort(8088);
public int AgiPort => _container.GetMappedPublicPort(4573);  // ← ELIMINAR
public string ContainerName => _container.Name;

public AsteriskContainer(INetwork network, IImage image)
{
    _container = new ContainerBuilder()
        .WithImage(image)
        .WithPortBinding(5038, true)
        .WithPortBinding(8088, true)
        .WithPortBinding(4573, true)           // ← ELIMINAR
        .WithBindMount(DockerPaths.AsteriskConfig, "/etc/asterisk", AccessMode.ReadOnly)
        .WithNetwork(network)
        .WithNetworkAliases("asterisk")
        .WithWaitStrategy(...)
        .Build();
}
```

Estado deseado:
```csharp
public string Host => _container.Hostname;
public int AmiPort => _container.GetMappedPublicPort(5038);
public int AriPort => _container.GetMappedPublicPort(8088);
public string ContainerName => _container.Name;

public AsteriskContainer(INetwork network, IImage image)
{
    _container = new ContainerBuilder()
        .WithImage(image)
        .WithPortBinding(5038, true)
        .WithPortBinding(8088, true)
        .WithExtraHost("host.docker.internal", "host-gateway")   // ← AGREGAR: container → host en Linux Docker
        .WithBindMount(DockerPaths.AsteriskConfig, "/etc/asterisk", AccessMode.ReadOnly)
        .WithNetwork(network)
        .WithNetworkAliases("asterisk")
        .WithWaitStrategy(...)
        .Build();
}
```

Cambios exactos:
1. Eliminar la línea `public int AgiPort => _container.GetMappedPublicPort(4573);`
2. Eliminar `.WithPortBinding(4573, true)` del ContainerBuilder
3. Agregar `.WithExtraHost("host.docker.internal", "host-gateway")` después de `.WithPortBinding(8088, true)`

- [ ] **Step 3: Modificar `FastAgiIntegrationTests.cs`**

Ruta completa: `Tests/Asterisk.Sdk.IntegrationTests/Agi/FastAgiIntegrationTests.cs`

Cambiar línea 28:
```csharp
// ANTES:
_agiServer = new FastAgiServer(_fixture.Asterisk.AgiPort, _strategy, NullLogger<FastAgiServer>.Instance);

// DESPUÉS:
_agiServer = new FastAgiServer(4573, _strategy, NullLogger<FastAgiServer>.Instance);
```

- [ ] **Step 4: Build — verificar 0 warnings**

```bash
dotnet build Asterisk.Sdk.slnx 2>&1 | tail -5
```

Esperado: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Commit**

```bash
git add Tests/Asterisk.Sdk.TestInfrastructure/Containers/AsteriskContainer.cs \
        Tests/Asterisk.Sdk.IntegrationTests/Agi/FastAgiIntegrationTests.cs
git commit -m "fix(test-infra): remove erroneous AGI port binding from AsteriskContainer

FastAGI is a reverse-callback protocol — Asterisk dials OUT to the host,
the container does not listen on 4573. WithPortBinding(4573, true) was
causing docker-proxy to own that host port before the test could bind it,
resulting in SocketException in FastAgiIntegrationTests.InitializeAsync.

Remove the port binding and the AgiPort property (wrong semantics).
Add WithExtraHost(\"host.docker.internal\", \"host-gateway\") so the
Asterisk container can reach the test's FastAgiServer at port 4573.
Update FastAgiIntegrationTests to bind the standard port 4573 directly."
```

---

## Task 2: Add extension 200 AGI dialplan

**Files:**
- Modify: `docker/functional/asterisk-config/extensions.conf`

> Este archivo es compartido entre tests de integración y funcionales (vía `DockerPaths.AsteriskConfig`). La extensión 200 solo se activa cuando algo la origina desde AMI — los tests funcionales existentes no usan la extensión 200, así que no hay riesgo de regresión.

- [ ] **Step 1: Agregar extensión 200 a `extensions.conf`**

Ruta completa: `docker/functional/asterisk-config/extensions.conf`

Estado actual del contexto `[default]`:
```ini
[default]
exten => _X.,1,NoOp(Default: ${EXTEN})
 same => n,Answer()
 same => n,Wait(5)
 same => n,Hangup()
```

Estado deseado:
```ini
[default]
; FastAGI integration test — extension 200 calls back to the host test server
exten => 200,1,NoOp(FastAGI Test)
 same => n,AGI(agi://host.docker.internal:4573/test-script)
 same => n,Hangup()

exten => _X.,1,NoOp(Default: ${EXTEN})
 same => n,Answer()
 same => n,Wait(5)
 same => n,Hangup()
```

> **Por qué antes del catch-all:** Asterisk evalúa extensiones exactas (como `200`) antes que patrones (`_X.`). Pero poner la extensión explícita primero es más claro.

> **Por qué `test-script`:** Ambos tests AGI de Asterisk (`AgiServer_ShouldAcceptConnection_WhenAsteriskCallsAgi` y `AgiScript_ShouldExecuteGetVariable`) registran su handler bajo el nombre `"test-script"` en `SimpleMappingStrategy`. El path del URI AGI (`/test-script`) es lo que FastAGI usa para hacer lookup.

- [ ] **Step 2: Verificar que el archivo tiene sintaxis válida** (visual — no hay herramienta de lint para dialplan)

Verificar que el archivo resultante tiene exactamente:
- Sección `[general]` al inicio
- Sección `[globals]`
- Sección `[default]` con la nueva extensión 200 antes de `_X.`
- Secciones `[stasis-test]`, `[queue-test]`, `echo` intactas

- [ ] **Step 3: Commit**

```bash
git add docker/functional/asterisk-config/extensions.conf
git commit -m "fix(test-infra): add extension 200 AGI dialplan for integration tests

Extension 200 in the [default] context calls back to the test's FastAGI
server at agi://host.docker.internal:4573/test-script. This is required
for AgiServer_ShouldAcceptConnection_WhenAsteriskCallsAgi and
AgiScript_ShouldExecuteGetVariable tests to exercise the full protocol."
```

---

## Verification

- [ ] **Build completo — 0 warnings:**

```bash
dotnet build Asterisk.Sdk.slnx 2>&1 | tail -5
```

Esperado: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **AGI unit tests — sin regresiones:**

```bash
dotnet test Tests/Asterisk.Sdk.Agi.Tests/ --no-build 2>&1 | tail -4
```

Esperado: `Passed: 184, Failed: 0`

- [ ] **AGI integration tests — los 4 deben pasar:**

```bash
dotnet test Tests/Asterisk.Sdk.IntegrationTests/ --filter "FullyQualifiedName~FastAgiIntegrationTests" 2>&1 | tail -8
```

Esperado: `Passed: 4, Failed: 0`

> ⚠️ Los tests 3 y 4 (`ShouldBeRunning_AfterStart`, `ShouldStop_Cleanly`) no necesitan el dialplan y deberían pasar al terminar Task 1. Los tests 1 y 2 (`ShouldAcceptConnection`, `ShouldExecuteGetVariable`) necesitan también Task 2 para pasar.

- [ ] **Integration suite completa — sin regresiones:**

```bash
dotnet test Tests/Asterisk.Sdk.IntegrationTests/ --no-build 2>&1 | tail -6
```

Esperado: al menos `Passed: 35+4 = 39` (los 35 que ya pasaban + los 4 AGI recuperados).

---

## Branch & commit plan

- Branch: `fix/agi-integration-tests`
- Commits:
  1. `fix(test-infra): remove erroneous AGI port binding from AsteriskContainer` (Task 1)
  2. `fix(test-infra): add extension 200 AGI dialplan for integration tests` (Task 2)
- PR hacia `main`

## Out of scope

- Fix del SIPp container hang (follow-up separado)
- `BoundPort` API en `FastAgiServer` — descartado por YAGNI (no hay consumidor en producción)
- Cambios en `src/` — el SDK es correcto
