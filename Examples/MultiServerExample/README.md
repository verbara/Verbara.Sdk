# MultiServerExample

Demonstrates managing a pool of Asterisk servers with federated agent routing: adding servers, looking up which server owns an agent, and originating calls on a specific server.

## Prerequisites

- .NET 10 SDK
- Two or more Asterisk PBX instances with AMI enabled

## Setup

```bash
docker compose -f docker/functional/docker-compose.functional.yml up -d
```

## Run

```bash
dotnet run --project Examples/MultiServerExample/
```

## What It Shows

- Registering multi-server support with `AddAsteriskMultiServer()`
- Adding named servers to `AsteriskServerPool` at runtime with `AddServerAsync()`
- Reading pool-wide statistics: `ServerCount`, `TotalAgentCount`
- Federated agent lookup with `GetServerForAgent(agentId)` across all servers
- Originating a call on a specific server with `OriginateAsync()`
- Removing a server from the pool with `RemoveServerAsync()`

## Key SDK Packages Used

- `Asterisk.Sdk.Live` — `AsteriskServerPool`, `AsteriskServer`
- `Asterisk.Sdk.Ami` — `AmiConnectionOptions`
- `Asterisk.Sdk.Hosting` — `AddAsteriskMultiServer()`
