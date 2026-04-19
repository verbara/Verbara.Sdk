# Testcontainers Functional Tests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace manual docker-compose with Testcontainers for automatic provisioning and cleanup of test infrastructure (Asterisk, PostgreSQL, Toxiproxy, SIPp, PSTN emulator).

**Architecture:** A shared `TestInfrastructure` project provides container wrappers and stack orchestrators that xunit collection fixtures use to spin up/tear down Docker infrastructure automatically. Random host ports avoid conflicts. Existing skip attributes remain as fallback for environments without Docker.

**Tech Stack:** Testcontainers 4.3.0 (already in Directory.Packages.props), xunit ICollectionFixture, Docker.

**Spec:** `docs/superpowers/specs/2026-03-22-testcontainers-functional-tests-design.md`

---

## File Structure

### New Files

```
Tests/Asterisk.Sdk.TestInfrastructure/
├── Asterisk.Sdk.TestInfrastructure.csproj     ← Shared class library (not test project)
├── Containers/
│   ├── AsteriskContainer.cs                   ← Wraps Dockerfile.asterisk-file
│   ├── AsteriskRealtimeContainer.cs           ← Wraps Dockerfile.asterisk-realtime
│   ├── PostgresContainer.cs                   ← PostgreSQL 17 from Docker Hub
│   ├── PstnEmulatorContainer.cs               ← PSTN stub Asterisk
│   ├── ToxiproxyContainer.cs                  ← Network chaos
│   └── SippContainer.cs                       ← SIP load generator
├── Stacks/
│   ├── FunctionalStack.cs                     ← Asterisk + PSTN + SIPp + Toxiproxy
│   ├── RealtimeStack.cs                       ← PostgreSQL + Asterisk ODBC
│   └── IntegrationStack.cs                    ← Asterisk solo
└── DockerPaths.cs                             ← Resolves solution-relative Docker paths
```

### Modified Files

```
Tests/Asterisk.Sdk.FunctionalTests/
├── Asterisk.Sdk.FunctionalTests.csproj        ← Add TestInfrastructure reference
├── Infrastructure/
│   ├── Collections/
│   │   └── FunctionalCollection.cs            ← NEW: [CollectionDefinition("Functional")]
│   ├── Fixtures/
│   │   └── AsteriskContainerFixture.cs        ← MODIFY: delegate to FunctionalStack
│   └── Helpers/
│       ├── AmiConnectionFactory.cs            ← MODIFY: add overload from stack
│       └── AriClientFactory.cs                ← MODIFY: add overload from stack

Tests/Asterisk.Sdk.IntegrationTests/
├── Asterisk.Sdk.IntegrationTests.csproj       ← Add TestInfrastructure reference
└── Infrastructure/
    └── IntegrationCollection.cs               ← NEW: [CollectionDefinition("Integration")]

.github/workflows/
└── ci.yml                                     ← NEW: CI pipeline
```

---

### Task 1: Create TestInfrastructure Project + DockerPaths

**Files:**
- Create: `Tests/Asterisk.Sdk.TestInfrastructure/Asterisk.Sdk.TestInfrastructure.csproj`
- Create: `Tests/Asterisk.Sdk.TestInfrastructure/DockerPaths.cs`

- [ ] **Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>false</IsTestProject>
    <IsAotCompatible>false</IsAotCompatible>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Testcontainers" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create DockerPaths helper**

This resolves paths relative to the solution root so containers can find Dockerfiles and config dirs regardless of working directory.

```csharp
namespace Asterisk.Sdk.TestInfrastructure;

/// <summary>Resolves Docker-related paths relative to the solution root.</summary>
public static class DockerPaths
{
    private static readonly Lazy<string> _solutionRoot = new(FindSolutionRoot);

    public static string SolutionRoot => _solutionRoot.Value;
    public static string DockerDir => Path.Combine(SolutionRoot, "docker");
    public static string FunctionalDir => Path.Combine(DockerDir, "functional");
    public static string FunctionalAsteriskConfig => Path.Combine(FunctionalDir, "asterisk-config");
    public static string PstnEmulatorConfig => Path.Combine(FunctionalDir, "pstn-emulator-config");
    public static string AsteriskFileDockerfile => Path.Combine(DockerDir, "Dockerfile.asterisk-file");
    public static string AsteriskRealtimeDockerfile => Path.Combine(DockerDir, "Dockerfile.asterisk-realtime");

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("Asterisk.Sdk.slnx").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find solution root (Asterisk.Sdk.slnx)");
    }
}
```

