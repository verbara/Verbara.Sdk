# Redis AOT Spike Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Validate StackExchange.Redis Native AOT compatibility and build a functional `RedisSessionStore` with integration tests against Docker Redis.

**Architecture:** Snapshot DTO (`CallSessionSnapshot`) captures `CallSession` state for AOT-safe JSON serialization via source-generated `SessionJsonContext`. `RedisSessionStore` extends `SessionStoreBase` using SE.Redis `IBatch` pipelining with `ast:` key prefix. AOT validation via a separate console project with `PublishAot=true`.

**Tech Stack:** .NET 10, StackExchange.Redis 2.12.1, System.Text.Json source generation, xunit, FluentAssertions, Docker Redis 7

**Spec:** `docs/superpowers/specs/2026-03-17-redis-aot-spike-design.md`

---

## File Structure

### New files

| File | Responsibility |
|------|---------------|
| `Tests/Asterisk.Sdk.Redis.Spike/Asterisk.Sdk.Redis.Spike.csproj` | Test project with SE.Redis dependency |
| `Tests/Asterisk.Sdk.Redis.Spike/Serialization/CallSessionSnapshot.cs` | Immutable DTO for `CallSession` serialization |
| `Tests/Asterisk.Sdk.Redis.Spike/Serialization/SessionJsonContext.cs` | Source-generated JSON context |
| `Tests/Asterisk.Sdk.Redis.Spike/Store/RedisSessionStore.cs` | `SessionStoreBase` implementation backed by Redis |
| `Tests/Asterisk.Sdk.Redis.Spike/Store/RedisSessionStoreOptions.cs` | Configuration options (prefix, TTL, DB index) |
| `Tests/Asterisk.Sdk.Redis.Spike/Tests/SnapshotSerializationTests.cs` | Serialization roundtrip unit tests |
| `Tests/Asterisk.Sdk.Redis.Spike/Tests/RedisSessionStoreTests.cs` | Integration tests against Docker Redis |
| `Tests/Asterisk.Sdk.Redis.Spike/Benchmarks/RedisLatencyBenchmark.cs` | Latency measurement tests |
| `Tests/Asterisk.Sdk.Redis.Spike/Fixtures/RedisFixture.cs` | xunit fixture for Redis connection lifecycle |
| `Tests/Asterisk.Sdk.Redis.Spike.Aot/Asterisk.Sdk.Redis.Spike.Aot.csproj` | AOT validation console app |
| `Tests/Asterisk.Sdk.Redis.Spike.Aot/Program.cs` | Minimal AOT validation: connect, set, get, hash, pubsub |
| `docker/docker-compose.redis-spike.yml` | Redis 7 container for tests |

### Modified files

| File | Change |
|------|--------|
| `src/Asterisk.Sdk.Sessions/Asterisk.Sdk.Sessions.csproj:5-8` | Add `InternalsVisibleTo` for spike project |
| `src/Asterisk.Sdk.Sessions/CallSession.cs` | Add `internal ToSnapshot()` and `static internal FromSnapshot()` methods |
| `Directory.Packages.props` | Add `StackExchange.Redis` version |
| `Asterisk.Sdk.slnx` | Add spike projects |

---

### Task 1: Project scaffolding and dependencies

**Files:**
- Create: `Tests/Asterisk.Sdk.Redis.Spike/Asterisk.Sdk.Redis.Spike.csproj`
- Create: `docker/docker-compose.redis-spike.yml`
- Modify: `Directory.Packages.props`
- Modify: `Asterisk.Sdk.slnx`
- Modify: `src/Asterisk.Sdk.Sessions/Asterisk.Sdk.Sessions.csproj:5-8`
- Modify: `src/Asterisk.Sdk.Sessions/CallSession.cs:12-13,30` (change `private` → `internal` for snapshot access)

- [ ] **Step 1: Add StackExchange.Redis to central package management**

In `Directory.Packages.props`, after the `Npgsql`/`Dapper` section (line 38), add:

```xml
  <!-- Redis (spike validation) -->
  <ItemGroup>
    <PackageVersion Include="StackExchange.Redis" Version="2.12.1" />
  </ItemGroup>
```

- [ ] **Step 2: Create the test project csproj**

Create `Tests/Asterisk.Sdk.Redis.Spike/Asterisk.Sdk.Redis.Spike.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\Asterisk.Sdk.Sessions\Asterisk.Sdk.Sessions.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="StackExchange.Redis" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Add InternalsVisibleTo for spike project**

In `src/Asterisk.Sdk.Sessions/Asterisk.Sdk.Sessions.csproj`, add after line 8:

```xml
    <InternalsVisibleTo Include="Asterisk.Sdk.Redis.Spike" />
    <InternalsVisibleTo Include="Asterisk.Sdk.Redis.Spike.Aot" />
```

- [ ] **Step 3b: Change private hold fields and State setter to internal**

`InternalsVisibleTo` only exposes `internal` members, NOT `private`. The snapshot needs read/write access to `_holdStartedAt`, `_accumulatedHoldTime`, and `State`'s setter.

In `src/Asterisk.Sdk.Sessions/CallSession.cs`:

Change line 12-13 from:
```csharp
    private DateTimeOffset? _holdStartedAt;
    private TimeSpan _accumulatedHoldTime;
```
to:
```csharp
    internal DateTimeOffset? _holdStartedAt;
    internal TimeSpan _accumulatedHoldTime;
```

Change line 30 from:
```csharp
    public CallSessionState State { get; private set; } = CallSessionState.Created;
```
to:
```csharp
    public CallSessionState State { get; internal set; } = CallSessionState.Created;
```

These are minimal visibility changes. All existing code still compiles (internal is less restrictive than private).

- [ ] **Step 4: Create docker-compose for Redis**

Create `docker/docker-compose.redis-spike.yml`:

```yaml
services:
  redis:
    image: redis:7-alpine
    ports:
      - "6379:6379"
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 5s
      timeout: 3s
      retries: 5
```

- [ ] **Step 5: Add spike projects to solution**

In `Asterisk.Sdk.slnx`, inside the `<Folder Name="/Tests/">` block, add:

```xml
    <Project Path="Tests/Asterisk.Sdk.Redis.Spike/Asterisk.Sdk.Redis.Spike.csproj" />
```

- [ ] **Step 6: Verify build**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add Directory.Packages.props Asterisk.Sdk.slnx \
  src/Asterisk.Sdk.Sessions/Asterisk.Sdk.Sessions.csproj \
  src/Asterisk.Sdk.Sessions/CallSession.cs \
  Tests/Asterisk.Sdk.Redis.Spike/Asterisk.Sdk.Redis.Spike.csproj \
  docker/docker-compose.redis-spike.yml
git commit -m "chore: scaffold Redis AOT spike project with SE.Redis dependency"
```

---

### Task 2: CallSessionSnapshot DTO

