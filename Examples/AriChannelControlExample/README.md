# AriChannelControlExample

Demonstrates full ARI channel lifecycle management: listing channels, creating a mixing bridge, originating a channel, adding it to the bridge, and cleaning up — all while receiving WebSocket events.

## Prerequisites

- .NET 10 SDK
- Asterisk PBX with ARI enabled (HTTP + WebSocket on port 8088)

## Setup

```bash
docker compose -f docker/functional/docker-compose.functional.yml up -d
```

## Run

```bash
dotnet run --project Examples/AriChannelControlExample/
```

## What It Shows

- Connecting to the ARI WebSocket and subscribing to `StasisStart` / `StasisEnd` events
- Listing existing channels with `ari.Channels.ListAsync()`
- Creating a mixing bridge with `ari.Bridges.CreateAsync()`
- Originating a channel into a Stasis application
- Adding and removing a channel from a bridge
- Hanging up a channel and destroying a bridge for clean teardown

## Key SDK Packages Used

- `Asterisk.Sdk.Ari` — `IAriClient`, channels and bridges resources
- `Asterisk.Sdk.Hosting` — DI registration (`AddAsterisk`)
