# Plan de Implementacion - Correcciones para Alta Carga (100K+ Agentes)

**Fecha:** 2026-03-01
**Basado en:** `docs/architecture-review-high-load.md`
**Objetivo:** Resolver los 17 hallazgos identificados en la revision arquitectonica
**Esfuerzo total estimado:** 5-6 dias de desarrollo

---

## Organizacion en 4 Sprints

```
Sprint 1 ─── Thread Safety & Correctness ─── (1.5 dias) ─── P0/P1
    │
Sprint 2 ─── Multi-Server & Reconnection ─── (1.5 dias) ─── P0/P1
    │
Sprint 3 ─── Performance & Indices ────────── (1 dia) ────── P1/P2
    │
Sprint 4 ─── Observabilidad & Tuning ──────── (1 dia) ────── P2/P3
```

Cada sprint es independiente para merge, pero el orden es critico: Sprint 1 corrige bugs que afectan la correctitud antes de agregar features en Sprint 2-4.

---

## Sprint 1: Thread Safety & Correctness

**Duracion:** 1.5 dias
**Hallazgos:** C-01, C-02, C-03, C-05, M-04, M-05
**Objetivo:** Eliminar race conditions, data corruption y perdida silenciosa de datos

### Tarea 1.1 — QueueManager thread-safe (C-01)

**Archivo:** `src/Asterisk.NetAot.Live/Queues/QueueManager.cs`
**Esfuerzo:** 2h

**Que hacer:**

1. Reemplazar `List<AsteriskQueueMember> Members` por `ConcurrentDictionary<string, AsteriskQueueMember>` con key = `Interface`:

```csharp
// ANTES (QueueManager.cs:129)
public List<AsteriskQueueMember> Members { get; } = [];

// DESPUES
public ConcurrentDictionary<string, AsteriskQueueMember> Members { get; } = new();
```

2. Reemplazar `List<AsteriskQueueEntry> Entries` por `ConcurrentDictionary<string, AsteriskQueueEntry>` con key = `Channel`:

```csharp
// ANTES (QueueManager.cs:130)
public List<AsteriskQueueEntry> Entries { get; } = [];

// DESPUES
public ConcurrentDictionary<string, AsteriskQueueEntry> Entries { get; } = new();
```

3. Actualizar todos los metodos del QueueManager:

```csharp
// OnMemberAdded (linea 41-55)
public void OnMemberAdded(string queueName, string iface, string? memberName,
    int penalty, bool paused, int status)
{
    var queue = _queues.GetOrAdd(queueName, _ => new AsteriskQueue { Name = queueName });
    var member = new AsteriskQueueMember
    {
        Interface = iface,
        MemberName = memberName,
        Penalty = penalty,
        Paused = paused,
        Status = (QueueMemberState)status
    };
    queue.Members[iface] = member;  // AddOrUpdate atomico
    MemberAdded?.Invoke(queueName, member);
}

// OnMemberRemoved (linea 58-69)
public void OnMemberRemoved(string queueName, string iface)
{
    if (_queues.TryGetValue(queueName, out var queue)
        && queue.Members.TryRemove(iface, out var member))
    {
        MemberRemoved?.Invoke(queueName, member);
    }
}

// OnMemberPaused (linea 72-83)
public void OnMemberPaused(string queueName, string iface, bool paused, string? reason = null)
{
    if (_queues.TryGetValue(queueName, out var queue)
        && queue.Members.TryGetValue(iface, out var member))
    {
        member.Paused = paused;
        member.PausedReason = reason;
    }
}

// OnCallerJoined (linea 86-98)
public void OnCallerJoined(string queueName, string channel, string? callerId, int position)
{
    var queue = _queues.GetOrAdd(queueName, _ => new AsteriskQueue { Name = queueName });
    var entry = new AsteriskQueueEntry
    {
        Channel = channel,
        CallerId = callerId,
        Position = position,
        JoinedAt = DateTimeOffset.UtcNow
    };
    queue.Entries[channel] = entry;  // AddOrUpdate atomico
    CallerJoined?.Invoke(queueName, entry);
}

// OnCallerLeft (linea 101-112)
public void OnCallerLeft(string queueName, string channel)
{
    if (_queues.TryGetValue(queueName, out var queue)
        && queue.Entries.TryRemove(channel, out var entry))
    {
        CallerLeft?.Invoke(queueName, entry);
    }
}
```

4. Agregar propiedades de conveniencia para conteo:

```csharp
// En AsteriskQueue
public int MemberCount => Members.Count;
public int EntryCount => Entries.Count;
```

**Tests a crear/actualizar:**
- `QueueManagerTests.OnMemberAdded_ConcurrentCalls_ShouldNotCorrupt`
- `QueueManagerTests.OnCallerJoined_ConcurrentCalls_ShouldNotCorrupt`
- Actualizar tests existentes que usan `.Members.Count` o iteran Members como List

**Criterio de aceptacion:**
- 0 `List<T>` mutable en AsteriskQueue
- Todas las operaciones de Members y Entries son atomicas
- Tests existentes pasan (adaptar los que accedian como List)

---

### Tarea 1.2 — Writer lock en AmiConnection (C-02)

**Archivo:** `src/Asterisk.NetAot.Ami/Connection/AmiConnection.cs`
**Esfuerzo:** 1h

**Que hacer:**

1. Agregar SemaphoreSlim como campo:

```csharp
// Agregar despues de linea 61
private readonly SemaphoreSlim _writeLock = new(1, 1);
```