**Files:**
- Create: `Tests/Asterisk.Sdk.Redis.Spike/Serialization/CallSessionSnapshot.cs`
- Modify: `src/Asterisk.Sdk.Sessions/CallSession.cs`

- [ ] **Step 1: Create the snapshot DTO**

Create `Tests/Asterisk.Sdk.Redis.Spike/Serialization/CallSessionSnapshot.cs`:

```csharp
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions;

namespace Asterisk.Sdk.Redis.Spike.Serialization;

public sealed class CallSessionSnapshot
{
    // Identity
    public required string SessionId { get; init; }
    public required string LinkedId { get; init; }
    public required string ServerId { get; init; }

    // State
    public CallSessionState State { get; init; }
    public CallDirection Direction { get; init; }

    // Dialplan context
    public string? Context { get; init; }
    public string? Extension { get; init; }

    // Call context
    public string? QueueName { get; init; }
    public string? AgentId { get; init; }
    public string? AgentInterface { get; init; }
    public string? BridgeId { get; init; }
    public HangupCause? HangupCause { get; init; }

    // Convenience — captured at snapshot time
    public string? CallerIdNum { get; init; }
    public string? CallerIdName { get; init; }

    // Timestamps
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DialingAt { get; init; }
    public DateTimeOffset? RingingAt { get; init; }
    public DateTimeOffset? QueuedAt { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    // Hold tracking (private state in CallSession)
    public DateTimeOffset? HoldStartedAt { get; init; }
    public TimeSpan AccumulatedHoldTime { get; init; }

    // Collections
    public List<SessionParticipant> Participants { get; init; } = [];
    public List<CallSessionEvent> Events { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = [];
}
```

- [ ] **Step 2: Add ToSnapshot() and FromSnapshot() to CallSession**

In `src/Asterisk.Sdk.Sessions/CallSession.cs`, add before the closing brace (before line 132):

```csharp
    // Snapshot support for external persistence (Redis, etc.)
    internal CallSessionSnapshot ToSnapshot() => new()
    {
        SessionId = SessionId,
        LinkedId = LinkedId,
        ServerId = ServerId,
        State = State,
        Direction = Direction,
        Context = Context,
        Extension = Extension,
        QueueName = QueueName,
        AgentId = AgentId,
        AgentInterface = AgentInterface,
        BridgeId = BridgeId,
        HangupCause = HangupCause,
        CallerIdNum = CallerIdNum,
        CallerIdName = CallerIdName,
        CreatedAt = CreatedAt,
        DialingAt = DialingAt,
        RingingAt = RingingAt,
        QueuedAt = QueuedAt,
        ConnectedAt = ConnectedAt,
        CompletedAt = CompletedAt,
        HoldStartedAt = _holdStartedAt,
        AccumulatedHoldTime = _accumulatedHoldTime,
        Participants = [.. _participants],
        Events = [.. _events],
        Metadata = new Dictionary<string, string>(_metadata),
    };

    internal static CallSession FromSnapshot(CallSessionSnapshot snapshot)
    {
        var session = new CallSession(snapshot.SessionId, snapshot.LinkedId, snapshot.ServerId, snapshot.Direction)
        {
            CreatedAt = snapshot.CreatedAt,
        };

        // Restore state via internal transition bypass
        session.State = snapshot.State;
        session.Context = snapshot.Context;
        session.Extension = snapshot.Extension;
        session.QueueName = snapshot.QueueName;
        session.AgentId = snapshot.AgentId;
        session.AgentInterface = snapshot.AgentInterface;
        session.BridgeId = snapshot.BridgeId;
        session.HangupCause = snapshot.HangupCause;
        session.DialingAt = snapshot.DialingAt;
        session.RingingAt = snapshot.RingingAt;
        session.QueuedAt = snapshot.QueuedAt;
        session.ConnectedAt = snapshot.ConnectedAt;
        session.CompletedAt = snapshot.CompletedAt;
        session._holdStartedAt = snapshot.HoldStartedAt;
        session._accumulatedHoldTime = snapshot.AccumulatedHoldTime;

        foreach (var p in snapshot.Participants)
            session._participants.Add(p);
        foreach (var e in snapshot.Events)
            session._events.Add(e);
        foreach (var kv in snapshot.Metadata)
            session._metadata[kv.Key] = kv.Value;

        return session;
    }
```

Note: `ToSnapshot()` references `CallSessionSnapshot` which lives in the spike project. This creates a circular dependency. **Fix:** The snapshot DTO must live in the Sessions project itself or we need a different approach.

**Alternative approach (no circular dependency):** Move the snapshot DTO concept inline — `ToSnapshot()` returns an anonymous/dynamic type... No, that breaks AOT.

**Correct approach:** The `ToSnapshot()` and `FromSnapshot()` methods live in the **spike project** as extension methods or a static helper, since `InternalsVisibleTo` gives access to all private/internal members.

**Revised Step 2:** Instead of modifying `CallSession.cs`, create a static helper in the spike project.

Delete the changes from Step 2. Instead, in `CallSessionSnapshot.cs`, add factory methods:

```csharp
    /// <summary>Captures all state from a CallSession into an immutable snapshot.</summary>
    public static CallSessionSnapshot FromSession(CallSession session) => new()
    {
        SessionId = session.SessionId,
        LinkedId = session.LinkedId,
        ServerId = session.ServerId,
        State = session.State,
        Direction = session.Direction,
        Context = session.Context,
        Extension = session.Extension,
        QueueName = session.QueueName,
        AgentId = session.AgentId,
        AgentInterface = session.AgentInterface,
        BridgeId = session.BridgeId,
        HangupCause = session.HangupCause,
        CallerIdNum = session.CallerIdNum,
        CallerIdName = session.CallerIdName,
        CreatedAt = session.CreatedAt,
        DialingAt = session.DialingAt,
        RingingAt = session.RingingAt,
        QueuedAt = session.QueuedAt,
        ConnectedAt = session.ConnectedAt,
        CompletedAt = session.CompletedAt,
        HoldStartedAt = session._holdStartedAt,
        AccumulatedHoldTime = session._accumulatedHoldTime,
        Participants = [.. session.Participants],
        Events = [.. session.Events],
        Metadata = session.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value),
    };

    /// <summary>Reconstructs a CallSession from a snapshot.</summary>
    public CallSession ToSession()
    {
        var session = new CallSession(SessionId, LinkedId, ServerId, Direction)
        {
            CreatedAt = CreatedAt,
        };

        session.State = State;
        session.Context = Context;
        session.Extension = Extension;
        session.QueueName = QueueName;
        session.AgentId = AgentId;
        session.AgentInterface = AgentInterface;
        session.BridgeId = BridgeId;
        session.HangupCause = HangupCause;
        session.DialingAt = DialingAt;
        session.RingingAt = RingingAt;
        session.QueuedAt = QueuedAt;
        session.ConnectedAt = ConnectedAt;
        session.CompletedAt = CompletedAt;
        session._holdStartedAt = HoldStartedAt;
        session._accumulatedHoldTime = AccumulatedHoldTime;

        foreach (var p in Participants)
            session.AddParticipant(p);
        foreach (var e in Events)
            session.AddEvent(e);
        foreach (var kv in Metadata)
            session.SetMetadata(kv.Key, kv.Value);

        return session;
    }
```

