# ADR-0032: Event bus transports facts only — Commands vía `ICommandDispatcher` separado

- **Status:** Proposed
- **Date:** 2026-04-20
- **Deciders:** Harold Reina
- **Related:**
  - PSD v2: `Asterisk.Platform/docs/specs/2026-04-19-product-strategy-v2.md` §12.7
  - ADR-0030 (CloudEvents adoption)
  - ADR-0031 (domain vs integration events)

## Context

Análisis arquitectónico del modelo de eventos actual identificó un anti-pattern latente: **el event bus se usa para todo** — facts (events), intents (commands), y a veces queries. Esto degrada el modelo en 6-12 meses:

- **Semántica se pierde:** subscribers no saben si `CallTransferRequested` es un hecho (past tense — alguien ya intentó transferir) o una intención (imperative — alguien quiere transferir).
- **Routing se complica:** commands DEBEN llegar a exactamente UN handler (routing deterministic). Events son fan-out (múltiples subscribers).
- **Reply semantics perdidas:** commands típicamente tienen response (success/failure + data). Events son fire-and-forget.
- **Testing confuso:** unit tests no pueden diferenciar "dispatching a command" de "publishing an event".

En CQRS purist: 3 mechanisms distintos (commands / events / queries).

**Actualmente no hay `ICommandDispatcher` en el SDK.** Cualquier intent se maneja via method calls directos o via PushEvent (disfrazado).

Con CloudEvents adoption (ADR-0030), esta decisión debe hacerse explícita antes de que se consolide el anti-pattern.

## Decision

**Separación explícita de 3 mechanisms:**

| Concepto | Semántica | Mechanism (SDK MIT) | Tense | Cardinality |
|---|---|---|---|---|
| **Event** | Fact que ya ocurrió | `IEventLog` / `IPushEventBus` | Past tense (`call.ended`) | Fan-out (0..N subscribers) |
| **Command** | Intent para cambiar estado | `ICommandDispatcher` | Imperative (`TransferCall`) | Point-to-point (exactly 1 handler) |
| **Query** | Request de información (pull) | Direct method call, no bus | Interrogative (`GetCallState`) | Point-to-point synchronous |

**Regla principal:** "event bus transports facts only (past tense)". Si tu `type` suena a imperative (`DoSomething`), no es un event — es un command.

**API del SDK:**

```csharp
// SDK MIT
public interface ICommandDispatcher
{
    Task<TResult> DispatchAsync<TCommand, TResult>(TCommand command, CancellationToken ct)
        where TCommand : class
        where TResult : class;
}

// Ejemplos:
// ✅ Event (fact): 'asterisk.domain.call.ended' con payload { callId, duration, hangupCause }
// ✅ Command (intent): dispatcher.DispatchAsync<TransferCallCommand, TransferResult>(new TransferCallCommand(callId, targetAgentId))
// ✅ Query (pull): callSessionManager.GetSessionAsync(callId)
```

**Commands characteristics:**
- Naming: imperative, no past tense. `TransferCall`, `MuteChannel`, `DialExtension`.
- Validation: commands tienen validators (pueden rechazarse). Events no.
- Routing: exactamente 1 handler registrado por tipo de command. Dispatcher falla si 0 o >1 handlers.
- Response: commands retornan `TResult` (success/failure/data). Events no tienen response.
- Persistence: commands pueden ser logged pero NO van a event log. Su RESULT (el event que representa el fact) va al log.

**Pattern típico:**
```
1. User requests: TransferCallCommand (dispatcher.DispatchAsync)
2. Handler validates + executes transfer
3. Handler publishes fact: asterisk.domain.call.transferred (bus.AppendAsync)
4. Subscribers react to the fact
```

**Constraints:**
- **No dual-mechanism events.** Un PushEvent que dice "CallTransferRequested" (imperative-sounding) debe renombrarse a `TransferCallCommand` + dispatched separately, o renombrarse a past tense si representa un fact ("transfer ya solicitado y registrado").

## Consequences

**Positivas:**
- Semántica clara — developers saben qué mechanism usar.
- Testing mejor — commands y events testables independientemente.
- Routing deterministic para commands, fan-out para events — sin confusión.
- Prepara el modelo para CQRS full si eventualmente se adopta.
- Evita colapso del bus en 6-12 meses.

**Negativas:**
- Agrega mechanism adicional (`ICommandDispatcher`) — surface pública crece.
- Developers pueden confundirse inicialmente sobre si algo es event o command.
- Migration window: PushEvents actuales con nombres imperative-sounding (si existen) deben refactorizarse.

**Mitigación:**
- Audit de PushEvent types actuales en v2.0 preview: cualquier tipo con naming imperative se renombra.
- Guide en `docs/guides/event-vs-command.md` con ejemplos concretos.
- Naming lint rule (futuro): event types deben terminar en verbo past tense.

## Alternatives considered

- **No distinguir, mantener bus único:** rechazado — anti-pattern documentado; collapsa en 6-12 meses.
- **Full CQRS con Separate Write/Read models:** rechazado por overreach — CQRS completo requiere read model projections separados. Este ADR introduce solo la distinción command/event/query; read models siguen siendo consultable directamente por query.
- **MediatR library adoption:** rechazado — adds dependency + reflection-heavy pattern (AOT concerns). `ICommandDispatcher` propio más simple.
- **Commands como events con ACK reply:** rechazado — complica routing (1:1 pretend en top de 1:N bus).

## References

- PSD §12.7
- CQRS pattern (Greg Young, Udi Dahan references)
- Commands vs Events (Martin Fowler article)
