# Session Engine Design Spec

**Date:** 2026-03-17
**Phase:** 2 — Session Engine MIT
**Target:** v0.5.0-beta
**Scope:** Live Layer Gap Fixes + Asterisk.Sdk.Sessions + PbxAdmin Migration

---

## 1. Overview

Build `Asterisk.Sdk.Sessions` — a domain layer that correlates AMI events into cohesive call sessions with state machines, domain events, and extension points for PRO consumers.

**Approach:** Opción 2 modificada (SDK puro primero, PbxAdmin migration después).

- **Sprint 5-6:** Live layer gap fixes + CallSession core + CallSessionManager
- **Sprint 7-8:** AgentSession + QueueSession + extension points + PbxAdmin migration

### Decision: Why Not Build With PbxAdmin Simultaneously

Investigation found that PbxAdmin's `CallFlowTracker` has ~85-90% overlap with CallSession but is tightly coupled to 4 pages (Calls, Home, TrafficAnalytics, LadderDiagram). Building the SDK layer first, validated with unit/integration tests, then migrating PbxAdmin in Sprint 7-8 gives:

1. Clean scope per sprint
2. No risk of breaking PbxAdmin during SDK design iteration
3. Domain model designed for SDK correctness, not UI convenience

---

## 2. Prerequisites: Live Layer Gap Fixes

### 2.1 Problem

The EventObserver in `AsteriskServer` handles only 14 event types. 10 critical events needed for Sessions are missing. Additionally, `ChannelManager.OnLink`/`OnUnlink` methods exist but are never called.

### 2.2 New: BridgeManager

New manager in `Asterisk.Sdk.Live`, following ChannelManager/QueueManager patterns.

**AsteriskBridge (domain object):**

```csharp
public sealed class AsteriskBridge : LiveObjectBase
{
    public string BridgeUniqueid { get; init; }
    public string? BridgeType { get; set; }        // "basic", "holding", "multiplexed"
    public string? Technology { get; set; }
    public string? Creator { get; set; }
    public string? Name { get; set; }
    public ConcurrentDictionary<string, byte> Channels { get; } = new();  // uniqueId set (like _queuesByMember pattern)
    public int NumChannels => Channels.Count;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DestroyedAt { get; set; }
    internal readonly Lock SyncRoot = new();  // internal readonly, consistent with AsteriskChannel/Agent/Queue
}
```

**BridgeManager:**

```csharp
public sealed class BridgeManager
{
    // Indices
    private readonly ConcurrentDictionary<string, AsteriskBridge> _bridges = new();
    private readonly ConcurrentDictionary<string, AsteriskBridge> _bridgeByChannel = new(); // reverse index: uniqueId → bridge

    // Events
    public event Action<AsteriskBridge>? BridgeCreated;
    public event Action<AsteriskBridge>? BridgeDestroyed;
    public event Action<AsteriskBridge, string>? ChannelEntered;  // bridge, uniqueId
    public event Action<AsteriskBridge, string>? ChannelLeft;     // bridge, uniqueId
    public event Action<BridgeTransferInfo>? TransferOccurred;    // typed record for clarity

    // Handlers
    public void OnBridgeCreated(string bridgeId, string? type, string? technology, string? creator, string? name);
    public void OnChannelEntered(string bridgeId, string uniqueId);  // updates _bridgeByChannel reverse index
    public void OnChannelLeft(string bridgeId, string uniqueId);     // removes from _bridgeByChannel
    public void OnBridgeDestroyed(string bridgeId);
    public void OnBlindTransfer(string bridgeId, string? targetChannel, string? extension, string? context);
    public void OnAttendedTransfer(string origBridgeId, string? secondBridgeId, string? destType, string? result);
    public void Clear();  // clears both _bridges and _bridgeByChannel

    // Queries
    public AsteriskBridge? GetById(string bridgeId);
    public IEnumerable<AsteriskBridge> ActiveBridges => _bridges.Values.Where(b => b.DestroyedAt is null);
    public AsteriskBridge? GetBridgeForChannel(string uniqueId);  // O(1) via _bridgeByChannel reverse index
}

public sealed record BridgeTransferInfo(
    string BridgeId, string TransferType, string? TargetChannel,
    string? SecondBridgeId, string? DestType, string? Result);
```

