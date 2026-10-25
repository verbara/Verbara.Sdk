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

## Closing

Every ending the SDK starts on an open WebSocket sends a close frame first, and every close Asterisk sends is answered:

- **`DisconnectAsync`** sends its close before the events loop stops reading, then waits for Asterisk's answering close
  up to the existing 5-second bound, or until the token you passed is cancelled, whichever comes first. The token never
  suppresses the frame: a disconnect whose token is already cancelled (a host's stopping token in a `finally`) still
  sends it, then lets the connection go without waiting. A disconnect does not start a reconnect, even with
  `AutoReconnect` on.
- **A close Asterisk sends** is answered with one close before the socket is released and before any reconnect the
  `AutoReconnect` policy allows. With `AutoReconnect` off the client then reads `Disconnected`.
- **Disposing the client** while it reconnects (in its backoff, or in a dial the far end holds) ends with
  `Disconnected`, as a disposal of a connected client does.
- **`AriOutboundListener.StopAsync`** stops accepting, sends every open connection its close at the same time and waits
  for the answers under one shared one-second bound; the stop's token can end that wait but never suppresses a frame.
  A connection that has not answered by then is aborted before it is released. Every connection's end logs
  `Connection closed` (Debug); an idle timeout also logs `Idle timeout` (Warning); a stop, or a close bound that runs
  out during a stop or the connection's own disconnect, logs no error. Only a transport failure logs
  `Connection error` (Error).

What this costs, measured on Asterisk 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2 against the full functional test stack:

| Ending | Result | Latency |
|---|---|---|
| A client close on `/ari/events` (settled, right after connect, events flowing; 100 runs per cell, two independent runs) | Asterisk answered 900/900 in each run | answer p50 at most 0.19 ms, max 7.9 ms |
| A close sent to Asterisk's outbound WebSocket client, 1, 10 or 50 connections at once (10 rounds each, two independent runs) | Asterisk answered 1,830/1,830 in each run | answer max 6.5 ms with 50 connections |
| A generic host stopping 1 or 10 `AriConnectionHostedService` instances (20 stops each) | 660/660 sessions ended `Disconnected` | stop p50 0.16–0.19 ms (1), 0.55–0.65 ms (10), max 7.1 ms |

The bounds are there for an Asterisk, proxy or network that does not answer; none of these runs came near them.

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
