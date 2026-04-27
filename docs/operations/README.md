# Operations — Observability starter kit

Prefabricated Grafana dashboards and Jaeger query examples for the SDK's OpenTelemetry catalog (15 meters + 9 `ActivitySource`s — see `AsteriskTelemetry.MeterNames` / `.ActivitySourceNames` in `Asterisk.Sdk` for the canonical list). These are **starter templates**, not production-tuned dashboards — copy into your Grafana instance and adapt thresholds / labels / queries to your deployment.

## Contents

| File | Purpose |
|---|---|
| [`dashboards/sdk-overall.json`](dashboards/sdk-overall.json) | Bird's-eye overview of the SDK runtime — AMI connection health, ARI WebSocket state, Push event rates, reconnect counters. |
| [`dashboards/webhook-delivery.json`](dashboards/webhook-delivery.json) | `Asterisk.Sdk.Push.Webhooks` delivery pipeline — succeeded / failed / retried / dead-letter + per-URL circuit breaker (opened / skipped). |
| [`dashboards/resilience.json`](dashboards/resilience.json) | `Asterisk.Sdk.Resilience` meter — retry attempts, circuit opened / closed, timeouts fired, circuit state gauge. |
| [`jaeger-queries.md`](jaeger-queries.md) | Query patterns for every SDK `ActivitySource` with example tag filters using `AsteriskSemanticConventions`. |

## Prerequisites

Consumers must have enrolled the SDK's telemetry catalog:

```csharp
builder.Services.AddAsteriskOpenTelemetry()
    .WithAllSources()          // enrols 9 ActivitySources + 15 meters
    .WithOtlpExporter();
```

The dashboards assume a Prometheus data source exposing metrics via OTLP export, and Jaeger / Tempo for traces. Datasource UIDs in the JSON are placeholders (`${DS_PROMETHEUS}`); set them to your environment's provisioned UID.

## Import

### Grafana (UI)

1. Navigate to **Dashboards → Import**.
2. Paste the contents of one of the JSON files.
3. Select your Prometheus data source when prompted.
4. Save.

### Grafana (CLI / provisioning)

Copy the JSON files into your Grafana provisioning directory (typically `/etc/grafana/provisioning/dashboards/`) and reference them from your `dashboards.yaml` provider.

## Limitations

- Dashboards use the default OTLP Prometheus exporter label shape. If you have re-labeled metrics via OTel collector transforms, adjust the queries accordingly.
- Thresholds and alert rules are not included — each deployment defines its own SLOs.
- The `sdk-overall` dashboard doesn't yet cover Pro-only meters (cluster health, dialer inflight, agent-assist LLM gauges). A separate Pro dashboard pack ships with the Pro package.

## Feedback

If a panel query is wrong for your metric shape, open an issue describing the label set you observe vs. what the dashboard expects — queries are easy to patch and we want these starter dashboards to be correct out of the box.
