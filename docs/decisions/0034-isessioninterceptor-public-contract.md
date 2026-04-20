# ADR-0034: `ISessionInterceptor` public contract — replace `InternalsVisibleTo Pro.Cluster` leak

- **Status:** Proposed
- **Date:** 2026-04-20
- **Deciders:** Harold Reina
- **Related:**
  - PSD v2: `Asterisk.Platform/docs/specs/2026-04-19-product-strategy-v2.md` §2.3, §9 (Mes 4)
  - ADR-0023 (PublicApi tracker adoption)

## Context

`src/Asterisk.Sdk.Sessions/Asterisk.Sdk.Sessions.csproj` línea 6 contiene:

```xml
<InternalsVisibleTo Include="Asterisk.Sdk.Pro.Cluster" />
```

Este leak fue agregado durante desarrollo del `Pro.Cluster` para permitirle acceder a internals de `CallSessionManager` (estado interno de sessions para cluster replication/failover). Problemas:

1. **Filtra metadata comercial:** consumers MIT ven el nombre del repo privado `Asterisk.Sdk.Pro.Cluster` al inspeccionar el paquete. Aunque es open-core declarado, la filtración en metadata pública es anti-pattern.
2. **Acopla SDK MIT a Pro:** cambios en internals de Sessions pueden romper Pro.Cluster silenciosamente. No hay contract explícito; cualquier internal es implícitamente "API" para Pro.Cluster.
3. **Bloquea modificación de internals:** refactor de `CallSessionManager` requiere coordinación cross-repo aunque nadie externo depende.
4. **No estándar:** Sessions no tiene este leak para otros Pro packages (Pro.EventStore, Pro.Dialer, etc.) que también interactúan con sessions. Es single-exception — smell.

Con el rebrand narrativa (ADR-0026) + stewardship pledge (ADR-0027) + identity limpia del SDK MIT, este leak debe eliminarse antes de v2.0 stable.

## Decision

**Introducir `ISessionInterceptor` como contract público en SDK v2.0 que reemplaza el `InternalsVisibleTo` leak.**

### Interface

```csharp
namespace Asterisk.Sdk.Sessions;

/// <summary>
/// Extension point for external systems (e.g., clustering, replication, custom analytics)
/// to participate in session lifecycle without accessing internals.
/// </summary>
public interface ISessionInterceptor
{
    /// <summary>
    /// Called after a session is created. Read-only observation.
    /// </summary>
    ValueTask OnSessionCreatedAsync(CallSession session, CancellationToken ct);

    /// <summary>
    /// Called after a session state transition. Read-only observation.
    /// Implementations MUST NOT mutate the session.
    /// </summary>
    ValueTask OnSessionTransitionedAsync(
        CallSession session,
        CallSessionState previousState,
        CallSessionEvent triggeringEvent,
        CancellationToken ct);

    /// <summary>
    /// Called before session completion. Last chance to enrich/annotate via
    /// the mutable SessionCompletionContext. Exceptions are logged, not propagated.
    /// </summary>
    ValueTask OnSessionCompletingAsync(
        SessionCompletionContext context,
        CancellationToken ct);

    /// <summary>
    /// Called after session is persisted/completed. Observation only.
    /// </summary>
    ValueTask OnSessionCompletedAsync(CallSession session, CancellationToken ct);
}

public sealed class SessionCompletionContext
{
    public required CallSession Session { get; init; }
    public Dictionary<string, string> Annotations { get; } = new(); // mutable enrichment
}
```

### DI registration

```csharp
// Multiple interceptors supported
services.AddSingleton<ISessionInterceptor, MyCustomInterceptor>();

// Pro.Cluster registers its own
services.AddSingleton<ISessionInterceptor, ClusterReplicationInterceptor>();
```

### `CallSessionManager` integration

`CallSessionManager` gets `IEnumerable<ISessionInterceptor>` injected. After each lifecycle point, iterates + calls interceptors (sync, sequential). Exceptions logged via `ILogger`, NOT propagated — interceptor failure must not break session processing.

### Pro.Cluster migration

Pro.Cluster current code que usa internals de Sessions se refactoriza:
- `ClusterReplicationLogic` consume `CallSession` public API + annotations via interceptor calls.
- Lo que requería internal access (ej: access a internal state fields) debe exposed como public read-only properties en `CallSession`, o pasar por `SessionCompletionContext.Annotations` como enrichment dict.
- Si hay necesidades que no pueden resolverse con public API + interceptor context → escalate a decision: ¿agregar public API? ¿indica que la feature no debería estar en Pro.Cluster?

### csproj cleanup

```xml
<!-- REMOVE from src/Asterisk.Sdk.Sessions/Asterisk.Sdk.Sessions.csproj -->
<InternalsVisibleTo Include="Asterisk.Sdk.Pro.Cluster" />
```

## Consequences

**Positivas:**
- SDK MIT metadata pública queda limpia — no filtra nombres Pro.
- Contract público estable (`ISessionInterceptor`) documenta qué puede hacer un external extender.
- Sessions internals pueden refactorizarse sin breaking silencioso de Pro.Cluster.
- Pattern reutilizable: otros Pro packages que quieran session hooks usan la misma extension point.
- Open-core stewardship pledge (ADR-0027) se honra: no hay special access privilegiado para commercial tier.

**Negativas:**
- Refactor de Pro.Cluster requerido en v2.0 — scope no trivial.
- Performance overhead marginal (interceptor iteration) — mitigado porque interceptors son opcionales + async ValueTask.
- Some existing cluster features pueden requerir new public API en `CallSession` si confiaban en internals.

**Mitigación:**
- Pro.Cluster refactor en Mes 4 del roadmap (coordinado con Pro bump SDK).
- Audit de qué exactamente Pro.Cluster accedía via internals — lista explícita en migration spec.
- Public API additions en `CallSession` se validan vs Sessions.Tests para evitar regression.

## Alternatives considered

- **Keep `InternalsVisibleTo`:** rechazado — leak pública + acoplamiento + pattern no escalable.
- **Mover Sessions a Pro tier completamente:** rechazado — CallSessionManager es infra core que MIT users necesitan (Asterisk event correlation básico).
- **Separate `Asterisk.Sdk.Sessions.Extensions` package con public internals:** rechazado — split artificial; `ISessionInterceptor` en Sessions package es natural.
- **Source generator que genera los hooks en compile-time:** rechazado — over-engineering; runtime interface pattern es estándar y adecuado.

## References

- PSD §9 Mes 4
- DDD Domain Events pattern — observation vs mutation separation
- ASP.NET Core IMiddleware pattern (similar lifecycle interceptor shape)