### 2.3 EventObserver: 10 New Cases

| Event | Handler | Field Mapping Notes |
|-------|---------|---------------------|
| BridgeCreateEvent | `Bridges.OnBridgeCreated()` | `evt.BridgeUniqueid` → bridgeId |
| BridgeEnterEvent | `Bridges.OnChannelEntered()` + `Channels.OnLink()` | `evt.BridgeUniqueid` → bridgeId, `evt.UniqueId` (from ManagerEvent base) → channelUniqueId. These are DIFFERENT fields. |
| BridgeLeaveEvent | `Bridges.OnChannelLeft()` + `Channels.OnUnlink()` | Same mapping as BridgeEnterEvent |
| BridgeDestroyEvent | `Bridges.OnBridgeDestroyed()` | `evt.BridgeUniqueid` → bridgeId |
| DialBeginEvent | `Channels.OnDialBegin()` | `evt.UniqueId` → source channel, `evt.DestUniqueid` → dest channel. DialBeginEvent extends ManagerEvent directly (NOT ChannelEventBase). |
| DialEndEvent | `Channels.OnDialEnd()` | `evt.UniqueId` → source, `evt.DialStatus` → result. Same hierarchy as DialBeginEvent. |
| HoldEvent | `Channels.OnHold()` | Extends ChannelEventBase normally |
| UnholdEvent | `Channels.OnUnhold()` | Extends ChannelEventBase normally |
| BlindTransferEvent | `Bridges.OnBlindTransfer()` | `evt.BridgeUniqueid` → bridgeId, `evt.Extension` → target |
| AttendedTransferEvent | `Bridges.OnAttendedTransfer()` | Complex: `evt.BridgeUniqueid` → origBridge, `evt.SecondBridgeUniqueid` → secondBridge, `evt.DestType`, `evt.Result`. Carries 110 properties across two bridges. |

### 2.4 ChannelManager: New Methods

```csharp
// New methods on ChannelManager
public void OnDialBegin(string uniqueId, string destUniqueId, string destChannel, string? dialString);
public void OnDialEnd(string uniqueId, string? dialStatus);
public void OnHold(string uniqueId, string? musicClass);
public void OnUnhold(string uniqueId);
```

**New properties on AsteriskChannel:**

```csharp
public string? LinkedId { get; init; }           // [C1 FIX] Set by OnNewChannel from NewChannelEvent.Linkedid — CRITICAL for session correlation
public string? DialedChannel { get; set; }       // Set by OnDialBegin
public string? DialStatus { get; set; }          // Set by OnDialEnd (ANSWER, BUSY, NOANSWER, etc.)
public bool IsOnHold { get; set; }               // Toggled by OnHold/OnUnhold
public string? HoldMusicClass { get; set; }      // Set by OnHold
```

**ChannelManager.OnNewChannel must be updated** to accept and store `linkedId` parameter from `NewChannelEvent.Linkedid`.

**New events on ChannelManager:**

```csharp
public event Action<AsteriskChannel>? ChannelDialBegin;
public event Action<AsteriskChannel>? ChannelDialEnd;
public event Action<AsteriskChannel>? ChannelHeld;
public event Action<AsteriskChannel>? ChannelUnheld;
```

### 2.5 AsteriskServer Changes

- New property: `BridgeManager Bridges { get; }` on concrete `AsteriskServer` class (NOT on `IAsteriskServer` interface — consistent with existing pattern where `Channels`, `Queues`, `Agents`, `MeetMe` are on concrete class only, avoiding circular dependency with core)
- `StartAsync()`: bridges built reactively from events (no initial state snapshot — `BridgeListAction` does not exist in AMI)
- Reconnect handler: add `Bridges.Clear()`

### 2.6 LiveMetrics: New Bridge Metrics

| Metric | Type | Description |
|--------|------|-------------|
| `live.bridges.active` | ObservableGauge | Active bridges count |
| `live.bridges.created` | Counter | Total bridges created |
| `live.bridges.destroyed` | Counter | Total bridges destroyed |

---

## 3. CallSession Domain Model

### 3.1 CallSessionState

```csharp
public enum CallSessionState
{
    Created,
    Dialing,
    Ringing,
    Connected,
    OnHold,
    Transferring,
    Conference,
    Completed,
    Failed,
    TimedOut
}
```

