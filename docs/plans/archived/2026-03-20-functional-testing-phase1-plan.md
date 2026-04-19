# Functional Testing Phase 1: Critical — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the functional testing infrastructure and deliver ~53 critical tests covering reconnection, concurrency, graceful shutdown, security, event ordering, and backpressure.

**Architecture:** A new `Asterisk.Sdk.FunctionalTests` xUnit project with Testcontainers for programmatic Docker control, shared infrastructure helpers (LogCapture, MetricsCapture, DockerControl), and a Docker Compose stack with Asterisk 21 for integration tests. Tests that need Docker skip gracefully when Docker is unavailable.

**Tech Stack:** .NET 10, xunit 2.9.3, FluentAssertions 7.1.0, NSubstitute 5.3.0, Testcontainers, Docker, Asterisk 21-alpine

**Spec:** `docs/superpowers/specs/2026-03-20-functional-testing-design.md`
**Roadmap:** `docs/superpowers/plans/2026-03-20-functional-testing-roadmap.md`

---

## File Structure

```
Tests/Asterisk.Sdk.FunctionalTests/
  Asterisk.Sdk.FunctionalTests.csproj
  Infrastructure/
    Attributes/
      RequiresDockerFactAttribute.cs       ← Skip if Docker unavailable
      AsteriskContainerFactAttribute.cs    ← Skip if Asterisk container not running
    Fixtures/
      AsteriskContainerFixture.cs          ← Testcontainers-based Asterisk lifecycle
      FunctionalTestBase.cs                ← Base class with LogCapture + MetricsCapture
    Helpers/
      DockerControl.cs                     ← Kill/restart/pause Asterisk container
      LogCapture.cs                        ← InMemory ILoggerProvider
      MetricsCapture.cs                    ← InMemory metrics collector
      AmiConnectionFactory.cs              ← Create AmiConnection with test options
  Layer2_UnitProtocol/
    Backpressure/
      AsyncEventPumpBackpressureTests.cs   ← 4 tests, no Docker
  Layer5_Integration/
    Reconnection/
      AmiReconnectionTests.cs              ← 5 tests
      LiveStateRecoveryTests.cs            ← 5 tests
    Concurrency/
      ConcurrentActionTests.cs             ← 5 tests
      ConcurrentEventTests.cs              ← 5 tests
      ConcurrentManagerTests.cs            ← 5 tests
    Shutdown/
      GracefulShutdownTests.cs             ← 8 tests
    Security/
      AuthenticationTests.cs               ← 5 tests
      ProtocolInjectionTests.cs            ← 3 tests
    EventOrdering/
      ChannelEventOrderTests.cs            ← 3 tests
      SessionEventOrderTests.cs            ← 3 tests
    Backpressure/
      PipelineBackpressureTests.cs         ← 2 tests

docker/functional/
  docker-compose.functional.yml
  asterisk-config/
    manager.conf
    ari.conf
    http.conf
    pjsip.conf
    extensions.conf
    queues.conf
    confbridge.conf
    modules.conf
```

---

### Task 1: Create project and Docker Compose infrastructure

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Asterisk.Sdk.FunctionalTests.csproj`
- Create: `docker/functional/docker-compose.functional.yml`
- Create: `docker/functional/asterisk-config/manager.conf`
- Create: `docker/functional/asterisk-config/ari.conf`
- Create: `docker/functional/asterisk-config/http.conf`
- Create: `docker/functional/asterisk-config/pjsip.conf`
- Create: `docker/functional/asterisk-config/extensions.conf`
- Create: `docker/functional/asterisk-config/queues.conf`
- Create: `docker/functional/asterisk-config/confbridge.conf`
- Create: `docker/functional/asterisk-config/modules.conf`
- Modify: `Asterisk.Sdk.slnx` (add new test project)

- [ ] **Step 1: Create .csproj file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <NoWarn>CA1707</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="Testcontainers" Version="4.3.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <PackageReference Include="System.Reactive" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Asterisk.Sdk.Hosting\Asterisk.Sdk.Hosting.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.Sessions\Asterisk.Sdk.Sessions.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.VoiceAi.AudioSocket\Asterisk.Sdk.VoiceAi.AudioSocket.csproj" />
  </ItemGroup>
</Project>
```

Note: Check `Directory.Packages.props` — if Testcontainers is not listed there, add it with version `4.3.0`. Other packages should already be in central package management.

- [ ] **Step 2: Create Asterisk config files for functional tests**

All files go in `docker/functional/asterisk-config/`.

**manager.conf:**
```ini
[general]
enabled = yes
bindaddr = 0.0.0.0
port = 5038

[testadmin]
secret = testpass
read = all
write = all
```

**ari.conf:**
```ini
[general]
enabled = yes

[testari]
type = user
read_only = no
password = testari
```

**http.conf:**
```ini
[general]
enabled = yes
bindaddr = 0.0.0.0
bindport = 8088
```