This works because `InternalsVisibleTo` grants access to `_holdStartedAt`, `_accumulatedHoldTime`, `State { private set; }`, `AddParticipant`, `AddEvent`, and the `internal` setters on `Context`/`Extension`.

- [ ] **Step 3: Verify build**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add Tests/Asterisk.Sdk.Redis.Spike/Serialization/CallSessionSnapshot.cs \
  src/Asterisk.Sdk.Sessions/Asterisk.Sdk.Sessions.csproj
git commit -m "feat(spike): add CallSessionSnapshot DTO with FromSession/ToSession"
```

---

### Task 3: Source-generated JSON context

**Files:**
- Create: `Tests/Asterisk.Sdk.Redis.Spike/Serialization/SessionJsonContext.cs`

- [ ] **Step 1: Create the JSON context**

Create `Tests/Asterisk.Sdk.Redis.Spike/Serialization/SessionJsonContext.cs`:

```csharp
using System.Text.Json.Serialization;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions;

namespace Asterisk.Sdk.Redis.Spike.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CallSessionSnapshot))]
[JsonSerializable(typeof(SessionParticipant))]
[JsonSerializable(typeof(CallSessionEvent))]
[JsonSerializable(typeof(List<SessionParticipant>))]
[JsonSerializable(typeof(List<CallSessionEvent>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(CallSessionState))]
[JsonSerializable(typeof(CallSessionEventType))]
[JsonSerializable(typeof(CallDirection))]
[JsonSerializable(typeof(ParticipantRole))]
[JsonSerializable(typeof(HangupCause))]
internal partial class SessionJsonContext : JsonSerializerContext;
```

- [ ] **Step 2: Verify build (source generator runs)**

Run: `dotnet build Tests/Asterisk.Sdk.Redis.Spike/`
Expected: 0 errors, 0 warnings. Source generator produces `SessionJsonContext.*.g.cs` in `obj/`.

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.Redis.Spike/Serialization/SessionJsonContext.cs
git commit -m "feat(spike): add source-generated SessionJsonContext for AOT serialization"
```

---

### Task 4: Snapshot serialization tests (unit tests, no Redis)

**Files:**
- Create: `Tests/Asterisk.Sdk.Redis.Spike/Tests/SnapshotSerializationTests.cs`

- [ ] **Step 1: Write serialization roundtrip tests**

Create `Tests/Asterisk.Sdk.Redis.Spike/Tests/SnapshotSerializationTests.cs`:

```csharp
using System.Text.Json;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Redis.Spike.Serialization;
using Asterisk.Sdk.Sessions;
using FluentAssertions;

namespace Asterisk.Sdk.Redis.Spike.Tests;

public class SnapshotSerializationTests
{
    private static CallSession CreateTestSession()
    {
        var session = new CallSession("sess-001", "linked-001", "srv-01", CallDirection.Inbound)
        {
            CreatedAt = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero),
        };

        session.QueueName = "support";
        session.AgentId = "agent-100";
        session.AgentInterface = "SIP/100";
        session.BridgeId = "bridge-001";
        session.SetMetadata("campaign", "spring-2026");
        session.SetMetadata("priority", "high");

        session.AddParticipant(new SessionParticipant
        {
            UniqueId = "chan-001",
            Channel = "SIP/trunk-001",
            Technology = "SIP",
            Role = ParticipantRole.Caller,
            CallerIdNum = "5551234567",
            CallerIdName = "John Doe",
        });

        session.AddEvent(new CallSessionEvent(
            DateTimeOffset.UtcNow,
            CallSessionEventType.Created,
            "SIP/trunk-001", null, "Session created"));

        // Advance state: Created -> Dialing -> Connected -> OnHold
        session.TryTransition(CallSessionState.Dialing);
        session.TryTransition(CallSessionState.Connected);
        session.StartHold();

        return session;
    }

    [Fact]
    public void FromSession_ShouldCaptureAllFields()
    {
        var session = CreateTestSession();
        var snapshot = CallSessionSnapshot.FromSession(session);

        snapshot.SessionId.Should().Be("sess-001");
        snapshot.LinkedId.Should().Be("linked-001");
        snapshot.ServerId.Should().Be("srv-01");
        snapshot.State.Should().Be(CallSessionState.Connected);
        snapshot.Direction.Should().Be(CallDirection.Inbound);
        snapshot.QueueName.Should().Be("support");
        snapshot.AgentId.Should().Be("agent-100");
        snapshot.CallerIdNum.Should().Be("5551234567");
        snapshot.CallerIdName.Should().Be("John Doe");
        snapshot.HoldStartedAt.Should().NotBeNull();
        snapshot.Participants.Should().HaveCount(1);
        snapshot.Events.Should().HaveCount(1);
        snapshot.Metadata.Should().ContainKey("campaign");
    }

    [Fact]
    public void JsonRoundtrip_ShouldPreserveAllFields()
    {
        var session = CreateTestSession();
        var snapshot = CallSessionSnapshot.FromSession(session);

        var json = JsonSerializer.Serialize(snapshot, SessionJsonContext.Default.CallSessionSnapshot);
        var deserialized = JsonSerializer.Deserialize(json, SessionJsonContext.Default.CallSessionSnapshot);

        deserialized.Should().NotBeNull();
        deserialized!.SessionId.Should().Be(snapshot.SessionId);
        deserialized.State.Should().Be(snapshot.State);
        deserialized.Direction.Should().Be(snapshot.Direction);
        deserialized.CallerIdNum.Should().Be(snapshot.CallerIdNum);
        deserialized.CallerIdName.Should().Be(snapshot.CallerIdName);
        deserialized.HoldStartedAt.Should().Be(snapshot.HoldStartedAt);
        deserialized.AccumulatedHoldTime.Should().Be(snapshot.AccumulatedHoldTime);
        deserialized.Participants.Should().HaveCount(1);
        deserialized.Participants[0].UniqueId.Should().Be("chan-001");
        deserialized.Participants[0].Role.Should().Be(ParticipantRole.Caller);
        deserialized.Events.Should().HaveCount(1);
        deserialized.Metadata.Should().ContainKey("campaign");
    }

    [Fact]
    public void ToSession_ShouldReconstructFromSnapshot()
    {
        var original = CreateTestSession();
        var snapshot = CallSessionSnapshot.FromSession(original);
        var reconstructed = snapshot.ToSession();

        reconstructed.SessionId.Should().Be(original.SessionId);
        reconstructed.State.Should().Be(original.State);
        reconstructed.QueueName.Should().Be(original.QueueName);
        reconstructed.CallerIdNum.Should().Be(original.CallerIdNum);
        reconstructed.Participants.Should().HaveCount(1);
        reconstructed.Events.Should().HaveCount(1);
        reconstructed.Metadata.Should().ContainKey("campaign");
    }

    [Fact]
    public void FullRoundtrip_Session_Snapshot_Json_Snapshot_Session()
    {
        var original = CreateTestSession();

        // Session -> Snapshot -> JSON -> Snapshot -> Session
        var snapshot = CallSessionSnapshot.FromSession(original);
        var json = JsonSerializer.Serialize(snapshot, SessionJsonContext.Default.CallSessionSnapshot);
        var deserialized = JsonSerializer.Deserialize(json, SessionJsonContext.Default.CallSessionSnapshot)!;
        var reconstructed = deserialized.ToSession();

        reconstructed.SessionId.Should().Be(original.SessionId);
        reconstructed.LinkedId.Should().Be(original.LinkedId);
        reconstructed.ServerId.Should().Be(original.ServerId);
        reconstructed.State.Should().Be(original.State);
        reconstructed.Direction.Should().Be(original.Direction);
        reconstructed.AgentId.Should().Be(original.AgentId);
        reconstructed.Participants.Should().HaveCount(original.Participants.Count);
        reconstructed.Events.Should().HaveCount(original.Events.Count);
    }

    [Fact]
    public void AllEnums_ShouldSerializeAsStrings()
    {
        var snapshot = new CallSessionSnapshot
        {
            SessionId = "s1", LinkedId = "l1", ServerId = "srv1",
            State = CallSessionState.OnHold,
            Direction = CallDirection.Outbound,
            HangupCause = HangupCause.NormalClearing,
        };

        var json = JsonSerializer.Serialize(snapshot, SessionJsonContext.Default.CallSessionSnapshot);

        json.Should().Contain("\"onHold\"").Or.Contain("\"OnHold\"");
        json.Should().Contain("\"outbound\"").Or.Contain("\"Outbound\"");
        json.Should().Contain("\"normalClearing\"").Or.Contain("\"NormalClearing\"");
        json.Should().NotContain(": 16"); // NormalClearing int value — must be string
    }

    [Fact]
    public void PrivateHoldState_ShouldSurviveRoundtrip()
    {
        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        session.TryTransition(CallSessionState.Dialing);
        session.TryTransition(CallSessionState.Connected);
        session.StartHold();
        // Simulate some hold time
        session.EndHold();
        session.StartHold(); // Start a second hold

        var snapshot = CallSessionSnapshot.FromSession(session);
        snapshot.AccumulatedHoldTime.Should().BeGreaterThan(TimeSpan.Zero);
        snapshot.HoldStartedAt.Should().NotBeNull();

        var json = JsonSerializer.Serialize(snapshot, SessionJsonContext.Default.CallSessionSnapshot);
        var deserialized = JsonSerializer.Deserialize(json, SessionJsonContext.Default.CallSessionSnapshot)!;

        deserialized.AccumulatedHoldTime.Should().Be(snapshot.AccumulatedHoldTime);
        deserialized.HoldStartedAt.Should().BeCloseTo(snapshot.HoldStartedAt!.Value, TimeSpan.FromMilliseconds(1));
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test Tests/Asterisk.Sdk.Redis.Spike/ --filter "FullyQualifiedName~SnapshotSerializationTests" -v n`
Expected: 6 tests pass.

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.Redis.Spike/Tests/SnapshotSerializationTests.cs
git commit -m "test(spike): add snapshot serialization roundtrip tests"
```

---

### Task 5: Redis fixture and store options

**Files:**
- Create: `Tests/Asterisk.Sdk.Redis.Spike/Fixtures/RedisFixture.cs`
- Create: `Tests/Asterisk.Sdk.Redis.Spike/Store/RedisSessionStoreOptions.cs`

- [ ] **Step 1: Create RedisFixture**

Create `Tests/Asterisk.Sdk.Redis.Spike/Fixtures/RedisFixture.cs`:

```csharp
using StackExchange.Redis;

namespace Asterisk.Sdk.Redis.Spike.Fixtures;

public sealed class RedisFixture : IAsyncLifetime
{
    private ConnectionMultiplexer? _redis;

    public IConnectionMultiplexer Redis => _redis ?? throw new InvalidOperationException("Redis not connected");
    public IDatabase Database => Redis.GetDatabase();

    public async Task InitializeAsync()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379";
        _redis = await ConnectionMultiplexer.ConnectAsync($"{host}:{port},allowAdmin=true");
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
            await _redis.DisposeAsync();
    }

    /// <summary>Flush the current database between tests.</summary>
    public async Task FlushAsync()
    {
        var server = _redis!.GetServer(_redis.GetEndPoints()[0]);
        await server.FlushDatabaseAsync();
    }
}