- [ ] **Step 3: Add project to solution**

Run: `dotnet sln Asterisk.Sdk.slnx add Tests/Asterisk.Sdk.TestInfrastructure/Asterisk.Sdk.TestInfrastructure.csproj`

- [ ] **Step 4: Build and verify**

Run: `dotnet build Tests/Asterisk.Sdk.TestInfrastructure/`
Expected: 0 warnings, 0 errors

- [ ] **Step 5: Commit**

Message: `feat(test-infra): create TestInfrastructure project with DockerPaths helper`

---

### Task 2: AsteriskContainer + IntegrationStack

**Files:**
- Create: `Tests/Asterisk.Sdk.TestInfrastructure/Containers/AsteriskContainer.cs`
- Create: `Tests/Asterisk.Sdk.TestInfrastructure/Stacks/IntegrationStack.cs`

- [ ] **Step 1: Create AsteriskContainer**

Wraps `Dockerfile.asterisk-file` build. Exposes AMI, ARI, AGI ports with random host mapping. Uses existing `docker/functional/asterisk-config/` for config files.

```csharp
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;

namespace Asterisk.Sdk.TestInfrastructure.Containers;

public sealed class AsteriskContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public string Host => _container.Hostname;
    public int AmiPort => _container.GetMappedPublicPort(5038);
    public int AriPort => _container.GetMappedPublicPort(8088);
    public int AgiPort => _container.GetMappedPublicPort(4573);

    public AsteriskContainer(INetwork? network = null)
    {
        var builder = new ContainerBuilder()
            .WithImage(new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(DockerPaths.DockerDir)
                .WithDockerfile("Dockerfile.asterisk-file")
                .Build())
            .WithPortBinding(5038, true)
            .WithPortBinding(8088, true)
            .WithPortBinding(4573, true)
            .WithBindMount(DockerPaths.FunctionalAsteriskConfig, "/etc/asterisk", AccessMode.ReadOnly)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilPortIsAvailable(5038)
                .UntilPortIsAvailable(8088));

        if (network is not null)
            builder = builder.WithNetwork(network).WithNetworkAliases("asterisk");

        _container = builder.Build();
    }

    public string ContainerName => _container.Name;
    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);
    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>Execute a CLI command inside the container (for DockerControl compat).</summary>
    public Task<ExecResult> ExecAsync(IList<string> command, CancellationToken ct = default)
        => _container.ExecAsync(command, ct);
}
```

**Note:** The exact Testcontainers API may differ slightly. The implementer should check the Testcontainers 4.3.0 docs and adjust imports/types. Key concepts: `ContainerBuilder`, `ImageFromDockerfileBuilder`, `WithPortBinding(port, true)` for random host ports, `WithWaitStrategy`, `WithNetwork`.

- [ ] **Step 2: Create IntegrationStack**

Simplest stack: just one Asterisk container. Used by IntegrationTests and Sessions.FunctionalTests.

```csharp
namespace Asterisk.Sdk.TestInfrastructure.Stacks;

public sealed class IntegrationStack : IAsyncLifetime
{
    public AsteriskContainer Asterisk { get; } = new();

    public async Task InitializeAsync()
    {
        await Asterisk.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await Asterisk.DisposeAsync();
    }
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build Tests/Asterisk.Sdk.TestInfrastructure/`
Expected: 0 warnings, 0 errors

- [ ] **Step 4: Commit**

Message: `feat(test-infra): add AsteriskContainer and IntegrationStack`

---

### Task 3: Remaining Containers (PostgreSQL, PSTN, Toxiproxy, SIPp, Asterisk Realtime)