**Transition matrix:**

| From | Valid transitions to |
|------|---------------------|
| Created | Dialing, Failed |
| Dialing | Ringing, Connected, Failed, TimedOut |
| Ringing | Connected, Failed, TimedOut |
| Connected | OnHold, Transferring, Conference, Completed, Failed |
| OnHold | Connected, Transferring, Completed, Failed |
| Transferring | Connected, Failed |
| Conference | Connected, Completed, Failed | (Connected when participants drop to 2)
| Completed | (terminal) |
| Failed | (terminal) |
| TimedOut | (terminal) |

Invalid transitions throw `InvalidSessionStateTransitionException`.

### 3.2 CallSession

```csharp
public sealed class CallSession
{
    // Identity
    public string SessionId { get; init; }          // GUID
    public string LinkedId { get; init; }            // Asterisk LinkedId (correlation key)
    public string ServerId { get; init; }            // Multi-server support

    // State (enforced transitions via TryTransition/Transition methods)
    public CallSessionState State { get; private set; }
    public CallDirection Direction { get; init; }  // Inbound or Outbound

    // Participants
    public IReadOnlyList<SessionParticipant> Participants { get; }

    // Context
    public string? QueueName { get; set; }
    public string? AgentId { get; set; }
    public string? AgentInterface { get; set; }
    public string? BridgeId { get; set; }
    public HangupCause? HangupCause { get; set; }

    // Timestamps
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DialingAt { get; set; }
    public DateTimeOffset? RingingAt { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // Hold time tracking (C2 FIX: accumulation mechanism)
    private DateTimeOffset? _holdStartedAt;
    private TimeSpan _accumulatedHoldTime;

    // Computed
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - CreatedAt;
    public TimeSpan? WaitTime => ConnectedAt.HasValue ? ConnectedAt - CreatedAt : null;
    public TimeSpan? TalkTime => CompletedAt.HasValue && ConnectedAt.HasValue
        ? (CompletedAt.Value - ConnectedAt.Value) - HoldTime  // Subtract hold from talk
        : null;
    public TimeSpan HoldTime => _accumulatedHoldTime +
        (_holdStartedAt.HasValue ? DateTimeOffset.UtcNow - _holdStartedAt.Value : TimeSpan.Zero);

    // Metadata (extensible key-value)
    public IReadOnlyDictionary<string, string> Metadata { get; }

    // Audit trail
    public IReadOnlyList<CallSessionEvent> Events { get; }

    // Thread safety
    public Lock SyncRoot { get; } = new();

    // Methods
    internal bool TryTransition(CallSessionState newState);
    internal void Transition(CallSessionState newState);  // Throws on invalid
    internal void AddParticipant(SessionParticipant participant);
    internal void AddEvent(CallSessionEvent evt);
    internal void SetMetadata(string key, string value);
    internal void StartHold() { _holdStartedAt = DateTimeOffset.UtcNow; }
    internal void EndHold()
    {
        if (_holdStartedAt.HasValue)
        {
            _accumulatedHoldTime += DateTimeOffset.UtcNow - _holdStartedAt.Value;
            _holdStartedAt = null;
        }
    }
}
```

### 3.3 SessionParticipant

```csharp
public sealed class SessionParticipant
{
    public string UniqueId { get; init; }
    public string Channel { get; init; }
    public string Technology { get; init; }          // PJSIP, SIP, IAX2, etc.
    public ParticipantRole Role { get; set; }
    public string? CallerIdNum { get; set; }
    public string? CallerIdName { get; set; }
    public DateTimeOffset JoinedAt { get; init; }
    public DateTimeOffset? LeftAt { get; set; }
    public HangupCause? HangupCause { get; set; }
}

public enum ParticipantRole { Caller, Destination, Agent, Transfer, Conference, Internal }
// Internal = Local/ channel pairs (Asterisk routing artifacts, not real participants)
```

### 3.3.1 CallDirection

```csharp
public enum CallDirection { Inbound, Outbound }
```

Note: `Activities.CallDirection` already exists but is in the Activities layer. Sessions defines its own to avoid taking a dependency on Activities.

### 3.4 CallSessionEvent (audit trail)

