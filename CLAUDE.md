# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Asterisk.NetAot is a .NET 10 Native AOT port of asterisk-java (790+ Java classes, v3.42.0-SNAPSHOT). It provides AMI, AGI, ARI, and Live API clients for Asterisk PBX with zero runtime reflection. The project language is C# 14, targeting `net10.0`.

## Build & Test Commands

```sh
# Build entire solution
dotnet build Asterisk.NetAot.slnx

# Run all tests
dotnet test Asterisk.NetAot.slnx

# Run a single test project
dotnet test Tests/Asterisk.NetAot.Ami.Tests/

# Run a single test by name
dotnet test Tests/Asterisk.NetAot.Ami.Tests/ --filter "FullyQualifiedName~AmiProtocolReaderTests.ReadMessageAsync_ShouldParseEvent"

# Run benchmarks
dotnet run --project Tests/Asterisk.NetAot.Benchmarks/

# Integration tests (requires Docker)
docker compose -f docker/docker-compose.test.yml up --build
```

**SDK requirement:** .NET 10.0.100+ (pinned in `global.json`).

## Architecture

### Project Dependency Graph

```
Abstractions  (pure interfaces, attributes, enums — no dependencies)
     ↑
    Ami  (+Ami.SourceGenerators as analyzer)
     ↑
   Agi  (→ Abstractions + Ami)
  Live  (→ Abstractions + Ami)
     ↑
   Pbx  (→ Abstractions + Ami + Agi + Live)
   Ari  (→ Abstractions only)
Config  (standalone)
     ↑
Asterisk.NetAot  (meta-package referencing all above)
```

### Key Design Decisions

- **System.IO.Pipelines** for zero-copy TCP parsing (AMI/AGI protocols via `PipelineSocketConnection`)
- **System.Threading.Channels** for async event pump (`AsyncEventPump`, bounded 20K, DropOldest)
- **Source generators** replace reflection for AOT compatibility (stubs in `Ami.SourceGenerators`, Phase 12)
- **System.Reactive** (`IObservable<T>`, `BehaviorSubject<T>`) for PBX activity state machines
- **System.Text.Json source generation** (`[JsonSerializable]`) for ARI JSON serialization
- AMI request/response correlation via `ConcurrentDictionary<string, TaskCompletionSource<AmiMessage>>`
- AMI auth supports MD5 challenge-response

### Layer Summary

| Layer | Purpose | Key Classes |
|-------|---------|-------------|
| **Ami** | AMI protocol, 111 actions, 215 events, 17 responses | `AmiConnection`, `AmiProtocolReader/Writer`, `AsyncEventPump` |
| **Agi** | FastAGI server, 54 commands, mapping strategies | `FastAgiServer`, `AgiChannel`, `SimpleMappingStrategy` |
| **Live** | Real-time domain objects from AMI events | `AsteriskServer`, `ChannelManager`, `QueueManager`, `AgentManager` |
| **Pbx** | High-level call activities with state machines | `ActivityBase`, `DialActivity`, `HoldActivity`, `BridgeActivity` |
| **Ari** | REST + WebSocket ARI client | `AriClient`, `AriChannelsResource`, `AriJsonContext` |
| **Config** | Asterisk `.conf` file parsers | `ConfigFileReader`, `ExtensionsConfigFileReader` |

## Code Conventions

- **AOT constraint:** No reflection at runtime. Use source generators, `[JsonSerializable]`, and static dispatch.
- **Async-first:** All I/O uses `ValueTask`/`Task` with `CancellationToken` support.
- **Private fields:** `_camelCase` prefix (enforced by .editorconfig).
- **File-scoped namespaces** (warning-level enforcement).
- **Test naming:** `Method_ShouldExpected_WhenCondition` (CA1707 suppressed in test projects).
- **Test stack:** xunit 2.9.3, FluentAssertions 7.1.0, NSubstitute 5.3.0.
- **TreatWarningsAsErrors** is on globally; build must be 0 warnings.
- **Central package management:** All NuGet versions in `Directory.Packages.props`.
- AMI Actions/Events were bulk-generated from Java sources via `tools/generate-*.sh` scripts.

## AI Agent

This project uses **Claude Code** with the specialized **dotnet-aot-dapper-asterisk-expert** sub-agent for tasks involving .NET Native AOT, Dapper ORM, and Asterisk PBX integration (AMI, ARI, AGI). This agent has deep knowledge of AOT trimming/reflection constraints, source generators, and Asterisk protocol specifics. Use it for architectural decisions, debugging AOT issues, and designing high-performance data access patterns.

## Migration Status

Phases 1-10 of 12 are complete (108 unit tests). Pending: Phase 11 (integration tests with Docker Asterisk) and Phase 12 (complete source generator bodies, Native AOT publish, benchmarks). See `docs/plan-migracion-asterisk-java-dotnet10.md` for the full migration plan.
