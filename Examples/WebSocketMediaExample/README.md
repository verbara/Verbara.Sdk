# WebSocketMediaExample

Demonstrates the **`chan_websocket`** Channel Driver integration added in v1.12.0 — native Asterisk 22.8 / 23.2+ audio transport over WebSocket with a JSON control plane over TEXT frames.

The example stands up `WebSocketAudioServer` on port 9093, prints each stream as it connects, keyed by the `channel_id` of the `MEDIA_START` Asterisk sends as the connection's first frame (the stream's `ChannelId`, and what `GetStream` finds it by), logs the control messages Asterisk sends (MEDIA_BUFFERING, MARK_MEDIA, DTMF, XON/XOFF, HANGUP) and sends back a MARK after 1 s + a `SET_MEDIA_DIRECTION=both` after 5 s, so you can see the full bidirectional control loop in the console.

## Prerequisites

- .NET 10 SDK
- Asterisk 22.8.0 / 23.2.0 or newer with `res_websocket_client.so` and `chan_websocket.so` loaded

## Asterisk configuration

chan_websocket connects through a `websocket_client` entry. Every call through one entry upgrades on
the same URI, so the URI names no call; the stream is keyed by the `channel_id` of its `MEDIA_START`.

```asterisk
; websocket_client.conf
[media-demo]
type = websocket_client
connection_type = per_call_config
uri = ws://127.0.0.1:9093/media/demo
protocols = media
```

Then either dial it from the dialplan, where the key is the uniqueid of the WebSocket channel
(not the caller's):

```asterisk
; extensions.conf
exten => 6000,1,Answer()
 same => n,Dial(WebSocket/media-demo/c(slin16))
 same => n,Hangup()
```

or create the channel over ARI with `POST /channels/externalMedia` and `transport=websocket`,
`encapsulation=none`, `external_host=media-demo`, where the key is the channel id the create returns.

(Replace `127.0.0.1` with the IP of the host running this example.)

## Run

```bash
dotnet run --project Examples/WebSocketMediaExample/
```

Then dial extension `6000` from a SIP phone. You should see:

```
Stream connected: channel_id=<the call's channel id> format=slin16 sample_rate=16000
Sent MARK_MEDIA  mark=demo-mark-1
MEDIA_MARK_PROCESSED  mark=demo-mark-1
Sent SET_MEDIA_DIRECTION  direction=both
```

## What It Shows

- `WebSocketAudioServer` — RFC 6455 server built on `TcpListener` + `WebSocket.CreateFromStream()`.
- `IChanWebSocketSession` — sub-interface that exposes JSON control messages as an `IObservable<ChanWebSocketControlMessage>`.
- Pattern-matching over the polymorphic message types (source-generated, AOT-clean).
- `SendMarkAsync`, `SendSetMediaDirectionAsync`, `SendXonAsync`, `SendXoffAsync` — outbound control messages.

## Key SDK Packages Used

- `Verbara.Sdk.Ari` — `WebSocketAudioServer`, `IChanWebSocketSession`, `ChanWebSocketControlMessage` hierarchy.