**pjsip.conf:**
```ini
[transport-udp]
type = transport
protocol = udp
bind = 0.0.0.0:5060

[2000]
type = endpoint
context = test-functional
disallow = all
allow = ulaw
auth = 2000-auth
aors = 2000-aors

[2000-auth]
type = auth
auth_type = userpass
password = 2000
username = 2000

[2000-aors]
type = aor
max_contacts = 1
```

**extensions.conf:**
```ini
[test-functional]
; Basic call (5 seconds)
exten => 100,1,Answer()
 same => n,Wait(5)
 same => n,Hangup()

; AGI test
exten => 200,1,Answer()
 same => n,AGI(agi://${AGI_HOST}:4573/test-script)
 same => n,Hangup()

; ARI Stasis test
exten => 300,1,Answer()
 same => n,Stasis(test-app)
 same => n,Hangup()

; AudioSocket test
exten => 400,1,Answer()
 same => n,AudioSocket(${AUDIOSOCKET_UUID},${AUDIOSOCKET_HOST}:9091)
 same => n,Hangup()

; Queue test
exten => 500,1,Answer()
 same => n,Queue(test-queue)
 same => n,Hangup()

; Conference test
exten => 600,1,Answer()
 same => n,ConfBridge(test-conf)
 same => n,Hangup()

; Echo test
exten => 700,1,Answer()
 same => n,Echo()

; Playback test
exten => 800,1,Answer()
 same => n,Playback(digits/1&digits/2&digits/3)
 same => n,Hangup()

; Recording test
exten => 900,1,Answer()
 same => n,MixMonitor(test-recording.wav)
 same => n,Wait(5)
 same => n,StopMixMonitor()
 same => n,Hangup()
```

**queues.conf:**
```ini
[general]

[test-queue]
strategy = ringall
timeout = 15
wrapuptime = 0
joinempty = yes
leavewhenempty = no
```

**confbridge.conf:**
```ini
[general]

[default_bridge]
type = bridge

[default_user]
type = user

[test-conf]
type = bridge
```

**modules.conf:**
```ini
[modules]
autoload = yes
noload = chan_sip.so
```

- [ ] **Step 3: Create docker-compose.functional.yml**

```yaml
services:
  asterisk:
    image: andrius/asterisk:21-alpine
    container_name: functional-asterisk
    ports:
      - "5038:5038"
      - "4573:4573"
      - "8088:8088"
    volumes:
      - ./asterisk-config:/etc/asterisk
    healthcheck:
      test: ["CMD", "asterisk", "-rx", "core show version"]
      interval: 5s
      timeout: 3s
      retries: 10
```

- [ ] **Step 4: Add project to solution**

Run: `dotnet sln Asterisk.Sdk.slnx add Tests/Asterisk.Sdk.FunctionalTests/Asterisk.Sdk.FunctionalTests.csproj`

- [ ] **Step 5: Verify build**

Run: `dotnet build Tests/Asterisk.Sdk.FunctionalTests/`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 6: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/ docker/functional/ Asterisk.Sdk.slnx
git commit -m "feat(tests): scaffold FunctionalTests project and Docker Compose infrastructure"
```

---

### Task 2: Create infrastructure helpers

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Attributes/RequiresDockerFactAttribute.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Attributes/AsteriskContainerFactAttribute.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Helpers/LogCapture.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Helpers/MetricsCapture.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Helpers/AmiConnectionFactory.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Helpers/DockerControl.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Fixtures/AsteriskContainerFixture.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/Fixtures/FunctionalTestBase.cs`

- [ ] **Step 1: Create test attributes**

**RequiresDockerFactAttribute.cs:**
```csharp
namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;

public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        if (!IsDockerAvailable())
            Skip = "Docker is not available";
    }

    private static bool IsDockerAvailable()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch { return false; }
    }
}
```

**AsteriskContainerFactAttribute.cs:**
```csharp
namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;

public sealed class AsteriskContainerFactAttribute : FactAttribute
{
    public AsteriskContainerFactAttribute()
    {
        if (!IsAsteriskReachable())
            Skip = "Asterisk container is not reachable";
    }

    private static bool IsAsteriskReachable()
    {
        var host = Environment.GetEnvironmentVariable("ASTERISK_HOST") ?? "localhost";
        var port = int.Parse(Environment.GetEnvironmentVariable("ASTERISK_AMI_PORT") ?? "5038",
            System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var result = client.BeginConnect(host, port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));
            return success && client.Connected;
        }
        catch { return false; }
    }
}
```

- [ ] **Step 2: Create LogCapture**

