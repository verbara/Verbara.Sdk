# Asterisk.Sdk

Core abstractions for the [Asterisk.Sdk](https://github.com/Harol-Reina/Asterisk.Sdk) ecosystem — shared interfaces, base types, telemetry catalogs, and source-generator attributes consumed by every other package in the family. Native AOT, zero reflection, MIT licensed.

## What it does

This package is referenced (transitively) by every other `Asterisk.Sdk.*` package. Most consumers do not install it directly — they install `Asterisk.Sdk.Hosting` or one of the protocol-specific packages (`Asterisk.Sdk.Ami`, `Asterisk.Sdk.Ari`, `Asterisk.Sdk.Agi`) and pick this one up automatically. Install it explicitly only when building a custom package that needs the abstractions without the protocol implementations.

### Public surface

- **AMI core types** — `IAmiConnection`, `IAmiConnectionFactory`, `ManagerAction`, `ManagerEvent`, `ManagerResponse`, `IEventListener` and the protocol-defined enums consumed by source generators in `Asterisk.Sdk.Ami`.
- **AGI / ARI base types** — protocol-shared enums and base records mirrored across `Asterisk.Sdk.Agi` and `Asterisk.Sdk.Ari`.
- **`AsteriskSemanticConventions`** — public static catalog of **60 const strings across 14 nested classes** (`Resource`, `Channel`, `Bridge`, `Calls`, `Dialplan`, `Sip`, `Media`, `Queues`, `Agent`, `VoiceAi`, `Events`, `Tenant`, `Event`, `Node`). Use these as `Activity.SetTag(...)` keys instead of hard-coded strings so dashboards survive SDK version bumps. Pinned by 14+ unit tests.
- **`AsteriskTelemetry`** — runtime-discoverable lists of every `ActivitySourceName` (9) and `MeterName` (15) shipped by the SDK family. Plug into `OpenTelemetry` with one call: `tracerBuilder.AddSource([.. AsteriskTelemetry.ActivitySourceNames])`.
- **Source-generator attributes** — `[ManagerActionAttribute]`, `[ManagerEventAttribute]`, `[JsonSerializable(typeof(...))]` markers that drive the four Roslyn source generators in `Asterisk.Sdk.Ami.SourceGenerators`. Replace runtime reflection entirely.

## Install

```sh
dotnet add package Asterisk.Sdk
```

Most consumers will instead want:

```sh
dotnet add package Asterisk.Sdk.Hosting   # meta-package: Sdk + Ami + Agi + Ari + Live + Activities + Sessions + Config + DI
```

## OpenTelemetry one-liner

```csharp
using Asterisk.Sdk;          // AsteriskTelemetry
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource([.. AsteriskTelemetry.ActivitySourceNames]).AddOtlpExporter())
    .WithMetrics(m => m.AddMeter([.. AsteriskTelemetry.MeterNames]).AddOtlpExporter());
```

For the batteries-included variant (Console + OTLP + Prometheus exporters wired automatically):

```sh
dotnet add package Asterisk.Sdk.OpenTelemetry
```

```csharp
builder.Services.AddAsteriskOpenTelemetry().WithAllSources();
```

## Native AOT

Zero runtime reflection. All serialization paths use Roslyn source generators (`ActionSerializerGenerator`, `EventDeserializerGenerator`, `EventRegistryGenerator`, `ResponseDeserializerGenerator`). Trim-safe (`<IsTrimmable>true</IsTrimmable>`); 0 trim warnings across the package family. See [ADR-0001](https://github.com/Harol-Reina/Asterisk.Sdk/blob/main/docs/decisions/0001-native-aot-first.md) and [ADR-0003](https://github.com/Harol-Reina/Asterisk.Sdk/blob/main/docs/decisions/0003-source-generators-over-reflection.md) for design rationale.

## License

MIT. Part of the [Asterisk.Sdk](https://github.com/Harol-Reina/Asterisk.Sdk) project.