2. Envolver TODAS las llamadas a `_writer!.WriteActionAsync` con el lock. Hay 5 call sites:

   a. `SendActionAsync` (linea 216):
   ```csharp
   await _writeLock.WaitAsync(cancellationToken);
   try
   {
       await _writer!.WriteActionAsync(actionName, actionId, fields, cancellationToken);
   }
   finally
   {
       _writeLock.Release();
   }
   ```

   b. `SendActionAsync<TResponse>` (linea 248): misma envoltura

   c. `SendEventGeneratingActionAsync` (linea 281): misma envoltura

   d. `LoginAsync` (lineas 119, 135): estas corren durante ConnectAsync que es secuencial, no necesitan lock pero no hace dano envolverlas por consistencia

   e. `DisconnectAsync` (linea 437): send Logoff, envolver tambien

3. Dispose del SemaphoreSlim en `CleanupAsync`:

```csharp
// En CleanupAsync, NO disponer _writeLock (se reutiliza en reconexion)
// Solo disponer en DisposeAsync final
```

**Tests a crear:**
- `AmiConnectionTests.SendActionAsync_ConcurrentCalls_ShouldNotInterleave`
  - Usar un mock de ISocketConnection que registra las escrituras
  - Enviar 100 acciones concurrentes con `Task.WhenAll`
  - Verificar que cada mensaje AMI esta completo (no intercalado)

**Criterio de aceptacion:**
- Toda escritura al PipeWriter pasa por _writeLock
- Test de concurrencia pasa con 100 escrituras paralelas

---

### Tarea 1.3 — AsyncEventPump: deteccion de drops + capacidad configurable (C-03)

**Archivo:** `src/Asterisk.NetAot.Ami/Internal/AsyncEventPump.cs`
**Esfuerzo:** 2h

**Que hacer:**

1. Agregar contador de eventos descartados y callback:

```csharp
public sealed class AsyncEventPump : IAsyncDisposable
{
    private readonly Channel<ManagerEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _consumerTask;

    private long _droppedEvents;
    private long _processedEvents;

    public const int DefaultCapacity = 20_000;

    /// <summary>Numero de eventos descartados desde el inicio.</summary>
    public long DroppedEvents => Volatile.Read(ref _droppedEvents);

    /// <summary>Numero de eventos procesados desde el inicio.</summary>
    public long ProcessedEvents => Volatile.Read(ref _processedEvents);

    /// <summary>Eventos pendientes en el buffer.</summary>
    public int PendingCount => _channel.Reader.Count;

    /// <summary>Callback invocado cuando un evento es descartado.</summary>
    public Action<ManagerEvent>? OnEventDropped { get; set; }

    public AsyncEventPump(int capacity = DefaultCapacity)
    {
        _channel = Channel.CreateBounded<ManagerEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
    }

    public void Start(Func<ManagerEvent, ValueTask> handler)
    {
        _consumerTask = Task.Run(async () =>
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                Interlocked.Increment(ref _processedEvents);
                await handler(evt);
            }
        });
    }

    public bool TryEnqueue(ManagerEvent evt)
    {
        if (!_channel.Writer.TryWrite(evt))
        {
            Interlocked.Increment(ref _droppedEvents);
            OnEventDropped?.Invoke(evt);
            return false;
        }
        return true;
    }

    // DisposeAsync sin cambios
}
```

2. En `AmiConnection.ConnectAsync`, configurar el callback de drop:

```csharp
_eventPump = new AsyncEventPump(options.EventPumpCapacity);
_eventPump.OnEventDropped = evt =>
    _logger.LogWarning("AMI event dropped due to full buffer: {EventType}", evt.EventType);
_eventPump.Start(DispatchEventAsync);
```

3. Agregar `EventPumpCapacity` a `AmiConnectionOptions`:

```csharp
// En AmiConnectionOptions
/// <summary>Capacidad del buffer de eventos. Default: 20,000.</summary>
public int EventPumpCapacity { get; set; } = AsyncEventPump.DefaultCapacity;
```

**Tests a crear:**
- `AsyncEventPumpTests.TryEnqueue_WhenFull_ShouldIncrementDroppedCount`
- `AsyncEventPumpTests.TryEnqueue_WhenFull_ShouldInvokeCallback`
- `AsyncEventPumpTests.ProcessedEvents_ShouldTrackConsumedCount`

**Criterio de aceptacion:**
- `DroppedEvents` refleja cantidad real de eventos perdidos
- Callback `OnEventDropped` se invoca por cada drop
- Capacidad configurable via opciones
- Log warning cuando se descarta un evento

---

### Tarea 1.4 — Actualizaciones atomicas en entidades Live (C-05)

**Archivos:**
- `src/Asterisk.NetAot.Live/Agents/AgentManager.cs`
- `src/Asterisk.NetAot.Live/Channels/ChannelManager.cs`
- `src/Asterisk.NetAot.Live/Queues/QueueManager.cs`

**Esfuerzo:** 4h

**Estrategia:** Usar el patron "build-then-swap" donde se construye el objeto completo y luego se inserta atomicamente en el diccionario. Para actualizaciones parciales, usar lock por entidad.

**Que hacer:**

1. Agregar `Lock` por entidad en `AgentManager`:

```csharp
// AgentManager.cs - Agregar lock interno a AsteriskAgent
public sealed class AsteriskAgent : LiveObjectBase
{
    internal readonly Lock SyncRoot = new();
    // ... propiedades existentes
}

// Actualizar OnAgentLogin
public void OnAgentLogin(string agentId, string? channel = null)
{
    var agent = _agents.GetOrAdd(agentId, _ => new AsteriskAgent { AgentId = agentId });
    lock (agent.SyncRoot)
    {
        agent.State = AgentState.Available;
        agent.Channel = channel;
        agent.LoggedInAt = DateTimeOffset.UtcNow;
    }
    AgentLoggedIn?.Invoke(agent);
}

// Mismo patron para OnAgentLogoff, OnAgentConnect, OnAgentComplete, OnAgentPaused
```