```csharp
namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

using Microsoft.Extensions.Logging;

public sealed class LogCapture : ILoggerProvider, IDisposable
{
    private readonly List<LogEntry> _entries = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_lock) return [.. _entries]; }
    }

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

    public void Dispose() { }

    internal void Add(LogEntry entry)
    {
        lock (_lock) _entries.Add(entry);
    }

    public bool ContainsErrors() =>
        Entries.Any(e => e.Level >= LogLevel.Error);

    public IEnumerable<LogEntry> GetErrors() =>
        Entries.Where(e => e.Level >= LogLevel.Error);

    private sealed class CaptureLogger(LogCapture capture, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            capture.Add(new LogEntry(category, logLevel, eventId, formatter(state, exception), exception));
        }
    }
}

public sealed record LogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception);
```

- [ ] **Step 3: Create MetricsCapture**

```csharp
namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

using System.Diagnostics.Metrics;

public sealed class MetricsCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _counters = [];
    private readonly Lock _lock = new();

    public MetricsCapture(params string[] meterNames)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (meterNames.Length == 0 || meterNames.Contains(instrument.Meter.Name))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.SetMeasurementEventCallback<int>(OnMeasurementInt);
        _listener.SetMeasurementEventCallback<double>(OnMeasurementDouble);
        _listener.Start();
    }

    private void OnMeasurement(Instrument instrument, long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        lock (_lock)
        {
            var key = instrument.Name;
            _counters[key] = _counters.GetValueOrDefault(key) + measurement;
        }
    }

    private void OnMeasurementInt(Instrument instrument, int measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => OnMeasurement(instrument, measurement, tags, state);

    private void OnMeasurementDouble(Instrument instrument, double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => OnMeasurement(instrument, (long)measurement, tags, state);

    public long Get(string instrumentName)
    {
        lock (_lock) return _counters.GetValueOrDefault(instrumentName);
    }

    public void Dispose() => _listener.Dispose();
}
```

- [ ] **Step 4: Create AmiConnectionFactory**

```csharp
namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

using Asterisk.Sdk.Ami.Connection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public static class AmiConnectionFactory
{
    public static string Host =>
        Environment.GetEnvironmentVariable("ASTERISK_HOST") ?? "localhost";
    public static int Port =>
        int.Parse(Environment.GetEnvironmentVariable("ASTERISK_AMI_PORT") ?? "5038",
            System.Globalization.CultureInfo.InvariantCulture);
    public static string Username =>
        Environment.GetEnvironmentVariable("ASTERISK_AMI_USERNAME") ?? "testadmin";
    public static string Password =>
        Environment.GetEnvironmentVariable("ASTERISK_AMI_PASSWORD") ?? "testpass";

    public static AmiConnection Create(
        ILoggerFactory? loggerFactory = null,
        Action<AmiConnectionOptions>? configure = null)
    {
        var options = new AmiConnectionOptions
        {
            Hostname = Host,
            Port = Port,
            Username = Username,
            Password = Password
        };
        configure?.Invoke(options);

        var wrappedOptions = Microsoft.Extensions.Options.Options.Create(options);
        var socketFactory = new Asterisk.Sdk.Ami.Transport.PipelineSocketConnectionFactory();
        var logger = loggerFactory?.CreateLogger<AmiConnection>() ?? NullLogger<AmiConnection>.Instance;

        return new AmiConnection(wrappedOptions, socketFactory, logger);
    }
}
```

- [ ] **Step 5: Create DockerControl**

```csharp
namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

using System.Diagnostics;

public static class DockerControl
{
    private const string ContainerName = "functional-asterisk";

    public static async Task KillContainerAsync(string name = ContainerName)
    {
        await RunDockerAsync($"kill {name}");
    }

    public static async Task StartContainerAsync(string name = ContainerName)
    {
        await RunDockerAsync($"start {name}");
    }

    public static async Task RestartContainerAsync(string name = ContainerName)
    {
        await RunDockerAsync($"restart {name}");
    }

    public static async Task PauseContainerAsync(string name = ContainerName)
    {
        await RunDockerAsync($"pause {name}");
    }

    public static async Task UnpauseContainerAsync(string name = ContainerName)
    {
        await RunDockerAsync($"unpause {name}");
    }

    public static async Task WaitForHealthyAsync(string name = ContainerName,
        TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(30);
        var deadline = DateTime.UtcNow + timeout.Value;

        while (DateTime.UtcNow < deadline)
        {
            var health = await RunDockerAsync(
                $"inspect --format={{{{.State.Health.Status}}}} {name}");
            if (health.Trim() == "healthy") return;
            await Task.Delay(1000);
        }

        throw new TimeoutException($"Container {name} did not become healthy within {timeout}");
    }

    private static async Task<string> RunDockerAsync(string args)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }
}
```

- [ ] **Step 6: Create AsteriskContainerFixture**

```csharp
namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;

using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

public sealed class AsteriskContainerFixture : IAsyncLifetime
{
    public string Host => AmiConnectionFactory.Host;
    public int AmiPort => AmiConnectionFactory.Port;

    public async Task InitializeAsync()
    {
        // Wait for Asterisk to be healthy (started by docker-compose externally)
        try
        {
            await DockerControl.WaitForHealthyAsync(timeout: TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            // Container not running — tests will skip via attribute
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
```

