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

## REST-only clients

A client you use only for REST calls can stay off the events WebSocket:

```csharp
var client = new AriClient(Options.Create(new AriClientOptions
{
    BaseUrl = "http://pbx.example.com:8088",
    Username = "admin",
    Password = "secret",
    Application = "myapp",
    EventsEnabled = false
}), logger);
```

With `EventsEnabled = false`, or `Asterisk:Ari:EventsEnabled` set to `false` through `AddVerbara`, the client opens no
events WebSocket, registers no Stasis application, runs no reconnect loop and keeps no event buffer, so it cannot replace
another client's application. The client reads the option once, when it is built. On such a client:

- `State` reads `RestOnly` until the client is disposed, and `IsConnected` reads `false`.
- `ConnectAsync` and `DisconnectAsync` do nothing and need no Asterisk, also through
  `IAriClientFactory.CreateAndConnectAsync`.
- `Subscribe` throws `InvalidOperationException` naming the option, so code that waits for events fails where the
  mistake is.
- The `ari` health check asks. Each run sends one `GET /ari/asterisk/ping`, bounded at 5 s: an answer reads `Healthy`
  and anything else reads `Unhealthy`, on that run. A client with events sends nothing from this check.

The option's name says less than it takes away. Measured with no WebSocket open, on Asterisk 20.20.1, 22.11.0, 22.12.0-rc2,
23.5.0, 23.6.0-rc2 and 24.0.0-rc2, 18 times per operation:

| What you do | What Asterisk does |
|---|---|
| `hold`/unhold a channel in **another** client's Stasis application | `204` 18/18, with `ChannelHold` seen on the other leg |
| `hold` a channel in the dialplan, in no Stasis application | `409 Channel not in Stasis application` 18/18, `AriConflictException` here |
| Originate, `GET`, a variable and `DELETE` on a dialplan channel | 2xx 18/18 |
| Originate into an application nobody registered | `200` with state `Down` 18/18, then the channel is gone within 2 ms |

- So this client controls channels another client registered, and dialplan channels only for those four operations. The
  `409` is ARI's own rule and not the missing socket: the same client, in the same state, gets `204` on a channel in
  Stasis.
- `hold` was measured with this client only: a `hold` outside Stasis from a client with a WebSocket was not. `mute` and
  `moh` were not measured with it.
- The `200` cannot tell whether anyone registered the application, so neither `State` nor the health check can.
  `Application` stays required, and on this client it has to name an application **someone else** registers, or the
  caller has to pass one to `Channels.CreateAsync`. Nothing checks it.
- `Channels.OriginateAsync` with an extension runs the dialplan and sends no application, on any client. Without one it
  sends `Application`.
- The client, its hosted service and its health check need no reachable Asterisk for a host to start. A host built by
  `AddVerbara` still needs AMI at start: the AMI connection dials then, and a failed dial stops the host. A host that
  wants only REST registers the client with `AddVerbaraAri` from `Verbara.Sdk.Hosting`, which registers nothing of AMI:

  ```csharp
  builder.Services.AddVerbaraAri(options =>
  {
      options.BaseUrl = "http://pbx.example.com:8088";
      options.Username = "admin";
      options.Password = "secret";
      options.Application = "myapp";
      options.EventsEnabled = false;
  });
  ```

  That host starts and stops with no Asterisk reachable, and its `ari` check reads `Unhealthy` until Asterisk answers.

## Observers

An observer that throws stops neither the other observers nor the client. The event goes on to the next observer, later
events keep arriving, on that connection and after a reconnect, and `DisposeAsync` still releases everything. Each failure
logs one Warning, `[ARI] Observer threw on <EventType>` (or `completion` or `error` at the end of the stream), with the
exception, and adds one to `ari.events.subscriber_faults`. It is not a dropped event.

What this cannot do for you:

- An observer subscribed directly stays subscribed. One composed through an Rx operator (`Where`, `Select`, `OfType`, …) is
  ended by that operator on its first failure: the failure is reported, and that subscription gets nothing more.
- Only what throws on the delivering thread is caught. An `async void` handler or a task nobody awaits fails outside it.
- An observer that blocks still holds every later event, and `OutOfMemoryException` is not caught.

## Documentation

- [High-Load Tuning Guide](../../docs/guides/high-load-tuning.md)
- [Asterisk Version Compatibility](../../docs/guides/asterisk-version-compatibility.md)