[CollectionDefinition("Redis")]
public class RedisCollection : ICollectionFixture<RedisFixture>;
```

- [ ] **Step 2: Create RedisSessionStoreOptions**

Create `Tests/Asterisk.Sdk.Redis.Spike/Store/RedisSessionStoreOptions.cs`:

```csharp
namespace Asterisk.Sdk.Redis.Spike.Store;

public sealed class RedisSessionStoreOptions
{
    /// <summary>Key prefix for all Redis keys. Default: "ast:".</summary>
    public string KeyPrefix { get; set; } = "ast:";

    /// <summary>How long completed sessions remain in Redis. Default: 10 minutes.</summary>
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Redis database index. Default: 0.</summary>
    public int DatabaseIndex { get; set; }
}
```

- [ ] **Step 3: Verify build**

Run: `dotnet build Tests/Asterisk.Sdk.Redis.Spike/`
Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add Tests/Asterisk.Sdk.Redis.Spike/Fixtures/RedisFixture.cs \
  Tests/Asterisk.Sdk.Redis.Spike/Store/RedisSessionStoreOptions.cs
git commit -m "feat(spike): add Redis fixture and store options"
```

---

### Task 6: RedisSessionStore implementation

**Files:**
- Create: `Tests/Asterisk.Sdk.Redis.Spike/Store/RedisSessionStore.cs`

- [ ] **Step 1: Implement RedisSessionStore**

Create `Tests/Asterisk.Sdk.Redis.Spike/Store/RedisSessionStore.cs`:

