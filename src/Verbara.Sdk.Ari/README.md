# Asterisk.Sdk.Ari

Asterisk REST Interface (ARI) client for .NET 10 with Native AOT support.

## Features

- REST client for all 8 ARI resources (Channels, Bridges, Playbacks, Recordings, Endpoints, Applications, Sounds, DeviceStates)
- WebSocket event streaming with 46 event types
- AOT-safe JSON serialization via `System.Text.Json` source generation
- Automatic reconnection with exponential backoff
- AudioSocket server for real-time audio streaming
- `System.Diagnostics.Metrics` for observability

## Quick Start

```csharp
var client = new AriClient(Options.Create(new AriClientOptions
{
    BaseUrl = "http://pbx.example.com:8088",
    Username = "admin",
    Password = "secret",
    Application = "myapp"
}), logger);

await client.ConnectAsync();

// Subscribe to events
client.Subscribe(Observer.Create<AriEvent>(evt =>
{
    if (evt is StasisStartEvent start)
        Console.WriteLine($"Channel entered Stasis: {start.Channel?.Name}");
}));

// REST operations
var channels = await client.Channels.ListAsync();
var bridge = await client.Bridges.CreateAsync("mixing", "mybridge");
await client.Bridges.AddChannelAsync(bridge.Id!, channels[0].Id!);
```

## Documentation

- [High-Load Tuning Guide](../../docs/high-load-tuning.md)
- [Asterisk Version Compatibility](../../docs/asterisk-version-compatibility.md)
