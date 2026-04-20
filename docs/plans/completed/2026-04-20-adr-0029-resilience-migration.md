# ADR-0029 Execution — `Asterisk.Sdk.Resilience` MIT (Fase 1 + 2 + 3)

## Context

**Por qué este cambio:** ADR-0029 ([docs/decisions/0029-resilience-primitives-mit.md](/media/Data/Source/IPcom/Asterisk.Sdk/docs/decisions/0029-resilience-primitives-mit.md)) declara que `Asterisk.Sdk.Pro.Resilience` es infraestructura genérica sin domain commercial y debe migrar al SDK (MIT) — alineado con stewardship pledge (ADR-0027) y evidencia industry (Polly/Resilience4j/Hystrix todos OSS). Hoy los primitives están "trapped in commercial": MIT users no pueden usar circuit breaker sin comprar Pro, y SDK mantiene 3 retry loops open-coded (AMI reconnect, ARI reconnect, Webhook delivery).

**Alcance aprobado:** Fase 1 (crear paquete SDK) + Fase 2 (Pro elimina duplicación + 5 consumers migran namespace) + Fase 3 (SDK adopta primitive internamente en AMI/ARI/Webhooks). Target: **SDK v1.14.0** + **Pro v1.9.0-pro** coordinated release.

**Resultado esperado:** MIT users obtienen resilience primitives gratis; Pro elimina código duplicado y se concentra en engine-specific policies; SDK hot paths (AMI/ARI reconnect) ya no tienen retry open-coded; stewardship pledge (ADR-0027) tiene su primer ejemplo concreto de Commercial→MIT movement.

---

## Decisión clave sobre mecánica de migración

**ADR-0029 menciona `[assembly: TypeForwardedTo]` — verificado no viable.** TypeForwardedTo requiere mismo FQN. Como el namespace cambia (`Asterisk.Sdk.Pro.Resilience.*` → `Asterisk.Sdk.Resilience.*`), type-forwarding falla en runtime (`TypeLoadException`). Validado por Plan agent.

**Mecánica real (clean break):**
- Pro.Resilience package **eliminado** del repo en v1.9.0-pro (deprecated on nuget.org).
- 5 Pro consumers migran `using` statements (one-line rename cada uno).
- Usuarios externos (paquete solo tiene 4 meses, sin adopción conocida): rename `using` + swap `<PackageReference>`. Documentado en migration guide.
- ADR-0029 se actualiza: reemplaza lenguaje "type-forward" con "namespace rename + migration guide + nuget.org deprecation".

---

## Phase 1 — SDK: create `Asterisk.Sdk.Resilience` package

**Target files creados:**

1. **`src/Asterisk.Sdk.Resilience/Asterisk.Sdk.Resilience.csproj`** — clon del patrón [Asterisk.Sdk.Push.csproj](/media/Data/Source/IPcom/Asterisk.Sdk/src/Asterisk.Sdk.Push/Asterisk.Sdk.Push.csproj) con `EnablePackageValidation=false` (baseline nuevo). Dependencies: `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions`. `InternalsVisibleTo Asterisk.Sdk.Resilience.Tests`.

2. **`src/Asterisk.Sdk.Resilience/CircuitBreakerState.cs`** — copia verbatim de [Pro.Resilience/CircuitBreakerState.cs](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Resilience/CircuitBreakerState.cs), cambia `namespace Asterisk.Sdk.Pro.Resilience;` → `namespace Asterisk.Sdk.Resilience;`.

3. **`src/Asterisk.Sdk.Resilience/ResiliencePolicy.cs`** — idem con [Pro.Resilience/ResiliencePolicy.cs](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Resilience/ResiliencePolicy.cs).

4. **`src/Asterisk.Sdk.Resilience/ResiliencePolicyBuilder.cs`** — idem con [Pro.Resilience/ResiliencePolicyBuilder.cs](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Resilience/ResiliencePolicyBuilder.cs).

5. **`src/Asterisk.Sdk.Resilience/CircuitBreakerOpenException.cs`** — idem con [Pro.Resilience/CircuitBreakerOpenException.cs](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Resilience/CircuitBreakerOpenException.cs).

