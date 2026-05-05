# Asterisk.Sdk.Ami

Asterisk Manager Interface (AMI) client for .NET 10 with Native AOT support.

## Features

- Full AMI protocol implementation (111 actions, 261 events, 17 response types)
- Zero-copy TCP parsing via `System.IO.Pipelines`
- Async event pump with configurable backpressure (`EventPumpCapacity`)
- MD5 challenge-response authentication
- Automatic reconnection with exponential backoff
- Source-generated serialization (no runtime reflection)
- `System.Diagnostics.Metrics` for observability

## Quick Start

```csharp
services.AddAsterisk(options =>
{
    options.AmiConnection.Hostname = "pbx.example.com";
    options.AmiConnection.Username = "admin";
    options.AmiConnection.Password = "secret";
});
```

```csharp
// Send an action
var response = await connection.SendActionAsync(new StatusAction());

// Subscribe to events
connection.Subscribe(Observer.Create<ManagerEvent>(evt =>
{
    Console.WriteLine($"Event: {evt.EventType}");
}));
```

## Documentation

- [High-Load Tuning Guide](../../docs/high-load-tuning.md)
- [Asterisk Version Compatibility](../../docs/asterisk-version-compatibility.md)
