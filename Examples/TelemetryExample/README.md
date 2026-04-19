# TelemetryExample

Minimal example showing how to consume every `ActivitySource` and `Meter` published by Asterisk.Sdk without hard-coding any names.

## What it demonstrates

- Iterating `AsteriskTelemetry.ActivitySourceNames` and `AsteriskTelemetry.MeterNames` (both `static readonly string[]`) to discover the full telemetry surface at runtime.
- Attaching a `System.Diagnostics.ActivityListener` that listens to exactly those sources.
- Attaching a `System.Diagnostics.Metrics.MeterListener` that subscribes to every instrument published by those meters.
- Printing spans and measurements to the console.

This is the lower-level path. In production you normally wire OpenTelemetry instead — see the snippet below.

## Run

```sh
dotnet run --project Examples/TelemetryExample/
```

Because the example does not `AddAsterisk(...)` itself, no spans or metrics will be emitted on their own. Add the SDK's DI extensions to see live traffic, or point the example at a separate process emitting the same sources.

## OpenTelemetry equivalent

Add the three OpenTelemetry packages to `Directory.Packages.props`:

```xml
<PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.10.0" />
<PackageVersion Include="OpenTelemetry.Exporter.Console" Version="1.10.0" />
<PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.10.0" />
```

Then replace the listeners with:

```csharp
using Asterisk.Sdk.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services
    .AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource([.. AsteriskTelemetry.ActivitySourceNames])
        .AddConsoleExporter()
        .AddOtlpExporter())              // send to Jaeger, Tempo, etc.
    .WithMetrics(m => m
        .AddMeter([.. AsteriskTelemetry.MeterNames])
        .AddConsoleExporter()
        .AddOtlpExporter());             // send to Prometheus, DataDog, etc.
```

The `[.. Collection]` spread syntax is important: `AddSource` / `AddMeter` each take a `params string[]`, so pass the discovered names as an array.

## Why the discovery pattern matters

Hard-coding the 9 `ActivitySource` names and 14 `Meter` names locks you to a specific SDK version. When future releases add new packages, you would need to update the consumer. Reading from `AsteriskTelemetry.*Names` keeps the consumer forward-compatible — the SDK is the single source of truth for what telemetry it publishes.

## See also

- [high-load-tuning.md](../../docs/guides/high-load-tuning.md) — sizing, metric definitions, alert thresholds.
- [troubleshooting.md — ActivitySource Diagnostics](../../docs/guides/troubleshooting.md) — which span represents which operation.