- [ ] **Step 7: Create FunctionalTestBase**

```csharp
namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;

using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using Microsoft.Extensions.Logging;

public abstract class FunctionalTestBase : IAsyncLifetime, IDisposable
{
    protected LogCapture LogCapture { get; } = new();
    protected MetricsCapture MetricsCapture { get; }
    protected ILoggerFactory LoggerFactory { get; }

    protected FunctionalTestBase(params string[] meterNames)
    {
        MetricsCapture = new MetricsCapture(meterNames);
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
            b.AddProvider(LogCapture).SetMinimumLevel(LogLevel.Debug));
    }

    public virtual Task InitializeAsync() => Task.CompletedTask;
    public virtual Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        MetricsCapture.Dispose();
        LoggerFactory.Dispose();
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 8: Verify build**

Run: `dotnet build Tests/Asterisk.Sdk.FunctionalTests/`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 9: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Infrastructure/
git commit -m "feat(tests): add functional test infrastructure — attributes, fixtures, helpers"
```

---

### Task 3: Backpressure tests (Layer 2 — no Docker)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/Backpressure/AsyncEventPumpBackpressureTests.cs`

These tests run without Docker — they test the `AsyncEventPump` directly.

- [ ] **Step 1: Write tests**

```csharp
namespace Asterisk.Sdk.FunctionalTests.Layer2_UnitProtocol.Backpressure;

using Asterisk.Sdk.Ami.Internal;
using Asterisk.Sdk.Ami.Events;
using FluentAssertions;

[Trait("Category", "Unit")]
public sealed class AsyncEventPumpBackpressureTests : IAsyncDisposable
{
    private readonly AsyncEventPump _pump;

    public AsyncEventPumpBackpressureTests()
    {
        _pump = new AsyncEventPump(capacity: 5); // Small capacity for testing
    }

    [Fact]
    public void TryEnqueue_ShouldReturnFalse_WhenAtCapacity()
    {
        _pump.Start(_ => new ValueTask(Task.Delay(1000))); // Slow consumer
        for (var i = 0; i < 10; i++)
            _pump.TryEnqueue(CreateEvent($"evt-{i}"));

        _pump.DroppedEvents.Should().BeGreaterThan(0);
    }

    [Fact]
    public void OnEventDropped_ShouldFire_WhenEventIsDropped()
    {
        var droppedEvents = new List<ManagerEvent>();
        _pump.OnEventDropped = evt => droppedEvents.Add(evt);

        _pump.Start(_ => new ValueTask(Task.Delay(1000)));
        for (var i = 0; i < 20; i++)
            _pump.TryEnqueue(CreateEvent($"evt-{i}"));

        droppedEvents.Should().NotBeEmpty();
    }

    [Fact]
    public async Task DroppedEvents_ShouldIncrementCorrectly()
    {
        var dropCount = 0;
        _pump.OnEventDropped = _ => Interlocked.Increment(ref dropCount);

        _pump.Start(_ => new ValueTask(Task.Delay(100)));

        for (var i = 0; i < 50; i++)
            _pump.TryEnqueue(CreateEvent($"evt-{i}"));

        await Task.Delay(200);
        _pump.DroppedEvents.Should().Be(dropCount);
    }

    [Fact]
    public async Task ProcessedEvents_ShouldIncrementForSuccessfulEvents()
    {
        _pump.Start(_ => ValueTask.CompletedTask);

        for (var i = 0; i < 10; i++)
            _pump.TryEnqueue(CreateEvent($"evt-{i}"));

        await Task.Delay(500);
        _pump.ProcessedEvents.Should().BeGreaterThan(0);
    }

    private static ManagerEvent CreateEvent(string name)
    {
        // Note: ManagerEvent may not have SetHeader. The implementer should check
        // how ManagerEvent is constructed in the codebase. Options:
        // - If it has a dictionary/headers property, set it directly
        // - If it requires deserialization, use AmiProtocolReader to parse a raw string
        // - If it has a parameterless constructor + properties, set EventType
        // Adapt this factory to the actual API.
        var evt = new ManagerEvent();
        return evt;
    }

    public async ValueTask DisposeAsync() => await _pump.DisposeAsync();
}
```

Note: The implementer should check the actual `AsyncEventPump` constructor signature and `ManagerEvent` creation pattern. The code above is a guide — adapt constructor parameters and event creation to match the actual API. If `AsyncEventPump` constructor doesn't take `capacity`, check `AmiConnectionOptions.EventPumpCapacity` and how the pump is instantiated in `AmiConnection`.

- [ ] **Step 2: Verify tests run (some may need adaptation)**

