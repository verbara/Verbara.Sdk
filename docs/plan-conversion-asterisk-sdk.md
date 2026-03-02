# Asterisk.Sdk -- Plan de Conversion a SDK

## Contexto

Convertir `Asterisk.NetAot` (Alpha SDK, 59/100) en `Asterisk.Sdk` (Beta SDK, publicable en NuGet). La libreria tiene 12 fases de migracion completadas, 164+ unit tests, y AOT verificado. Se identificaron 7 gaps criticos vs AWS/Azure SDK que deben cerrarse junto con el rename.

**Nombre elegido:** `Asterisk.Sdk`
**Scope:** Solo Asterisk (no multi-PBX)
**Version target:** `0.1.0-beta.1`

---

## Inventario de Cambios

| Categoria | Cantidad |
|-----------|----------|
| Proyectos fuente (.csproj) | 9 a renombrar |
| Proyectos test | 8 a renombrar |
| Proyectos ejemplo | 5 a implementar |
| Archivos .cs con namespace | ~530 |
| AGI command stubs | 53 a implementar |
| ARI resources | 5 nuevas a crear |
| Excepciones nuevas | ~8 clases |

---

## Sprint 0: Preparacion (0.5 dia)

- [x] Crear branch `feature/rename-asterisk-sdk`
- [x] Verificar baseline: `dotnet build && dotnet test` → 176 tests passing
- [x] Commit: `chore: baseline verification before SDK rename`

## Sprint 1: Rename Filesystem (1 dia)

**Renombrar directorios, .csproj, solution file con `git mv`:**

| Actual | Nuevo |
|--------|-------|
| `Asterisk.NetAot.slnx` | `Asterisk.Sdk.slnx` |
| `src/Asterisk.NetAot.Abstractions/` | **MERGE** → `src/Asterisk.Sdk/` |
| `src/Asterisk.NetAot/` | `src/Asterisk.Sdk/` |
| `src/Asterisk.NetAot.Ami/` | `src/Asterisk.Sdk.Ami/` |
| `src/Asterisk.NetAot.Ami.SourceGenerators/` | `src/Asterisk.Sdk.Ami.SourceGenerators/` |
| `src/Asterisk.NetAot.Agi/` | `src/Asterisk.Sdk.Agi/` |
| `src/Asterisk.NetAot.Ari/` | `src/Asterisk.Sdk.Ari/` |
| `src/Asterisk.NetAot.Live/` | `src/Asterisk.Sdk.Live/` |
| `src/Asterisk.NetAot.Pbx/` | `src/Asterisk.Sdk.Activities/` |
| `src/Asterisk.NetAot.Config/` | `src/Asterisk.Sdk.Config/` |
| Tests: mismo patron `NetAot` → `Sdk`, `Pbx` → `Activities` |

- [x] Merge Abstractions (9 archivos) en `src/Asterisk.Sdk/` preservando subdirs (`Attributes/`, `Enums/`)
- [x] Actualizar todos los `<ProjectReference>` paths en .csproj
- [x] Actualizar `Directory.Build.props`: Authors, Company, Product, RepositoryUrl, PackageVersion → `0.1.0-beta.1`, PackageTags += `sdk`
- [ ] Commit: `refactor: rename project directories from Asterisk.NetAot to Asterisk.Sdk`

## Sprint 2: Rename Namespaces (1 dia)

**Bulk replace en todos los .cs (orden importa — mas especifico primero):**

1. `Asterisk.NetAot.Abstractions.Attributes` → `Asterisk.Sdk.Attributes`
2. `Asterisk.NetAot.Abstractions.Enums` → `Asterisk.Sdk.Enums`
3. `Asterisk.NetAot.Abstractions` → `Asterisk.Sdk`
4. `Asterisk.NetAot.Ami.SourceGenerators` → `Asterisk.Sdk.Ami.SourceGenerators`
5. `Asterisk.NetAot.Ami` → `Asterisk.Sdk.Ami`
6. `Asterisk.NetAot.Agi` → `Asterisk.Sdk.Agi`
7. `Asterisk.NetAot.Ari` → `Asterisk.Sdk.Ari`
8. `Asterisk.NetAot.Live` → `Asterisk.Sdk.Live`
9. `Asterisk.NetAot.Pbx` → `Asterisk.Sdk.Activities`
10. `Asterisk.NetAot.Config` → `Asterisk.Sdk.Config`
11. `Asterisk.NetAot` → `Asterisk.Sdk` (catch-all)