6. **`src/Asterisk.Sdk.Resilience/Diagnostics/ResilienceMetrics.cs`** — **meter renamed** `Asterisk.Sdk.Pro.Resilience` → `Asterisk.Sdk.Resilience`. Namespace `Asterisk.Sdk.Resilience.Diagnostics`.

7. **`src/Asterisk.Sdk.Resilience/DependencyInjection/ResilienceServiceCollectionExtensions.cs`** — renombrar `AddProResilience()` → `AddAsteriskResilience()` (alineado con SDK convention `AddAsterisk*`). Namespace `Asterisk.Sdk.Resilience.DependencyInjection`.

8. **`src/Asterisk.Sdk.Resilience/README.md`** — patrón de [Push README](/media/Data/Source/IPcom/Asterisk.Sdk/src/Asterisk.Sdk.Push/README.md): "What it does" + Install + Quick start + Observability section (meter `Asterisk.Sdk.Resilience`).

9. **`src/Asterisk.Sdk.Resilience/PublicAPI.Shipped.txt`** — `#nullable enable` + vacío.

10. **`src/Asterisk.Sdk.Resilience/PublicAPI.Unshipped.txt`** — populated con todos los símbolos públicos (generados por el analyzer durante primer build).

**Tests migrados (38 tests):**

11. **`Tests/Asterisk.Sdk.Resilience.Tests/Asterisk.Sdk.Resilience.Tests.csproj`** — clon de [Push.Tests.csproj](/media/Data/Source/IPcom/Asterisk.Sdk/Tests/Asterisk.Sdk.Push.Tests/Asterisk.Sdk.Push.Tests.csproj).

12. Migrar 6 archivos de test desde [tests/Asterisk.Sdk.Pro.Resilience.Tests/](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/tests/Asterisk.Sdk.Pro.Resilience.Tests/) — cambiar namespace `Asterisk.Sdk.Pro.Resilience.Tests` → `Asterisk.Sdk.Resilience.Tests`. 38 tests cubren CircuitBreakerState (10) + ResiliencePolicyBuilder (7) + ResiliencePolicy (14) + ResilienceMetrics (3) + DI (4). Renombrar `AddProResilience()` a `AddAsteriskResilience()` en los tests de DI.

**Solution + packages registry:**

13. **[Asterisk.Sdk.slnx](/media/Data/Source/IPcom/Asterisk.Sdk/Asterisk.Sdk.slnx)** — agregar 2 entries (`/src/` + `/Tests/`).

14. **[Directory.Packages.props](/media/Data/Source/IPcom/Asterisk.Sdk/Directory.Packages.props)** — agregar `<PackageVersion Include="Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions">` si no existe (copiar versión actualmente usada por otro package).

**Validación Fase 1:**
```sh
cd /media/Data/Source/IPcom/Asterisk.Sdk
dotnet build Asterisk.Sdk.slnx --nologo /warnaserror   # 0 warnings
dotnet test Tests/Asterisk.Sdk.Resilience.Tests/       # 38 passing
dotnet pack src/Asterisk.Sdk.Resilience/Asterisk.Sdk.Resilience.csproj \
  -c Release -o /media/Data/Source/IPcom/local-nuget-feed/
```

---

## Phase 2 — Pro: eliminar Pro.Resilience, migrar 5 consumers

**Deleted files:**

15. Eliminar directorio completo: [src/Asterisk.Sdk.Pro.Resilience/](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Resilience/).
16. Eliminar directorio completo: [tests/Asterisk.Sdk.Pro.Resilience.Tests/](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/tests/Asterisk.Sdk.Pro.Resilience.Tests/).

**Updated files (slnx + central package management):**

17. **[Asterisk.Sdk.Pro.slnx](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/Asterisk.Sdk.Pro.slnx)** — eliminar 2 entries (`src/Asterisk.Sdk.Pro.Resilience/*` + `tests/Asterisk.Sdk.Pro.Resilience.Tests/*`).

18. **[Directory.Packages.props](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/Directory.Packages.props)** — agregar `<PackageVersion Include="Asterisk.Sdk.Resilience" Version="1.14.0" />`.

**5 consumers: rename `using` + swap reference (`<ProjectReference>` → `<PackageReference>`):**