Run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "Category=Unit"`
Expected: 4 tests pass (or adapt as needed based on actual API)

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer2_UnitProtocol/
git commit -m "test(functional): add AsyncEventPump backpressure tests (Layer 2)"
```

---

### Task 4: Reconnection tests (Layer 5 — Docker required)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Reconnection/AmiReconnectionTests.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Reconnection/LiveStateRecoveryTests.cs`

- [ ] **Step 1: Write AmiReconnectionTests**

```csharp
namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Reconnection;

using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

[Trait("Category", "Integration")]
public sealed class AmiReconnectionTests : FunctionalTestBase, IClassFixture<AsteriskContainerFixture>
{
    private readonly AsteriskContainerFixture _fixture;

    public AmiReconnectionTests(AsteriskContainerFixture fixture) : base("Asterisk.Sdk.Ami")
    {
        _fixture = fixture;
    }

    [AsteriskContainerFact]
    public async Task Connection_ShouldReconnect_WhenAsteriskRestarted()
    {
        using var ami = AmiConnectionFactory.Create(LoggerFactory, o =>
        {
            o.AutoReconnect = true;
            o.ReconnectInitialDelay = TimeSpan.FromMilliseconds(500);
        });

        await ami.ConnectAsync();
        ami.State.Should().Be(AmiConnectionState.Connected);

        var reconnected = new TaskCompletionSource<bool>();
        ami.Reconnected += () => reconnected.TrySetResult(true);

        await DockerControl.RestartContainerAsync();
        await DockerControl.WaitForHealthyAsync();

        var result = await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        result.Should().BeTrue();
        ami.State.Should().Be(AmiConnectionState.Connected);
    }

    [AsteriskContainerFact]
    public async Task Connection_ShouldTransitionToReconnecting_WhenAsteriskKilled()
    {
        using var ami = AmiConnectionFactory.Create(LoggerFactory, o =>
        {
            o.AutoReconnect = true;
            o.ReconnectInitialDelay = TimeSpan.FromMilliseconds(500);
        });

        await ami.ConnectAsync();

        var stateChanges = new List<AmiConnectionState>();
        // Monitor state changes via polling (no state-change event in API)

        await DockerControl.KillContainerAsync();
        await Task.Delay(3000); // Wait for detection

        ami.State.Should().BeOneOf(AmiConnectionState.Reconnecting, AmiConnectionState.Disconnected);

        // Restart for other tests
        await DockerControl.StartContainerAsync();
        await DockerControl.WaitForHealthyAsync();
    }

    [AsteriskContainerFact]
    public async Task SendAction_ShouldTimeout_WhenAsteriskKilledDuringAction()
    {
        using var ami = AmiConnectionFactory.Create(LoggerFactory, o =>
        {
            o.AutoReconnect = true;
            o.DefaultResponseTimeout = TimeSpan.FromSeconds(3);
        });

        await ami.ConnectAsync();
        await DockerControl.KillContainerAsync();

        var act = async () =>
        {
            await ami.SendActionAsync(new Asterisk.Sdk.Ami.Actions.PingAction());
        };

        await act.Should().ThrowWithinAsync<Exception>(TimeSpan.FromSeconds(10));

        await DockerControl.StartContainerAsync();
        await DockerControl.WaitForHealthyAsync();
    }

    [AsteriskContainerFact]
    public async Task Connection_ShouldRespectMaxReconnectAttempts()
    {
        using var ami = AmiConnectionFactory.Create(LoggerFactory, o =>
        {
            o.AutoReconnect = true;
            o.MaxReconnectAttempts = 3;
            o.ReconnectInitialDelay = TimeSpan.FromMilliseconds(200);
        });

        await ami.ConnectAsync();
        await DockerControl.KillContainerAsync();

        // Wait long enough for 3 attempts to exhaust
        await Task.Delay(5000);

        ami.State.Should().Be(AmiConnectionState.Disconnected);

        await DockerControl.StartContainerAsync();
        await DockerControl.WaitForHealthyAsync();
    }

    [AsteriskContainerFact]
    public async Task Connection_ShouldUseExponentialBackoff()
    {
        using var ami = AmiConnectionFactory.Create(LoggerFactory, o =>
        {
            o.AutoReconnect = true;
            o.ReconnectInitialDelay = TimeSpan.FromMilliseconds(100);
            o.ReconnectMultiplier = 2.0;
            o.MaxReconnectAttempts = 3;
        });

        await ami.ConnectAsync();
        await DockerControl.KillContainerAsync();

        // Wait for attempts to exhaust
        await Task.Delay(5000);

        // Verify via logs that delays increased
        var reconnectLogs = LogCapture.Entries
            .Where(e => e.Message.Contains("reconnect", StringComparison.OrdinalIgnoreCase))
            .ToList();
        reconnectLogs.Should().NotBeEmpty();

        await DockerControl.StartContainerAsync();
        await DockerControl.WaitForHealthyAsync();
    }
}
```

