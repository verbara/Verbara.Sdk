# AriStasisExample

Demonstrates connecting to the ARI WebSocket as a Stasis application, subscribing to all ARI events, and listing active channels.

## Prerequisites

- .NET 10 SDK
- Asterisk PBX with ARI enabled (HTTP + WebSocket on port 8088)

## Setup

```bash
docker compose -f docker/functional/docker-compose.functional.yml up -d
```

## Run

```bash
dotnet run --project Examples/AriStasisExample/
```

## What It Shows

- Constructing `AriClient` directly with `AriClientOptions` (without full DI host)
- Connecting to the ARI WebSocket for the `hello-stasis` application
- Subscribing to ARI events via `IObserver<AriEvent>`
- Listing active channels with `client.Channels.ListAsync()`

## Key SDK Packages Used

- `Asterisk.Sdk.Ari` — `AriClient`, `AriClientOptions`, `AriEvent`