```csharp
public sealed record CallSessionEvent(
    DateTimeOffset Timestamp,
    CallSessionEventType Type,
    string? SourceChannel,
    string? TargetChannel,
    string? Detail);

public enum CallSessionEventType
{
    Created, Dialing, Ringing, Connected,
    Hold, Unhold, Transfer, Conference,
    ParticipantJoined, ParticipantLeft,
    QueueJoined, AgentConnected,
    Completed, Failed, TimedOut
}
```

### 3.5 Domain Events (IObservable)

Published by `CallSessionManager` for external consumers (PRO, analytics):

```csharp
public abstract record SessionDomainEvent(string SessionId, string ServerId, DateTimeOffset Timestamp);

public sealed record CallStartedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    CallDirection Direction, string? CallerIdNum) : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record CallConnectedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    string? AgentId, string? QueueName, TimeSpan WaitTime) : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record CallTransferredEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    string TransferType, string? TargetChannel) : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record CallHeldEvent(string SessionId, string ServerId, DateTimeOffset Timestamp)
    : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record CallResumedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp)
    : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record CallEndedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    HangupCause? Cause, TimeSpan Duration, TimeSpan? TalkTime) : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record CallFailedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    string Reason) : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record SessionMergedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    string MergedSessionId) : SessionDomainEvent(SessionId, ServerId, Timestamp);
```

**Distinction:**
- `CallSessionEvent` = internal audit trail (list inside CallSession)
- `SessionDomainEvent` = published via `IObservable<SessionDomainEvent>` for external consumers

---

## 4. CallSessionManager

### 4.1 Interface

```csharp
public interface ICallSessionManager : IAsyncDisposable
{
    // Queries
    CallSession? GetById(string sessionId);
    CallSession? GetByLinkedId(string linkedId);
    CallSession? GetByChannelId(string uniqueId);
    CallSession? GetByBridgeId(string bridgeId);
    IEnumerable<CallSession> ActiveSessions { get; }
    IEnumerable<CallSession> GetRecentCompleted(int count = 100);

    // Observable streams
    IObservable<SessionDomainEvent> Events { get; }

    // Lifecycle
    void AttachToServer(AsteriskServer server, string serverId);
    void DetachFromServer(string serverId);
}
```

### 4.2 Internal Indices (O(1) lookup)

```csharp
ConcurrentDictionary<string, CallSession> _sessions;       // sessionId → session
ConcurrentDictionary<string, CallSession> _byLinkedId;     // linkedId → session
ConcurrentDictionary<string, CallSession> _byChannelId;    // uniqueId → session (many-to-one: multiple channels per session)
ConcurrentDictionary<string, string> _bridgeToSession;     // bridgeId → sessionId (re-indexed when bridges change during transfers)
ConcurrentQueue<string> _completedOrder;                    // eviction FIFO
```

All indices maintained automatically when participants, bridges are added/removed.

### 4.3 Event Subscription Strategy

`CallSessionManager` subscribes to Live layer manager events (NOT raw AMI events):

| Manager Event (actual name) | Signature | Session Action |
|----------------------------|-----------|----------------|
| `Channels.ChannelAdded` | `Action<AsteriskChannel>` | Create session or add participant |
| `Channels.ChannelStateChanged` | `Action<AsteriskChannel>` | Transition Dialing → Ringing → Connected |
| `Channels.ChannelRemoved` | `Action<AsteriskChannel>` | Mark participant left, check completion |
| `Channels.ChannelDialBegin` | `Action<AsteriskChannel>` (NEW) | Transition to Dialing, link destination |
| `Channels.ChannelDialEnd` | `Action<AsteriskChannel>` (NEW) | Process dial result |
| `Channels.ChannelHeld` | `Action<AsteriskChannel>` (NEW) | Transition to OnHold, call StartHold() |
| `Channels.ChannelUnheld` | `Action<AsteriskChannel>` (NEW) | Transition to Connected, call EndHold() |
| `Bridges.ChannelEntered` | `Action<AsteriskBridge, string>` (NEW) | Set BridgeId, transition to Connected |
| `Bridges.ChannelLeft` | `Action<AsteriskBridge, string>` (NEW) | Check if bridge empty |
| `Bridges.BridgeDestroyed` | `Action<AsteriskBridge>` (NEW) | Clear BridgeId, re-index _bridgeToSession |
| `Bridges.TransferOccurred` | `Action<BridgeTransferInfo>` (NEW) | Transition to Transferring, handle merge |
| `Queues.CallerJoined` | `Action<string, AsteriskQueueEntry>` (existing) | Set QueueName, add QueueJoined event |
| `Queues.CallerLeft` | `Action<string, AsteriskQueueEntry>` (existing) | (info only, queue context preserved) |
| `Agents.AgentStateChanged` | `Action<AsteriskAgent>` (existing) | When state=OnCall: set AgentId/Interface |
| `Agents.AgentStateChanged` | `Action<AsteriskAgent>` (existing) | When state=Available after OnCall: agent complete |