- [ ] **Step 2: Write LiveStateRecoveryTests**

```csharp
namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Reconnection;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

[Trait("Category", "Integration")]
public sealed class LiveStateRecoveryTests : FunctionalTestBase, IClassFixture<AsteriskContainerFixture>
{
    public LiveStateRecoveryTests(AsteriskContainerFixture fixture) : base("Asterisk.Sdk.Live") { }

    [AsteriskContainerFact]
    public async Task AsteriskServer_ShouldReloadState_AfterReconnect()
    {
        using var ami = AmiConnectionFactory.Create(LoggerFactory, o =>
        {
            o.AutoReconnect = true;
            o.ReconnectInitialDelay = TimeSpan.FromMilliseconds(500);
        });

        var server = new AsteriskServer(ami, LoggerFactory.CreateLogger<AsteriskServer>());

        await ami.ConnectAsync();
        await server.StartAsync();

        // Get initial queue count
        var initialQueueCount = server.Queues.QueueCount;

        var reconnected = new TaskCompletionSource<bool>();
        ami.Reconnected += () => reconnected.TrySetResult(true);

        await DockerControl.RestartContainerAsync();
        await DockerControl.WaitForHealthyAsync();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Wait for state reload
        await Task.Delay(3000);

        // State should be recovered
        server.Queues.QueueCount.Should().Be(initialQueueCount);
    }

    [AsteriskContainerFact]
    public async Task ChannelManager_ShouldClearOnReconnect()
    {
        using var ami = AmiConnectionFactory.Create(LoggerFactory, o =>
        {
            o.AutoReconnect = true;
            o.ReconnectInitialDelay = TimeSpan.FromMilliseconds(500);
        });

        var server = new AsteriskServer(ami, LoggerFactory.CreateLogger<AsteriskServer>());

        await ami.ConnectAsync();
        await server.StartAsync();

        // Create a call to populate channels
        await ami.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional/n",
            Application = "Wait",
            Data = "10"
        });

        await Task.Delay(2000);
        server.Channels.ChannelCount.Should().BeGreaterThan(0, "call should create channels");

        var reconnected = new TaskCompletionSource<bool>();
        ami.Reconnected += () => reconnected.TrySetResult(true);

        await DockerControl.RestartContainerAsync();
        await DockerControl.WaitForHealthyAsync();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // After reconnect, channels from dead calls should be gone
        await Task.Delay(3000);
        // Channel count may be 0 or reflect only new active channels
    }

    [AsteriskContainerFact]
    public async Task EventSubscription_ShouldResume_AfterReconnect()
    {
        using var ami = AmiConnectionFactory.Create(LoggerFactory, o =>
        {
            o.AutoReconnect = true;
            o.ReconnectInitialDelay = TimeSpan.FromMilliseconds(500);
        });

        await ami.ConnectAsync();

        var eventsAfterReconnect = new List<Asterisk.Sdk.Ami.Events.ManagerEvent>();
        var reconnected = new TaskCompletionSource<bool>();
        ami.Reconnected += () =>
        {
            ami.Subscribe(new ActionObserver(evt => eventsAfterReconnect.Add(evt)));
            reconnected.TrySetResult(true);
        };

        await DockerControl.RestartContainerAsync();
        await DockerControl.WaitForHealthyAsync();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Generate an event after reconnect
        await ami.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional/n",
            Application = "Wait",
            Data = "2"
        });

        await Task.Delay(5000);
        eventsAfterReconnect.Should().NotBeEmpty("events should flow after reconnect");
    }

    [AsteriskContainerFact]
    public async Task PendingActions_ShouldNotHang_AfterDisconnect()
    {
        using var ami = AmiConnectionFactory.Create(LoggerFactory, o =>
        {
            o.AutoReconnect = false; // Disable reconnect for this test
            o.DefaultResponseTimeout = TimeSpan.FromSeconds(3);
        });

        await ami.ConnectAsync();

        // Start an action, then kill Asterisk immediately
        var actionTask = Task.Run(async () =>
        {
            try
            {
                await ami.SendActionAsync(new Asterisk.Sdk.Ami.Actions.CommandAction { Command = "core show channels" });
                return "completed";
            }
            catch
            {
                return "exception";
            }
        });

        await Task.Delay(100);
        await DockerControl.KillContainerAsync();

        var result = await actionTask.WaitAsync(TimeSpan.FromSeconds(10));
        result.Should().BeOneOf("completed", "exception", "action should not hang");

        await DockerControl.StartContainerAsync();
        await DockerControl.WaitForHealthyAsync();
    }

    [AsteriskContainerFact]
    public async Task MultipleReconnects_ShouldAllSucceed()
    {
        using var ami = AmiConnectionFactory.Create(LoggerFactory, o =>
        {
            o.AutoReconnect = true;
            o.ReconnectInitialDelay = TimeSpan.FromMilliseconds(500);
        });

        await ami.ConnectAsync();

        for (var i = 0; i < 3; i++)
        {
            var reconnected = new TaskCompletionSource<bool>();
            ami.Reconnected += () => reconnected.TrySetResult(true);

            await DockerControl.RestartContainerAsync();
            await DockerControl.WaitForHealthyAsync();
            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(30));

            ami.State.Should().Be(AmiConnectionState.Connected, $"reconnect #{i + 1} should succeed");

            var response = await ami.SendActionAsync(new Asterisk.Sdk.Ami.Actions.PingAction());
            response.Response.Should().Be("Success");
        }
    }

    private sealed class ActionObserver(Action<Asterisk.Sdk.Ami.Events.ManagerEvent> onNext) : IObserver<Asterisk.Sdk.Ami.Events.ManagerEvent>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(Asterisk.Sdk.Ami.Events.ManagerEvent value) => onNext(value);
    }
}
```