**CRITICO — Source generators tienen FQN strings hardcoded:**
- `ActionSerializerGenerator.cs`: actualizar `AsteriskMappingFqn`, `ManagerActionFqn`, namespace generado
- `EventDeserializerGenerator.cs`: idem con `ManagerEventFqn`
- `EventRegistryGenerator.cs`: idem
- `ResponseDeserializerGenerator.cs`: idem con `ManagerResponseFqn`

- [x] Bulk replace de namespaces en 522 .cs files
- [x] Source generators FQN strings actualizados
- [ ] Verificar: `dotnet build && dotnet test` → todos los tests pasan
- [ ] Commit: `refactor: update all namespaces from Asterisk.NetAot to Asterisk.Sdk`

## Sprint 3: Naming Fixes — GAP-07 (0.5 dia)

- `AddAsteriskNetAot()` → `AddAsterisk()`, `AsteriskNetAotOptions` → `AsteriskOptions`
- `AddAsteriskNetAotMultiServer()` → `AddAsteriskMultiServer()`
- `StartTracking()` → `StartAsync(CancellationToken)` en AsteriskServer
- Crear `IAsteriskServer` interface en `src/Asterisk.Sdk/`, registrar en DI
- `OriginateAction.Async` → `OriginateAction.IsAsync` + `[AsteriskMapping("Async")]`
- `AriBridge.Channels` → `IReadOnlyList<string>`
- `AriChannel.State` → enum `AriChannelState`
- Actualizar tests y ejemplos
- Commit: `refactor: fix naming inconsistencies — AddAsterisk, StartAsync, IAsteriskServer (GAP-07)`

## Sprint 4: Exception Hierarchy — GAP-02 (0.5 dia)

**Crear en `src/Asterisk.Sdk/Exceptions/`:**
- `AsteriskException` (base, namespace `Asterisk.Sdk`)

**Crear en `src/Asterisk.Sdk.Ami/`:**
- `AmiAuthenticationException`, `AmiConnectionException`, `AmiProtocolException`, `AmiTimeoutException`, `AmiNotConnectedException`

**Crear en `src/Asterisk.Sdk.Ari/`:**
- `AriException(message, statusCode)`, `AriNotFoundException`, `AriConflictException`

**Rebase excepciones existentes:** `AgiException`, `LiveException`, `PbxException` → heredan `AsteriskException`

**Reemplazar 5 `InvalidOperationException` en `AmiConnection.cs`** con excepciones tipadas

- Commit: `feat: typed exception hierarchy for AMI and ARI (GAP-02)`

## Sprint 5: AGI Commands — GAP-04 (1 dia)

- Implementar `BuildCommand()` en las 53 clases AGI con formato de protocolo correcto
- Crear `AgiHostedService : IHostedService` en `src/Asterisk.Sdk.Agi/Hosting/`
- Agregar `Microsoft.Extensions.Hosting.Abstractions` a deps
- Tests parametrizados por cada comando
- Commit: `feat: implement all 53 AGI BuildCommand() methods and AgiHostedService (GAP-04)`

## Sprint 6: Options Validation — GAP-05 (0.5 dia)

- DataAnnotations en `AmiConnectionOptions`: `[Required]` en Hostname/Username/Password, `[Range]` en Port
- DataAnnotations en `AriClientOptions`: `[Required]` en BaseUrl/Username/Password/Application, `[Url]` en BaseUrl
- Registrar con `ValidateDataAnnotations()` + `ValidateOnStart()` en DI
- Agregar `Microsoft.Extensions.Options.DataAnnotations` a deps
- Tests de validacion
- Commit: `feat: DataAnnotations validation on options classes (GAP-05)`

## Sprint 7: ARI Completitud + Resilience — GAP-03 + GAP-06 (3 dias)

**ARI:**
- Migrar a `IHttpClientFactory` (agregar `Microsoft.Extensions.Http`)
- Fix WebSocket fragmentation (multi-segment receive con `ArrayBufferWriter`)
- Implementar WebSocket reconnect (honrar `AutoReconnect`, exponential backoff)
- URL parameter encoding (`Uri.EscapeDataString`)
- ARI error response deserialization (`AriErrorResponse` model + `AriResponseHelper`)
- `ListAsync()` en Channels y Bridges
- 5 nuevas resources: Playbacks, Recordings, Endpoints, Applications, Sounds
- Typed event dispatch en `ParseEvent()` (wiring 12+ subclases de AriEvent en switch + AriJsonContext)