2. Mismo patron en `ChannelManager`:

```csharp
public sealed class AsteriskChannel : LiveObjectBase
{
    internal readonly Lock SyncRoot = new();
    // ... propiedades existentes
}

// OnNewChannel: el objeto se crea completo antes de insertarlo (ya es atomico)
// OnNewState: lock agent.SyncRoot antes de mutar
public void OnNewState(string uniqueId, ChannelState newState, string? channelName = null)
{
    if (_channelsByUniqueId.TryGetValue(uniqueId, out var channel))
    {
        lock (channel.SyncRoot)
        {
            channel.State = newState;
            if (channelName is not null) channel.Name = channelName;
        }
        ChannelStateChanged?.Invoke(channel);
    }
}
```

3. En `QueueManager.OnQueueParams`, lock en la queue:

```csharp
public void OnQueueParams(string queueName, int max, string? strategy,
    int calls, int holdTime, int talkTime, int completed, int abandoned)
{
    var queue = _queues.GetOrAdd(queueName, _ => new AsteriskQueue { Name = queueName });
    lock (queue.SyncRoot)  // Agregar Lock a AsteriskQueue tambien
    {
        queue.Max = max;
        queue.Strategy = strategy;
        queue.Calls = calls;
        queue.HoldTime = holdTime;
        queue.TalkTime = talkTime;
        queue.Completed = completed;
        queue.Abandoned = abandoned;
    }
    QueueUpdated?.Invoke(queue);
}
```

**Tests a crear:**
- `AgentManagerTests.OnAgentLogin_ConcurrentReads_ShouldSeeConsistentState`
- `ChannelManagerTests.OnNewState_ConcurrentReads_ShouldSeeConsistentState`

**Criterio de aceptacion:**
- Toda mutacion de propiedades ocurre dentro de lock
- Eventos se disparan DESPUES del lock (lectores ven estado completo)
- No deadlocks (locks son por entidad, nunca anidados)

---

### Tarea 1.5 — EventObserver.OnError con logging (M-04)

**Archivo:** `src/Asterisk.NetAot.Live/Server/AsteriskServer.cs`
**Esfuerzo:** 30min

**Que hacer:**

1. Implementar `OnError` y `OnCompleted` en EventObserver:

```csharp
private sealed class EventObserver(AsteriskServer server) : IObserver<ManagerEvent>
{
    public void OnNext(ManagerEvent value)
    {
        // ... switch existente sin cambios
    }

    public void OnError(Exception error)
    {
        server._logger.LogError(error, "AMI connection error in Live API tracking");
        // Limpiar estado stale - los managers conservan datos pero se marcan como "desconectado"
        ConnectionLost?.Invoke(error);
    }

    public void OnCompleted()
    {
        server._logger.LogInformation("AMI connection closed, Live API tracking stopped");
        ConnectionLost?.Invoke(null);
    }
}
```

2. Agregar evento publico a `AsteriskServer`:

```csharp
/// <summary>Se dispara cuando la conexion AMI se pierde o completa.</summary>
public event Action<Exception?>? ConnectionLost;
```

**Tests a crear:**
- `AsteriskServerTests.OnError_ShouldLogAndFireEvent`
- `AsteriskServerTests.OnCompleted_ShouldLogAndFireEvent`

---

### Tarea 1.6 — Manejar QueueMemberStatus/Pause events (M-05)

**Archivo:** `src/Asterisk.NetAot.Live/Server/AsteriskServer.cs`
**Esfuerzo:** 1h

**Que hacer:**

1. Agregar cases al switch en `EventObserver.OnNext`:

```csharp
case QueueMemberStatusEvent qms:
    server.Queues.OnMemberStatusChanged(
        qms.Queue ?? "",
        qms.Interface ?? "",
        qms.Status ?? 0);
    break;

case QueueMemberPauseEvent qmp:
    server.Queues.OnMemberPaused(
        qmp.Queue ?? "",
        qmp.Interface ?? "",
        qmp.Paused ?? false,
        qmp.Reason);
    break;
```

2. Agregar `OnMemberStatusChanged` a `QueueManager`:

```csharp
public void OnMemberStatusChanged(string queueName, string iface, int status)
{
    if (_queues.TryGetValue(queueName, out var queue)
        && queue.Members.TryGetValue(iface, out var member))
    {
        member.Status = (QueueMemberState)status;
        MemberStatusChanged?.Invoke(queueName, member);
    }
}
```

3. Agregar evento:

```csharp
public event Action<string, AsteriskQueueMember>? MemberStatusChanged;
```

4. Verificar que `QueueMemberStatusEvent` y `QueueMemberPauseEvent` existen en `Ami/Events/`. Si no existen, crearlos.

**Tests a crear:**
- `QueueManagerTests.OnMemberStatusChanged_ShouldUpdateStatus`
- `AsteriskServerTests.QueueMemberStatusEvent_ShouldDispatchToQueueManager`

---

### Checklist Sprint 1

- [ ] Tarea 1.1: QueueManager con ConcurrentDictionary
- [ ] Tarea 1.2: SemaphoreSlim en escrituras de AmiConnection
- [ ] Tarea 1.3: AsyncEventPump con drop metrics + callback
- [ ] Tarea 1.4: Lock por entidad en managers
- [ ] Tarea 1.5: EventObserver.OnError con logging
- [ ] Tarea 1.6: QueueMemberStatus events
- [ ] Todos los tests existentes pasan
- [ ] Nuevos tests de concurrencia pasan
- [ ] Build: 0 warnings, 0 errors
- [ ] `dotnet test` completo exitoso

---

## Sprint 2: Multi-Server & Reconnection