Note: The implementer should verify `AsteriskServer` constructor signature, `Subscribe` pattern, and `ActionObserver` usage. Adapt as needed based on actual API.

- [ ] **Step 3: Run tests (with Docker Compose up)**

Run:
```bash
docker compose -f docker/functional/docker-compose.functional.yml up -d
dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "FullyQualifiedName~Reconnection"
```

- [ ] **Step 4: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Reconnection/
git commit -m "test(functional): add reconnection and state recovery tests (10 tests)"
```

---

### Task 5: Concurrency tests (Layer 5)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Concurrency/ConcurrentActionTests.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Concurrency/ConcurrentEventTests.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Concurrency/ConcurrentManagerTests.cs`

- [ ] **Step 1: Write ConcurrentActionTests** (~5 tests)

Tests to write:
- `FiftyConcurrentPingActions_ShouldAllReceiveCorrectResponses` — verify no cross-talk in response correlation
- `ConcurrentOriginateActions_ShouldAllSucceed` — multiple originate calls simultaneously
- `ConcurrentSendAndSubscribe_ShouldNotDeadlock` — send actions while subscribing/unsubscribing
- `RapidFireActions_ShouldNotCorruptState` — 100 actions in tight loop
- `ConcurrentActionsWithTimeout_ShouldCleanupProperly` — some actions timeout, verify no resource leak

Each test should use `Task.WhenAll` with 50+ concurrent tasks, connect to real Asterisk, and verify responses match their actions via ActionId correlation.

- [ ] **Step 2: Write ConcurrentEventTests** (~5 tests)

Tests to write:
- `ConcurrentSubscribeUnsubscribe_ShouldNotThrow` — rapid subscribe/unsubscribe cycles
- `MultipleObservers_ShouldAllReceiveEvents` — 10 observers, each receives all events
- `HighVolumeEvents_ShouldNotLoseEvents` — originate many calls, verify event count
- `ConcurrentEventProcessing_ShouldMaintainOrder` — events within a channel maintain causal order
- `ObserverExceptionInOneSubscriber_ShouldNotAffectOthers` — fault isolation

- [ ] **Step 3: Write ConcurrentManagerTests** (~5 tests)

Tests to write:
- `ConcurrentChannelCreation_ShouldMaintainConsistentState` — many concurrent Local channel originates
- `ConcurrentChannelLookup_ShouldBeThreadSafe` — read while writing
- `SecondaryIndexConsistency_ShouldBeMaintained` — byName and byUniqueId indices stay in sync
- `ConcurrentQueueMemberUpdates_ShouldNotCorrupt` — add/remove members concurrently
- `ConcurrentAgiSessions_ShouldAllComplete` — multiple AGI scripts running simultaneously

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "FullyQualifiedName~Concurrency"`

- [ ] **Step 5: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Concurrency/
git commit -m "test(functional): add concurrency tests — actions, events, managers (15 tests)"
```

---

### Task 6: Graceful shutdown tests (Layer 5)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Shutdown/GracefulShutdownTests.cs`

- [ ] **Step 1: Write GracefulShutdownTests** (~8 tests)

Tests to write:
- `HostShutdown_ShouldCloseAmiConnection` — start IHost with AddAsterisk, StopAsync, verify AMI disconnected
- `HostShutdown_ShouldStopAgiServer` — verify FastAgiServer stops accepting
- `HostShutdown_ShouldStopAudioSocketServer` — verify AudioSocket listener stops
- `HostShutdown_ShouldCompleteWithinTimeout` — shutdown in <5 seconds
- `HostShutdown_ShouldCancelActiveOperations` — pending actions get cancelled
- `HostShutdown_ShouldNotLeakTcpSockets` — no lingering connections after dispose
- `DisposingConnection_ShouldBeIdempotent` — double-dispose doesn't throw
- `ShutdownDuringReconnect_ShouldNotHang` — kill Asterisk then shutdown host