19. **Pro.EventStore** — [EventStoreServiceCollectionExtensions.cs](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.EventStore/EventStoreServiceCollectionExtensions.cs): `using Asterisk.Sdk.Pro.Resilience;` → `using Asterisk.Sdk.Resilience;`. csproj: `<ProjectReference Include="..\Asterisk.Sdk.Pro.Resilience\*" />` → `<PackageReference Include="Asterisk.Sdk.Resilience" />`. También `using Asterisk.Sdk.Pro.Resilience.DependencyInjection;` → `using Asterisk.Sdk.Resilience.DependencyInjection;`; `AddProResilience()` → `AddAsteriskResilience()`.

20. **Pro.Analytics** — misma transformación en [AnalyticsServiceCollectionExtensions.cs](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Analytics/AnalyticsServiceCollectionExtensions.cs).

21. **Pro.AgentAssist** — misma transformación en [AgentAssistServiceCollectionExtensions.cs](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.AgentAssist/AgentAssistServiceCollectionExtensions.cs), [Engine/AgentAssistEngine.cs](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.AgentAssist/Engine/AgentAssistEngine.cs), [Engine/AgentAssistSession.cs](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.AgentAssist/Engine/AgentAssistSession.cs).

22. **Pro.CallAnalytics** — misma transformación en [CallAnalyticsServiceCollectionExtensions.cs](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.CallAnalytics/CallAnalyticsServiceCollectionExtensions.cs), [Engine/CallAnalyticsEngine.cs](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.CallAnalytics/Engine/CallAnalyticsEngine.cs).

23. **Pro.Dialer** — [Execution/DefaultOriginateExecutor.cs](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.Dialer/Execution/DefaultOriginateExecutor.cs) usa `CircuitBreakerState` directamente. Misma transformación.

**Pro.OpenTelemetry wrapper:**

24. **[Pro.OpenTelemetry/ProTelemetryExtensions.cs](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.OpenTelemetry/)** — remover `.AddMeter("Asterisk.Sdk.Pro.Resilience")`. El meter `Asterisk.Sdk.Resilience` es enrollado ahora por SDK's OpenTelemetry wrapper (Fase 3 lo confirma).

**Otros tests Pro que importan Resilience types:**

25. Grep ancho: buscar `Asterisk.Sdk.Pro.Resilience` en `/media/Data/Source/IPcom/Asterisk.Sdk.Pro/tests/` para capturar fixture setups / helpers que puedan referenciar el namespace antiguo. Update según se encuentre.

**Version bump + CHANGELOG:**

26. **[Directory.Build.props](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/Directory.Build.props)** — `PackageVersion` `1.8.1-pro` → `1.9.0-pro`.

27. **[CHANGELOG.md](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/CHANGELOG.md)** — nueva sección `[1.9.0-pro] - 2026-04-20` explicando: package `Asterisk.Sdk.Pro.Resilience` removed; namespace migration guide; meter name change; breaking source rename.

28. **[docs/packages.md](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/docs/packages.md)** — eliminar fila `Pro.Resilience`. 25 packages → 24.

29. **[docs/roadmap.md](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/docs/roadmap.md)** — entry en "Shipped" para 1.9.0-pro.

30. **[docs/architecture.md](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/docs/architecture.md)** — reemplazar referencias a `Pro.Resilience` con `Asterisk.Sdk.Resilience` en ASCII diagrams + pipeline descriptions.

31. **[docs/di-registration.md](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/docs/di-registration.md)** — reemplazar `services.AddProResilience(...)` con `services.AddAsteriskResilience(...)`.

32. **[CLAUDE.md](/media/Data/Source/IPcom/Asterisk.Sdk.Pro/CLAUDE.md)** — version bump + meter count (16 meters reducido si se contabilizaba Resilience, verificar).

**Nuevo ADR Pro (documenta la sunset):**

33. **`docs/decisions/0006-pro-resilience-sunset.md`** — ADR detallando: razón (ADR-0029 SDK), mecánica (clean break, no type-forward), impacto en consumers (5 internal + external rename).