**Duracion:** 1.5 dias
**Hallazgos:** C-04, A-04
**Objetivo:** Soportar N servidores Asterisk y reconciliar estado post-reconexion

### Tarea 2.1 — IAmiConnectionFactory (C-04, parte 1)

**Archivos nuevos:**
- `src/Asterisk.NetAot.Abstractions/IAmiConnectionFactory.cs`
- `src/Asterisk.NetAot.Ami/Connection/AmiConnectionFactory.cs`

**Esfuerzo:** 3h

**Que hacer:**

1. Crear interfaz en Abstractions:

```csharp
// src/Asterisk.NetAot.Abstractions/IAmiConnectionFactory.cs
namespace Asterisk.NetAot.Abstractions;

/// <summary>
/// Factory para crear conexiones AMI a multiples servidores Asterisk.
/// </summary>
public interface IAmiConnectionFactory
{
    /// <summary>Crea una nueva conexion AMI con las opciones especificadas.</summary>
    IAmiConnection Create(AmiConnectionOptions options);

    /// <summary>Crea una conexion AMI y conecta inmediatamente.</summary>
    ValueTask<IAmiConnection> CreateAndConnectAsync(
        AmiConnectionOptions options,
        CancellationToken cancellationToken = default);
}
```

2. Mover `AmiConnectionOptions` a Abstractions (si no esta ya) para que la factory pueda referenciarlo.

3. Implementar en Ami:

```csharp
// src/Asterisk.NetAot.Ami/Connection/AmiConnectionFactory.cs
namespace Asterisk.NetAot.Ami.Connection;

public sealed class AmiConnectionFactory : IAmiConnectionFactory
{
    private readonly ISocketConnectionFactory _socketFactory;
    private readonly ILoggerFactory _loggerFactory;

    public AmiConnectionFactory(
        ISocketConnectionFactory socketFactory,
        ILoggerFactory loggerFactory)
    {
        _socketFactory = socketFactory;
        _loggerFactory = loggerFactory;
    }

    public IAmiConnection Create(AmiConnectionOptions options)
    {
        var wrappedOptions = Options.Create(options);
        var logger = _loggerFactory.CreateLogger<AmiConnection>();
        return new AmiConnection(wrappedOptions, _socketFactory, logger);
    }

    public async ValueTask<IAmiConnection> CreateAndConnectAsync(
        AmiConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var connection = Create(options);
        await connection.ConnectAsync(cancellationToken);
        return connection;
    }
}
```

**Tests a crear:**
- `AmiConnectionFactoryTests.Create_ShouldReturnNewInstance`
- `AmiConnectionFactoryTests.CreateAndConnectAsync_ShouldConnectBeforeReturning`

---

### Tarea 2.2 — AsteriskServerPool (C-04, parte 2)

**Archivo nuevo:** `src/Asterisk.NetAot.Live/Server/AsteriskServerPool.cs`
**Esfuerzo:** 4h

**Que hacer:**

```csharp
namespace Asterisk.NetAot.Live.Server;

/// <summary>
/// Administra multiples AsteriskServer conectados a diferentes instancias de Asterisk.
/// Proporciona una vista federada de agentes, colas y canales.
/// </summary>
public sealed class AsteriskServerPool : IAsyncDisposable
{
    private readonly IAmiConnectionFactory _connectionFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, AsteriskServer> _servers = new();
    private readonly ConcurrentDictionary<string, string> _agentRouting = new();

    public AsteriskServerPool(
        IAmiConnectionFactory connectionFactory,
        ILoggerFactory loggerFactory)
    {
        _connectionFactory = connectionFactory;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Agrega y conecta un servidor Asterisk al pool.</summary>
    public async ValueTask<AsteriskServer> AddServerAsync(
        string serverId,
        AmiConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var connection = await _connectionFactory.CreateAndConnectAsync(options, cancellationToken);
        var logger = _loggerFactory.CreateLogger<AsteriskServer>();
        var server = new AsteriskServer(connection, logger);
        server.StartTracking();
        await server.RequestInitialStateAsync(cancellationToken);

        if (!_servers.TryAdd(serverId, server))
        {
            await server.DisposeAsync();
            throw new InvalidOperationException($"Server '{serverId}' already exists in pool");
        }

        // Indexar agentes de este servidor
        foreach (var agent in server.Agents.Agents)
        {
            _agentRouting[agent.AgentId] = serverId;
        }

        // Suscribirse a eventos de agentes para mantener routing actualizado
        server.Agents.AgentLoggedIn += a => _agentRouting[a.AgentId] = serverId;
        server.Agents.AgentLoggedOff += a => _agentRouting.TryRemove(a.AgentId, out _);

        return server;
    }

    /// <summary>Remueve y desconecta un servidor del pool.</summary>
    public async ValueTask RemoveServerAsync(string serverId)
    {
        if (_servers.TryRemove(serverId, out var server))
        {
            // Limpiar routing de agentes de este servidor
            foreach (var agent in server.Agents.Agents)
            {
                _agentRouting.TryRemove(agent.AgentId, out _);
            }
            await server.DisposeAsync();
        }
    }

    /// <summary>Obtiene el servidor donde esta un agente.</summary>
    public AsteriskServer? GetServerForAgent(string agentId)
    {
        if (_agentRouting.TryGetValue(agentId, out var serverId)
            && _servers.TryGetValue(serverId, out var server))
        {
            return server;
        }
        return null;
    }

    /// <summary>Obtiene un servidor por ID.</summary>
    public AsteriskServer? GetServer(string serverId) =>
        _servers.GetValueOrDefault(serverId);

    /// <summary>Todos los servidores.</summary>
    public IEnumerable<KeyValuePair<string, AsteriskServer>> Servers => _servers;

    /// <summary>Total de agentes en todos los servidores.</summary>
    public int TotalAgentCount => _servers.Values.Sum(s => s.Agents.Agents.Count);

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers.Values)
        {
            await server.DisposeAsync();
        }
        _servers.Clear();
        _agentRouting.Clear();
    }
}
```