**Note:** `Agents.AgentConnect` and `Agents.AgentComplete` are not events on AgentManager. We use `AgentStateChanged` and check the state transition (LoggedOff→Available, Available→OnCall, OnCall→Available, etc.).

**Key design choice:** Subscribing to Live managers (not raw AMI) ensures events are already processed and enriched before Sessions consumes them.

### 4.4 Correlation Logic

**NewChannel → Session resolution:**

1. Extract `LinkedId` from channel (via `AsteriskChannel` which now tracks it)
2. If session exists with that LinkedId → add as participant
3. If no session → create new with this channel as Caller
4. Second channel with same LinkedId → Destination role
5. Additional channels (transfer, conference) → role inferred from context

**Direction inference:**
- Context `from-internal` or `from-sip` → Outbound
- Context `from-trunk` or `from-pstn` → Inbound
- Configurable context patterns in `SessionOptions`
- Fallback: first channel = Caller, second = Destination

### 4.4.1 Local Channel Handling

Asterisk `Local/` channels create paired channels (`Local/ext@ctx-00000001;1` and `Local/ext@ctx-00000001;2`) with different UniqueIds but the same LinkedId. These are internal routing artifacts, extremely common in queue scenarios.

**Strategy:**
- Detect `Local/` technology prefix on participant
- Mark with `ParticipantRole.Internal` (new role)
- Do NOT create separate sessions for Local channel pairs
- LinkedId correlation ensures both legs join the same session
- UI consumers can filter out `Internal` participants for display

```csharp
public enum ParticipantRole { Caller, Destination, Agent, Transfer, Conference, Internal }
```

### 4.4.2 Transfer Handling

**Blind transfer:** Simple — one bridge, one target extension. Session transitions to `Transferring`, then back to `Connected` when new channel bridges.

**Attended transfer (complex):**
- Involves two bridges (original call + consultation leg)
- `AttendedTransferEvent` carries `OrigBridge*` and `SecondBridge*` properties
- The consultation leg may be a separate session (different LinkedId)

**Strategy:**
1. When `AttendedTransferEvent` fires with `Result=Success`:
   - Find session by `OrigBridgeUniqueid`
   - Transition to `Transferring`
   - Add transfer target as new participant
   - If consultation session exists (by `SecondBridgeUniqueid`), mark it as `Completed` with metadata `cause=transfer_merged`
   - Emit `CallTransferredEvent` domain event
   - When new bridge forms, transition back to `Connected`
2. When `Result=Fail`: no state change, log event only
3. New domain event: `SessionMergedEvent` emitted when consultation leg merges

```csharp
public sealed record SessionMergedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    string MergedSessionId) : SessionDomainEvent(SessionId, ServerId, Timestamp);
```

### 4.5 Orphan Reconciliation

Periodic `Timer` (default 30s):

1. Take a true snapshot: `ActiveSessions.ToArray()` (ConcurrentDictionary.Values is a live enumeration, not a snapshot)
2. For each session, verify at least one participant has active channel in `ChannelManager`
3. No active channels → transition to `Failed` with metadata `cause=orphaned`
4. Session in Dialing/Ringing beyond timeout → transition to `TimedOut`

### 4.6 SessionOptions

```csharp
public sealed class SessionOptions
{
    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan DialingTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan RingingTimeout { get; set; } = TimeSpan.FromSeconds(120);
    public int MaxCompletedSessions { get; set; } = 1000;
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan QueueMetricsWindow { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan SlaThreshold { get; set; } = TimeSpan.FromSeconds(20);
    public string[] InboundContextPatterns { get; set; } = ["from-trunk", "from-pstn", "from-external"];
    public string[] OutboundContextPatterns { get; set; } = ["from-internal", "from-sip", "from-users"];
}
```

