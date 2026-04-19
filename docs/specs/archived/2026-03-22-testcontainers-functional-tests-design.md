# Testcontainers for Functional/Integration Tests

**Autor:** Harol Reina
**Fecha:** 2026-03-22
**Estado:** Aprobado

---

## 1. Problema

Los tests funcionales (600), de integración (46) y realtime (~20) requieren infraestructura Docker (Asterisk, PostgreSQL, Toxiproxy, SIPp, PSTN emulator) pero:
- No hay CI pipeline que los ejecute
- Localmente requieren `docker-compose up` manual previo
- Si el proceso de tests muere, containers quedan huérfanos
- 124 tests Layer5 + 31 integration tests fallan constantemente porque no hay infra levantada

## 2. Solución

Usar **Testcontainers for .NET** para provisionar infraestructura efímera automáticamente. Los containers se crean al inicio de la suite, se comparten entre tests, y se destruyen al finalizar (garantizado por `IAsyncDisposable`).

## 3. Alcance

| Suite | Migrar a Testcontainers | Cambio |
|-------|------------------------|--------|
| Functional Tests (600) | Si | Rewire fixtures to use stacks |
| Integration Tests (46) | Si | Rewire fixtures to use IntegrationStack |
| Sessions Functional (37) | Si | Rewire fixtures to use IntegrationStack |
| Unit Tests (1267) | No | Sin Docker, sin cambios |
| PbxAdmin E2E (70) | No | Sigue con compose manual |
| PbxAdmin bUnit (255) | No | Sin Docker, sin cambios |
| Benchmarks | No | Sin cambios |

## 4. Arquitectura

### 4.1 Shared Project

```
Tests/Asterisk.Sdk.TestInfrastructure/
├── Asterisk.Sdk.TestInfrastructure.csproj    ← Class library, not test project
├── Containers/
│   ├── AsteriskContainer.cs                  ← File-based Asterisk (Dockerfile.asterisk-file)
│   ├── AsteriskRealtimeContainer.cs          ← ODBC Asterisk (Dockerfile.asterisk-realtime)
│   ├── PostgresContainer.cs                  ← PostgreSQL 17 for realtime
│   ├── PstnEmulatorContainer.cs              ← PSTN stub Asterisk
│   ├── ToxiproxyContainer.cs                 ← Network chaos proxy
│   └── SippContainer.cs                      ← SIP load generator
├── Stacks/
│   ├── FunctionalStack.cs                    ← Asterisk + PSTN + SIPp + Toxiproxy
│   ├── RealtimeStack.cs                      ← PostgreSQL + Asterisk ODBC
│   └── IntegrationStack.cs                   ← Asterisk only
├── Networks/
│   └── TestNetwork.cs                        ← Shared Docker network for inter-container comms
└── Waiters/
    └── AsteriskReadyWaiter.cs                ← Wait for AMI TCP + ARI HTTP health
```

### 4.2 Container Pattern

Each container class wraps Testcontainers `IContainer` and encapsulates:
- **Image build:** `ImageFromDockerfileBuilder` pointing to existing `docker/Dockerfile.*` files
- **Port mapping:** Random host ports via `WithPortBinding(containerPort, assignRandomHostPort: true)`
- **Health check:** `WithWaitStrategy` using TCP port availability or HTTP endpoint
- **Network:** Shared `INetwork` so containers can resolve each other by name
- **Cleanup:** `IAsyncDisposable` guarantees container removal even on crash

```csharp
public sealed class AsteriskContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public string Host => _container.Hostname;
    public int AmiPort => _container.GetMappedPublicPort(5038);
    public int AriPort => _container.GetMappedPublicPort(8088);
    public int AgiPort => _container.GetMappedPublicPort(4573);

    public AsteriskContainer(INetwork network)
    {
        _container = new ContainerBuilder()
            .WithImage(new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(CommonDirectoryPath.GetSolutionDirectory(), "docker")
                .WithDockerfile("Dockerfile.asterisk-file")
                .Build())
            .WithNetwork(network)
            .WithNetworkAliases("asterisk")
            .WithPortBinding(5038, true)
            .WithPortBinding(8088, true)
            .WithPortBinding(4573, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilPortIsAvailable(5038))
            .Build();
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
```

### 4.3 Stack Pattern

Stacks are xunit `IAsyncLifetime` collection fixtures that orchestrate multiple containers:

```csharp
public sealed class FunctionalStack : IAsyncLifetime
{
    private readonly INetwork _network;
    public AsteriskContainer Asterisk { get; }
    public PstnEmulatorContainer PstnEmulator { get; }
    public ToxiproxyContainer Toxiproxy { get; }
    public SippContainer Sipp { get; }

    public FunctionalStack()
    {
        _network = new NetworkBuilder().Build();
        Asterisk = new AsteriskContainer(_network);
        PstnEmulator = new PstnEmulatorContainer(_network);
        Toxiproxy = new ToxiproxyContainer(_network);
        Sipp = new SippContainer(_network);
    }

    public async Task InitializeAsync()
    {
        await _network.CreateAsync();
        // Start containers in dependency order
        await Task.WhenAll(
            Asterisk.StartAsync(),
            PstnEmulator.StartAsync(),
            Toxiproxy.StartAsync());
        // Sipp depends on Asterisk being ready
        await AsteriskReadyWaiter.WaitAsync(Asterisk);
        await Sipp.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await Sipp.DisposeAsync();
        await Toxiproxy.DisposeAsync();
        await PstnEmulator.DisposeAsync();
        await Asterisk.DisposeAsync();
        await _network.DisposeAsync();
    }
}
```