**Files:**
- Create: `Tests/Asterisk.Sdk.TestInfrastructure/Containers/PostgresContainer.cs`
- Create: `Tests/Asterisk.Sdk.TestInfrastructure/Containers/PstnEmulatorContainer.cs`
- Create: `Tests/Asterisk.Sdk.TestInfrastructure/Containers/ToxiproxyContainer.cs`
- Create: `Tests/Asterisk.Sdk.TestInfrastructure/Containers/SippContainer.cs`
- Create: `Tests/Asterisk.Sdk.TestInfrastructure/Containers/AsteriskRealtimeContainer.cs`

- [ ] **Step 1: PostgresContainer**

Use official `postgres:17-alpine` image (no Dockerfile build needed). Expose port 5432. Init scripts from `docker/functional/sql/`.

```csharp
public sealed class PostgresContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public string Host => _container.Hostname;
    public int Port => _container.GetMappedPublicPort(5432);
    public string ConnectionString => $"Host={Host};Port={Port};Database=asterisk;Username=asterisk;Password=asterisk";

    public PostgresContainer(INetwork? network = null)
    {
        var builder = new ContainerBuilder()
            .WithImage("postgres:17-alpine")
            .WithEnvironment("POSTGRES_USER", "asterisk")
            .WithEnvironment("POSTGRES_PASSWORD", "asterisk")
            .WithEnvironment("POSTGRES_DB", "asterisk")
            .WithPortBinding(5432, true)
            .WithBindMount(Path.Combine(DockerPaths.FunctionalDir, "sql"), "/docker-entrypoint-initdb.d", AccessMode.ReadOnly)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilPortIsAvailable(5432));

        if (network is not null)
            builder = builder.WithNetwork(network).WithNetworkAliases("postgres");

        _container = builder.Build();
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
```

- [ ] **Step 2: PstnEmulatorContainer**

Same Dockerfile as Asterisk but with PSTN emulator config. Needs to be on same network as Asterisk.

```csharp
public sealed class PstnEmulatorContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public PstnEmulatorContainer(INetwork network)
    {
        _container = new ContainerBuilder()
            .WithImage(new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(DockerPaths.DockerDir)
                .WithDockerfile("Dockerfile.asterisk-file")
                .Build())
            .WithNetwork(network)
            .WithNetworkAliases("pstn-emulator")
            .WithBindMount(DockerPaths.PstnEmulatorConfig, "/etc/asterisk", AccessMode.ReadOnly)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilPortIsAvailable(5038))
            .Build();
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
```

- [ ] **Step 3: ToxiproxyContainer**

Use official image. Expose API port (8474) and AMI proxy port.

```csharp
public sealed class ToxiproxyContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public string Host => _container.Hostname;
    public int ApiPort => _container.GetMappedPublicPort(8474);
    public int AmiProxyPort => _container.GetMappedPublicPort(15038);

    public ToxiproxyContainer(INetwork? network = null)
    {
        var builder = new ContainerBuilder()
            .WithImage("ghcr.io/shopify/toxiproxy:2.9.0")
            .WithPortBinding(8474, true)
            .WithPortBinding(15038, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilPortIsAvailable(8474));

        if (network is not null)
            builder = builder.WithNetwork(network).WithNetworkAliases("toxiproxy");

        _container = builder.Build();
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
```

- [ ] **Step 4: SippContainer**

SIPp container shares Asterisk's network. Uses `sleep infinity` entrypoint; actual SIPp scenarios run via `ExecAsync`.

```csharp
public sealed class SippContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public SippContainer(INetwork network)
    {
        _container = new ContainerBuilder()
            .WithImage("ctaloi/sipp")
            .WithNetwork(network)
            .WithNetworkAliases("sipp")
            .WithEntrypoint("sleep", "infinity")
            .WithBindMount(
                Path.Combine(DockerPaths.FunctionalDir, "sipp-scenarios"),
                "/sipp-scenarios", AccessMode.ReadOnly)
            .Build();
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);
    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public Task<ExecResult> RunScenarioAsync(string scenarioFile, string target, CancellationToken ct = default)
        => _container.ExecAsync(["sipp", target, "-sf", $"/sipp-scenarios/{scenarioFile}", "-m", "1"], ct);
}
```

- [ ] **Step 5: AsteriskRealtimeContainer**

Uses `Dockerfile.asterisk-realtime`. Needs PostgreSQL on same network.

