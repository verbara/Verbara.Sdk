# Troubleshooting Guide

## Connection Issues

### AMI: Connection refused / timeout

**Symptoms:** `SocketException: Connection refused` or timeout on `ConnectAsync`.

**Checklist:**
1. Verify Asterisk is running: `asterisk -rx "core show version"`
2. Check AMI is enabled in `/etc/asterisk/manager.conf`:
   ```ini
   [general]
   enabled = yes
   port = 5038
   bindaddr = 0.0.0.0
   ```
3. Verify firewall allows port 5038
4. Check AMI user has correct permissions:
   ```ini
   [admin]
   secret = your_password
   read = all
   write = all
   ```
5. Increase `ConnectionTimeout` if network is slow:
   ```json
   { "AmiConnection": { "ConnectionTimeout": "00:00:10" } }
   ```

### AMI: Authentication failed

**Symptoms:** `AmiAuthenticationException` after connect.

**Checklist:**
1. Verify username/password match `manager.conf`
2. Check `deny`/`permit` ACL in the AMI user section
3. Reload manager config: `asterisk -rx "manager reload"`

### ARI: WebSocket connection failed

**Symptoms:** `WebSocketException` on `ConnectAsync`.

**Checklist:**
1. Verify ARI is enabled in `/etc/asterisk/ari.conf`:
   ```ini
   [general]
   enabled = yes

   [admin]
   type = user
   password = secret
   read_only = no
   ```
2. Check HTTP server in `/etc/asterisk/http.conf`:
   ```ini
   [general]
   enabled = yes
   bindaddr = 0.0.0.0
   bindport = 8088
   ```
3. Verify firewall allows port 8088
4. Reload: `asterisk -rx "ari reload"` and `asterisk -rx "http show status"`

---

## Event Issues

### Events dropped (AMI)

**Symptoms:** `[AMI_EVENT] Dropped` in logs, `ami.events.dropped` counter increasing.

**Cause:** Event pump buffer is full. Subscribers are processing events slower than they arrive.

**Solutions:**
1. Increase buffer capacity:
   ```json
   { "AmiConnection": { "EventPumpCapacity": 50000 } }
   ```
2. Speed up event handlers — offload heavy work to background queues
3. Filter high-volume events (e.g., `VarSet`) early in your observer
4. Monitor with: `dotnet-counters monitor --process-id <pid> Asterisk.Sdk.Ami`

See [High-Load Tuning Guide](high-load-tuning.md) for sizing recommendations.

### Events dropped (ARI)

**Symptoms:** `ari.events.dropped` counter increasing.

**Cause:** Same as AMI — ARI event pump buffer full.

**Solutions:** Same approach as AMI. ARI typically has lower event volume than AMI.

### Missing events

**Symptoms:** Expected events never arrive.

**Checklist:**
1. Verify AMI user has `read = all` (or specific classes like `read = call,agent,queue`)
2. Check ARI application name matches your Stasis app
3. Ensure you subscribe before the events fire (subscribe before `ConnectAsync` or use `ReplaySubject`)

---

## Reconnection Issues

### Infinite reconnection loop

**Symptoms:** Continuous reconnect attempts filling logs.

**Solution:** Set `MaxReconnectAttempts` to a finite value:
```json
{
  "AmiConnection": { "MaxReconnectAttempts": 10 },
  "AriClient": { "MaxReconnectAttempts": 10 }
}
```

### State lost after reconnect

**Symptoms:** After AMI reconnect, channels/agents/queues are empty.

**Expected behavior:** `AsteriskServer` clears and reloads all managers on reconnect via the `Reconnected` event. There may be a brief gap during reload.

---

## AOT / Trimming Issues

### JsonSerializer throws at runtime

**Symptoms:** `NotSupportedException: Serialization and deserialization of 'MyType' is not supported`.

**Cause:** Type not registered with `[JsonSerializable]` in the source-generated context.

**Solution:** Add `[JsonSerializable(typeof(MyType))]` to `AriJsonContext`.

### Source generator not running

**Symptoms:** Missing generated code, `CS0103` errors on generated types.

**Checklist:**
1. Ensure `<OutputItemType>Analyzer</OutputItemType>` is set in the generator project reference
2. Clean and rebuild: `dotnet clean && dotnet build`
3. Check generator output in `obj/Debug/net10.0/generated/`