```csharp
using System.Text.Json;
using Asterisk.Sdk.Redis.Spike.Serialization;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Asterisk.Sdk.Redis.Spike.Store;

public sealed class RedisSessionStore : SessionStoreBase
{
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisSessionStoreOptions _options;

    public RedisSessionStore(IConnectionMultiplexer redis, IOptions<RedisSessionStoreOptions> options)
    {
        _redis = redis;
        _options = options.Value;
    }

    public RedisSessionStore(IConnectionMultiplexer redis, RedisSessionStoreOptions? options = null)
    {
        _redis = redis;
        _options = options ?? new RedisSessionStoreOptions();
    }

    private IDatabase Db => _redis.GetDatabase(_options.DatabaseIndex);
    private string Key(string suffix) => $"{_options.KeyPrefix}{suffix}";

    private static readonly CallSessionState[] TerminalStates =
        [CallSessionState.Completed, CallSessionState.Failed, CallSessionState.TimedOut];

    public override async ValueTask SaveAsync(CallSession session, CancellationToken ct)
    {
        var snapshot = CallSessionSnapshot.FromSession(session);
        var json = JsonSerializer.Serialize(snapshot, SessionJsonContext.Default.CallSessionSnapshot);

        var db = Db;
        var batch = db.CreateBatch();
        var sessionKey = Key($"session:{session.SessionId}");
        var linkedKey = Key($"idx:linked:{session.LinkedId}");
        var activeSetKey = Key("sessions:active");
        var completedSetKey = Key("sessions:completed");

        // Primary storage
        _ = batch.StringSetAsync(sessionKey, json);

        // Secondary index: linkedId -> sessionId
        _ = batch.StringSetAsync(linkedKey, session.SessionId);

        if (TerminalStates.Contains(snapshot.State))
        {
            // Move from active to completed
            _ = batch.SetRemoveAsync(activeSetKey, session.SessionId);
            _ = batch.SortedSetAddAsync(completedSetKey,
                session.SessionId,
                snapshot.CompletedAt?.ToUnixTimeMilliseconds() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            // Set TTL on session and linked index
            _ = batch.KeyExpireAsync(sessionKey, _options.CompletedRetention);
            _ = batch.KeyExpireAsync(linkedKey, _options.CompletedRetention);
        }
        else
        {
            _ = batch.SetAddAsync(activeSetKey, session.SessionId);
        }

        batch.Execute();

        // Trim stale completed entries (best-effort)
        var cutoff = DateTimeOffset.UtcNow.Add(-_options.CompletedRetention).ToUnixTimeMilliseconds();
        await db.SortedSetRemoveRangeByScoreAsync(completedSetKey, double.NegativeInfinity, cutoff);
    }

    public override async ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct)
    {
        var json = await Db.StringGetAsync(Key($"session:{sessionId}"));
        if (json.IsNullOrEmpty)
            return null;

        var snapshot = JsonSerializer.Deserialize(json.ToString(), SessionJsonContext.Default.CallSessionSnapshot);
        return snapshot?.ToSession();
    }

    public override async ValueTask<CallSession?> GetByLinkedIdAsync(string linkedId, CancellationToken ct)
    {
        var sessionId = await Db.StringGetAsync(Key($"idx:linked:{linkedId}"));
        if (sessionId.IsNullOrEmpty)
            return null;

        return await GetAsync(sessionId.ToString(), ct);
    }

    public override async ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct)
    {
        var db = Db;
        var activeSetKey = Key("sessions:active");
        var sessions = new List<CallSession>();

        // Use SSCAN to avoid blocking on large sets
        await foreach (var entry in db.SetScanAsync(activeSetKey, pageSize: 500))
        {
            var json = await db.StringGetAsync(Key($"session:{entry}"));
            if (json.IsNullOrEmpty)
                continue;

            var snapshot = JsonSerializer.Deserialize(json.ToString(), SessionJsonContext.Default.CallSessionSnapshot);
            if (snapshot is not null)
                sessions.Add(snapshot.ToSession());
        }

        return sessions;
    }

    public override async ValueTask DeleteAsync(string sessionId, CancellationToken ct)
    {
        var db = Db;

        // Read session to get linkedId for index cleanup
        var json = await db.StringGetAsync(Key($"session:{sessionId}"));

        var batch = db.CreateBatch();

        _ = batch.KeyDeleteAsync(Key($"session:{sessionId}"));
        _ = batch.SetRemoveAsync(Key("sessions:active"), sessionId);
        _ = batch.SortedSetRemoveAsync(Key("sessions:completed"), sessionId);

        if (!json.IsNullOrEmpty)
        {
            var snapshot = JsonSerializer.Deserialize(json.ToString(), SessionJsonContext.Default.CallSessionSnapshot);
            if (snapshot is not null)
                _ = batch.KeyDeleteAsync(Key($"idx:linked:{snapshot.LinkedId}"));
        }

        batch.Execute();
    }

    public override async ValueTask SaveBatchAsync(IReadOnlyList<CallSession> sessions, CancellationToken ct)
    {
        var db = Db;
        var batch = db.CreateBatch();

        foreach (var session in sessions)
        {
            var snapshot = CallSessionSnapshot.FromSession(session);
            var json = JsonSerializer.Serialize(snapshot, SessionJsonContext.Default.CallSessionSnapshot);
            var sessionKey = Key($"session:{session.SessionId}");

            _ = batch.StringSetAsync(sessionKey, json);
            _ = batch.StringSetAsync(Key($"idx:linked:{session.LinkedId}"), session.SessionId);
            _ = batch.SetAddAsync(Key("sessions:active"), session.SessionId);
        }

        batch.Execute();
    }
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build Tests/Asterisk.Sdk.Redis.Spike/`
Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.Redis.Spike/Store/RedisSessionStore.cs
git commit -m "feat(spike): implement RedisSessionStore with pipeline batching"
```

---

### Task 7: Integration tests

**Files:**
- Create: `Tests/Asterisk.Sdk.Redis.Spike/Tests/RedisSessionStoreTests.cs`

**Prerequisite:** Docker Redis running: `docker compose -f docker/docker-compose.redis-spike.yml up -d`

- [ ] **Step 1: Write integration tests**

Create `Tests/Asterisk.Sdk.Redis.Spike/Tests/RedisSessionStoreTests.cs`:

```csharp
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Redis.Spike.Fixtures;
using Asterisk.Sdk.Redis.Spike.Store;
using Asterisk.Sdk.Sessions;
using FluentAssertions;

namespace Asterisk.Sdk.Redis.Spike.Tests;

[Collection("Redis")]
[Trait("Category", "Integration")]
public class RedisSessionStoreTests : IAsyncLifetime
{
    private readonly RedisFixture _fixture;
    private readonly RedisSessionStore _store;

    public RedisSessionStoreTests(RedisFixture fixture)
    {
        _fixture = fixture;
        _store = new RedisSessionStore(fixture.Redis, new RedisSessionStoreOptions
        {
            CompletedRetention = TimeSpan.FromSeconds(5),
        });
    }