**Validación Fase 2:**
```sh
cd /media/Data/Source/IPcom/Asterisk.Sdk.Pro
dotnet nuget locals all --clear                              # clear cache
rm -rf ~/.nuget/packages/asterisk.sdk.resilience/            # ensure fresh pull
dotnet restore Asterisk.Sdk.Pro.slnx
dotnet build Asterisk.Sdk.Pro.slnx --nologo /warnaserror     # 0 warnings
dotnet test Asterisk.Sdk.Pro.slnx --filter "FullyQualifiedName!~Postgres"  # 1,287 unit tests green
```

---

## Decisiones tomadas durante ejecución (registro incremental)

### Decisión #1 — Migration mechanism: clean break (no type-forward) — Fase 2

**Contexto:** ADR-0029 original proponía `[assembly: TypeForwardedTo]` para backward compat.

**Decisión:** Clean break. Delete `Asterisk.Sdk.Pro.Resilience` package completo; consumers renombran namespace.

**Razón:** TypeForwardedTo resuelve tipos por FQN idéntico. Como el namespace cambia (`Asterisk.Sdk.Pro.Resilience.*` → `Asterisk.Sdk.Resilience.*`), no hay forwarding viable. Validado por Plan agent + audit. Costo consumer externo = rename `using` + swap `<PackageReference>`. Paquete solo 4 meses sin adopción externa conocida.

**Artefacto:** Pro ADR-0006 (`docs/decisions/0006-pro-resilience-sunset.md`).

### Decisión #2 — DI method rename: `AddProResilience` → `AddAsteriskResilience` — Fase 1

**Razón:** SDK convention `AddAsterisk*`. Método vive en SDK ahora, no en Pro.

### Decisión #3 — Meter name rename: `Asterisk.Sdk.Pro.Resilience` → `Asterisk.Sdk.Resilience` — Fase 1

**Razón:** Coincidir con package namespace. Dashboards migran en una acción. Dual-emit NO implementado por scope mínimo.

### Decisión #4 — DI file/class rename `Pro` prefix removed — Phase 1 audit follow-up

`ResilienceServiceCollectionExtensions.cs` + class. Consistencia con rename del método.

### Decisión #5 — `AsteriskTelemetry.MeterNames` catalog includes new meter — Phase 1 audit follow-up

`WithAllSources()` itera este catalog. Sin registro, README claim era falsa.

### Decisión #6 — Drop `HealthChecks.Abstractions` dep — Phase 1

Pro.Resilience declaraba la dep pero grep del source = zero uso. Dep removed del nuevo csproj.

### Decisión #7 — Drop Resilience meter from Pro.OpenTelemetry wrapper — Fase 2

Pro catalog: 16 → 15. SDK `.WithAllSources()` ahora enrola el meter. Zero regression.

### Decisión #8 — Pro version bump 1.8.1-pro → 1.9.0-pro (minor) — Fase 2

**Razón:** Rename es technically source-breaking pero sin adopción externa conocida. Major bump sería sobre-reacción para paquete con 4 meses de vida. Documentado en ADR-0006.

### Decisión #9 — Phase 3 hybrid: `BackoffSchedule` helper para TODOS los 3 call-sites (no full `ResiliencePolicy`) — Fase 3

**Contexto:** Plan original decía "AmiConnection + AriLoggingHandler + WebhookDeliveryService usan Resilience primitive". Auditar encontró abstraction mismatch:

- `ResiliencePolicy.ExecuteAsync` = retry bounded de op fallible.
- AMI/ARI `ReconnectLoopAsync` = state loop continuo con backoff schedule y infinite retry hasta state flag cambia. **Concepto distinto.**

**Opciones evaluadas (12 total):** A (solo Webhook), B (full en 3), C (defer), D (extend builder WithMaxDelay/WithMultiplier), E (solo extraer helper), F (benchmark-first), G (adapter FromReconnectOptions), H (solo CB en AMI/ARI), I (green-field only), J (refactor options breaking), K (full + dual metrics), **L (hybrid — helper AMI/ARI + full Policy Webhook)**.

**Decisión:** Opción L refinada — `BackoffSchedule` helper en los 3 call-sites; full `ResiliencePolicy.ExecuteAsync` NO adoptado en Webhook.

