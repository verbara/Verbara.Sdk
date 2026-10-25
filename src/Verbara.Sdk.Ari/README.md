# Verbara.Sdk.Ari

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

The token you hand `ConnectAsync` bounds the dial only: cancelling it once the connect has returned (a host's start
or stopping token, a request token) does not end the connection, its events or a reconnect. End the connection with
`DisconnectAsync` or by disposing the client.

## Connection attempts

One `ConnectAsync` call or automatic reconnect attempt can open more than one TCP connection within the existing
five-second connect bound. The .NET HTTP transport can retry a connection that closes before an upgrade response;
these transport retries are separate from the ARI client's automatic reconnect policy.

With .NET SDK 10.0.402 and runtime 10.0.13, twenty independent failed caller dials per loopback-peer shape recorded:

| Peer behavior | TCP connections per caller dial |
|---|---:|
| Accept, then close immediately with FIN | 4 |
| Accept, then reset immediately with RST | 1–4 |
| Reply HTTP 401 or HTTP 503 | 1 |

These are measured framework-specific counts, pinned by `AriDialConnectionCountTests`; a framework upgrade may
change them. They are not a runtime-independent limit for every network. Measurements against Asterisk on the same
framework also found one TCP connection for a wrong password or direct refusal, and one to four when a proxy accepted
and closed while Asterisk was stopped. Additional starts matched HTTP transport retry events; no extra SDK retry layer
was observed. The client does not suppress or replace the runtime's transport retries.

## Documentation

- [High-Load Tuning Guide](../../docs/guides/high-load-tuning.md)
- [Asterisk Version Compatibility](../../docs/guides/asterisk-version-compatibility.md)