**Tests a crear:**
- `AsteriskServerPoolTests.AddServerAsync_ShouldTrackServer`
- `AsteriskServerPoolTests.GetServerForAgent_ShouldRouteCorrectly`
- `AsteriskServerPoolTests.RemoveServerAsync_ShouldCleanupRouting`

---

### Tarea 2.3 — Actualizar DI registration (C-04, parte 3)

**Archivo:** `src/Asterisk.NetAot/ServiceCollectionExtensions.cs`
**Esfuerzo:** 1h

**Que hacer:**

1. Registrar factory ademas del singleton:

```csharp
// Mantener API actual para retrocompatibilidad (single server)
services.TryAddSingleton<IAmiConnection, AmiConnection>();

// Agregar factory para multi-server
services.TryAddSingleton<IAmiConnectionFactory, AmiConnectionFactory>();

// Agregar pool (opcional, solo si se configuran multiples servidores)
services.TryAddSingleton<AsteriskServerPool>();
```

2. Agregar overload para multi-server:

```csharp
/// <summary>
/// Registra Asterisk.NetAot con soporte multi-servidor.
/// </summary>
public static IServiceCollection AddAsteriskNetAotMultiServer(
    this IServiceCollection services)
{
    services.TryAddSingleton<ISocketConnectionFactory, PipelineSocketConnectionFactory>();
    services.TryAddSingleton<IAmiConnectionFactory, AmiConnectionFactory>();
    services.TryAddSingleton<AsteriskServerPool>();
    return services;
}
```

---

### Tarea 2.4 — Evento OnReconnected + reconciliacion de estado (A-04)

**Archivos:**
- `src/Asterisk.NetAot.Ami/Connection/AmiConnection.cs`
- `src/Asterisk.NetAot.Live/Server/AsteriskServer.cs`

**Esfuerzo:** 3h

**Que hacer:**

1. Agregar evento `Reconnected` a `IAmiConnection` (en Abstractions):

```csharp
// IAmiConnection.cs - agregar
/// <summary>Se dispara despues de una reconexion exitosa.</summary>
event Action? Reconnected;
```

2. Disparar desde `AmiConnection.ReconnectLoopAsync`:

```csharp
private async Task ReconnectLoopAsync()
{
    var attempt = 0;
    while (_state == AmiConnectionState.Reconnecting)
    {
        attempt++;
        var delay = attempt <= 10 ? 50 : 5000;
        AmiConnectionLog.Reconnecting(_logger, delay, attempt);
        await Task.Delay(delay);

        if (_options.MaxReconnectAttempts > 0 && attempt >= _options.MaxReconnectAttempts)
        {
            _state = AmiConnectionState.Disconnected;
            break;
        }

        try
        {
            await CleanupAsync();
            await ConnectAsync();
            Reconnected?.Invoke();  // <-- NUEVO: notificar reconexion exitosa
            return;
        }
        catch
        {
            // Retry
        }
    }
}
```

3. Suscribirse desde `AsteriskServer`:

```csharp
public void StartTracking()
{
    _subscription = _connection.Subscribe(new EventObserver(this));
    _connection.Reconnected += OnReconnected;
}

private async void OnReconnected()
{
    try
    {
        _logger.LogInformation("AMI reconnected, reloading Live API state");

        // Limpiar estado stale
        Channels.Clear();
        Queues.Clear();
        Agents.Clear();
        MeetMe.Clear();

        // Re-suscribir observer (la conexion es nueva)
        _subscription?.Dispose();
        _subscription = _connection.Subscribe(new EventObserver(this));

        // Recargar estado fresco
        await RequestInitialStateAsync();
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to reload state after reconnection");
    }
}
```

**Tests a crear:**
- `AsteriskServerTests.OnReconnected_ShouldClearAndReloadState`
- `AmiConnectionTests.ReconnectLoop_ShouldFireReconnectedEvent`

---

### Checklist Sprint 2

- [ ] Tarea 2.1: IAmiConnectionFactory implementada
- [ ] Tarea 2.2: AsteriskServerPool con routing de agentes
- [ ] Tarea 2.3: DI registration multi-server
- [ ] Tarea 2.4: Evento Reconnected + reconciliacion
- [ ] Tests existentes pasan (retrocompatibilidad)
- [ ] Nuevos tests multi-server pasan
- [ ] Build: 0 warnings, 0 errors

---

## Sprint 3: Performance & Indices

**Duracion:** 1 dia
**Hallazgos:** A-01, A-02, A-03, M-01, M-02, B-02
**Objetivo:** Eliminar scans O(n), reducir allocations, agregar indices secundarios

### Tarea 3.1 — Indice secundario por nombre en ChannelManager (A-01)

**Archivo:** `src/Asterisk.NetAot.Live/Channels/ChannelManager.cs`
**Esfuerzo:** 1h

**Que hacer:**

1. Agregar segundo diccionario:

```csharp
private readonly ConcurrentDictionary<string, AsteriskChannel> _channelsByUniqueId = new();
private readonly ConcurrentDictionary<string, AsteriskChannel> _channelsByName = new();
```

2. Mantener sincronizado en todas las operaciones:

