# Asterisk.Sdk.Sessions

Session Engine for the Asterisk.Sdk ecosystem. Provides call session correlation, lifecycle state machines, and domain events for real-time telephony monitoring.

## Features

- **CallSession** - Models the full lifecycle of a call: Created, Dialing, Ringing, Queued, Connected, OnHold, Transferring, Conference, Completed, Failed, TimedOut
- **CallSessionManager** - Automatic session creation and correlation by LinkedId, with 4-tier O(1) indexing
- **SessionReconciler** - Orphan detection and timeout handling for abandoned sessions
- **Domain Events** - Observable stream of CallStarted, CallConnected, CallQueued, CallHeld, CallEnded, CallFailed events
- **Extension Points** - Abstract base classes for custom routing (CallRouterBase), agent selection (AgentSelectorBase), and persistence (SessionStoreBase)
- **SessionMetrics** - System.Diagnostics.Metrics counters for sessions created, completed, failed, timed out

## Quick Start

```csharp
services.AddAsterisk(options => { /* AMI config */ });
services.AddAsteriskSessions(options =>
{
    options.InboundContextPatterns = ["from-external"];
    options.OutboundContextPatterns = ["from-internal"];
});

var sessionManager = app.Services.GetRequiredService<ICallSessionManager>();
sessionManager.Events.Subscribe(evt => Console.WriteLine(evt));
```

## Custom Persistence

```csharp
public class PostgresSessionStore : SessionStoreBase
{
    public override ValueTask SaveAsync(CallSession session, CancellationToken ct) { /* ... */ }
    public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) { /* ... */ }
}

services.AddSingleton<SessionStoreBase, PostgresSessionStore>();
services.AddAsteriskSessions();
```
