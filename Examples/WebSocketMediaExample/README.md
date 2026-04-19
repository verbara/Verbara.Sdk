# WebSocketMediaExample

Demonstrates the **`chan_websocket`** Channel Driver integration added in v1.12.0 — native Asterisk 22.8 / 23.2+ audio transport over WebSocket with a JSON control plane over TEXT frames.

The example stands up `WebSocketAudioServer` on port 9093, logs every control message Asterisk sends (MEDIA_START, MEDIA_BUFFERING, MARK_MEDIA, DTMF, XON/XOFF, HANGUP) and sends back a MARK after 1 s + a `SET_MEDIA_DIRECTION=both` after 5 s, so you can see the full bidirectional control loop in the console.

## Prerequisites

- .NET 10 SDK
- Asterisk 22.8.0 / 23.2.0 or newer with `res_websocket_client.so` and `chan_websocket.so` loaded

## Dialplan

```asterisk
; extensions.conf
exten => 6000,1,Answer()
 same => n,WebSocket(ws://127.0.0.1:9093/audio,slin16)
 same => n,Hangup()
```

(Replace `127.0.0.1` with the IP of the host running this example.)

## Run

```bash
dotnet run --project Examples/WebSocketMediaExample/
```

Then dial extension `6000` from a SIP phone. You should see:

```
Stream connected: channel_id=... format=slin16 sample_rate=16000
MEDIA_START  format=slin16 rate=16000
Sent MARK_MEDIA  mark=demo-mark-1
MEDIA_MARK_PROCESSED  mark=demo-mark-1
Sent SET_MEDIA_DIRECTION  direction=both
```

## What It Shows

- `WebSocketAudioServer` — RFC 6455 server built on `TcpListener` + `WebSocket.CreateFromStream()` (see ADR-0017).
- `IChanWebSocketSession` — sub-interface that exposes JSON control messages as an `IObservable<ChanWebSocketControlMessage>`.
- Pattern-matching over the polymorphic message types (source-generated, AOT-clean).
- `SendMarkAsync`, `SendSetMediaDirectionAsync`, `SendXonAsync`, `SendXoffAsync` — outbound control messages.

## Key SDK Packages Used

- `Asterisk.Sdk.Ari` — `WebSocketAudioServer`, `IChanWebSocketSession`, `ChanWebSocketControlMessage` hierarchy.