    public Task InitializeAsync() => _fixture.FlushAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static CallSession CreateSession(string id = "sess-001", CallDirection dir = CallDirection.Inbound)
    {
        var session = new CallSession(id, $"linked-{id}", "srv-01", dir);
        session.QueueName = "support";
        session.AgentId = "agent-100";
        session.SetMetadata("key1", "val1");
        session.AddParticipant(new SessionParticipant
        {
            UniqueId = $"chan-{id}",
            Channel = "SIP/trunk-001",
            Technology = "SIP",
            Role = ParticipantRole.Caller,
            CallerIdNum = "5551234567",
            CallerIdName = "Test Caller",
        });
        session.AddEvent(new CallSessionEvent(
            DateTimeOffset.UtcNow, CallSessionEventType.Created,
            "SIP/trunk-001", null, "created"));
        return session;
    }

    [Fact]
    public async Task SaveAndGet_ShouldRoundtrip()
    {
        var session = CreateSession();
        session.TryTransition(CallSessionState.Dialing);
        session.TryTransition(CallSessionState.Connected);

        await _store.SaveAsync(session, CancellationToken.None);
        var loaded = await _store.GetAsync("sess-001", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.SessionId.Should().Be("sess-001");
        loaded.State.Should().Be(CallSessionState.Connected);
        loaded.Direction.Should().Be(CallDirection.Inbound);
        loaded.QueueName.Should().Be("support");
        loaded.AgentId.Should().Be("agent-100");
        loaded.CallerIdNum.Should().Be("5551234567");
        loaded.Participants.Should().HaveCount(1);
        loaded.Events.Should().HaveCount(1);
        loaded.Metadata.Should().ContainKey("key1");
    }

    [Fact]
    public async Task SaveAndGetByLinkedId_ShouldResolve()
    {
        var session = CreateSession();
        await _store.SaveAsync(session, CancellationToken.None);

        var loaded = await _store.GetByLinkedIdAsync("linked-sess-001", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.SessionId.Should().Be("sess-001");
    }

    [Fact]
    public async Task GetActive_ShouldReturnOnlyActiveSessions()
    {
        var active1 = CreateSession("active-1");
        var active2 = CreateSession("active-2");
        var completed = CreateSession("done-1");
        completed.TryTransition(CallSessionState.Dialing);
        completed.TryTransition(CallSessionState.Connected);
        completed.TryTransition(CallSessionState.Completed);

        await _store.SaveAsync(active1, CancellationToken.None);
        await _store.SaveAsync(active2, CancellationToken.None);
        await _store.SaveAsync(completed, CancellationToken.None);

        var active = (await _store.GetActiveAsync(CancellationToken.None)).ToList();

        active.Should().HaveCount(2);
        active.Select(s => s.SessionId).Should().BeEquivalentTo(["active-1", "active-2"]);
    }

    [Fact]
    public async Task Delete_ShouldRemoveSessionAndIndices()
    {
        var session = CreateSession();
        await _store.SaveAsync(session, CancellationToken.None);

        await _store.DeleteAsync("sess-001", CancellationToken.None);

        var loaded = await _store.GetAsync("sess-001", CancellationToken.None);
        loaded.Should().BeNull();

        var byLinked = await _store.GetByLinkedIdAsync("linked-sess-001", CancellationToken.None);
        byLinked.Should().BeNull();

        var active = (await _store.GetActiveAsync(CancellationToken.None)).ToList();
        active.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveBatch_ShouldPipelineAll()
    {
        var sessions = Enumerable.Range(1, 100)
            .Select(i => CreateSession($"batch-{i:D3}"))
            .ToList();

        await _store.SaveBatchAsync(sessions, CancellationToken.None);

        var active = (await _store.GetActiveAsync(CancellationToken.None)).ToList();
        active.Should().HaveCount(100);

        // Spot check
        var s50 = await _store.GetAsync("batch-050", CancellationToken.None);
        s50.Should().NotBeNull();
        s50!.QueueName.Should().Be("support");
    }

    [Fact]
    public async Task CompletedSession_ShouldExpire()
    {
        var session = CreateSession();
        session.TryTransition(CallSessionState.Dialing);
        session.TryTransition(CallSessionState.Connected);
        session.TryTransition(CallSessionState.Completed);

        // Store has CompletedRetention = 5 seconds
        await _store.SaveAsync(session, CancellationToken.None);

        // Immediately readable
        var loaded = await _store.GetAsync("sess-001", CancellationToken.None);
        loaded.Should().NotBeNull();

        // Wait for TTL
        await Task.Delay(TimeSpan.FromSeconds(6));

        var expired = await _store.GetAsync("sess-001", CancellationToken.None);
        expired.Should().BeNull();
    }

    [Fact]
    public async Task SnapshotPreservesPrivateState()
    {
        var session = CreateSession();
        session.TryTransition(CallSessionState.Dialing);
        session.TryTransition(CallSessionState.Connected);
        session.StartHold();
        await Task.Delay(50); // Accumulate some hold time
        session.EndHold();

        await _store.SaveAsync(session, CancellationToken.None);
        var loaded = await _store.GetAsync("sess-001", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.HoldTime.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task ConcurrentSaves_ShouldNotCorrupt()
    {
        var session = CreateSession();

        // 50 concurrent saves of the same session with different metadata
        var tasks = Enumerable.Range(1, 50).Select(async i =>
        {
            session.SetMetadata("counter", i.ToString());
            await _store.SaveAsync(session, CancellationToken.None);
        });

        await Task.WhenAll(tasks);

        var loaded = await _store.GetAsync("sess-001", CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.SessionId.Should().Be("sess-001");
        // The final value should be one of the 50 writes (last-writer-wins)
        int.Parse(loaded.Metadata["counter"]).Should().BeInRange(1, 50);
    }

    [Fact]
    public async Task EnumSerialization_ShouldRoundtrip()
    {
        var session = CreateSession();
        session.HangupCause = HangupCause.NormalClearing;
        session.TryTransition(CallSessionState.Dialing);
        session.TryTransition(CallSessionState.Ringing);
        session.AddParticipant(new SessionParticipant
        {
            UniqueId = "chan-002",
            Channel = "SIP/agent-100",
            Technology = "SIP",
            Role = ParticipantRole.Agent,
            HangupCause = HangupCause.UserBusy,
        });

        await _store.SaveAsync(session, CancellationToken.None);
        var loaded = await _store.GetAsync("sess-001", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.HangupCause.Should().Be(HangupCause.NormalClearing);
        loaded.State.Should().Be(CallSessionState.Ringing);
        loaded.Participants.Should().Contain(p => p.Role == ParticipantRole.Agent);
        loaded.Participants.First(p => p.Role == ParticipantRole.Agent)
            .HangupCause.Should().Be(HangupCause.UserBusy);
    }
}
```

- [ ] **Step 2: Start Redis and run integration tests**

Run:
```bash
docker compose -f docker/docker-compose.redis-spike.yml up -d
dotnet test Tests/Asterisk.Sdk.Redis.Spike/ --filter "Category=Integration" -v n
```
Expected: 9 tests pass (CompletedSession_ShouldExpire takes ~6 seconds).

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.Redis.Spike/Tests/RedisSessionStoreTests.cs
git commit -m "test(spike): add 9 Redis integration tests for RedisSessionStore"
```

---

### Task 8: Latency benchmark

**Files:**
- Create: `Tests/Asterisk.Sdk.Redis.Spike/Benchmarks/RedisLatencyBenchmark.cs`

- [ ] **Step 1: Write benchmark test**

Create `Tests/Asterisk.Sdk.Redis.Spike/Benchmarks/RedisLatencyBenchmark.cs`:

```csharp
using System.Diagnostics;
using Asterisk.Sdk.Redis.Spike.Fixtures;
using Asterisk.Sdk.Redis.Spike.Store;
using Asterisk.Sdk.Sessions;
using Xunit.Abstractions;

namespace Asterisk.Sdk.Redis.Spike.Tests;

[Collection("Redis")]
[Trait("Category", "Integration")]
public class RedisLatencyBenchmark : IAsyncLifetime
{
    private readonly RedisFixture _fixture;
    private readonly RedisSessionStore _store;
    private readonly ITestOutputHelper _output;

    public RedisLatencyBenchmark(RedisFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _store = new RedisSessionStore(fixture.Redis);
    }

    public Task InitializeAsync() => _fixture.FlushAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static CallSession CreateRealisticSession(int i)
    {
        var session = new CallSession($"bench-{i:D6}", $"link-{i:D6}", "srv-01", CallDirection.Inbound);
        session.QueueName = "support";
        session.AgentId = $"agent-{i % 100:D3}";
        session.SetMetadata("campaign", "bench");
        session.AddParticipant(new SessionParticipant
        {
            UniqueId = $"chan-{i:D6}",
            Channel = $"SIP/trunk-{i:D6}",
            Technology = "SIP",
            Role = ParticipantRole.Caller,
            CallerIdNum = $"555{i:D7}",
        });
        session.AddEvent(new CallSessionEvent(
            DateTimeOffset.UtcNow, CallSessionEventType.Created,
            $"SIP/trunk-{i:D6}", null, "created"));
        session.TryTransition(CallSessionState.Dialing);
        session.TryTransition(CallSessionState.Connected);
        return session;
    }

    [Fact]
    public async Task Benchmark_SaveLatency()
    {
        const int iterations = 1000;
        var latencies = new double[iterations];
        var sw = new Stopwatch();

        // Warmup
        for (var i = 0; i < 10; i++)
            await _store.SaveAsync(CreateRealisticSession(i + 900_000), CancellationToken.None);

        for (var i = 0; i < iterations; i++)
        {
            var session = CreateRealisticSession(i);
            sw.Restart();
            await _store.SaveAsync(session, CancellationToken.None);
            sw.Stop();
            latencies[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(latencies);
        _output.WriteLine($"SaveAsync latency ({iterations} iterations):");
        _output.WriteLine($"  p50: {latencies[iterations / 2]:F3} ms");
        _output.WriteLine($"  p95: {latencies[(int)(iterations * 0.95)]:F3} ms");
        _output.WriteLine($"  p99: {latencies[(int)(iterations * 0.99)]:F3} ms");
        _output.WriteLine($"  max: {latencies[^1]:F3} ms");
    }

    [Fact]
    public async Task Benchmark_GetLatency()
    {
        const int iterations = 1000;

        // Pre-populate
        for (var i = 0; i < iterations; i++)
            await _store.SaveAsync(CreateRealisticSession(i), CancellationToken.None);

        var latencies = new double[iterations];
        var sw = new Stopwatch();

        for (var i = 0; i < iterations; i++)
        {
            sw.Restart();
            await _store.GetAsync($"bench-{i:D6}", CancellationToken.None);
            sw.Stop();
            latencies[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(latencies);
        _output.WriteLine($"GetAsync latency ({iterations} iterations):");
        _output.WriteLine($"  p50: {latencies[iterations / 2]:F3} ms");
        _output.WriteLine($"  p95: {latencies[(int)(iterations * 0.95)]:F3} ms");
        _output.WriteLine($"  p99: {latencies[(int)(iterations * 0.99)]:F3} ms");
        _output.WriteLine($"  max: {latencies[^1]:F3} ms");
    }

    [Fact]
    public async Task Benchmark_BatchThroughput()
    {
        const int batchSize = 500;
        const int batches = 10;
        var durations = new double[batches];
        var sw = new Stopwatch();

        for (var b = 0; b < batches; b++)
        {
            var sessions = Enumerable.Range(b * batchSize, batchSize)
                .Select(CreateRealisticSession)
                .ToList();

            sw.Restart();
            await _store.SaveBatchAsync(sessions, CancellationToken.None);
            sw.Stop();
            durations[b] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(durations);
        _output.WriteLine($"SaveBatchAsync ({batchSize} sessions/batch, {batches} batches):");
        _output.WriteLine($"  p50: {durations[batches / 2]:F1} ms ({batchSize / durations[batches / 2] * 1000:F0} sessions/sec)");
        _output.WriteLine($"  max: {durations[^1]:F1} ms");
    }
}
```

- [ ] **Step 2: Run benchmarks**

Run:
```bash
dotnet test Tests/Asterisk.Sdk.Redis.Spike/ --filter "FullyQualifiedName~Benchmark" -v n --logger "console;verbosity=detailed"
```
Expected: 3 tests pass with latency output in console.

- [ ] **Step 3: Commit**

```bash
git add Tests/Asterisk.Sdk.Redis.Spike/Benchmarks/RedisLatencyBenchmark.cs
git commit -m "test(spike): add Redis latency benchmarks (save/get/batch)"
```

---

### Task 9: AOT validation project

**Files:**
- Create: `Tests/Asterisk.Sdk.Redis.Spike.Aot/Asterisk.Sdk.Redis.Spike.Aot.csproj`
- Create: `Tests/Asterisk.Sdk.Redis.Spike.Aot/Program.cs`
- Modify: `Asterisk.Sdk.slnx`

- [ ] **Step 1: Create AOT validation csproj**

Create `Tests/Asterisk.Sdk.Redis.Spike.Aot/Asterisk.Sdk.Redis.Spike.Aot.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <PublishAot>true</PublishAot>
    <TrimMode>full</TrimMode>
    <!-- Override test-project defaults from Directory.Build.props -->
    <IsTestProject>false</IsTestProject>
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Asterisk.Sdk.Sessions\Asterisk.Sdk.Sessions.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="StackExchange.Redis" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create AOT validation program**

Create `Tests/Asterisk.Sdk.Redis.Spike.Aot/Program.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions;
using StackExchange.Redis;

// === JSON Context (must be in the AOT project to verify source generation) ===

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AotSessionSnapshot))]
[JsonSerializable(typeof(SessionParticipant))]
[JsonSerializable(typeof(CallSessionEvent))]
[JsonSerializable(typeof(List<SessionParticipant>))]
[JsonSerializable(typeof(List<CallSessionEvent>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(CallSessionState))]
[JsonSerializable(typeof(CallSessionEventType))]
[JsonSerializable(typeof(CallDirection))]
[JsonSerializable(typeof(ParticipantRole))]
[JsonSerializable(typeof(HangupCause))]
internal partial class AotJsonContext : JsonSerializerContext;

// Minimal snapshot for AOT validation
internal sealed class AotSessionSnapshot
{
    public required string SessionId { get; init; }
    public CallSessionState State { get; init; }
    public CallDirection Direction { get; init; }
    public HangupCause? HangupCause { get; init; }
    public List<SessionParticipant> Participants { get; init; } = [];
    public List<CallSessionEvent> Events { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = [];
}

// === Main ===

var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "localhost";
Console.WriteLine($"[AOT] Connecting to Redis at {host}:6379...");

try
{
    using var redis = await ConnectionMultiplexer.ConnectAsync($"{host}:6379,connectTimeout=5000");
    var db = redis.GetDatabase();
    Console.WriteLine("[AOT] Connected.");

    // 1. StringSet/StringGet
    await db.StringSetAsync("aot:test:string", "hello-aot");
    var val = await db.StringGetAsync("aot:test:string");
    Console.WriteLine($"[AOT] StringGet: {val}");

    // 2. HashSet/HashGetAll
    await db.HashSetAsync("aot:test:hash", [
        new HashEntry("field1", "value1"),
        new HashEntry("field2", "42"),
    ]);
    var hash = await db.HashGetAllAsync("aot:test:hash");
    Console.WriteLine($"[AOT] HashGetAll: {hash.Length} entries");

    // 3. JSON Serialize/Deserialize with source gen
    var snapshot = new AotSessionSnapshot
    {
        SessionId = "aot-001",
        State = CallSessionState.Connected,
        Direction = CallDirection.Inbound,
        HangupCause = HangupCause.NormalClearing,
        Participants = [new SessionParticipant
        {
            UniqueId = "chan-001", Channel = "SIP/test", Technology = "SIP",
            Role = ParticipantRole.Caller,
        }],
        Events = [new CallSessionEvent(DateTimeOffset.UtcNow, CallSessionEventType.Created, null, null, "test")],
        Metadata = new() { ["key"] = "value" },
    };

    var json = JsonSerializer.Serialize(snapshot, AotJsonContext.Default.AotSessionSnapshot);
    Console.WriteLine($"[AOT] Serialized: {json.Length} chars");

    var deserialized = JsonSerializer.Deserialize(json, AotJsonContext.Default.AotSessionSnapshot);
    Console.WriteLine($"[AOT] Deserialized: SessionId={deserialized?.SessionId}, State={deserialized?.State}");

    // 4. Store JSON in Redis
    await db.StringSetAsync("aot:test:session", json);
    var loaded = await db.StringGetAsync("aot:test:session");
    var fromRedis = JsonSerializer.Deserialize(loaded.ToString(), AotJsonContext.Default.AotSessionSnapshot);
    Console.WriteLine($"[AOT] From Redis: SessionId={fromRedis?.SessionId}");

    // 5. Pub/Sub
    var sub = redis.GetSubscriber();
    var tcs = new TaskCompletionSource<string>();
    await sub.SubscribeAsync(RedisChannel.Literal("aot:test:channel"), (_, message) =>
    {
        tcs.TrySetResult(message.ToString());
    });
    await sub.PublishAsync(RedisChannel.Literal("aot:test:channel"), "hello-pubsub");
    var pubsubResult = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine($"[AOT] Pub/Sub: {pubsubResult}");

    // Cleanup
    await db.KeyDeleteAsync(["aot:test:string", "aot:test:hash", "aot:test:session"]);

    Console.WriteLine("[AOT] All checks passed!");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[AOT] FAILED: {ex.Message}");
    return 1;
}
```

- [ ] **Step 3: Add to solution**

In `Asterisk.Sdk.slnx`, inside the `<Folder Name="/Tests/">` block, add:

```xml
    <Project Path="Tests/Asterisk.Sdk.Redis.Spike.Aot/Asterisk.Sdk.Redis.Spike.Aot.csproj" />
```

- [ ] **Step 4: Verify build (non-AOT)**

Run: `dotnet build Tests/Asterisk.Sdk.Redis.Spike.Aot/`
Expected: 0 errors, 0 warnings.

- [ ] **Step 5: Publish with AOT and check trim warnings**

Run:
```bash
dotnet publish Tests/Asterisk.Sdk.Redis.Spike.Aot/ -r linux-x64 -c Release 2>&1 | tee /tmp/aot-publish.log
grep -i "warning" /tmp/aot-publish.log | grep -v "informational" || echo "No warnings!"
```

Expected: 0 actionable trim warnings. Assembly-level informational warnings from SE.Redis (not declaring `IsAotCompatible`) are acceptable.

- [ ] **Step 6: Run AOT binary against Docker Redis**

Run:
```bash
docker compose -f docker/docker-compose.redis-spike.yml up -d
./Tests/Asterisk.Sdk.Redis.Spike.Aot/bin/Release/net10.0/linux-x64/publish/Asterisk.Sdk.Redis.Spike.Aot
```

Expected: All 5 checks print "passed" and exit code 0.

- [ ] **Step 7: Commit**

```bash
git add Tests/Asterisk.Sdk.Redis.Spike.Aot/ Asterisk.Sdk.slnx
git commit -m "feat(spike): add AOT validation project — SE.Redis + JSON source gen"
```

---

### Task 10: Run full test suite and document results

- [ ] **Step 1: Run existing test suite to verify no regressions**

Run: `dotnet test Asterisk.Sdk.slnx --filter "Category!=Integration" -v n`
Expected: All existing tests pass (878+).

- [ ] **Step 2: Run spike integration tests**

Run:
```bash
docker compose -f docker/docker-compose.redis-spike.yml up -d
dotnet test Tests/Asterisk.Sdk.Redis.Spike/ --filter "Category=Integration" -v n --logger "console;verbosity=detailed"
```
Expected: 12 tests pass (9 store + 3 benchmark).

- [ ] **Step 3: Capture benchmark results in spec**

Append benchmark results to the design spec at `docs/superpowers/specs/2026-03-17-redis-aot-spike-design.md`, under a new section `## 16. Spike Results`.

- [ ] **Step 4: Final commit**

```bash
git add docs/superpowers/specs/2026-03-17-redis-aot-spike-design.md
git commit -m "docs(spike): add benchmark and AOT validation results"
```