```csharp
public sealed class AsteriskRealtimeContainer : IAsyncDisposable
{
    private readonly IContainer _container;

    public string Host => _container.Hostname;
    public int AmiPort => _container.GetMappedPublicPort(5038);

    public AsteriskRealtimeContainer(INetwork network)
    {
        _container = new ContainerBuilder()
            .WithImage(new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(DockerPaths.DockerDir)
                .WithDockerfile("Dockerfile.asterisk-realtime")
                .Build())
            .WithNetwork(network)
            .WithNetworkAliases("asterisk-realtime")
            .WithPortBinding(5038, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilPortIsAvailable(5038))
            .Build();
    }

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
```

- [ ] **Step 6: Build and verify**

Run: `dotnet build Tests/Asterisk.Sdk.TestInfrastructure/`
Expected: 0 warnings, 0 errors

- [ ] **Step 7: Commit**

Message: `feat(test-infra): add PostgreSQL, PSTN, Toxiproxy, SIPp, Asterisk Realtime containers`

---

### Task 4: FunctionalStack + RealtimeStack

**Files:**
- Create: `Tests/Asterisk.Sdk.TestInfrastructure/Stacks/FunctionalStack.cs`
- Create: `Tests/Asterisk.Sdk.TestInfrastructure/Stacks/RealtimeStack.cs`

- [ ] **Step 1: FunctionalStack**

Orchestrates all functional test containers with shared network.

```csharp
using DotNet.Testcontainers.Networks;
using Asterisk.Sdk.TestInfrastructure.Containers;

namespace Asterisk.Sdk.TestInfrastructure.Stacks;

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
        await Task.WhenAll(
            Asterisk.StartAsync(),
            PstnEmulator.StartAsync(),
            Toxiproxy.StartAsync());
        // Sipp needs Asterisk ready first
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

- [ ] **Step 2: RealtimeStack**

PostgreSQL + Asterisk ODBC on shared network.

```csharp
namespace Asterisk.Sdk.TestInfrastructure.Stacks;

public sealed class RealtimeStack : IAsyncLifetime
{
    private readonly INetwork _network;

    public PostgresContainer Postgres { get; }
    public AsteriskRealtimeContainer Asterisk { get; }

    public RealtimeStack()
    {
        _network = new NetworkBuilder().Build();
        Postgres = new PostgresContainer(_network);
        Asterisk = new AsteriskRealtimeContainer(_network);
    }