**Razón del refinamiento Webhook:** Full `ResiliencePolicy` en Webhook introducía 3 behavior changes observables: (a) ±20% jitter hardcoded donde no había, (b) `MaxDelay` cap no respetado, (c) `MaxRetryAttemptsCap=10` clamp. Respetar "0 breaking en minors" fue prioridad. Circuit-breaker-per-URL es valor nuevo pero requiere diseño explícito (qué = "failure"? 5xx? exception? 4xx rate-limit?). Difiere a release dedicado como feature.

**Scope final Phase 3:**
- SDK gana `BackoffSchedule.Compute()` + `ComputeWithJitter()`.
- AMI/ARI/Webhook reconnect/retry delegan backoff math al helper.
- Behavior observable = preservado (multiplier configurable, cap respetado, no jitter added).
- Tests existentes siguen verde (633 AMI + 423 ARI + 13 Webhooks + 50 Resilience) = zero-regression.

**Qué NO se hizo (deferred):**
- Full `ResiliencePolicy.ExecuteAsync` wrap en Webhook — deferido a release con circuit-breaker-per-URL feature explícito.
- Circuit breaker para AMI/ARI — no es fit arquitectónico (infinite-retry loop no se beneficia).

### Decisión #10 — Tests migrated verbatim with 2 Meziantou fixes — Phase 1

