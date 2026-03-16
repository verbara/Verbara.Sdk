# High-Load Tuning Guide

> Guidance for configuring Asterisk.Sdk in high-load scenarios (1K-100K+ agents).

---

## EventPump Sizing

Both AMI (`AsyncEventPump`) and ARI (`AriEventPump`) use bounded `Channel<T>` buffers. When the buffer fills, new events are **dropped** and counted via metrics.

### Recommended `EventPumpCapacity` by Scale

| Agents | Events/sec (est.) | `EventPumpCapacity` | RAM per buffer (est.) |
|--------|-------------------|---------------------|-----------------------|
| 100 | ~50-200 | 20,000 (default) | ~5 MB |
| 1,000 | ~500-2,000 | 20,000 (default) | ~5 MB |
| 10,000 | ~5,000-20,000 | 50,000 | ~12 MB |
| 100,000 | ~50,000-200,000 | 100,000-200,000 | ~25-50 MB |

> **Rule of thumb:** Set capacity to handle ~10 seconds of peak event volume. At 100K agents, a queue storm can generate 200K events/sec for a few seconds.

### Configuration

```json
{
  "AmiConnection": {
    "EventPumpCapacity": 50000
  }
}
```

```csharp
services.AddAsterisk(options =>
{
    options.AmiConnection.EventPumpCapacity = 50_000;
});
```

---

## Metrics to Monitor

Use `dotnet-counters`, OpenTelemetry, or Prometheus to track these metrics.

### AMI Metrics (`Asterisk.Sdk.Ami`)

| Metric | Type | Alert Threshold | Description |
|--------|------|-----------------|-------------|
| `ami.events.received` | Counter | — | Total events received from Asterisk |
| `ami.events.dropped` | Counter | > 0 | Events dropped due to full buffer. **Action:** increase `EventPumpCapacity` |
| `ami.events.dispatched` | Counter | — | Events successfully dispatched to observers |
| `ami.event.dispatch` | Histogram (ms) | p99 > 50ms | Time to dispatch one event. High values indicate slow observers |
| `ami.action.roundtrip` | Histogram (ms) | p99 > 2000ms | Action send-to-response time. High values indicate Asterisk overload |
| `ami.reconnections` | Counter | > 0 | Connection drops. Investigate network or Asterisk stability |

### ARI Metrics (`Asterisk.Sdk.Ari`)

| Metric | Type | Alert Threshold | Description |
|--------|------|-----------------|-------------|
| `ari.events.received` | Counter | — | Total WebSocket events received |
| `ari.events.dropped` | Counter | > 0 | Events dropped due to full buffer |
| `ari.events.dispatched` | Counter | — | Events dispatched to observers |
| `ari.event.dispatch` | Histogram (ms) | p99 > 50ms | Event dispatch time |
| `ari.rest.roundtrip` | Histogram (ms) | p99 > 5000ms | REST API roundtrip time |
| `ari.reconnections` | Counter | > 0 | WebSocket reconnection attempts |

### Monitoring Commands

```sh
# Real-time AMI metrics
dotnet-counters monitor --process-id <pid> Asterisk.Sdk.Ami

# Real-time ARI metrics
dotnet-counters monitor --process-id <pid> Asterisk.Sdk.Ari

# Both meters simultaneously
dotnet-counters monitor --process-id <pid> Asterisk.Sdk.Ami Asterisk.Sdk.Ari
```

---

## PipelineSocketConnection Backpressure

The AMI TCP layer uses `System.IO.Pipelines` with built-in backpressure:

| Parameter | Value | Effect |
|-----------|-------|--------|
| `pauseWriterThreshold` | 1 MB | Pipe pauses reads when buffer exceeds 1 MB |
| `resumeWriterThreshold` | 512 KB | Pipe resumes reads when buffer drains to 512 KB |
| `minimumSegmentSize` | 4 KB | Minimum buffer allocation unit |
| Memory pool | `MemoryPool<byte>.Shared` | Pooled allocations, reduces GC pressure |

These values are hardcoded and suitable for most scenarios. At 100K+ agents, the bottleneck is typically the event pump dispatch speed, not the TCP pipe buffer.

---

## Reconnection Tuning

Both AMI and ARI support exponential backoff reconnection.

### AMI (`AmiConnectionOptions`)

```json
{
  "AmiConnection": {
    "AutoReconnect": true,
    "MaxReconnectAttempts": 0,
    "ReconnectInitialDelay": "00:00:01",
    "ReconnectMaxDelay": "00:00:30",
    "ReconnectMultiplier": 2.0
  }
}
```

### ARI (`AriClientOptions`)

```json
{
  "AriClient": {
    "AutoReconnect": true,
    "MaxReconnectAttempts": 0,
    "ReconnectInitialDelay": "00:00:01",
    "ReconnectMaxDelay": "00:00:30",
    "ReconnectMultiplier": 2.0
  }
}
```

> `MaxReconnectAttempts = 0` means unlimited. Set to a positive value (e.g., 10) to prevent infinite loops in production.

---

## Example: 10K Agent Configuration

```json
{
  "AmiConnection": {
    "Hostname": "pbx.example.com",
    "Port": 5038,
    "Username": "sdk",
    "Password": "secret",
    "EventPumpCapacity": 50000,
    "AutoReconnect": true,
    "MaxReconnectAttempts": 10,
    "ReconnectInitialDelay": "00:00:01",
    "ReconnectMaxDelay": "00:00:30",
    "DefaultResponseTimeout": "00:00:05"
  }
}
```

## Example: 100K Agent Configuration (Multi-Server)

At 100K+ agents, use `AsteriskServerPool` to distribute load across multiple Asterisk servers:

```csharp
services.AddAsterisk(options =>
{
    options.AmiConnection.EventPumpCapacity = 200_000;
    options.AmiConnection.MaxReconnectAttempts = 20;
    options.AmiConnection.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
});
```

Key considerations:
- **Multi-server:** Use `AsteriskServerPool` to federate N servers with agent routing
- **Observer speed:** Keep event handlers fast (< 10ms). Offload heavy work to background queues
- **VarSet filtering:** `VarSet` events can be 50%+ of total volume. Filter early in observers
- **GC tuning:** Consider `ServerGC` and `gcServer=true` in `runtimeconfig.json`

```json
{
  "runtimeOptions": {
    "configProperties": {
      "System.GC.Server": true,
      "System.GC.Concurrent": true
    }
  }
}
```