**Resilience AMI:**
- Exponential backoff configurable: `ReconnectInitialDelay`, `ReconnectMaxDelay`, `ReconnectMultiplier`
- Fix `async void OnReconnected` → `private void` que dispara `Task.Run(OnReconnectedAsync)`
- Enforce `DefaultEventTimeout` en `SendEventGeneratingActionAsync`

- Commit: `feat: complete ARI client (7 resources, WebSocket reconnect, IHttpClientFactory) + AMI resilience (GAP-03, GAP-06)`

## Sprint 8: Documentacion y Ejemplos — GAP-01 (1.5 dias)

- Quitar `<NoWarn>CS1591</NoWarn>` de src/ (mantener en Tests/Examples)
- XML docs en: todas las interfaces/clases/enums de `Asterisk.Sdk/`, options classes, ServiceCollectionExtensions, excepciones, ARI resources
- Crear `README.md` con: badges, features, installation, quickstart por layer, tabla de paquetes, requirements, license
- Implementar 5 ejemplos con lifecycle completo (connect → use → dispose → error handling)
- Commit: `docs: README, XML documentation, working examples (GAP-01)`

## Sprint 9: Verificacion Final (1 dia)

- `dotnet test Asterisk.Sdk.slnx` → todos los tests pasan
- `dotnet publish Examples/BasicAmiExample/ -c Release` → 0 trim warnings, binary ≤ 2 MB
- `dotnet run --project Tests/Asterisk.Sdk.Benchmarks/ -c Release` → sin regresiones
- Actualizar `CLAUDE.md` y `docs/plan-migracion-*.md`
- Limpiar `bin/obj` de nombres viejos
- Commit: `chore: final verification for Asterisk.Sdk beta`

## Sprint 10: NuGet Packaging (0.5 dia)

- Verificar metadata: `PackageProjectUrl`, `PackageIcon`, `PackageReadmeFile`
- `dotnet pack Asterisk.Sdk.slnx -c Release -o ./artifacts`
- Verificar 8 .nupkg generados con README, license, XML docs
- Commit: `chore: configure NuGet packaging for Asterisk.Sdk 0.1.0-beta.1`

---

## Dependency Tree Final

```
Asterisk.Sdk                    (core: interfaces, enums, attributes, base types, DI)
     ^
Asterisk.Sdk.Ami                (+Ami.SourceGenerators como analyzer)
     ^
Asterisk.Sdk.Agi               (-> Sdk + Ami)
Asterisk.Sdk.Live              (-> Sdk + Ami)
     ^
Asterisk.Sdk.Activities         (-> Sdk + Ami + Agi + Live)
Asterisk.Sdk.Ari               (-> Sdk only)
Asterisk.Sdk.Config             (-> Sdk only)
```

## Verificacion

1. `dotnet build Asterisk.Sdk.slnx` → 0 warnings, 0 errors
2. `dotnet test Asterisk.Sdk.slnx` → 200+ tests, 0 failures
3. `dotnet publish Examples/BasicAmiExample/ -c Release` → 0 trim warnings
4. `dotnet pack -c Release -o artifacts` → 8 .nupkg correctos

## Esfuerzo Total: ~10 dias

## Progreso

| Sprint | Estado | Notas |
|--------|--------|-------|
| Sprint 0 | ✅ Completado | Branch creado, baseline verificado (176 tests) |
| Sprint 1 | ✅ Completado | Filesystem renombrado, csproj actualizados |
| Sprint 2 | ✅ Completado | Namespaces reemplazados, build + tests passing (e78c0c7) |
| Sprint 3 | ✅ Completado | AddAsterisk, StartAsync, IAsteriskServer (9278703) |
| Sprint 4 | ✅ Completado | Typed exception hierarchy AMI/ARI (b7d2dac) |
| Sprint 5 | ✅ Completado | 53 AGI BuildCommand() + AgiHostedService (8c30532) |
| Sprint 6 | ✅ Completado | DataAnnotations validation on options (31c5d05) |
| Sprint 7 | ✅ Completado | ARI client completo + AMI resilience (5d73109) |
| Sprint 8 | ✅ Completado | README, XML docs, ejemplos funcionales (8a2755d, 224771a) |
| Sprint 9 | 🔄 En progreso | |
| Sprint 10 | ⬜ Pendiente | |