```csharp
// OnNewChannel
_channelsByUniqueId[uniqueId] = channel;
_channelsByName[channelName] = channel;

// OnHangup
if (_channelsByUniqueId.TryRemove(uniqueId, out var channel))
{
    _channelsByName.TryRemove(channel.Name, out _);
    // ...
}

// OnRename
if (_channelsByUniqueId.TryGetValue(uniqueId, out var channel))
{
    _channelsByName.TryRemove(channel.Name, out _);
    lock (channel.SyncRoot)
    {
        channel.Name = newName;
    }
    _channelsByName[newName] = channel;
}

// GetByName: O(1) ahora
public AsteriskChannel? GetByName(string name) =>
    _channelsByName.GetValueOrDefault(name);

// Clear
public void Clear()
{
    _channelsByUniqueId.Clear();
    _channelsByName.Clear();
}
```

---

### Tarea 3.2 — Indice inverso agente->colas en QueueManager (A-02)

**Archivo:** `src/Asterisk.NetAot.Live/Queues/QueueManager.cs`
**Esfuerzo:** 2h

**Que hacer:**

1. Agregar indice inverso:

```csharp
private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _queuesByMember = new();
```

2. Mantener en cada operacion de miembros:

```csharp
// OnMemberAdded
queue.Members[iface] = member;
_queuesByMember.GetOrAdd(iface, _ => new()).TryAdd(queueName, 0);

// OnMemberRemoved
if (queue.Members.TryRemove(iface, out var member))
{
    if (_queuesByMember.TryGetValue(iface, out var queues))
        queues.TryRemove(queueName, out _);
}
```

3. Exponer API de consulta:

```csharp
/// <summary>Obtiene los nombres de las colas donde esta un miembro.</summary>
public IEnumerable<string> GetQueuesForMember(string memberInterface)
{
    if (_queuesByMember.TryGetValue(memberInterface, out var queues))
        return queues.Keys;
    return [];
}

/// <summary>Obtiene todas las colas con sus objetos donde esta un miembro.</summary>
public IEnumerable<AsteriskQueue> GetQueueObjectsForMember(string memberInterface)
{
    if (_queuesByMember.TryGetValue(memberInterface, out var queueNames))
    {
        foreach (var name in queueNames.Keys)
        {
            if (_queues.TryGetValue(name, out var queue))
                yield return queue;
        }
    }
}
```

**Tests a crear:**
- `QueueManagerTests.GetQueuesForMember_ShouldReturnAllQueues`
- `QueueManagerTests.OnMemberRemoved_ShouldUpdateReverseIndex`

---

### Tarea 3.3 — Eliminar snapshot allocations en properties (A-03)

**Archivos:** AgentManager.cs, QueueManager.cs, ChannelManager.cs
**Esfuerzo:** 1h

**Que hacer:**

Reemplazar en los 3 managers:

```csharp
// ANTES
public IReadOnlyCollection<AsteriskAgent> Agents =>
    _agents.Values.ToList().AsReadOnly();

// DESPUES
public IEnumerable<AsteriskAgent> Agents => _agents.Values;
public int AgentCount => _agents.Count;
```

Mismo cambio para `Queues` y `ActiveChannels`.

Para codigo que necesite un snapshot puntual (ej: serializar a JSON), agregar metodo explicito:

```csharp
/// <summary>Crea una copia snapshot de todos los agentes. Usar con cuidado en colecciones grandes.</summary>
public IReadOnlyList<AsteriskAgent> GetSnapshot() => [.. _agents.Values];
```

**Impacto en AsteriskServer:** Actualizar `RequestInitialStateAsync` linea 115 que usa `.Count`:

```csharp
// ANTES
AsteriskServerLog.InitialStateLoaded(_logger, Channels.ActiveChannels.Count, ...);

// DESPUES
AsteriskServerLog.InitialStateLoaded(_logger, Channels.ChannelCount, ...);
```

---

### Tarea 3.4 — Copy-on-write para observers (M-01)

**Archivo:** `src/Asterisk.NetAot.Ami/Connection/AmiConnection.cs`
**Esfuerzo:** 1h

**Que hacer:**

```csharp
// Reemplazar lineas 60-61
// ANTES
private readonly List<IObserver<ManagerEvent>> _observers = [];
private readonly Lock _observerLock = new();

// DESPUES
private volatile ImmutableArray<IObserver<ManagerEvent>> _observers = [];
private readonly Lock _observerLock = new();  // Solo para Subscribe/Unsubscribe

// DispatchEventAsync: sin lock, sin allocation
private ValueTask DispatchEventAsync(ManagerEvent evt)
{
    var snapshot = _observers;  // Lectura atomica de referencia

    foreach (var observer in snapshot)
    {
        try
        {
            observer.OnNext(evt);
        }
        catch
        {
            // Observer errors should not crash the pump
        }
    }

    return OnEvent?.Invoke(evt) ?? ValueTask.CompletedTask;
}

// Subscribe: copy-on-write bajo lock
public IDisposable Subscribe(IObserver<ManagerEvent> observer)
{
    lock (_observerLock)
    {
        _observers = _observers.Add(observer);
    }
    return new Unsubscriber(this, observer);
}

// Unsubscriber.Dispose:
public void Dispose()
{
    lock (connection._observerLock)
    {
        connection._observers = connection._observers.Remove(observer);
    }
}
```

Agregar using: `using System.Collections.Immutable;`

---

### Tarea 3.5 — ExtensionHistory con limite (M-02)

**Archivo:** `src/Asterisk.NetAot.Live/Channels/ChannelManager.cs`
**Esfuerzo:** 30min

**Que hacer:**

Implementar limite en la property de AsteriskChannel:

```csharp
public sealed class AsteriskChannel : LiveObjectBase
{
    private const int MaxExtensionHistorySize = 100;

    private readonly List<ExtensionHistoryEntry> _extensionHistory = [];
    public IReadOnlyList<ExtensionHistoryEntry> ExtensionHistory => _extensionHistory;

    internal void AddExtensionHistory(ExtensionHistoryEntry entry)
    {
        if (_extensionHistory.Count >= MaxExtensionHistorySize)
            _extensionHistory.RemoveAt(0);
        _extensionHistory.Add(entry);
    }
}
```