### 4.7 Thread Safety

- Indices: `ConcurrentDictionary` (lock-free reads)
- State transitions: per-session `Lock` (`CallSession.SyncRoot`)
- Domain event emission: `Subject<SessionDomainEvent>` (System.Reactive, thread-safe)
- Reconciliation timer: operates on snapshots, no contention with event handlers

---

## 5. Extension Points

### 5.1 Abstract Base Classes

All extension points live in `Asterisk.Sdk.Sessions` (not core) to avoid circular dependencies.

```csharp
public abstract class CallRouterBase
{
    public abstract ValueTask<string> SelectNodeAsync(CallSession session, CancellationToken ct);
    public virtual ValueTask<bool> CanRouteAsync(CallSession session, CancellationToken ct)
        => ValueTask.FromResult(true);
}

public abstract class AgentSelectorBase
{
    public abstract ValueTask<AsteriskAgent?> SelectAgentAsync(AsteriskQueue queue, CancellationToken ct);
    public virtual ValueTask<IReadOnlyList<AsteriskAgent>> RankAgentsAsync(
        AsteriskQueue queue, IReadOnlyList<AsteriskAgent> candidates, CancellationToken ct)
        => ValueTask.FromResult(candidates);
}

public abstract class SessionStoreBase
{
    public abstract ValueTask SaveAsync(CallSession session, CancellationToken ct);
    public abstract ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct);
    public virtual ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct)
        => ValueTask.FromResult(Enumerable.Empty<CallSession>());
    public virtual ValueTask DeleteAsync(string sessionId, CancellationToken ct)
        => ValueTask.CompletedTask;
}
```

Uses abstract base classes (not interfaces) for forward-compatible evolution — new virtual members can be added without breaking PRO implementations.

### 5.2 Default MIT Implementations

```csharp
internal sealed class PassthroughCallRouter : CallRouterBase { /* returns serverId as-is */ }
internal sealed class NativeAgentSelector : AgentSelectorBase { /* no-op, let Asterisk decide */ }
internal sealed class InMemorySessionStore : SessionStoreBase { /* ConcurrentDictionary */ }
```

All `internal` — registered as defaults in DI, user never instantiates directly.

### 5.3 PRO Override Pattern

```csharp
// Future: Asterisk.Sdk.Pro.Hosting
services.AddAsteriskSessions();
services.Replace(ServiceDescriptor.Singleton<SessionStoreBase, PostgresSessionStore>());
services.Replace(ServiceDescriptor.Singleton<CallRouterBase, ClusterRouter>());
```

---

## 6. DI Registration

### 6.1 New Extension Method

In `Asterisk.Sdk.Hosting`:

```csharp
public static IServiceCollection AddAsteriskSessions(
    this IServiceCollection services,
    Action<SessionOptions>? configure = null)
{
    services.AddSingleton<ICallSessionManager, CallSessionManager>();
    services.AddSingleton<CallRouterBase, PassthroughCallRouter>();
    services.AddSingleton<AgentSelectorBase, NativeAgentSelector>();
    services.AddSingleton<SessionStoreBase, InMemorySessionStore>();

    if (configure is not null)
        services.Configure(configure);

    // AOT-safe validation via [OptionsValidator] source generator (not reflection-based DataAnnotations)
    services.AddSingleton<IValidateOptions<SessionOptions>, SessionOptionsValidator>();
    services.AddOptions<SessionOptions>().ValidateOnStart();
    services.AddHostedService<SessionManagerHostedService>();

    return services;
}
```

### 6.2 Hosted Service

```csharp
internal sealed class SessionManagerHostedService : IHostedService
{
    // StartAsync: resolve AsteriskServer(s), call AttachToServer()
    // StopAsync: call DetachFromServer() + DisposeAsync()
}
```

Automatically registered by `AddAsteriskSessions()`. For multi-server: resolves `AsteriskServerPool` and attaches to each server.

---

## 7. Sprint 7-8: AgentSession + QueueSession

### 7.1 AgentSession

