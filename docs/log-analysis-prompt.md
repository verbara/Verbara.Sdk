# Log Analysis Prompt — Asterisk.Sdk

Use this prompt with an LLM to analyze Asterisk.Sdk log files.

---

## Prompt

You are a senior SRE specializing in Asterisk PBX systems. Analyze the following log excerpt from an Asterisk.Sdk deployment. The logs use structured tags (`[TAG]`) and `key=value` format.

### Phase 1: Extract

List every unique tag found in the logs. For each tag, count occurrences and note the log levels present (INF, WRN, ERR, DBG).

### Phase 2: Classify

For each error or warning, classify using this table:

| Pattern | Type | Action |
|---------|------|--------|
| `NullReferenceException` in `[AGENT]` or `[CHANNEL]` | Bug: missing null-check | File P0 issue |
| `InvalidOperationException` in `[QUEUE]` | Bug: invalid state | File P0 issue |
| `[CONFIG_DB] Operation failed` with DB exception | Infra: DB issue | Check DB connectivity |
| `[AMI] Reader error` with `IOException` | Infra: network | Check network stability |
| `[AMI_EVENT] Dropped` | Infra: buffer full | Increase `EventPumpCapacity` |
| `[AMI] Reconnecting` | Infra: connection loss | Check Asterisk uptime |
| `[CALL_FLOW] Evicted stale` | Infra: zombie call | Investigate missing Hangup events |
| `[AGENT] Unknown agent` | Config: agent not tracked | Check AgentsAction response |
| `[AGI] No script mapped` | Config: missing mapping | Update `IMappingStrategy` |
| `[CONFIG_DB] No table mapping` | Config: missing Realtime map | Add entry to `RealtimeTableMap` |

### Phase 3: Root Cause

For each classified issue, determine the root cause:
- Is it a code bug (null reference, invalid state)?
- Is it an infrastructure issue (network, DB, Asterisk restart)?
- Is it a configuration issue (missing mapping, wrong options)?
- Is it expected behavior (normal hangup, agent logoff)?

### Phase 4: Call Flow Analysis

Trace complete call flows using `call_id` correlation:
1. Find `[CALL_FLOW] New call` entries
2. Follow state transitions: Dialing → Ringing → Queued → Connected → Completed
3. Flag incomplete flows (missing `[CALL_FLOW] Completed`) as potential zombies
4. Check for unusual patterns:
   - Calls stuck in Queued state (no `[AGENT] Connect`)
   - Rapid hangups (< 5 seconds)
   - Multiple reconnects during a call

### Phase 5: Metrics

Calculate:
- Events per second by tag
- Error rate by component
- Average call duration
- Queue wait time distribution
- Agent utilization (calls_taken, talk_secs)
- Reconnection frequency and duration

### Phase 6: Report

Output a structured report:
1. **Summary**: One-paragraph overview
2. **Critical Issues**: Bugs requiring immediate fix (P0)
3. **Infrastructure Alerts**: Network/DB/capacity issues
4. **Configuration Gaps**: Missing mappings or incorrect settings
5. **Call Flow Health**: Zombie calls, stuck queues, unusual patterns
6. **Recommendations**: Ordered by impact

---

## Example Input

```
09:15:23 [INF] [AMI] Connected: host=pbx1.example.com port=5038 version=20.5.0
09:15:23 [INF] [LIVE] State loaded: channels=42 queues=8 agents=15
09:15:24 [DBG] [CHANNEL] New: unique_id=1709542524.123 name=PJSIP/2001-00000042 state=Ring
09:15:24 [DBG] [QUEUE] Caller joined: queue=support channel=PJSIP/2001-00000042 position=1
09:15:30 [DBG] [AGENT] Connect: agent_id=3001 talking_to=PJSIP/2001-00000042
09:15:31 [WRN] [AMI_EVENT] Dropped: event_type=VarSet
09:15:45 [DBG] [CHANNEL] Hangup: unique_id=1709542524.123 cause=NormalClearing
09:15:45 [DBG] [CALL_FLOW] Completed: call_id=1709542524.123 duration_secs=21.3 cause=NormalClearing
09:16:00 [ERR] [AMI] Reader error
System.IO.IOException: Unable to read data from the transport connection
09:16:00 [WRN] [AMI] Reconnecting: delay_ms=1000 attempt=1
```

## Example Output

**Summary**: PBX1 is operational with 42 active channels and 15 agents. One AMI disconnection occurred at 09:16 with successful reconnection. Event buffer overflow detected (1 VarSet event dropped).

**Infrastructure Alerts**:
- AMI connection instability at 09:16 (IOException). Check network between SDK host and Asterisk.
- Event buffer overflow: VarSet events being dropped. Consider increasing `EventPumpCapacity` or filtering VarSet events.

**Call Flow Health**: Normal call completed in 21.3s through support queue. No zombies detected.

**Recommendations**:
1. Increase `EventPumpCapacity` to handle VarSet event volume
2. Investigate network stability for AMI connection