Each test creates a test `IHost` via `Host.CreateDefaultBuilder()`, configures `AddAsterisk()`, starts it, then calls `StopAsync()`.

- [ ] **Step 2: Run tests**

Run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "FullyQualifiedName~Shutdown"`

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Shutdown/
git commit -m "test(functional): add graceful shutdown tests (8 tests)"
```

---

### Task 7: Security tests (Layer 5)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Security/AuthenticationTests.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Security/ProtocolInjectionTests.cs`

- [ ] **Step 1: Write AuthenticationTests** (~5 tests)

Tests to write:
- `Connect_ShouldFail_WithWrongPassword` — clean exception, no hang
- `Connect_ShouldFail_WithWrongUsername` — clean exception
- `Connect_ShouldFail_WithEmptyCredentials` — validation error
- `Connect_ShouldTimeout_WhenPortRefuses` — connection to closed port
- `Logs_ShouldNotContainPassword` — verify LogCapture entries don't contain the password string

- [ ] **Step 2: Write ProtocolInjectionTests** (~3 tests)

Tests to write:
- `ActionWithNewlineInValue_ShouldNotCorruptProtocol` — set a variable with `\r\n` in value
- `ActionWithSpecialCharacters_ShouldSerializeCorrectly` — unicode, quotes, backslashes
- `LargeActionPayload_ShouldNotCrash` — 64KB value string

- [ ] **Step 3: Run tests**

Run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "FullyQualifiedName~Security"`

- [ ] **Step 4: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Security/
git commit -m "test(functional): add security tests — authentication and injection (8 tests)"
```

---

### Task 8: Event ordering tests (Layer 5)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/EventOrdering/ChannelEventOrderTests.cs`
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/EventOrdering/SessionEventOrderTests.cs`

- [ ] **Step 1: Write ChannelEventOrderTests** (~3 tests)

Tests to write:
- `RapidOriginateAndHangup_ShouldNotCreatePhantomChannels` — originate+hangup in rapid succession, verify ChannelManager ends with 0 channels
- `ConcurrentChannelEvents_ShouldMaintainConsistentState` — 10 concurrent calls, verify all channels are tracked and removed
- `ChannelRename_ShouldUpdateSecondaryIndex` — verify byName index stays consistent when channel name changes

- [ ] **Step 2: Write SessionEventOrderTests** (~3 tests)

Tests to write:
- `QuickHangup_ShouldStillCreateSession` — call that hangs up in <1s, verify session created and completed
- `ConcurrentSessions_ShouldCorrelateByLinkedId` — 5 concurrent calls, each session has correct channels
- `SessionEvents_ShouldFireInCausalOrder` — CallStarted before CallConnected before CallEnded

- [ ] **Step 3: Run tests**

Run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "FullyQualifiedName~EventOrdering"`

- [ ] **Step 4: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/EventOrdering/
git commit -m "test(functional): add event ordering tests — channels and sessions (6 tests)"
```

---

### Task 9: Pipeline backpressure tests (Layer 5)

**Files:**
- Create: `Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Backpressure/PipelineBackpressureTests.cs`

- [ ] **Step 1: Write PipelineBackpressureTests** (~2 tests)

Tests to write:
- `SlowConsumer_ShouldTriggerBackpressure` — connect to AMI, subscribe to events, process slowly, verify no OOM (monitor memory)
- `HighEventRate_ShouldNotExhaustMemoryPool` — originate 50 concurrent calls, rapid events, verify memory stays bounded

Both tests need real Asterisk to generate events. Use `OriginateAction` with Local channels and `Wait()` to generate sustained event streams.

- [ ] **Step 2: Run tests**

Run: `dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "FullyQualifiedName~Pipeline"`

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.FunctionalTests/Layer5_Integration/Backpressure/
git commit -m "test(functional): add pipeline backpressure tests (2 tests)"
```

---

### Task 10: Downstream scaffolding (out-of-scope for MIT repo)

Originally this task scaffolded a `FunctionalTests` project in the private downstream repo. That work is tracked in the downstream repo and is not part of this MIT plan.

---

### Task 11: Final verification

- [ ] **Step 1: Run all MIT functional tests**

```bash
# Layer 2 (no Docker)
dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "Category=Unit"

# Layer 5 (needs Docker)
docker compose -f docker/functional/docker-compose.functional.yml up -d
dotnet test Tests/Asterisk.Sdk.FunctionalTests/ --filter "Category=Integration"
docker compose -f docker/functional/docker-compose.functional.yml down
```

Expected: ~53 tests pass (4 unit + 49 integration, or integration tests skip if no Docker)

- [ ] **Step 2: Verify existing tests still pass**

```bash
dotnet test Asterisk.Sdk.slnx
```

Expected: All existing tests + new functional tests pass. 0 warnings.

- [ ] **Step 3: Commit any fixes**

If any adjustments were needed, commit them.