```csharp
public sealed class AgentSession
{
    public string AgentId { get; init; }
    public string? AgentInterface { get; set; }
    public AgentSessionState State { get; set; }
    public CallSession? CurrentCall { get; set; }

    // Statistics (accumulated since login)
    public int CallsHandled { get; set; }
    public int CallsMissed { get; set; }
    public TimeSpan TotalTalkTime { get; set; }
    public TimeSpan TotalHoldTime { get; set; }
    public TimeSpan TotalWrapTime { get; set; }
    public TimeSpan TotalIdleTime { get; set; }
    public DateTimeOffset? LastCallEndedAt { get; set; }

    // Computed
    public TimeSpan AvgTalkTime => CallsHandled > 0 ? TotalTalkTime / CallsHandled : TimeSpan.Zero;
    public TimeSpan AvgHandleTime => CallsHandled > 0 ? (TotalTalkTime + TotalWrapTime) / CallsHandled : TimeSpan.Zero;
    public TimeSpan StateElapsed => DateTimeOffset.UtcNow - LastStateChangeAt;
    public DateTimeOffset LastStateChangeAt { get; set; }

    // Thread safety (per-entity Lock, consistent with AsteriskAgent pattern)
    internal readonly Lock SyncRoot = new();
}

public enum AgentSessionState { Idle, OnCall, Wrapping, Paused }
```

**Wrap time:** Measured as interval between `CallEnded` and agent returning to `Available` state (observed via `QueueMemberStatus(NotInUse)`).

### 7.2 QueueSession

```csharp
public sealed class QueueSession
{
    public string QueueName { get; init; }

    // Rolling window metrics (configurable, default 30 min)
    public int CallsOffered { get; set; }
    public int CallsAnswered { get; set; }
    public int CallsAbandoned { get; set; }
    public int CallsTimedOut { get; set; }

    // SLA
    public double ServiceLevel { get; }             // % answered within threshold
    public TimeSpan SlaThreshold { get; init; }

    // Timing
    public TimeSpan AvgWaitTime { get; }
    public TimeSpan MaxWaitTime { get; }
    public TimeSpan AvgTalkTime { get; }

    // Current state
    public int CurrentWaiting { get; }
    public int CurrentAgentsAvailable { get; }

    // Per-agent breakdown
    public IReadOnlyDictionary<string, AgentQueueStats> AgentStats { get; }

    // Thread safety (per-entity Lock)
    internal readonly Lock SyncRoot = new();
}

public sealed record AgentQueueStats(
    int CallsHandled, int CallsMissed,
    TimeSpan AvgTalkTime, TimeSpan AvgHoldTime);
```

---

## 8. PbxAdmin Migration (Sprint 7-8)

### 8.1 Strategy

Migrate in order of increasing coupling:

| Phase | Page | Impact | Strategy |
|-------|------|--------|----------|
| A | AsteriskMonitorService | Low | Add `_callSessionManager.AttachToServer(server, id)` |
| B | Home | ~40% | Replace CallFlowTracker metrics with QueueSession + ActiveSessions |
| C | TrafficAnalytics | ~60% | Replace AllCalls with GetRecentCompleted(); use pre-computed WaitTime/TalkTime |
| D | Calls | ~70% | Replace ActiveCalls/Search/GetRecentCompleted with SDK equivalents |
| E | LadderDiagram | ~50% | Map CallFlowEvent → CallSessionEvent (compatible types) |
| F | New pages | New | SessionDetail, AgentDashboard, QueueDashboard |
| G | Cleanup | — | Delete CallFlowTracker, CallFlow, CallParticipant, CallFlowEvent, related models |

### 8.2 AsteriskMonitorService Changes

```csharp
// Constructor: inject ICallSessionManager
// StartAsync: after server.StartAsync(), call _sessionManager.AttachToServer(server, id)
// DisposeAsync: call _sessionManager.DetachFromServer(id)
// Remove: CallFlowTracker observer subscription (after all pages migrated)
```

### 8.3 New PbxAdmin Pages

| Page | Description | Data Source |
|------|-------------|-------------|
| SessionDetail | Timeline + participants + metrics + event log | CallSession by ID |
| AgentDashboard | Agent cards with current call, wrap time, SLA | AgentSession |
| QueueDashboard | SLA gauge, wait time, agent breakdown | QueueSession |

---

## 9. Project Structure