SDK tiene Meziantou analyzer (Pro no). Fixes mecánicos:
- `object _gate = new()` → `Lock _gate = new()` (C# 13, MA0158).
- `ArgumentException` sin paramName → `InvalidOperationException` (MA0015). Contract testeado idéntico.

---

## Phase 3 — SDK adopta primitive en 3 call-sites

**Refactoring interno (0 cambios en public API surface):**

34. **[src/Asterisk.Sdk.Ami/Connection/AmiConnection.cs](/media/Data/Source/IPcom/Asterisk.Sdk/src/Asterisk.Sdk.Ami/Connection/AmiConnection.cs)** — `ReconnectLoopAsync` (lines 545-569) reemplazar exponential backoff loop con `ResiliencePolicyBuilder.WithRetry(_options.MaxReconnectAttempts, _options.ReconnectInitialDelay).WithTimeProvider(_time).Build()`. Mantener meter `AmiMetrics.ReconnectionAttempts.Add(1)` (distinto del meter Resilience — sirven propósitos separados).

35. **[src/Asterisk.Sdk.Ari/Client/AriClient.cs](/media/Data/Source/IPcom/Asterisk.Sdk/src/Asterisk.Sdk.Ari/Client/AriClient.cs)** — `ReconnectLoopAsync` (lines 196-225) misma refactorización.

36. **[src/Asterisk.Sdk.Push.Webhooks/WebhookDeliveryService.cs](/media/Data/Source/IPcom/Asterisk.Sdk/src/Asterisk.Sdk.Push.Webhooks/WebhookDeliveryService.cs)** — `DeliverAsync` (lines 121-190) reemplazar retry loop con `ResiliencePolicy.ExecuteAsync(...)`. Circuit breaker opcional por URL subscription (nuevo feature deseable — documentar si se activa ahora o queda diferido).

37. Agregar `<ProjectReference Include="..\Asterisk.Sdk.Resilience\*" />` en 3 csproj afectados.

**Tests de adopción:**

38. Agregar ~10 tests cubriendo: retry exponential backoff respetado, cancellation propaga, metrics emit en reconexión. Tests existentes de reconexión permanecen verde (sin cambio de comportamiento observable).

**Validación Fase 3:**
```sh
cd /media/Data/Source/IPcom/Asterisk.Sdk
dotnet test Tests/Asterisk.Sdk.Ami.Tests/
dotnet test Tests/Asterisk.Sdk.Ari.Tests/
dotnet test Tests/Asterisk.Sdk.Push.Webhooks.Tests/
```

---

## Phase 4 — SDK v1.14.0 release + CHANGELOG + ADR update

39. **[CHANGELOG.md](/media/Data/Source/IPcom/Asterisk.Sdk/CHANGELOG.md)** — sección `[1.14.0] - 2026-04-20` documentando: new package `Asterisk.Sdk.Resilience` + AMI/ARI/Webhook internal adoption + migration guide link.

40. **[Directory.Build.props](/media/Data/Source/IPcom/Asterisk.Sdk/Directory.Build.props)** — `PackageVersion` `1.13.0` → `1.14.0`.

41. **[docs/decisions/0029-resilience-primitives-mit.md](/media/Data/Source/IPcom/Asterisk.Sdk/docs/decisions/0029-resilience-primitives-mit.md)** — actualizar:
    - Status: `Proposed` → `Accepted`.
    - Section Consequences: reemplazar "type-forwards backward compat" con "clean break namespace rename + migration guide".
    - Añadir sección "Migration guide" con before/after code.

42. **`docs/migrations/v1-to-v2.md`** (nuevo) — sección Resilience: `Asterisk.Sdk.Pro.Resilience` → `Asterisk.Sdk.Resilience`; `AddProResilience()` → `AddAsteriskResilience()`; meter rename.

---

## Verification end-to-end

**Cross-repo smoke test (después de Fase 4):**

```sh
# SDK 1.14.0 pack
cd /media/Data/Source/IPcom/Asterisk.Sdk
dotnet pack Asterisk.Sdk.slnx -c Release -o /media/Data/Source/IPcom/local-nuget-feed/

# Clear caches de Pro
rm -rf ~/.nuget/packages/asterisk.sdk*/
rm -rf ~/.nuget/packages/asterisk.sdk.pro*/

# Pro restore + build + test + pack
cd /media/Data/Source/IPcom/Asterisk.Sdk.Pro
dotnet restore Asterisk.Sdk.Pro.slnx
dotnet build Asterisk.Sdk.Pro.slnx --nologo /warnaserror
dotnet test Asterisk.Sdk.Pro.slnx --filter "FullyQualifiedName!~Postgres"  # 1,287 unit green
dotnet test tests/Asterisk.Sdk.Pro.IntegrationTests/                        # 149 IT (Docker)
dotnet pack Asterisk.Sdk.Pro.slnx -c Release -o /media/Data/Source/IPcom/local-nuget-feed/

# Platform smoke (opcional en este plan — Platform bump es separate release)
# cd /media/Data/Source/IPcom/Asterisk.Platform
# docker compose -f docker/docker-compose.full.yml up --build
```

**Criterios de éxito:**
- ✅ SDK v1.14.0 packs con 0 warnings (pack-check CI green).
- ✅ `Asterisk.Sdk.Resilience` package publicable: README + icon (via Directory.Build.props) + license + 38 tests green + public API stable.
- ✅ Pro v1.9.0-pro packs con 0 warnings + 1,287 unit tests green.
- ✅ AMI/ARI reconnect behavior unchanged observably (metrics + state transitions iguales).
- ✅ `Asterisk.Sdk.Pro.Resilience` ya no aparece en solution; listed como deprecated en nuget.org después de publish.

**Commits esperados (ordenados):**
1. SDK: `feat(resilience): add Asterisk.Sdk.Resilience MIT package — primitives migrated from Pro`
2. SDK: `refactor(ami,ari,webhooks): adopt ResiliencePolicy primitive (eliminate open-coded retry)`
3. SDK: `docs(changelog,adr-0029): v1.14.0 + ADR Accepted with corrected migration mechanics`
4. SDK pack + push tag `v1.14.0` — **confirmar con usuario antes**.
5. Pro: `refactor(resilience): migrate 5 consumers to Asterisk.Sdk.Resilience namespace`
6. Pro: `chore(resilience): remove Asterisk.Sdk.Pro.Resilience package (superseded by SDK)`
7. Pro: `docs(adr-0006,changelog): document Pro.Resilience sunset + v1.9.0-pro release`
8. Pro pack + push tag `v1.9.0-pro` — **confirmar con usuario antes**.

**Permissions requested:**
- `Bash`: `dotnet build|test|pack|restore`, `git status|add|commit|diff|log`, `rm -rf ~/.nuget/packages/asterisk.*` (local cache only).
- `Write` / `Edit` en archivos listados en Phases 1-4.
- `Bash` eventual `git push` **con confirmación explícita** por commit (política de usuario).
- NO se toca Platform en este plan — Platform bump a Pro 1.9.0-pro es release separado.