---

### Tarea 3.6 — ResponseEventCollector bounded (B-02)

**Archivo:** `src/Asterisk.NetAot.Ami/Connection/AmiConnection.cs`
**Esfuerzo:** 30min

**Que hacer:**

```csharp
// ANTES (linea 537-538)
private readonly Channel<ManagerEvent> _channel =
    Channel.CreateUnbounded<ManagerEvent>();

// DESPUES
private readonly Channel<ManagerEvent> _channel =
    Channel.CreateBounded<ManagerEvent>(new BoundedChannelOptions(100_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true
    });
```

---

### Checklist Sprint 3

- [ ] Tarea 3.1: ChannelManager.GetByName O(1)
- [ ] Tarea 3.2: Indice inverso agente->colas
- [ ] Tarea 3.3: Snapshot allocations eliminadas
- [ ] Tarea 3.4: Copy-on-write para observers
- [ ] Tarea 3.5: ExtensionHistory con limite
- [ ] Tarea 3.6: ResponseEventCollector bounded
- [ ] Tests existentes pasan
- [ ] Build: 0 warnings, 0 errors

---

## Sprint 4: Observabilidad & Tuning

**Duracion:** 1 dia
**Hallazgos:** M-03, B-01, B-03
**Objetivo:** Metricas con System.Diagnostics.Metrics, tuning de pipelines

### Tarea 4.1 — System.Diagnostics.Metrics para AMI (M-03)

**Archivo nuevo:** `src/Asterisk.NetAot.Ami/Diagnostics/AmiMetrics.cs`
**Esfuerzo:** 4h

**Que hacer:**

1. Crear clase de metricas:

```csharp
namespace Asterisk.NetAot.Ami.Diagnostics;

/// <summary>
/// Metricas de la conexion AMI expuestas via System.Diagnostics.Metrics.
/// Compatible con OpenTelemetry, Prometheus, dotnet-counters, etc.
/// </summary>
public static class AmiMetrics
{
    public static readonly Meter Meter = new("Asterisk.NetAot.Ami", "1.0.0");

    // Counters
    public static readonly Counter<long> EventsReceived =
        Meter.CreateCounter<long>("ami.events.received", "events",
            "Total AMI events received from Asterisk");

    public static readonly Counter<long> EventsDropped =
        Meter.CreateCounter<long>("ami.events.dropped", "events",
            "AMI events dropped due to full event pump");

    public static readonly Counter<long> EventsDispatched =
        Meter.CreateCounter<long>("ami.events.dispatched", "events",
            "AMI events dispatched to observers");

    public static readonly Counter<long> ActionsSent =
        Meter.CreateCounter<long>("ami.actions.sent", "actions",
            "Total AMI actions sent to Asterisk");

    public static readonly Counter<long> ResponsesReceived =
        Meter.CreateCounter<long>("ami.responses.received", "responses",
            "Total AMI responses received");

    public static readonly Counter<long> ReconnectionAttempts =
        Meter.CreateCounter<long>("ami.reconnections", "attempts",
            "AMI reconnection attempts");

    // Gauges (via ObservableGauge, registrados por AmiConnection)
    // ami.event_pump.pending - eventos pendientes en el pump
    // ami.pending_actions - acciones esperando respuesta
    // ami.connection.state - estado actual de la conexion

    // Histograms
    public static readonly Histogram<double> ActionRoundtripMs =
        Meter.CreateHistogram<double>("ami.action.roundtrip", "ms",
            "Roundtrip time for AMI action send->response");

    public static readonly Histogram<double> EventDispatchMs =
        Meter.CreateHistogram<double>("ami.event.dispatch", "ms",
            "Time to dispatch an event to all observers");
}
```

2. Instrumentar `AmiConnection`:

```csharp
// En SendActionAsync, medir roundtrip:
var sw = Stopwatch.GetTimestamp();
var responseMsg = await tcs.Task.WaitAsync(linked.Token);
AmiMetrics.ActionRoundtripMs.Record(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);
AmiMetrics.ActionsSent.Add(1);

// En ReaderLoopAsync, contar eventos:
AmiMetrics.EventsReceived.Add(1);

// En DispatchEventAsync, medir dispatch:
var sw = Stopwatch.GetTimestamp();
// ... dispatch ...
AmiMetrics.EventDispatchMs.Record(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);
AmiMetrics.EventsDispatched.Add(1);

// En ConnectAsync, registrar gauges:
Meter.CreateObservableGauge("ami.event_pump.pending", () => _eventPump?.PendingCount ?? 0);
Meter.CreateObservableGauge("ami.pending_actions", () => _pendingActions.Count);
```

3. Crear metricas para Live API:

**Archivo nuevo:** `src/Asterisk.NetAot.Live/Diagnostics/LiveMetrics.cs`

```csharp
public static class LiveMetrics
{
    public static readonly Meter Meter = new("Asterisk.NetAot.Live", "1.0.0");

    // Registrados como ObservableGauge por cada manager
    // live.channels.active
    // live.queues.count
    // live.agents.total
    // live.agents.available
    // live.agents.on_call
    // live.agents.paused
}
```

**Tests a crear:**
- Verificar que contadores se incrementan en tests unitarios usando `MeterListener`

---

### Tarea 4.2 — PipeOptions tuning (B-01)

**Archivo:** `src/Asterisk.NetAot.Ami/Transport/PipelineSocketConnection.cs`
**Esfuerzo:** 30min

**Que hacer:**

