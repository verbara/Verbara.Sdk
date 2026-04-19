# AriOutboundExample

Demonstrates the **ARI Outbound WebSocket** pattern added in Asterisk 22.5+ and covered by the SDK in v1.12.0 (see `src/Asterisk.Sdk.Ari/Outbound/`).

In this mode the control relationship is reversed: rather than the consumer app dialing into Asterisk's `/ari/events` endpoint, **Asterisk dials into the consumer app's WebSocket server**. It unlocks deployments where the consumer sits behind a NAT with no inbound port, or where per-call application registration is wanted.

## Prerequisites

- .NET 10 SDK
- Asterisk 22.5.0 or newer with `res_websocket_client.so` loaded

## Configuration

`ari.conf`:

```ini
[outbound-app]
type = user
read_only = yes
password = s3cret

application = outbound
websocket_client_id = outbound-app-client
```

`res_websocket_client.conf`:

```ini
[outbound-app-client]
type = websocket_client
uri = ws://HOST:8099/ari/events
authentication = basic
username = outbound-app
password = s3cret
```

Dialplan:

```asterisk
exten => 7000,1,Answer()
 same => n,Stasis(outbound-app)
 same => n,Hangup()
```

Restart Asterisk after the config changes.

## Run

```bash
dotnet run --project Examples/AriOutboundExample/
```

Then dial extension `7000`. You should see:

```
[conn] accepted application=outbound-app remote=...
[event] StasisStart application=outbound-app
[event] ChannelStateChange application=outbound-app
```

## What It Shows

- `AddAriOutboundListener` — one-line DI registration + hosted-service lifecycle.
- `AriOutboundListenerOptions` — port, path, credentials, allow-list validation.
- `IAriOutboundListener.OnConnectionAccepted` — `IObservable<AriOutboundConnection>` per accepted Asterisk-initiated session.
- `AriOutboundConnection.Events` — typed `IObservable<AriEvent>` per connection.
- `ActiveConnectionCount` — tracks how many Asterisk instances are currently connected.

## Key SDK Packages Used

- `Asterisk.Sdk.Ari` — `IAriOutboundListener`, `AriOutboundConnection`, `AriOutboundListenerOptions`.
- `Asterisk.Sdk.Hosting` — `AddAriOutboundListener` DI extension + hosted service.
