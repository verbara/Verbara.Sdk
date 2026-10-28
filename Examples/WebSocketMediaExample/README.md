# WebSocketMediaExample

Demonstrates the **`chan_websocket`** Channel Driver integration added in v1.12.0 — native Asterisk 22.8 / 23.2+ audio transport over WebSocket with control messages over TEXT frames, in plain text (Asterisk's default) or JSON (`f(json)`).

The example stands up `WebSocketAudioServer` on port 9093 and prints each stream as it connects, keyed by the `channel_id` of the `MEDIA_START` Asterisk sends as the connection's first frame (the stream's `ChannelId`, and what `GetStream` finds it by), with the control format that frame negotiated (`MediaStart.ControlMessageFormat`). It logs the control messages Asterisk sends (`DTMF_END`, `MEDIA_XOFF`/`MEDIA_XON`, `MEDIA_MARK_PROCESSED`, `ERROR`, and any message the SDK does not model, by its event name), sends a `MARK_MEDIA` after 1 s, which Asterisk answers with `MEDIA_MARK_PROCESSED`, and a `SET_MEDIA_DIRECTION` (both) after 5 s on a JSON connection only. A hangup is not a message: Asterisk closes the connection, and the example prints the stream's ending from `StateChanges`.

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
(not the caller's). `f(json)` asks for JSON control messages, the only format in which Asterisk
accepts `SET_MEDIA_DIRECTION`:

```asterisk
; extensions.conf
exten => 6000,1,Answer()
 same => n,Dial(WebSocket/media-demo/c(slin16)f(json))
 same => n,Hangup()
```

or create the channel over ARI with `POST /channels/externalMedia` and `transport=websocket`,
`encapsulation=none`, `external_host=media-demo`, where the key is the channel id the create returns.
That connection uses Asterisk's global `control_message_format` (plain text unless
`chan_websocket.conf` sets `json`), so the example skips its `SET_MEDIA_DIRECTION` there.

(Replace `127.0.0.1` with the IP of the host running this example.)

## Run

```bash
dotnet run --project Examples/WebSocketMediaExample/
```

Then dial extension `6000` from a SIP phone. You should see:

```
[stream] connected channel_id=<the call's channel id> format=slin16 sample_rate=16000
[stream] control format=Json
[send] MARK_MEDIA  mark=demo-mark-1
[control] MEDIA_MARK_PROCESSED  mark=demo-mark-1
[send] SET_MEDIA_DIRECTION  direction=both
[stream] ended channel_id=<the call's channel id>
```

The last line appears when the call hangs up.

## What It Shows

- `WebSocketAudioServer` — RFC 6455 server built on `TcpListener` + `WebSocket.CreateFromStream()`.
- `IChanWebSocketSession` — sub-interface that exposes the control messages Asterisk sends, read in either format, as an `IObservable<ChanWebSocketControlMessage>`, and the connection's `MEDIA_START` as `MediaStart`.
- Pattern-matching over the message records, with `ChanWebSocketUnknownMessage` for a message the SDK does not model (AOT-clean, no reflection).
- `SendMarkAsync`, `SendSetMediaDirectionAsync` — the two commands Asterisk acts on (`SendXonAsync`/`SendXoffAsync` are deprecated, `VSDK0004`: Asterisk has no such command, and they throw).
- `IAudioStream.StateChanges` — the stream's ending, which is how a hangup reaches the application.

## Key SDK Packages Used

- `Verbara.Sdk.Ari` — `WebSocketAudioServer`, `IChanWebSocketSession`, `ChanWebSocketControlMessage` hierarchy.
