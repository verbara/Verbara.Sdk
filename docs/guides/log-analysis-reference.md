# Log Analysis Reference — Verbara.Sdk

## Tag Catalog

### SDK Tags (11)

| Tag | Domain | Class(es) | Events |
|-----|--------|-----------|--------|
| `[AMI]` | AMI connection | `AmiConnectionLog` | Connect, disconnect, reconnect, reconnect gave up, reader error, notification handler error, notification handler that has not returned |
| `[AMI_EVENT]` | AMI events | `AmiConnectionLog` | Event received, dropped, discarded on caller ending, `OnEvent` handler fault, observer fault |
| `[AMI_ACTION]` | AMI actions | `AmiConnectionLog` | Response received |
| `[LIVE]` | Live state | `VerbaraServerLog` | Initial state, reconnect reload, `Status` refused |
| `[CHANNEL]` | Channels | `ChannelManagerLog` | New, state change, hangup, rename, link/unlink |
| `[QUEUE]` | Queues | `QueueManagerLog` | Params, member add/remove/pause/status, caller join/leave |
| `[AGENT]` | Agents | `AgentManagerLog` | Login, logoff, connect, complete, pause |
| `[CONFERENCE]` | Conferences | `MeetMeManagerLog` | Join, leave, mute, talking |
| `[AGI]` | FastAGI | `FastAgiServerLog` | Server start/stop, script map, connection |
| `[ARI]` | ARI client | `AriClientLog` | Connect, disconnect, WS event, reconnect |
| `[POOL]` | Multi-server | `VerbaraServerPoolLog` | Server dispose failed, AMI connection dispose failed (Error, with the server id and the exception; the pool's disposal, a removal or a failed add logs it and goes on) |

### Dashboard Tags (8)

| Tag | Domain | Class(es) | Events |
|-----|--------|-----------|--------|
| `[MONITOR]` | Monitor service | `MonitorServiceLog` | Connect, disconnect, reconnect |
| `[CONFIG_AMI]` | Config via AMI | `PbxConfigLog` | CRUD operations, command exec |
| `[CONFIG_DB]` | Config via DB | `DbConfigLog` | CRUD operations, table mapping |
| `[TRUNK]` | Trunk management | `TrunkServiceLog` | CRUD, status merge |
| `[CALL_FLOW]` | Call tracking | `CallFlowLog` | New call, state transitions, hangup, eviction |
| `[EVENT_LOG]` | Event log | `EventLogServiceLog` | Event capture (debug) |
| `[DI]` | DI registration | Program.cs | Provider type selection |
| `[STARTUP]` | Startup | Program.cs | Configuration loaded |

## Classification by Type

| Pattern | Type | Priority |
|---------|------|----------|
| `NullReferenceException` in `[AGENT]` or `[CHANNEL]` | Bug: missing null-check | P0 |
| `InvalidOperationException` in `[QUEUE]` | Bug: invalid state | P0 |
| `[CONFIG_DB] Operation failed` with `NpgsqlException` | Infra: DB unavailable | — |
| `[AMI] Reader error` with `IOException` | Infra: unstable network | — |
| `[AMI] State-change handler error` | Bug: an application `StateChanged` handler threw (exception attached); the handlers after it still ran | P1 |
| `[AMI] A <Event> handler has not returned after <n> s` | Bug: an application `StateChanged`, `ConnectionLost` (`Lost`) or `Reconnected` handler blocks; later notifications, the Live reload included, wait for it | P1 |
| `[AMI] Reconnect gave up after <n> attempts; the connection will not come back` | Infra: the reconnect reached `MaxReconnectAttempts` and the connection stays `Disconnected` (last attempt's exception attached; `AmiAuthenticationException` means rejected credentials) | P0 |
| `[AMI_EVENT] Dropped` | Infra: event buffer full (tune `EventPumpCapacity`) | — |
| `[AMI_EVENT] Discarded on caller ending` | Expected: the caller ended the connection with events buffered | Ignore |
| `[AMI_EVENT] OnEvent handler threw on <EventType>` | Bug: an application `OnEvent` handler failed (exception attached); delivery went on | P1 |
| `[AMI_EVENT] Observer threw on <EventType>` | Bug: an application observer's `OnNext` failed (exception attached); it stays subscribed and delivery went on. A session subscriber's failure no longer shows here (up to 2.8.x it surfaced through the live state's own AMI observer); it has its own line, below | P1 |
| `A subscriber of the session events threw on <EventType> [for session <SessionId>]; the other subscribers and the session engine went on` | Bug: an application subscriber of `ICallSessionManager.Events` failed (exception attached; `completion` at disposal carries no session); the other subscribers and the engine went on; counted on `sessions.events.subscriber_faults`. A subscriber subscribed directly stays subscribed; one composed through an operator (`Where`, `Select`, `OfType`, …) is gone after this first failure | P1 |
| `A subscriber of the agent state changes threw on the change of agent <AgentId> from <PreviousState> to <NewState>; …` | Bug: an application subscriber of `IAgentSessionTracker.StateChanges` failed (exception attached); the same report and the same caveat as the session line above | P1 |
| `[POOL] Server dispose failed` or `[POOL] AMI connection dispose failed` | Bug: a server's ARI client or an AMI connection threw while the pool released it (exception attached); the pool still released everything else, and the call it came from (`DisposeAsync`, `RemoveServerAsync`, or a failed `AddServerAsync`, which rethrows its start's exception) never throws this one | P1 |
| `[LIVE] Status refused` | Config: the AMI user may not run `Status` (needs `system`, `call` or `reporting` in `write`); no channel was reconciled | — |
| `[QUEUE] Caller left` without Exception | Expected: caller hung up | Ignore |
| `[AGENT] Logoff` without Exception | Expected: agent disconnected | Ignore |
| `[CALL_FLOW] Completed` without Exception | Expected: call ended | Ignore |

## Expected Call Sequence

A normal call flow produces this log sequence (used for zombie detection):

```
[CALL_FLOW] New call: call_id=X ...
[CHANNEL] New: ...
[QUEUE] Caller joined: ...
[AGENT] Connect: ...
[CALL_FLOW] State changed: ... state=Connected
[CHANNEL] Hangup: ...
[CALL_FLOW] Completed: ...
```

## Quick Extraction Commands

```bash
# Heat map by tag
jq -r '.RenderedMessage' logs/dashboard-*.json | grep -oP '\[[A-Z_]+\]' | sort | uniq -c | sort -rn

# Errors by component
jq 'select(.Level == "Error") | .RenderedMessage' logs/dashboard-*.json | grep -oP '\[[A-Z_]+\]' | sort | uniq -c | sort -rn

# Zombie calls (no Completed after New call)
jq -r 'select(.RenderedMessage | test("\\[CALL_FLOW\\] New call")) | .Properties.CallId' logs/dashboard-*.json | sort > /tmp/new_calls.txt
jq -r 'select(.RenderedMessage | test("\\[CALL_FLOW\\] Completed")) | .Properties.CallId' logs/dashboard-*.json | sort > /tmp/completed.txt
comm -23 /tmp/new_calls.txt /tmp/completed.txt

# Servers with most errors
jq 'select(.Level == "Error") | .Properties.ServerId' logs/dashboard-*.json | sort | uniq -c | sort -rn

# Events dropped per hour
jq -r 'select(.RenderedMessage | test("\\[AMI_EVENT\\] Dropped")) | .Timestamp[:13]' logs/dashboard-*.json | sort | uniq -c

# Agent login/logoff timeline
jq -r 'select(.RenderedMessage | test("\\[AGENT\\] (Login|Logoff)")) | "\(.Timestamp) \(.RenderedMessage)"' logs/dashboard-*.json

# Queue caller wait time analysis
jq -r 'select(.RenderedMessage | test("\\[QUEUE\\] Caller (joined|left)")) | "\(.Timestamp) \(.RenderedMessage)"' logs/dashboard-*.json
```