```
src/Asterisk.Sdk.Sessions/
├── Asterisk.Sdk.Sessions.csproj
├── CallSession.cs
├── CallSessionState.cs
├── CallSessionEvent.cs
├── CallDirection.cs
├── SessionParticipant.cs
├── ParticipantRole.cs
├── SessionDomainEvent.cs
├── Manager/
│   ├── ICallSessionManager.cs
│   ├── CallSessionManager.cs
│   ├── SessionOptions.cs
│   └── SessionReconciler.cs
├── Extensions/
│   ├── CallRouterBase.cs
│   ├── AgentSelectorBase.cs
│   └── SessionStoreBase.cs
├── Internal/
│   ├── PassthroughCallRouter.cs
│   ├── NativeAgentSelector.cs
│   ├── InMemorySessionStore.cs
│   ├── SessionCorrelator.cs
│   └── SessionOptionsValidator.cs  // [OptionsValidator] source-generated, AOT-safe
├── Exceptions/
│   ├── SessionException.cs
│   └── InvalidSessionStateTransitionException.cs
└── Diagnostics/
    └── SessionMetrics.cs
```

**Dependencies:**

```xml
<ProjectReference Include="../Asterisk.Sdk/Asterisk.Sdk.csproj" />
<ProjectReference Include="../Asterisk.Sdk.Ami/Asterisk.Sdk.Ami.csproj" />
<ProjectReference Include="../Asterisk.Sdk.Live/Asterisk.Sdk.Live.csproj" />
```

---

## 10. Metrics (SessionMetrics)

| Metric | Type | Description |
|--------|------|-------------|
| `sessions.active` | ObservableGauge | Active sessions count |
| `sessions.created` | Counter | Total sessions created |
| `sessions.completed` | Counter | Total sessions completed |
| `sessions.failed` | Counter | Total sessions failed |
| `sessions.timed_out` | Counter | Total sessions timed out |
| `sessions.orphaned` | Counter | Orphaned sessions detected by reconciler |
| `sessions.wait_time` | Histogram | Queue wait time (ms) |
| `sessions.talk_time` | Histogram | Talk time (ms) |
| `sessions.hold_time` | Histogram | Hold time (ms) |
| `sessions.duration` | Histogram | Total session duration (ms) |

---

## 11. Test Strategy

```
Tests/Asterisk.Sdk.Sessions.Tests/
├── CallSessionTests.cs              // State transitions, invalid transitions throw
├── CallSessionManagerTests.cs       // Correlation, event processing, lifecycle
├── SessionCorrelatorTests.cs        // LinkedId resolution, direction inference
├── SessionReconcilerTests.cs        // Orphan detection, timeout logic
├── AgentSessionTests.cs             // Wrap time, statistics accumulation
├── QueueSessionTests.cs             // SLA calculation, rolling window
├── InMemorySessionStoreTests.cs     // CRUD, eviction
└── Integration/
    └── SessionIntegrationTests.cs   // Docker Asterisk, end-to-end call flow
```

**Benchmarks (in Asterisk.Sdk.Benchmarks):**
- Session creation throughput
- Correlation lookup (by LinkedId, ChannelId, BridgeId)
- Reconciliation sweep with 10K/100K sessions

---

## 12. Sprint Breakdown

### Sprint 5-6: Live Layer Fixes + CallSession Core

1. BridgeManager + AsteriskBridge domain object
2. EventObserver: 10 new event cases
3. ChannelManager: 4 new methods + properties + events
4. AsteriskServer: expose BridgeManager, update reconnect
5. LiveMetrics: bridge metrics
6. Live layer tests for new events
7. CallSession, CallSessionState, SessionParticipant models
8. CallSessionManager with correlation + indices
9. SessionReconciler (orphan sweep)
10. SessionDomainEvent + IObservable emission
11. SessionMetrics
12. Unit tests for all Session classes
13. Integration tests with Docker

### Sprint 7-8: AgentSession + Extensions + PbxAdmin

1. AgentSession model + wrap time tracking
2. QueueSession model + SLA calculation + rolling window
3. Extension points: CallRouterBase, AgentSelectorBase, SessionStoreBase
4. Default implementations (Passthrough, Native, InMemory)
5. DI registration: AddAsteriskSessions()
6. SessionManagerHostedService
7. PbxAdmin migration (phases A-G)
8. New PbxAdmin pages: SessionDetail, AgentDashboard, QueueDashboard
9. Benchmarks
10. Publish v0.5.0-beta