### 4.4 xunit Collection Wiring

```csharp
// Collection definition (one per stack)
[CollectionDefinition("Functional")]
public sealed class FunctionalCollection : ICollectionFixture<FunctionalStack>;

// Tests use collection
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class QueueMemberTests(FunctionalStack stack)
{
    [Fact]
    public async Task QueueMember_ShouldJoin()
    {
        // Use stack.Asterisk.AmiPort for connection
        var connection = new AmiConnection(new AmiConnectionOptions
        {
            Hostname = stack.Asterisk.Host,
            Port = stack.Asterisk.AmiPort,
            Username = "testadmin",
            Password = "testpass"
        });
        // ...
    }
}
```

### 4.5 Docker Image Build & Cache

- Testcontainers builds images using existing `docker/Dockerfile.*` files
- Docker layer cache is used automatically — first build ~30s, subsequent builds ~2s
- Images are tagged with content hash by Testcontainers (automatic cache invalidation if Dockerfile changes)
- No external registry needed

### 4.6 Random Ports

All containers use random host ports (`WithPortBinding(port, true)`) to:
- Avoid conflicts with manually running containers
- Allow parallel test runs on CI
- Prevent port-in-use failures

Tests read actual ports from container objects (e.g., `stack.Asterisk.AmiPort`).

## 5. Skip Attribute Compatibility

Existing skip attributes (`[AsteriskContainerFact]`, `[RealtimeFact]`, etc.) are **preserved** as fallback. They detect infrastructure by probing TCP/HTTP endpoints.

When running via Testcontainers collection fixtures, the containers are already up, so skip attributes will find the services available and tests run normally.

When running without Docker (e.g., `dotnet test --filter "Category!=Functional"`), skip attributes gracefully skip infrastructure-dependent tests.

## 6. Test Helper Migration

### Current helpers to update:

| Helper | Current | After |
|--------|---------|-------|
| `AmiConnectionFactory` | Reads env vars (ASTERISK_HOST, etc.) | Accepts host/port from stack or falls back to env vars |
| `AriClientFactory` | Reads env vars | Same pattern |
| `RealtimeFixture` | Hardcoded ports | Reads from RealtimeStack or falls back to hardcoded |
| `DockerControl` | Shells out to `docker` CLI | Keep for chaos tests (pause/kill), but container lifecycle moves to Testcontainers |
| `ToxiproxyControl` | HTTP client to fixed port | Reads port from stack |

### Backward compatibility

All factories gain an overload accepting the stack object, but keep the existing env-var-based constructor. This allows:
- Testcontainers path: `new AmiConnectionFactory(stack.Asterisk)`
- Manual path: `new AmiConnectionFactory()` (reads env vars as before)

## 7. CI Pipeline

```yaml
# .github/workflows/ci.yml
name: CI

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

jobs:
  unit-tests:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet build Asterisk.Sdk.slnx
      - run: dotnet test Asterisk.Sdk.slnx --filter "Category!=Functional&Category!=Integration&Category!=Realtime"
    # ~2 min, no Docker

  aot-check:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: bash tools/verify-aot.sh
    # ~3 min

  functional-tests:
    runs-on: ubuntu-latest
    needs: unit-tests
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet test Asterisk.Sdk.slnx --filter "Category=Functional|Category=Integration|Category=Realtime"
    # ~8 min, Testcontainers auto-provisions Docker
    # ubuntu-latest has Docker pre-installed
```

### CI traits:
- `Category=Functional` — FunctionalTests project (Layer5)
- `Category=Integration` — IntegrationTests project
- `Category=Realtime` — Realtime DB tests

## 8. What Does NOT Change

- Unit tests (1267) — no Docker, no changes
- PbxAdmin E2E (70) — stays with manual docker-compose
- PbxAdmin bUnit (255) — no Docker, no changes
- Docker compose files — preserved for manual/demo use
- Dockerfiles — reused by Testcontainers, no changes
- Skip attributes — preserved as fallback
- Asterisk config files — reused, no changes

## 9. Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `Testcontainers` | latest | Core container management |
| `DotNet.Testcontainers` | (included in above) | .NET bindings |

Already referenced in `Directory.Packages.props` (Testcontainers is already a dependency, just unused).

## 10. Success Criteria

- [ ] `dotnet test --filter "Category=Functional"` provisions Docker automatically and all 600 tests execute
- [ ] `dotnet test --filter "Category=Integration"` provisions Docker automatically and all 46 tests pass
- [ ] Containers are destroyed after test run completes (verify with `docker ps`)
- [ ] Containers are destroyed even if tests crash/timeout (Testcontainers guarantee)
- [ ] Running without Docker still skips gracefully (skip attributes work)
- [ ] CI pipeline runs all tests green
- [ ] No port conflicts with manual containers
- [ ] Second run is fast (Docker image cache hit)
