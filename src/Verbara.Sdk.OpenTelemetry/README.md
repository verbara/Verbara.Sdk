# Asterisk.Sdk.OpenTelemetry

Batteries-included OpenTelemetry wiring for `Asterisk.Sdk`. One call enrolls every ActivitySource and Meter registered across the SDK and attaches a choice of Console, OTLP, and Prometheus exporters.

## Usage

```csharp
using Asterisk.Sdk.OpenTelemetry;

builder.Services.AddAsteriskOpenTelemetry(b => b
    .WithAllSources()                                      // enlist the 9 ActivitySources + 12 Meters
    .WithPrometheusExporter()                              // /metrics for scraping
    .WithOtlpExporter(o => o.Endpoint = new("http://tempo:4317")));

// then, on the ASP.NET Core pipeline:
app.UseOpenTelemetryPrometheusScrapingEndpoint();
```

The package layers on top of the standard OpenTelemetry SDK (`OpenTelemetry.Extensions.Hosting`) — consumers who need extras (samplers, views, custom processors) can access the raw builders via `ConfigureTracing` / `ConfigureMetrics`:

```csharp
builder.Services.AddAsteriskOpenTelemetry(b => b
    .WithAllSources()
    .ConfigureTracing(t => t.SetSampler(new TraceIdRatioBasedSampler(0.1)))
    .ConfigureMetrics(m => m.AddView("sessions.wait_time", new ExplicitBucketHistogramConfiguration {
        Boundaries = [50, 100, 250, 500, 1000, 2500]
    })));
```

## Related

- [docs/guides/session-store-backends.md](../../docs/guides/session-store-backends.md)
- `Examples/TelemetryExample/` — same telemetry signals via raw `ActivityListener` / `MeterListener` (no OpenTelemetry SDK dependency)