### Trim warnings

**Symptoms:** `IL2026`, `IL2072` warnings during publish.

**Cause:** Code path uses reflection that the trimmer can't analyze.

**Solution:** The SDK is designed for zero-trim-warnings. If you see warnings from SDK code, please report it. For your own code, use `[DynamicDependency]` or source generators.

---

## Logging Configuration

Enable detailed logging for diagnostics:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Asterisk.Sdk.Ami": "Debug",
      "Asterisk.Sdk.Ari": "Debug",
      "Asterisk.Sdk.Live": "Debug"
    }
  }
}
```

For AMI protocol-level debugging (very verbose):
```json
{
  "Logging": {
    "LogLevel": {
      "Asterisk.Sdk.Ami.Connection": "Trace"
    }
  }
}
```

---

## ActivitySource Diagnostics (v1.9.0+)

**When to use:** correlating AMI auth failures with the specific action that triggered the reconnect, measuring per-utterance VoiceAi latency, tracing a session across AGI → Live → Sessions, or diagnosing event-pump lag under load without writing extra logging.

**Registered sources** (9 total):

| ActivitySource | Representative spans |
|----------------|----------------------|
| `Asterisk.Sdk.Ami` | connect / login / send-action / receive-event |
| `Asterisk.Sdk.Ari` | request / websocket-event |
| `Asterisk.Sdk.Agi` | session / command |
| `Asterisk.Sdk.Live` | manager-load / entity-update |
| `Asterisk.Sdk.Sessions` | session-start / state-transition / reconcile |
| `Asterisk.Sdk.Push` | publish / deliver / authorize |
| `Asterisk.Sdk.VoiceAi` | pipeline-session / stt-recognition / tts-synthesis |
| `Asterisk.Sdk.VoiceAi.AudioSocket` | inbound-connection / frame-roundtrip |
| `Asterisk.Sdk.VoiceAi.OpenAiRealtime` | realtime-session / turn |

Discover them at runtime without hard-coding names:

```csharp
using Asterisk.Sdk.Hosting;

builder.Services
    .AddOpenTelemetry()
    .WithTracing(t => t.AddSource([.. AsteriskTelemetry.ActivitySourceNames])
                       .AddOtlpExporter());  // or AddConsoleExporter()
```

**Quick capture without OpenTelemetry:**
```sh
dotnet-trace collect --process-id <pid> \
    --providers "System.Diagnostics.Metrics,Asterisk.Sdk.Ami,Asterisk.Sdk.VoiceAi"
```
Open the resulting `.nettrace` in PerfView or Chromium `about:tracing`.

**Common issue:** activities report zero spans. Cause: the `ActivitySource` has no listener — add the source to your OpenTelemetry `AddSource(...)` call or set `ActivityListener.ShouldListenTo`.

---

## Session Reconciliation Backpressure (v1.7.0+)

**Symptoms:**
- After an AMI reconnect, a large spike in `asterisk.sdk.sessions.state_changed` counter over 5-30 seconds.
- Transient climb of `asterisk.sdk.ami.events.dropped` during the same window.
- `SessionReconciliationService` background task logs a large batch of `Reconciling orphaned session ...` entries.

**Cause:** `SessionReconciliationService` runs every `SessionOptions.ReconciliationInterval` (default 30s). After a reconnect it has to re-scan all active sessions to detect orphans / timeouts; if the previous connection was lost with many sessions in flight, the scan enqueues a burst of state-change events.

**Solutions:**
1. **Increase EventPumpCapacity** to absorb the burst (see [high-load-tuning.md](high-load-tuning.md)):
   ```json
   { "AmiConnection": { "EventPumpCapacity": 50000 } }
   ```
2. **Stagger reconciliation** if the burst is disruptive — lengthen the interval and accept slightly slower orphan detection:
   ```json
   { "Sessions": { "ReconciliationInterval": "00:01:00" } }
   ```
3. **Opt out entirely** in non-critical deployments by not registering `SessionReconciliationService` (skip `AddSessions(reconcile: true)` and call the reconciler manually on demand).

**Observability:** watch the `Asterisk.Sdk.Sessions` activity source — `reconcile` spans carry a `sessions.scanned` tag so you can correlate burst size with reconnect events.
