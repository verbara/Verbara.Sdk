# Log Analysis Reference — Asterisk.Sdk

## Tag Catalog

### SDK Tags (11)

| Tag | Domain | Class(es) | Events |
|-----|--------|-----------|--------|
| `[AMI]` | AMI connection | `AmiConnectionLog` | Connect, disconnect, reconnect, reader error |
| `[AMI_EVENT]` | AMI events | `AmiConnectionLog` | Event received, dropped |
| `[AMI_ACTION]` | AMI actions | `AmiConnectionLog` | Response received |
| `[LIVE]` | Live state | `AsteriskServerLog` | Initial state, reconnect reload |
| `[CHANNEL]` | Channels | `ChannelManagerLog` | New, state change, hangup, rename, link/unlink |
| `[QUEUE]` | Queues | `QueueManagerLog` | Params, member add/remove/pause/status, caller join/leave |
| `[AGENT]` | Agents | `AgentManagerLog` | Login, logoff, connect, complete, pause |
| `[CONFERENCE]` | Conferences | `MeetMeManagerLog` | Join, leave |
| `[AGI]` | FastAGI | `FastAgiServerLog` | Server start/stop, script map, connection |
| `[ARI]` | ARI client | `AriClientLog` | Connect, disconnect, WS event, reconnect |
| `[POOL]` | Multi-server | `AsteriskServerPool` | Server added/removed (future) |

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
| `[AMI_EVENT] Dropped` | Infra: event buffer full (tune `EventPumpCapacity`) | — |
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