    public async Task InitializeAsync()
    {
        await _network.CreateAsync();
        await Postgres.StartAsync();
        // Asterisk needs PostgreSQL ready for ODBC
        await Asterisk.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await Asterisk.DisposeAsync();
        await Postgres.DisposeAsync();
        await _network.DisposeAsync();
    }
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build Tests/Asterisk.Sdk.TestInfrastructure/`
Expected: 0 warnings, 0 errors

- [ ] **Step 4: Commit**

Message: `feat(test-infra): add FunctionalStack and RealtimeStack orchestrators`

---

### Task 5: Wire FunctionalTests to FunctionalStack

**Files:**
- Modify: `Tests/Asterisk.Sdk.FunctionalTests/Asterisk.Sdk.FunctionalTests.csproj` — add ProjectReference
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Collections/FunctionalCollection.cs`
- Modify: `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Fixtures/AsteriskContainerFixture.cs` — wrap FunctionalStack
- Modify: `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Helpers/AmiConnectionFactory.cs` — add stack overload
- Modify: `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Helpers/AriClientFactory.cs` — add stack overload

- [ ] **Step 1: Add project reference**

Add to `Asterisk.Sdk.FunctionalTests.csproj`:
```xml
<ProjectReference Include="..\Asterisk.Sdk.TestInfrastructure\Asterisk.Sdk.TestInfrastructure.csproj" />
```

- [ ] **Step 2: Create FunctionalCollection**

```csharp
using Asterisk.Sdk.TestInfrastructure.Stacks;

namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Collections;

[CollectionDefinition("Functional")]
public sealed class FunctionalCollection : ICollectionFixture<FunctionalStack>;
```

- [ ] **Step 3: Modify AsteriskContainerFixture to delegate to FunctionalStack**

The existing fixture should become a thin wrapper that reads ports from the stack when available, falling back to the old env-var/docker-compose approach.

Replace `AsteriskContainerFixture` body with:

```csharp
public sealed class AsteriskContainerFixture : IAsyncLifetime
{
    private readonly FunctionalStack? _stack;

    public string Host { get; private set; } = "localhost";
    public int AmiPort { get; private set; } = 5038;
    public int AriPort { get; private set; } = 8088;

    /// <summary>Used by [Collection("Functional")] — stack is managed by collection.</summary>
    public AsteriskContainerFixture(FunctionalStack stack)
    {
        _stack = stack;
    }

    /// <summary>Fallback for tests not using collection — waits for external container.</summary>
    public AsteriskContainerFixture()
    {
    }

    public async Task InitializeAsync()
    {
        if (_stack is not null)
        {
            Host = _stack.Asterisk.Host;
            AmiPort = _stack.Asterisk.AmiPort;
            AriPort = _stack.Asterisk.AriPort;
        }
        else
        {
            Host = Environment.GetEnvironmentVariable("ASTERISK_HOST") ?? "localhost";
            AmiPort = int.Parse(Environment.GetEnvironmentVariable("ASTERISK_AMI_PORT") ?? "5038");
            AriPort = int.Parse(Environment.GetEnvironmentVariable("ASTERISK_ARI_PORT") ?? "8088");
            await DockerControl.WaitForHealthyAsync(timeout: TimeSpan.FromSeconds(60));
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
```

- [ ] **Step 4: Add stack overload to AmiConnectionFactory**

Add a static method that takes the stack:
```csharp
public static AmiConnection Create(FunctionalStack stack, ILoggerFactory? loggerFactory = null,
    Action<AmiConnectionOptions>? configure = null)
{
    return Create(stack.Asterisk.Host, stack.Asterisk.AmiPort, loggerFactory, configure);
}

// Refactor existing Create to accept host/port explicitly:
public static AmiConnection Create(string host = "localhost", int port = 5038,
    ILoggerFactory? loggerFactory = null, Action<AmiConnectionOptions>? configure = null)
{
    // ... existing logic but using host/port params instead of env vars
}
```

Keep the existing parameterless overload as backward compat (reads env vars, delegates to host/port version).

- [ ] **Step 5: Same pattern for AriClientFactory**

Add overload accepting stack with host/port from `stack.Asterisk`.

- [ ] **Step 6: Migrate ONE Layer5 test file to use [Collection("Functional")]**

Pick a simple test file (e.g., one with 2-3 tests). Change:
- Remove `IClassFixture<AsteriskContainerFixture>`
- Add `[Collection("Functional")]`
- Inject `FunctionalStack` via constructor
- Use `AmiConnectionFactory.Create(stack)` instead of `AmiConnectionFactory.Create()`

- [ ] **Step 7: Build and run the migrated test**

Run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "FullyQualifiedName~<TestClassName>"`
Expected: If Docker available → test runs (containers auto-provisioned). If no Docker → test skips gracefully via existing skip attribute.

- [ ] **Step 8: Commit**

Message: `feat(functional): wire FunctionalTests to Testcontainers FunctionalStack`

---

### Task 6: Migrate All Functional Tests to Collections

**Files:**
- Modify: All test files in `Tests/Asterisk.Sdk.FunctionalTests/` that use `IClassFixture<AsteriskContainerFixture>`

- [ ] **Step 1: Find all test files using IClassFixture**

Run: `grep -rl "IClassFixture<AsteriskContainerFixture>" Tests/Asterisk.Sdk.FunctionalTests/`

- [ ] **Step 2: For each file, apply the migration pattern**

Replace:
```csharp
public sealed class SomeTests : FunctionalTestBase, IClassFixture<AsteriskContainerFixture>
```
With:
```csharp
[Collection("Functional")]
public sealed class SomeTests : FunctionalTestBase
```

Add `FunctionalStack` constructor parameter where tests need connection info. Update `AmiConnectionFactory.Create()` calls to pass stack.

**Note:** Tests that use `[AsteriskContainerFact]` keep the attribute — it now serves as a double-check that Asterisk is reachable (belt and suspenders).

- [ ] **Step 3: Create RealtimeCollection**

```csharp
[CollectionDefinition("Realtime")]
public sealed class RealtimeCollection : ICollectionFixture<RealtimeStack>;
```

Migrate `[RealtimeFact]` tests to `[Collection("Realtime")]`.

- [ ] **Step 4: Ensure Trait categories are set**

Every test class should have `[Trait("Category", "Functional")]` or `[Trait("Category", "Realtime")]` for CI filtering.

- [ ] **Step 5: Build entire solution**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 warnings, 0 errors

- [ ] **Step 6: Run functional tests**

Run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "Category=Functional"`
Expected: Tests that need Docker either run (if Docker available) or skip cleanly.

- [ ] **Step 7: Commit**

Message: `refactor(functional): migrate all functional tests to Testcontainers collections`

---

### Task 7: Wire IntegrationTests to IntegrationStack

**Files:**
- Modify: `Tests/Asterisk.Sdk.IntegrationTests/Asterisk.Sdk.IntegrationTests.csproj`
- Create: `Tests/Asterisk.Sdk.IntegrationTests/Infrastructure/IntegrationCollection.cs`
- Modify: Integration test files to use `[Collection("Integration")]`

- [ ] **Step 1: Add TestInfrastructure reference**

- [ ] **Step 2: Create IntegrationCollection**

```csharp
[CollectionDefinition("Integration")]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationStack>;
```

- [ ] **Step 3: Migrate test files**

Replace `[AsteriskAvailableFact]` pattern with `[Collection("Integration")]` + `[Trait("Category", "Integration")]`. Inject `IntegrationStack` and use `stack.Asterisk.Host`/`stack.Asterisk.AmiPort`.

- [ ] **Step 4: Build and run**

Run: `dotnet test Tests/Asterisk.Sdk.IntegrationTests/ --filter "Category=Integration"`

- [ ] **Step 5: Commit**

Message: `refactor(integration): migrate integration tests to Testcontainers IntegrationStack`

---

### Task 8: CI Pipeline

**Files:**
- Create: `.github/workflows/ci.yml`

- [ ] **Step 1: Create CI workflow**

```yaml
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
      - run: dotnet test Asterisk.Sdk.slnx --no-build --filter "Category!=Functional&Category!=Integration&Category!=Realtime"

  aot-check:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: bash tools/verify-aot.sh

  functional-tests:
    runs-on: ubuntu-latest
    needs: unit-tests
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet test Asterisk.Sdk.slnx --filter "Category=Functional|Category=Integration|Category=Realtime" --logger "console;verbosity=detailed"
        timeout-minutes: 15
```

- [ ] **Step 2: Verify locally that filter works**

Run: `dotnet test Asterisk.Sdk.slnx --filter "Category!=Functional&Category!=Integration&Category!=Realtime" --list-tests 2>&1 | tail -5`
Expected: Only unit tests listed, no functional/integration tests.

- [ ] **Step 3: Commit**

Message: `ci: add GitHub Actions pipeline with unit, AOT, and functional test jobs`

---

### Task 9: Verify Full Pipeline + Cleanup

- [ ] **Step 1: Run unit tests (no Docker)**

Run: `dotnet test Asterisk.Sdk.slnx --filter "Category!=Functional&Category!=Integration&Category!=Realtime"`
Expected: All ~1267 unit tests pass, 0 skipped.

- [ ] **Step 2: Run functional tests (with Docker)**

Run: `dotnet test Asterisk.Sdk.slnx --filter "Category=Functional|Category=Integration|Category=Realtime"`
Expected: Testcontainers auto-provisions containers. Tests run. Containers cleaned up.

- [ ] **Step 3: Verify containers are destroyed**

Run: `docker ps -a --filter "label=org.testcontainers=true"`
Expected: No containers remaining.

- [ ] **Step 4: Run without Docker daemon to verify skip**

Stop Docker, run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "Category=Functional"`
Expected: Tests skip gracefully (not error).

- [ ] **Step 5: Full solution build**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 warnings, 0 errors.

- [ ] **Step 6: Commit any final fixes**

Message: `test: verify Testcontainers pipeline end-to-end`