```csharp
// ANTES (lineas 57-59)
var pipeOptions = new PipeOptions(
    minimumSegmentSize: MinimumBufferSize,
    useSynchronizationContext: false);

// DESPUES
var pipeOptions = new PipeOptions(
    pool: MemoryPool<byte>.Shared,
    minimumSegmentSize: MinimumBufferSize,
    pauseWriterThreshold: 1024 * 1024,      // 1 MB: pausa escritura si reader no consume
    resumeWriterThreshold: 512 * 1024,       // 512 KB: resume escritura
    readerScheduler: PipeScheduler.Inline,    // No schedule, ejecutar inline
    writerScheduler: PipeScheduler.Inline,
    useSynchronizationContext: false);
```

---

### Tarea 4.3 — IAsyncEnumerable en managers (B-03)

**Archivos:** AgentManager.cs, QueueManager.cs, ChannelManager.cs
**Esfuerzo:** 2h

**Que hacer:**

Agregar metodos de consulta con filtrado lazy (sin reemplazar los existentes):

```csharp
// AgentManager
public IEnumerable<AsteriskAgent> GetAgentsByState(AgentState state) =>
    _agents.Values.Where(a => a.State == state);

public IEnumerable<AsteriskAgent> GetAgentsWhere(Func<AsteriskAgent, bool> predicate) =>
    _agents.Values.Where(predicate);

// QueueManager
public IEnumerable<AsteriskQueueMember> GetMembersWhere(
    string queueName, Func<AsteriskQueueMember, bool> predicate)
{
    if (_queues.TryGetValue(queueName, out var queue))
        return queue.Members.Values.Where(predicate);
    return [];
}

// ChannelManager
public IEnumerable<AsteriskChannel> GetChannelsByState(ChannelState state) =>
    _channelsByUniqueId.Values.Where(c => c.State == state);
```

---

### Checklist Sprint 4

- [ ] Tarea 4.1: AmiMetrics + LiveMetrics con System.Diagnostics.Metrics
- [ ] Tarea 4.2: PipeOptions con backpressure y MemoryPool
- [ ] Tarea 4.3: Queries lazy en managers
- [ ] Verificar con `dotnet-counters` que las metricas son visibles
- [ ] Build: 0 warnings, 0 errors
- [ ] Trim analysis: 0 warnings (System.Diagnostics.Metrics es AOT-safe)

---

## Resumen de Archivos Modificados/Creados

### Archivos Modificados

| Sprint | Archivo | Cambio principal |
|--------|---------|-----------------|
| 1 | `src/Asterisk.NetAot.Live/Queues/QueueManager.cs` | List -> ConcurrentDictionary, lock, MemberStatus |
| 1 | `src/Asterisk.NetAot.Ami/Connection/AmiConnection.cs` | SemaphoreSlim writer, ImmutableArray observers, Reconnected event |
| 1 | `src/Asterisk.NetAot.Ami/Internal/AsyncEventPump.cs` | Drop metrics, callback, capacidad configurable |
| 1 | `src/Asterisk.NetAot.Live/Agents/AgentManager.cs` | Lock por entidad, IEnumerable property |
| 1 | `src/Asterisk.NetAot.Live/Channels/ChannelManager.cs` | Lock por entidad, indice por nombre, IEnumerable, ExtensionHistory limit |
| 1 | `src/Asterisk.NetAot.Live/Server/AsteriskServer.cs` | OnError, OnCompleted, QueueMemberStatus, Reconnected |
| 2 | `src/Asterisk.NetAot/ServiceCollectionExtensions.cs` | AddAsteriskNetAotMultiServer |
| 2 | `src/Asterisk.NetAot.Abstractions/IAmiConnection.cs` | Evento Reconnected |
| 3 | `src/Asterisk.NetAot.Ami/Transport/PipelineSocketConnection.cs` | PipeOptions tuning |

### Archivos Nuevos

| Sprint | Archivo | Proposito |
|--------|---------|-----------|
| 2 | `src/Asterisk.NetAot.Abstractions/IAmiConnectionFactory.cs` | Interfaz factory |
| 2 | `src/Asterisk.NetAot.Ami/Connection/AmiConnectionFactory.cs` | Implementacion factory |
| 2 | `src/Asterisk.NetAot.Live/Server/AsteriskServerPool.cs` | Pool multi-server |
| 4 | `src/Asterisk.NetAot.Ami/Diagnostics/AmiMetrics.cs` | Metricas AMI |
| 4 | `src/Asterisk.NetAot.Live/Diagnostics/LiveMetrics.cs` | Metricas Live |

### Tests Nuevos Estimados

| Sprint | Tests nuevos | Archivos |
|--------|-------------|----------|
| 1 | ~12 tests | QueueManager, AmiConnection, AsyncEventPump, AsteriskServer concurrency |
| 2 | ~8 tests | AmiConnectionFactory, AsteriskServerPool, Reconnection |
| 3 | ~6 tests | ChannelManager indices, QueueManager reverse index, snapshots |
| 4 | ~4 tests | Metrics verification con MeterListener |
| **Total** | **~30 tests nuevos** | |

---

## Criterio de Aceptacion Global

- [ ] Build completo: 0 warnings, 0 errors
- [ ] `dotnet test` completo: todos los tests pasan (164 existentes + ~30 nuevos)
- [ ] Trim analysis: 0 warnings (`dotnet publish -c Release` con AOT)
- [ ] Benchmark: sin regresiones en los 15 benchmarks existentes
- [ ] API publica retrocompatible (AddAsteriskNetAot sigue funcionando igual)
- [ ] Nuevo AddAsteriskNetAotMultiServer funciona para multi-server
- [ ] Metricas visibles con `dotnet-counters monitor --process-id <pid> Asterisk.NetAot.Ami`
