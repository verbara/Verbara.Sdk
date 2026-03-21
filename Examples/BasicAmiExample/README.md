# BasicAmiExample

Demonstrates the minimal setup to connect to Asterisk AMI: DI registration, connect, send a `PingAction`, subscribe to events, and disconnect.

## Prerequisites

- .NET 10 SDK
- Asterisk PBX with AMI enabled (or Docker: see below)

## Setup

```bash
docker compose -f docker/functional/docker-compose.functional.yml up -d
```

## Run

```bash
dotnet run --project Examples/BasicAmiExample/
```

## What It Shows

- Registering Asterisk services with `AddAsterisk()` and `IServiceCollection`
- Connecting to AMI with `IAmiConnection.ConnectAsync()`
- Subscribing to the event stream via `IObserver<ManagerEvent>`
- Sending a `PingAction` and reading the correlated response
- Graceful disconnect with `DisconnectAsync()`

## Key SDK Packages Used

- `Asterisk.Sdk.Ami` — AMI connection and actions
- `Asterisk.Sdk.Hosting` — DI registration (`AddAsterisk`)
