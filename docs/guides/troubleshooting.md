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
