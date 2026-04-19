# Session Engine Sprint 5-6 Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build Live Layer gap fixes (BridgeManager + 10 new EventObserver cases) and Asterisk.Sdk.Sessions core (CallSession, CallSessionManager, domain events, extension points, DI registration).

**Architecture:** New `Asterisk.Sdk.Sessions` project subscribes to Live layer manager events (not raw AMI) to correlate channels into call sessions via LinkedId. BridgeManager added to Live layer. Extension points use abstract base classes for PRO forward-compatibility.

**Tech Stack:** .NET 10, C# 14, Native AOT, System.Reactive 6.0.1, System.Diagnostics.Metrics, ConcurrentDictionary, xunit 2.9.3, FluentAssertions 7.1.0, NSubstitute 5.3.0

**Spec:** `docs/superpowers/specs/2026-03-17-session-engine-design.md`

---

## File Structure

### Live Layer Changes (Modify)

| File | Responsibility |
|------|---------------|
| `src/Asterisk.Sdk.Live/Bridges/AsteriskBridge.cs` | **CREATE** — Bridge domain object |
| `src/Asterisk.Sdk.Live/Bridges/BridgeManager.cs` | **CREATE** — Bridge lifecycle manager with reverse index |
| `src/Asterisk.Sdk.Live/Bridges/BridgeTransferInfo.cs` | **CREATE** — Transfer event record |
| `src/Asterisk.Sdk.Live/Channels/ChannelManager.cs` | **MODIFY** — Add LinkedId, OnDialBegin/End, OnHold/Unhold + 4 new events |
| `src/Asterisk.Sdk.Live/Channels/AsteriskChannel.cs` | **MODIFY** — Add LinkedId, DialedChannel, DialStatus, IsOnHold, HoldMusicClass |
| `src/Asterisk.Sdk.Live/Server/AsteriskServer.cs` | **MODIFY** — Add BridgeManager property, 10 new EventObserver cases, reconnect clear |
| `src/Asterisk.Sdk.Live/Diagnostics/LiveMetrics.cs` | **MODIFY** — Add 3 bridge metrics |

### Sessions Project (Create)

| File | Responsibility |
|------|---------------|
| `src/Asterisk.Sdk.Sessions/Asterisk.Sdk.Sessions.csproj` | Project file |
| `src/Asterisk.Sdk.Sessions/CallSessionState.cs` | State enum + transition validation |
| `src/Asterisk.Sdk.Sessions/CallSession.cs` | Aggregate root with state machine, hold tracking, metadata |
| `src/Asterisk.Sdk.Sessions/SessionParticipant.cs` | Participant model + ParticipantRole enum |
| `src/Asterisk.Sdk.Sessions/CallDirection.cs` | Inbound/Outbound enum |
| `src/Asterisk.Sdk.Sessions/CallSessionEvent.cs` | Audit trail record + event type enum |
| `src/Asterisk.Sdk.Sessions/SessionDomainEvent.cs` | Abstract base + 8 sealed domain event records |
| `src/Asterisk.Sdk.Sessions/Manager/ICallSessionManager.cs` | Public interface |
| `src/Asterisk.Sdk.Sessions/Manager/CallSessionManager.cs` | Core correlation engine |
| `src/Asterisk.Sdk.Sessions/Manager/SessionOptions.cs` | Configuration class |
| `src/Asterisk.Sdk.Sessions/Manager/SessionReconciler.cs` | Orphan sweep timer |
| `src/Asterisk.Sdk.Sessions/Extensions/CallRouterBase.cs` | Abstract routing extension |
| `src/Asterisk.Sdk.Sessions/Extensions/AgentSelectorBase.cs` | Abstract agent selection extension |
| `src/Asterisk.Sdk.Sessions/Extensions/SessionStoreBase.cs` | Abstract persistence extension |
| `src/Asterisk.Sdk.Sessions/Internal/PassthroughCallRouter.cs` | Default router |
| `src/Asterisk.Sdk.Sessions/Internal/NativeAgentSelector.cs` | Default agent selector |
| `src/Asterisk.Sdk.Sessions/Internal/InMemorySessionStore.cs` | Default in-memory store |
| `src/Asterisk.Sdk.Sessions/Internal/SessionCorrelator.cs` | LinkedId resolution + direction inference |
| `src/Asterisk.Sdk.Sessions/Internal/SessionOptionsValidator.cs` | AOT-safe [OptionsValidator] |
| `src/Asterisk.Sdk.Sessions/Exceptions/SessionException.cs` | Exception hierarchy |
| `src/Asterisk.Sdk.Sessions/Diagnostics/SessionMetrics.cs` | Metrics (counters, histograms, gauges) |

### Hosting Changes (Modify)

| File | Responsibility |
|------|---------------|
| `src/Asterisk.Sdk.Hosting/SessionManagerHostedService.cs` | **CREATE** — IHostedService lifecycle |
| `src/Asterisk.Sdk.Hosting/ServiceCollectionExtensions.cs` | **MODIFY** — Add AddAsteriskSessions() |
| `src/Asterisk.Sdk.Hosting/Asterisk.Sdk.Hosting.csproj` | **MODIFY** — Add Sessions reference |

### Test Projects (Create)

| File | Responsibility |
|------|---------------|
| `Tests/Asterisk.Sdk.Sessions.Tests/Asterisk.Sdk.Sessions.Tests.csproj` | Test project |
| `Tests/Asterisk.Sdk.Sessions.Tests/Usings.cs` | Global usings |
| `Tests/Asterisk.Sdk.Sessions.Tests/CallSessionTests.cs` | State transitions, hold time |
| `Tests/Asterisk.Sdk.Sessions.Tests/CallSessionManagerTests.cs` | Correlation, lifecycle |
| `Tests/Asterisk.Sdk.Sessions.Tests/SessionCorrelatorTests.cs` | LinkedId, direction |
| `Tests/Asterisk.Sdk.Sessions.Tests/SessionReconcilerTests.cs` | Orphan detection |
| `Tests/Asterisk.Sdk.Sessions.Tests/InMemorySessionStoreTests.cs` | Store CRUD |
| `Tests/Asterisk.Sdk.Live.Tests/Bridges/BridgeManagerTests.cs` | Bridge manager tests |
| `Tests/Asterisk.Sdk.Live.Tests/Channels/ChannelManagerExtendedTests.cs` | New channel methods |

### Solution & Infrastructure (Modify)

| File | Responsibility |
|------|---------------|
| `Asterisk.Sdk.slnx` | **MODIFY** — Add Sessions project + test project |

---

## Task 1: Project Scaffolding

**Files:**
- Create: `src/Asterisk.Sdk.Sessions/Asterisk.Sdk.Sessions.csproj`
- Create: `Tests/Asterisk.Sdk.Sessions.Tests/Asterisk.Sdk.Sessions.Tests.csproj`
- Create: `Tests/Asterisk.Sdk.Sessions.Tests/Usings.cs`
- Modify: `Asterisk.Sdk.slnx`

- [ ] **Step 1: Create Sessions project file**

```xml
<!-- src/Asterisk.Sdk.Sessions/Asterisk.Sdk.Sessions.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Description>Session Engine - call session correlation, state machines, and domain events</Description>
    <InternalsVisibleTo Include="Asterisk.Sdk.Sessions.Tests" />
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Asterisk.Sdk\Asterisk.Sdk.csproj" />
    <ProjectReference Include="..\Asterisk.Sdk.Ami\Asterisk.Sdk.Ami.csproj" />
    <ProjectReference Include="..\Asterisk.Sdk.Live\Asterisk.Sdk.Live.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="System.Reactive" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create test project file**

```xml
<!-- Tests/Asterisk.Sdk.Sessions.Tests/Asterisk.Sdk.Sessions.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\Asterisk.Sdk.Sessions\Asterisk.Sdk.Sessions.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Sdk.Live\Asterisk.Sdk.Live.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create test Usings.cs**

```csharp
global using Xunit;
```

- [ ] **Step 4: Add to solution file**

Add to `Asterisk.Sdk.slnx` inside `/src/` folder:
```xml
<Project Path="src/Asterisk.Sdk.Sessions/Asterisk.Sdk.Sessions.csproj" />
```

Add to `/Tests/` folder:
```xml
<Project Path="Tests/Asterisk.Sdk.Sessions.Tests/Asterisk.Sdk.Sessions.Tests.csproj" />
```

- [ ] **Step 5: Add Sessions reference to Hosting project**

Modify `src/Asterisk.Sdk.Hosting/Asterisk.Sdk.Hosting.csproj`, add to ItemGroup:
```xml
<ProjectReference Include="..\Asterisk.Sdk.Sessions\Asterisk.Sdk.Sessions.csproj" />
```

- [ ] **Step 6: Build to verify**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 7: Commit**

```
feat(sessions): scaffold Asterisk.Sdk.Sessions project and test project
```

---

## Task 2: AsteriskBridge Domain Object + BridgeManager

**Files:**
- Create: `src/Asterisk.Sdk.Live/Bridges/AsteriskBridge.cs`
- Create: `src/Asterisk.Sdk.Live/Bridges/BridgeManager.cs`
- Create: `src/Asterisk.Sdk.Live/Bridges/BridgeTransferInfo.cs`
- Create: `Tests/Asterisk.Sdk.Live.Tests/Bridges/BridgeManagerTests.cs`

- [ ] **Step 1: Write BridgeManager tests**

```csharp
// Tests/Asterisk.Sdk.Live.Tests/Bridges/BridgeManagerTests.cs
using Asterisk.Sdk.Live.Bridges;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.Live.Tests.Bridges;

public sealed class BridgeManagerTests
{
    private readonly BridgeManager _sut = new(NullLogger.Instance);

    [Fact]
    public void OnBridgeCreated_ShouldAddBridge()
    {
        _sut.OnBridgeCreated("bridge-1", "basic", "simple_bridge", "dial", "test");
        _sut.GetById("bridge-1").Should().NotBeNull();
        _sut.GetById("bridge-1")!.BridgeType.Should().Be("basic");
    }

    [Fact]
    public void OnChannelEntered_ShouldAddChannelToBridge()
    {
        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnChannelEntered("bridge-1", "chan-001");

        var bridge = _sut.GetById("bridge-1")!;
        bridge.Channels.Should().ContainKey("chan-001");
        bridge.NumChannels.Should().Be(1);
    }

    [Fact]
    public void OnChannelEntered_ShouldUpdateReverseIndex()
    {
        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnChannelEntered("bridge-1", "chan-001");

        _sut.GetBridgeForChannel("chan-001").Should().NotBeNull();
        _sut.GetBridgeForChannel("chan-001")!.BridgeUniqueid.Should().Be("bridge-1");
    }

    [Fact]
    public void OnChannelLeft_ShouldRemoveFromBridgeAndReverseIndex()
    {
        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnChannelEntered("bridge-1", "chan-001");
        _sut.OnChannelLeft("bridge-1", "chan-001");

        var bridge = _sut.GetById("bridge-1")!;
        bridge.Channels.Should().BeEmpty();
        _sut.GetBridgeForChannel("chan-001").Should().BeNull();
    }

    [Fact]
    public void OnBridgeDestroyed_ShouldMarkDestroyedAndCleanReverseIndex()
    {
        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnChannelEntered("bridge-1", "chan-001");
        _sut.OnBridgeDestroyed("bridge-1");

        var bridge = _sut.GetById("bridge-1")!;
        bridge.DestroyedAt.Should().NotBeNull();
        _sut.GetBridgeForChannel("chan-001").Should().BeNull();
        _sut.ActiveBridges.Should().BeEmpty();
    }

    [Fact]
    public void ActiveBridges_ShouldExcludeDestroyed()
    {
        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnBridgeCreated("bridge-2", "basic", null, null, null);
        _sut.OnBridgeDestroyed("bridge-1");

        _sut.ActiveBridges.Should().HaveCount(1);
    }

    [Fact]
    public void OnBridgeCreated_ShouldFireEvent()
    {
        AsteriskBridge? fired = null;
        _sut.BridgeCreated += b => fired = b;

        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        fired.Should().NotBeNull();
    }

    [Fact]
    public void OnChannelEntered_ShouldFireEvent()
    {
        AsteriskBridge? firedBridge = null;
        string? firedUniqueId = null;
        _sut.ChannelEntered += (b, uid) => { firedBridge = b; firedUniqueId = uid; };

        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnChannelEntered("bridge-1", "chan-001");

        firedBridge.Should().NotBeNull();
        firedUniqueId.Should().Be("chan-001");
    }

    [Fact]
    public void Clear_ShouldRemoveAllBridgesAndReverseIndex()
    {
        _sut.OnBridgeCreated("bridge-1", "basic", null, null, null);
        _sut.OnChannelEntered("bridge-1", "chan-001");
        _sut.Clear();

        _sut.GetById("bridge-1").Should().BeNull();
        _sut.GetBridgeForChannel("chan-001").Should().BeNull();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Asterisk.Sdk.Live.Tests/ --filter "FullyQualifiedName~BridgeManagerTests" -v n`
Expected: FAIL (types don't exist yet)

- [ ] **Step 3: Create AsteriskBridge**

```csharp
// src/Asterisk.Sdk.Live/Bridges/AsteriskBridge.cs
using System.Collections.Concurrent;

namespace Asterisk.Sdk.Live.Bridges;

public sealed class AsteriskBridge : LiveObjectBase
{
    internal readonly Lock SyncRoot = new();
    public override string Id => BridgeUniqueid;
    public string BridgeUniqueid { get; init; } = string.Empty;
    public string? BridgeType { get; set; }
    public string? Technology { get; set; }
    public string? Creator { get; set; }
    public string? Name { get; set; }
    public ConcurrentDictionary<string, byte> Channels { get; } = new();
    public int NumChannels => Channels.Count;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DestroyedAt { get; set; }
}
```

- [ ] **Step 4: Create BridgeTransferInfo**

```csharp
// src/Asterisk.Sdk.Live/Bridges/BridgeTransferInfo.cs
namespace Asterisk.Sdk.Live.Bridges;

public sealed record BridgeTransferInfo(
    string BridgeId,
    string TransferType,
    string? TargetChannel,
    string? SecondBridgeId,
    string? DestType,
    string? Result);
```

- [ ] **Step 5: Create BridgeManager**

```csharp
// src/Asterisk.Sdk.Live/Bridges/BridgeManager.cs
using System.Collections.Concurrent;
using Asterisk.Sdk.Live.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Asterisk.Sdk.Live.Bridges;

public sealed class BridgeManager
{
    private readonly ConcurrentDictionary<string, AsteriskBridge> _bridges = new();
    private readonly ConcurrentDictionary<string, AsteriskBridge> _bridgeByChannel = new();
    private readonly ILogger _logger;

    public event Action<AsteriskBridge>? BridgeCreated;
    public event Action<AsteriskBridge>? BridgeDestroyed;
    public event Action<AsteriskBridge, string>? ChannelEntered;
    public event Action<AsteriskBridge, string>? ChannelLeft;
    public event Action<BridgeTransferInfo>? TransferOccurred;

    public BridgeManager(ILogger logger) => _logger = logger;

    public IEnumerable<AsteriskBridge> ActiveBridges => _bridges.Values.Where(b => b.DestroyedAt is null);
    public int BridgeCount => _bridges.Count;

    public AsteriskBridge? GetById(string bridgeId) =>
        _bridges.GetValueOrDefault(bridgeId);

    public AsteriskBridge? GetBridgeForChannel(string uniqueId) =>
        _bridgeByChannel.GetValueOrDefault(uniqueId);

    public void OnBridgeCreated(string bridgeId, string? type, string? technology, string? creator, string? name)
    {
        var bridge = new AsteriskBridge
        {
            BridgeUniqueid = bridgeId,
            BridgeType = type,
            Technology = technology,
            Creator = creator,
            Name = name
        };

        if (_bridges.TryAdd(bridgeId, bridge))
        {
            LiveMetrics.BridgesCreated.Add(1);
            BridgeCreated?.Invoke(bridge);
        }
    }

    public void OnChannelEntered(string bridgeId, string uniqueId)
    {
        if (!_bridges.TryGetValue(bridgeId, out var bridge)) return;

        lock (bridge.SyncRoot)
        {
            bridge.Channels.TryAdd(uniqueId, 0);
        }

        _bridgeByChannel[uniqueId] = bridge;
        ChannelEntered?.Invoke(bridge, uniqueId);
    }

    public void OnChannelLeft(string bridgeId, string uniqueId)
    {
        if (!_bridges.TryGetValue(bridgeId, out var bridge)) return;

        lock (bridge.SyncRoot)
        {
            bridge.Channels.TryRemove(uniqueId, out _);
        }

        _bridgeByChannel.TryRemove(uniqueId, out _);
        ChannelLeft?.Invoke(bridge, uniqueId);
    }

    public void OnBridgeDestroyed(string bridgeId)
    {
        if (!_bridges.TryGetValue(bridgeId, out var bridge)) return;

        lock (bridge.SyncRoot)
        {
            bridge.DestroyedAt = DateTimeOffset.UtcNow;
            foreach (var channelId in bridge.Channels.Keys)
                _bridgeByChannel.TryRemove(channelId, out _);
        }

        LiveMetrics.BridgesDestroyed.Add(1);
        BridgeDestroyed?.Invoke(bridge);
    }

    public void OnBlindTransfer(string bridgeId, string? targetChannel, string? extension, string? context)
    {
        var info = new BridgeTransferInfo(bridgeId, "Blind", targetChannel, null, null, null);
        TransferOccurred?.Invoke(info);
    }

    public void OnAttendedTransfer(string origBridgeId, string? secondBridgeId, string? destType, string? result)
    {
        var info = new BridgeTransferInfo(origBridgeId, "Attended", null, secondBridgeId, destType, result);
        TransferOccurred?.Invoke(info);
    }

    public void Clear()
    {
        _bridges.Clear();
        _bridgeByChannel.Clear();
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Tests/Asterisk.Sdk.Live.Tests/ --filter "FullyQualifiedName~BridgeManagerTests" -v n`
Expected: 10 PASS

- [ ] **Step 7: Commit**

```
feat(live): add BridgeManager with reverse index for bridge lifecycle tracking
```

---

## Task 3: ChannelManager — New Properties and Methods

**Files:**
- Modify: `src/Asterisk.Sdk.Live/Channels/ChannelManager.cs` (lines 59-80 for OnNewChannel, add new methods after OnUnlink)
- Note: AsteriskChannel is defined in the same file at line 196
- Create: `Tests/Asterisk.Sdk.Live.Tests/Channels/ChannelManagerExtendedTests.cs`

- [ ] **Step 1: Write tests for new channel properties and methods**

```csharp
// Tests/Asterisk.Sdk.Live.Tests/Channels/ChannelManagerExtendedTests.cs
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Live.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.Live.Tests.Channels;

public sealed class ChannelManagerExtendedTests
{
    private readonly ChannelManager _sut = new(NullLogger.Instance);

    [Fact]
    public void OnNewChannel_ShouldStoreLinkedId()
    {
        _sut.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");

        _sut.GetByUniqueId("uid-1")!.LinkedId.Should().Be("linked-1");
    }

    [Fact]
    public void OnDialBegin_ShouldSetDialedChannel()
    {
        _sut.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring);

        _sut.OnDialBegin("uid-1", "uid-2", "PJSIP/200-001", "PJSIP/200");

        var ch = _sut.GetByUniqueId("uid-1")!;
        ch.DialedChannel.Should().Be("PJSIP/200-001");
    }

    [Fact]
    public void OnDialBegin_ShouldFireEvent()
    {
        AsteriskChannel? fired = null;
        _sut.ChannelDialBegin += c => fired = c;

        _sut.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring);
        _sut.OnDialBegin("uid-1", "uid-2", "PJSIP/200-001", null);

        fired.Should().NotBeNull();
    }

    [Fact]
    public void OnDialEnd_ShouldSetDialStatus()
    {
        _sut.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring);
        _sut.OnDialEnd("uid-1", "ANSWER");

        _sut.GetByUniqueId("uid-1")!.DialStatus.Should().Be("ANSWER");
    }

    [Fact]
    public void OnHold_ShouldSetIsOnHold()
    {
        _sut.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Up);
        _sut.OnHold("uid-1", "default");

        var ch = _sut.GetByUniqueId("uid-1")!;
        ch.IsOnHold.Should().BeTrue();
        ch.HoldMusicClass.Should().Be("default");
    }

    [Fact]
    public void OnUnhold_ShouldClearIsOnHold()
    {
        _sut.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Up);
        _sut.OnHold("uid-1", "default");
        _sut.OnUnhold("uid-1");

        _sut.GetByUniqueId("uid-1")!.IsOnHold.Should().BeFalse();
    }

    [Fact]
    public void OnHold_ShouldFireEvent()
    {
        AsteriskChannel? fired = null;
        _sut.ChannelHeld += c => fired = c;

        _sut.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Up);
        _sut.OnHold("uid-1", null);

        fired.Should().NotBeNull();
    }

    [Fact]
    public void OnUnhold_ShouldFireEvent()
    {
        AsteriskChannel? fired = null;
        _sut.ChannelUnheld += c => fired = c;

        _sut.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Up);
        _sut.OnHold("uid-1", null);
        _sut.OnUnhold("uid-1");

        fired.Should().NotBeNull();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Asterisk.Sdk.Live.Tests/ --filter "FullyQualifiedName~ChannelManagerExtendedTests" -v n`
Expected: FAIL

- [ ] **Step 3: Add new properties to AsteriskChannel**

In `src/Asterisk.Sdk.Live/Channels/ChannelManager.cs`, add to `AsteriskChannel` class (after `HangupCause` property, ~line 211):

```csharp
    public string? LinkedId { get; init; }
    public string? DialedChannel { get; set; }
    public string? DialStatus { get; set; }
    public bool IsOnHold { get; set; }
    public string? HoldMusicClass { get; set; }
```

- [ ] **Step 4: Update OnNewChannel to accept linkedId**

Modify `OnNewChannel` signature (~line 59) to add `string? linkedId = null` parameter. Pass it to the `AsteriskChannel` constructor:

```csharp
public void OnNewChannel(string uniqueId, string channelName, ChannelState state,
    string? callerIdNum = null, string? callerIdName = null,
    string? context = null, string? exten = null, int priority = 1,
    string? linkedId = null)
```

Set in the channel creation: `LinkedId = linkedId,`

- [ ] **Step 5: Add new methods and events to ChannelManager**

Add after `OnUnlink` method (~line 155):

```csharp
    public event Action<AsteriskChannel>? ChannelDialBegin;
    public event Action<AsteriskChannel>? ChannelDialEnd;
    public event Action<AsteriskChannel>? ChannelHeld;
    public event Action<AsteriskChannel>? ChannelUnheld;

    public void OnDialBegin(string uniqueId, string destUniqueId, string destChannel, string? dialString)
    {
        if (!_channelsByUniqueId.TryGetValue(uniqueId, out var channel)) return;
        lock (channel.SyncRoot)
        {
            channel.DialedChannel = destChannel;
        }
        ChannelDialBegin?.Invoke(channel);
    }

    public void OnDialEnd(string uniqueId, string? dialStatus)
    {
        if (!_channelsByUniqueId.TryGetValue(uniqueId, out var channel)) return;
        lock (channel.SyncRoot)
        {
            channel.DialStatus = dialStatus;
        }
        ChannelDialEnd?.Invoke(channel);
    }

    public void OnHold(string uniqueId, string? musicClass)
    {
        if (!_channelsByUniqueId.TryGetValue(uniqueId, out var channel)) return;
        lock (channel.SyncRoot)
        {
            channel.IsOnHold = true;
            channel.HoldMusicClass = musicClass;
        }
        ChannelHeld?.Invoke(channel);
    }

    public void OnUnhold(string uniqueId)
    {
        if (!_channelsByUniqueId.TryGetValue(uniqueId, out var channel)) return;
        lock (channel.SyncRoot)
        {
            channel.IsOnHold = false;
            channel.HoldMusicClass = null;
        }
        ChannelUnheld?.Invoke(channel);
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Tests/Asterisk.Sdk.Live.Tests/ --filter "FullyQualifiedName~ChannelManagerExtendedTests" -v n`
Expected: 8 PASS

- [ ] **Step 7: Run all existing Live tests to verify no regressions**

Run: `dotnet test Tests/Asterisk.Sdk.Live.Tests/ -v n`
Expected: All PASS

- [ ] **Step 8: Commit**

```
feat(live): add LinkedId, dial, hold properties and methods to ChannelManager
```

---

## Task 4: EventObserver — 10 New Cases + Bridge Metrics

**Files:**
- Modify: `src/Asterisk.Sdk.Live/Server/AsteriskServer.cs` (lines 229-380, EventObserver class)
- Modify: `src/Asterisk.Sdk.Live/Diagnostics/LiveMetrics.cs`

- [ ] **Step 1: Add bridge metrics to LiveMetrics**

Add after existing counters in `src/Asterisk.Sdk.Live/Diagnostics/LiveMetrics.cs`:

```csharp
    // Bridge metrics
    public static readonly Counter<long> BridgesCreated =
        Meter.CreateCounter<long>("live.bridges.created", "bridges", "Total bridges created");
    public static readonly Counter<long> BridgesDestroyed =
        Meter.CreateCounter<long>("live.bridges.destroyed", "bridges", "Total bridges destroyed");
```

- [ ] **Step 2: Add BridgeManager to AsteriskServer**

In `src/Asterisk.Sdk.Live/Server/AsteriskServer.cs`:

Add using: `using Asterisk.Sdk.Live.Bridges;`

Add property after MeetMe (~line 47):
```csharp
    public BridgeManager Bridges { get; }
```

Add to constructor (~line 55-63), after MeetMe initialization:
```csharp
    Bridges = new BridgeManager(logger);
```

Add `Bridges.Clear()` to `OnReconnected()` method (~line 113), alongside existing manager clears.

- [ ] **Step 3: Update OnNewChannel EventObserver case to pass LinkedId**

Modify the `NewChannelEvent` case (~line 236) to pass `linkedId`. Note: `nce.ChannelState` is `string?` so we must preserve the existing `Enum.TryParse` pattern:

```csharp
case NewChannelEvent nce:
    server.Channels.OnNewChannel(
        nce.UniqueId ?? "",
        nce.Channel ?? "",
        Enum.TryParse<ChannelState>(nce.ChannelState, out var cs) ? cs : ChannelState.Unknown,
        nce.CallerIdNum,
        nce.CallerIdName,
        nce.Context,
        nce.Exten,
        nce.Priority ?? 1,
        nce.Linkedid);       // NEW: pass LinkedId
    break;
```

- [ ] **Step 4: Add 10 new cases to EventObserver.OnNext**

Add before the closing `}` of the switch statement (~line 367):

```csharp
// Bridge events
case BridgeCreateEvent bce:
    server.Bridges.OnBridgeCreated(
        bce.BridgeUniqueid!,
        bce.BridgeType,
        bce.BridgeTechnology,
        bce.BridgeCreator,
        bce.BridgeName);
    break;

case BridgeEnterEvent bee:
    server.Bridges.OnChannelEntered(bee.BridgeUniqueid!, bee.UniqueId!);
    // Wire up existing OnLink: find other channel in bridge
    var enterBridge = server.Bridges.GetById(bee.BridgeUniqueid!);
    if (enterBridge is not null && enterBridge.NumChannels == 2)
    {
        var otherUid = enterBridge.Channels.Keys.FirstOrDefault(k => k != bee.UniqueId);
        if (otherUid is not null)
            server.Channels.OnLink(bee.UniqueId!, otherUid);
    }
    break;

case BridgeLeaveEvent ble:
    // Unlink before removing from bridge
    var leaveBridge = server.Bridges.GetById(ble.BridgeUniqueid!);
    if (leaveBridge is not null)
    {
        var otherUid2 = leaveBridge.Channels.Keys.FirstOrDefault(k => k != ble.UniqueId);
        if (otherUid2 is not null)
            server.Channels.OnUnlink(ble.UniqueId!, otherUid2);
    }
    server.Bridges.OnChannelLeft(ble.BridgeUniqueid!, ble.UniqueId!);
    break;

case BridgeDestroyEvent bde:
    server.Bridges.OnBridgeDestroyed(bde.BridgeUniqueid!);
    break;

// Dial events
case DialBeginEvent dbe:
    server.Channels.OnDialBegin(
        dbe.UniqueId!,
        dbe.DestUniqueid!,
        dbe.DestChannel!,
        dbe.DialString);
    break;

case DialEndEvent dee:
    server.Channels.OnDialEnd(dee.UniqueId!, dee.DialStatus);
    break;

// Hold events
case HoldEvent hoe:
    server.Channels.OnHold(hoe.UniqueId!, hoe.MusicClass);
    break;

case UnholdEvent uhe:
    server.Channels.OnUnhold(uhe.UniqueId!);
    break;

// Transfer events
case BlindTransferEvent bte:
    server.Bridges.OnBlindTransfer(
        bte.BridgeUniqueid!,
        bte.TransfereeChannel,
        bte.Extension,
        bte.TransfereeContext);
    break;

case AttendedTransferEvent ate:
    server.Bridges.OnAttendedTransfer(
        ate.OrigBridgeUniqueid!,
        ate.SecondBridgeUniqueid,
        ate.DestType,
        ate.Result);
    break;
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build src/Asterisk.Sdk.Live/`
Expected: 0 errors, 0 warnings

- [ ] **Step 6: Run all Live tests**

Run: `dotnet test Tests/Asterisk.Sdk.Live.Tests/ -v n`
Expected: All PASS

- [ ] **Step 7: Commit**

```
feat(live): add 10 new EventObserver cases for bridge, dial, hold, transfer events
```

---

## Task 5: Session Domain Models

**Files:**
- Create: `src/Asterisk.Sdk.Sessions/CallDirection.cs`
- Create: `src/Asterisk.Sdk.Sessions/CallSessionState.cs`
- Create: `src/Asterisk.Sdk.Sessions/SessionParticipant.cs`
- Create: `src/Asterisk.Sdk.Sessions/CallSessionEvent.cs`
- Create: `src/Asterisk.Sdk.Sessions/CallSession.cs`
- Create: `src/Asterisk.Sdk.Sessions/Exceptions/SessionException.cs`
- Create: `Tests/Asterisk.Sdk.Sessions.Tests/CallSessionTests.cs`

- [ ] **Step 1: Write CallSession state transition tests**

```csharp
// Tests/Asterisk.Sdk.Sessions.Tests/CallSessionTests.cs
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Exceptions;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.Tests;

public sealed class CallSessionTests
{
    private CallSession CreateSession() => new("test-session", "linked-1", "server-1", CallDirection.Inbound);

    [Fact]
    public void NewSession_ShouldBeInCreatedState()
    {
        var session = CreateSession();
        session.State.Should().Be(CallSessionState.Created);
    }

    [Theory]
    [InlineData(CallSessionState.Dialing)]
    [InlineData(CallSessionState.Failed)]
    public void Created_ShouldAllowValidTransitions(CallSessionState target)
    {
        var session = CreateSession();
        session.TryTransition(target).Should().BeTrue();
        session.State.Should().Be(target);
    }

    [Theory]
    [InlineData(CallSessionState.Connected)]
    [InlineData(CallSessionState.OnHold)]
    [InlineData(CallSessionState.Completed)]
    public void Created_ShouldRejectInvalidTransitions(CallSessionState target)
    {
        var session = CreateSession();
        session.TryTransition(target).Should().BeFalse();
        session.State.Should().Be(CallSessionState.Created);
    }

    [Fact]
    public void Transition_ShouldThrowOnInvalid()
    {
        var session = CreateSession();
        var act = () => session.Transition(CallSessionState.Completed);
        act.Should().Throw<InvalidSessionStateTransitionException>();
    }

    [Fact]
    public void FullCallLifecycle_ShouldTransitionCorrectly()
    {
        var session = CreateSession();
        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Ringing);
        session.Transition(CallSessionState.Connected);
        session.Transition(CallSessionState.Completed);

        session.State.Should().Be(CallSessionState.Completed);
    }

    [Fact]
    public void Connected_ShouldAllowHoldCycle()
    {
        var session = CreateSession();
        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Connected);
        session.Transition(CallSessionState.OnHold);
        session.Transition(CallSessionState.Connected);
        session.Transition(CallSessionState.OnHold);
        session.Transition(CallSessionState.Connected);

        session.State.Should().Be(CallSessionState.Connected);
    }

    [Fact]
    public void Conference_ShouldAllowBackToConnected()
    {
        var session = CreateSession();
        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Connected);
        session.Transition(CallSessionState.Conference);
        session.Transition(CallSessionState.Connected);

        session.State.Should().Be(CallSessionState.Connected);
    }

    [Fact]
    public void HoldTime_ShouldAccumulate()
    {
        var session = CreateSession();
        session.Transition(CallSessionState.Dialing);
        session.Transition(CallSessionState.Connected);

        session.StartHold();
        Thread.Sleep(50);
        session.EndHold();

        session.StartHold();
        Thread.Sleep(50);
        session.EndHold();

        session.HoldTime.TotalMilliseconds.Should().BeGreaterThan(80);
    }

    [Fact]
    public void AddParticipant_ShouldAppearInList()
    {
        var session = CreateSession();
        session.AddParticipant(new SessionParticipant
        {
            UniqueId = "uid-1", Channel = "PJSIP/100-001",
            Technology = "PJSIP", Role = ParticipantRole.Caller,
            JoinedAt = DateTimeOffset.UtcNow
        });

        session.Participants.Should().HaveCount(1);
        session.Participants[0].Role.Should().Be(ParticipantRole.Caller);
    }

    [Fact]
    public void AddEvent_ShouldAppearInList()
    {
        var session = CreateSession();
        session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
            CallSessionEventType.Created, null, null, null));

        session.Events.Should().HaveCount(1);
    }

    [Fact]
    public void SetMetadata_ShouldBeReadable()
    {
        var session = CreateSession();
        session.SetMetadata("key1", "value1");

        session.Metadata.Should().ContainKey("key1");
        session.Metadata["key1"].Should().Be("value1");
    }

    [Fact]
    public void TerminalState_ShouldRejectAllTransitions()
    {
        var session = CreateSession();
        session.Transition(CallSessionState.Failed);

        session.TryTransition(CallSessionState.Dialing).Should().BeFalse();
        session.TryTransition(CallSessionState.Connected).Should().BeFalse();
    }

    [Fact]
    public void WaitTime_ShouldComputeCorrectly()
    {
        var session = CreateSession();
        session.DialingAt = session.CreatedAt.AddSeconds(1);
        session.ConnectedAt = session.CreatedAt.AddSeconds(5);

        session.WaitTime.Should().NotBeNull();
        session.WaitTime!.Value.TotalSeconds.Should().BeApproximately(5, 0.1);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Asterisk.Sdk.Sessions.Tests/ --filter "FullyQualifiedName~CallSessionTests" -v n`
Expected: FAIL

- [ ] **Step 3: Create CallDirection enum**

```csharp
// src/Asterisk.Sdk.Sessions/CallDirection.cs
namespace Asterisk.Sdk.Sessions;

public enum CallDirection { Inbound, Outbound }
```

- [ ] **Step 4: Create CallSessionState with transition validation**

```csharp
// src/Asterisk.Sdk.Sessions/CallSessionState.cs
namespace Asterisk.Sdk.Sessions;

public enum CallSessionState
{
    Created, Dialing, Ringing, Connected, OnHold,
    Transferring, Conference, Completed, Failed, TimedOut
}

internal static class CallSessionStateTransitions
{
    private static readonly Dictionary<CallSessionState, HashSet<CallSessionState>> ValidTransitions = new()
    {
        [CallSessionState.Created] = [CallSessionState.Dialing, CallSessionState.Failed],
        [CallSessionState.Dialing] = [CallSessionState.Ringing, CallSessionState.Connected, CallSessionState.Failed, CallSessionState.TimedOut],
        [CallSessionState.Ringing] = [CallSessionState.Connected, CallSessionState.Failed, CallSessionState.TimedOut],
        [CallSessionState.Connected] = [CallSessionState.OnHold, CallSessionState.Transferring, CallSessionState.Conference, CallSessionState.Completed, CallSessionState.Failed],
        [CallSessionState.OnHold] = [CallSessionState.Connected, CallSessionState.Transferring, CallSessionState.Completed, CallSessionState.Failed],
        [CallSessionState.Transferring] = [CallSessionState.Connected, CallSessionState.Failed],
        [CallSessionState.Conference] = [CallSessionState.Connected, CallSessionState.Completed, CallSessionState.Failed],
        [CallSessionState.Completed] = [],
        [CallSessionState.Failed] = [],
        [CallSessionState.TimedOut] = [],
    };

    public static bool IsValid(CallSessionState from, CallSessionState to) =>
        ValidTransitions.TryGetValue(from, out var targets) && targets.Contains(to);
}
```

- [ ] **Step 5: Create SessionParticipant and ParticipantRole**

```csharp
// src/Asterisk.Sdk.Sessions/SessionParticipant.cs
using Asterisk.Sdk.Enums;

namespace Asterisk.Sdk.Sessions;

public sealed class SessionParticipant
{
    public required string UniqueId { get; init; }
    public required string Channel { get; init; }
    public required string Technology { get; init; }
    public ParticipantRole Role { get; set; }
    public string? CallerIdNum { get; set; }
    public string? CallerIdName { get; set; }
    public DateTimeOffset JoinedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LeftAt { get; set; }
    public HangupCause? HangupCause { get; set; }
}

public enum ParticipantRole { Caller, Destination, Agent, Transfer, Conference, Internal }
```

- [ ] **Step 6: Create CallSessionEvent**

```csharp
// src/Asterisk.Sdk.Sessions/CallSessionEvent.cs
namespace Asterisk.Sdk.Sessions;

public sealed record CallSessionEvent(
    DateTimeOffset Timestamp,
    CallSessionEventType Type,
    string? SourceChannel,
    string? TargetChannel,
    string? Detail);

public enum CallSessionEventType
{
    Created, Dialing, Ringing, Connected,
    Hold, Unhold, Transfer, Conference,
    ParticipantJoined, ParticipantLeft,
    QueueJoined, AgentConnected,
    Completed, Failed, TimedOut
}
```

- [ ] **Step 7: Create SessionException hierarchy**

```csharp
// src/Asterisk.Sdk.Sessions/Exceptions/SessionException.cs
using Asterisk.Sdk;

namespace Asterisk.Sdk.Sessions.Exceptions;

public class SessionException(string message, Exception? inner = null)
    : AsteriskException(message, inner);

public class InvalidSessionStateTransitionException(CallSessionState from, CallSessionState to)
    : SessionException($"Invalid session state transition from {from} to {to}");
```

- [ ] **Step 8: Create CallSession aggregate root**

```csharp
// src/Asterisk.Sdk.Sessions/CallSession.cs
using System.Collections.Concurrent;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions.Exceptions;

namespace Asterisk.Sdk.Sessions;

public sealed class CallSession
{
    private readonly List<SessionParticipant> _participants = [];
    private readonly List<CallSessionEvent> _events = [];
    private readonly ConcurrentDictionary<string, string> _metadata = new();
    private DateTimeOffset? _holdStartedAt;
    private TimeSpan _accumulatedHoldTime;

    public CallSession(string sessionId, string linkedId, string serverId, CallDirection direction)
    {
        SessionId = sessionId;
        LinkedId = linkedId;
        ServerId = serverId;
        Direction = direction;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    // Identity
    public string SessionId { get; }
    public string LinkedId { get; }
    public string ServerId { get; }

    // State
    public CallSessionState State { get; private set; } = CallSessionState.Created;
    public CallDirection Direction { get; }

    // Participants
    public IReadOnlyList<SessionParticipant> Participants => _participants;

    // Context
    public string? QueueName { get; set; }
    public string? AgentId { get; set; }
    public string? AgentInterface { get; set; }
    public string? BridgeId { get; set; }
    public HangupCause? HangupCause { get; set; }

    // Timestamps
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DialingAt { get; set; }
    public DateTimeOffset? RingingAt { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // Computed
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - CreatedAt;
    public TimeSpan? WaitTime => ConnectedAt.HasValue ? ConnectedAt.Value - CreatedAt : null;
    public TimeSpan? TalkTime => CompletedAt.HasValue && ConnectedAt.HasValue
        ? (CompletedAt.Value - ConnectedAt.Value) - HoldTime
        : null;
    public TimeSpan HoldTime => _accumulatedHoldTime +
        (_holdStartedAt.HasValue ? DateTimeOffset.UtcNow - _holdStartedAt.Value : TimeSpan.Zero);

    // Metadata
    public IReadOnlyDictionary<string, string> Metadata => _metadata;

    // Audit trail
    public IReadOnlyList<CallSessionEvent> Events => _events;

    // Thread safety
    internal readonly Lock SyncRoot = new();

    // State transitions (internal — only CallSessionManager drives transitions)
    internal bool TryTransition(CallSessionState newState)
    {
        if (!CallSessionStateTransitions.IsValid(State, newState))
            return false;

        State = newState;
        UpdateTimestamp(newState);
        return true;
    }

    internal void Transition(CallSessionState newState)
    {
        if (!TryTransition(newState))
            throw new InvalidSessionStateTransitionException(State, newState);
    }

    // Hold time tracking
    internal void StartHold() => _holdStartedAt = DateTimeOffset.UtcNow;

    internal void EndHold()
    {
        if (_holdStartedAt.HasValue)
        {
            _accumulatedHoldTime += DateTimeOffset.UtcNow - _holdStartedAt.Value;
            _holdStartedAt = null;
        }
    }

    // Mutators
    internal void AddParticipant(SessionParticipant participant) => _participants.Add(participant);
    internal void AddEvent(CallSessionEvent evt) => _events.Add(evt);
    internal void SetMetadata(string key, string value) => _metadata[key] = value;

    private void UpdateTimestamp(CallSessionState state)
    {
        var now = DateTimeOffset.UtcNow;
        switch (state)
        {
            case CallSessionState.Dialing: DialingAt ??= now; break;
            case CallSessionState.Ringing: RingingAt ??= now; break;
            case CallSessionState.Connected: ConnectedAt ??= now; break;
            case CallSessionState.Completed:
            case CallSessionState.Failed:
            case CallSessionState.TimedOut:
                CompletedAt ??= now; break;
        }
    }
}
```

- [ ] **Step 9: Run tests**

Run: `dotnet test Tests/Asterisk.Sdk.Sessions.Tests/ --filter "FullyQualifiedName~CallSessionTests" -v n`
Expected: All PASS

- [ ] **Step 10: Build full solution**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 11: Commit**

```
feat(sessions): add CallSession domain model with state machine, participants, events
```

---

## Task 6: Session Domain Events

**Files:**
- Create: `src/Asterisk.Sdk.Sessions/SessionDomainEvent.cs`

- [ ] **Step 1: Create domain events**

```csharp
// src/Asterisk.Sdk.Sessions/SessionDomainEvent.cs
using Asterisk.Sdk.Enums;

namespace Asterisk.Sdk.Sessions;

public abstract record SessionDomainEvent(string SessionId, string ServerId, DateTimeOffset Timestamp);

public sealed record CallStartedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    CallDirection Direction, string? CallerIdNum) : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record CallConnectedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    string? AgentId, string? QueueName, TimeSpan WaitTime) : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record CallTransferredEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    string TransferType, string? TargetChannel) : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record CallHeldEvent(string SessionId, string ServerId, DateTimeOffset Timestamp)
    : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record CallResumedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp)
    : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record CallEndedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    HangupCause? Cause, TimeSpan Duration, TimeSpan? TalkTime) : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record CallFailedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    string Reason) : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record SessionMergedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    string MergedSessionId) : SessionDomainEvent(SessionId, ServerId, Timestamp);
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Asterisk.Sdk.Sessions/`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Commit**

```
feat(sessions): add SessionDomainEvent hierarchy for external consumers
```

---

## Task 7: SessionCorrelator + SessionMetrics

**Files:**
- Create: `src/Asterisk.Sdk.Sessions/Internal/SessionCorrelator.cs`
- Create: `src/Asterisk.Sdk.Sessions/Diagnostics/SessionMetrics.cs`
- Create: `Tests/Asterisk.Sdk.Sessions.Tests/SessionCorrelatorTests.cs`

- [ ] **Step 1: Write correlator tests**

```csharp
// Tests/Asterisk.Sdk.Sessions.Tests/SessionCorrelatorTests.cs
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Internal;
using Asterisk.Sdk.Sessions.Manager;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.Tests;

public sealed class SessionCorrelatorTests
{
    private readonly SessionCorrelator _sut = new(new SessionOptions());

    [Fact]
    public void InferDirection_ShouldReturnInbound_WhenContextMatchesTrunk()
    {
        _sut.InferDirection("from-trunk", "s").Should().Be(CallDirection.Inbound);
    }

    [Fact]
    public void InferDirection_ShouldReturnOutbound_WhenContextMatchesInternal()
    {
        _sut.InferDirection("from-internal", "100").Should().Be(CallDirection.Outbound);
    }

    [Fact]
    public void InferDirection_ShouldReturnInbound_WhenContextUnknown()
    {
        _sut.InferDirection("custom-context", "100").Should().Be(CallDirection.Inbound);
    }

    [Fact]
    public void IsLocalChannel_ShouldReturnTrue_ForLocalPrefix()
    {
        SessionCorrelator.IsLocalChannel("Local/100@default-00000001;1").Should().BeTrue();
    }

    [Fact]
    public void IsLocalChannel_ShouldReturnFalse_ForPjsip()
    {
        SessionCorrelator.IsLocalChannel("PJSIP/100-00000001").Should().BeFalse();
    }

    [Fact]
    public void ExtractTechnology_ShouldParsePjsip()
    {
        SessionCorrelator.ExtractTechnology("PJSIP/100-00000001").Should().Be("PJSIP");
    }

    [Fact]
    public void ExtractTechnology_ShouldParseLocal()
    {
        SessionCorrelator.ExtractTechnology("Local/100@default-00000001;1").Should().Be("Local");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Asterisk.Sdk.Sessions.Tests/ --filter "FullyQualifiedName~SessionCorrelatorTests" -v n`
Expected: FAIL

- [ ] **Step 3: Create SessionOptions**

```csharp
// src/Asterisk.Sdk.Sessions/Manager/SessionOptions.cs
namespace Asterisk.Sdk.Sessions.Manager;

public sealed class SessionOptions
{
    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan DialingTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan RingingTimeout { get; set; } = TimeSpan.FromSeconds(120);
    public int MaxCompletedSessions { get; set; } = 1000;
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan QueueMetricsWindow { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan SlaThreshold { get; set; } = TimeSpan.FromSeconds(20);
    public string[] InboundContextPatterns { get; set; } = ["from-trunk", "from-pstn", "from-external"];
    public string[] OutboundContextPatterns { get; set; } = ["from-internal", "from-sip", "from-users"];
}
```

- [ ] **Step 4: Create SessionCorrelator**

```csharp
// src/Asterisk.Sdk.Sessions/Internal/SessionCorrelator.cs
using Asterisk.Sdk.Sessions.Manager;

namespace Asterisk.Sdk.Sessions.Internal;

internal sealed class SessionCorrelator
{
    private readonly SessionOptions _options;

    public SessionCorrelator(SessionOptions options) => _options = options;

    public CallDirection InferDirection(string? context, string? extension)
    {
        if (context is null) return CallDirection.Inbound;

        foreach (var pattern in _options.OutboundContextPatterns)
            if (context.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                return CallDirection.Outbound;

        foreach (var pattern in _options.InboundContextPatterns)
            if (context.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                return CallDirection.Inbound;

        return CallDirection.Inbound; // Default
    }

    public static bool IsLocalChannel(string channelName) =>
        channelName.StartsWith("Local/", StringComparison.OrdinalIgnoreCase);

    public static string ExtractTechnology(string channelName)
    {
        var slashIndex = channelName.IndexOf('/');
        return slashIndex > 0 ? channelName[..slashIndex] : "Unknown";
    }

    public ParticipantRole InferRole(string channelName, int participantCount) =>
        IsLocalChannel(channelName) ? ParticipantRole.Internal
        : participantCount == 0 ? ParticipantRole.Caller
        : ParticipantRole.Destination;
}
```

- [ ] **Step 5: Create SessionMetrics**

```csharp
// src/Asterisk.Sdk.Sessions/Diagnostics/SessionMetrics.cs
using System.Diagnostics.Metrics;

namespace Asterisk.Sdk.Sessions.Diagnostics;

public static class SessionMetrics
{
    public static readonly Meter Meter = new("Asterisk.Sdk.Sessions", "1.0.0");

    public static readonly Counter<long> SessionsCreated =
        Meter.CreateCounter<long>("sessions.created", "sessions", "Total sessions created");
    public static readonly Counter<long> SessionsCompleted =
        Meter.CreateCounter<long>("sessions.completed", "sessions", "Total sessions completed");
    public static readonly Counter<long> SessionsFailed =
        Meter.CreateCounter<long>("sessions.failed", "sessions", "Total sessions failed");
    public static readonly Counter<long> SessionsTimedOut =
        Meter.CreateCounter<long>("sessions.timed_out", "sessions", "Total sessions timed out");
    public static readonly Counter<long> SessionsOrphaned =
        Meter.CreateCounter<long>("sessions.orphaned", "sessions", "Orphaned sessions detected");

    public static readonly Histogram<double> WaitTimeMs =
        Meter.CreateHistogram<double>("sessions.wait_time", "ms", "Queue wait time");
    public static readonly Histogram<double> TalkTimeMs =
        Meter.CreateHistogram<double>("sessions.talk_time", "ms", "Talk time");
    public static readonly Histogram<double> HoldTimeMs =
        Meter.CreateHistogram<double>("sessions.hold_time", "ms", "Hold time");
    public static readonly Histogram<double> DurationMs =
        Meter.CreateHistogram<double>("sessions.duration", "ms", "Total session duration");
}
```

- [ ] **Step 6: Run tests**

Run: `dotnet test Tests/Asterisk.Sdk.Sessions.Tests/ --filter "FullyQualifiedName~SessionCorrelatorTests" -v n`
Expected: All PASS

- [ ] **Step 7: Commit**

```
feat(sessions): add SessionCorrelator for LinkedId resolution and SessionMetrics
```

---

## Task 8: Extension Points + Default Implementations

**Files:**
- Create: `src/Asterisk.Sdk.Sessions/Extensions/CallRouterBase.cs`
- Create: `src/Asterisk.Sdk.Sessions/Extensions/AgentSelectorBase.cs`
- Create: `src/Asterisk.Sdk.Sessions/Extensions/SessionStoreBase.cs`
- Create: `src/Asterisk.Sdk.Sessions/Internal/PassthroughCallRouter.cs`
- Create: `src/Asterisk.Sdk.Sessions/Internal/NativeAgentSelector.cs`
- Create: `src/Asterisk.Sdk.Sessions/Internal/InMemorySessionStore.cs`
- Create: `Tests/Asterisk.Sdk.Sessions.Tests/InMemorySessionStoreTests.cs`

- [ ] **Step 1: Write InMemorySessionStore tests**

```csharp
// Tests/Asterisk.Sdk.Sessions.Tests/InMemorySessionStoreTests.cs
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Internal;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.Tests;

public sealed class InMemorySessionStoreTests
{
    private readonly InMemorySessionStore _sut = new();

    [Fact]
    public async Task SaveAsync_ShouldPersistSession()
    {
        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        await _sut.SaveAsync(session, CancellationToken.None);

        var result = await _sut.GetAsync("s1", CancellationToken.None);
        result.Should().NotBeNull();
        result!.SessionId.Should().Be("s1");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenNotFound()
    {
        var result = await _sut.GetAsync("nonexistent", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveSession()
    {
        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        await _sut.SaveAsync(session, CancellationToken.None);
        await _sut.DeleteAsync("s1", CancellationToken.None);

        var result = await _sut.GetAsync("s1", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveAsync_ShouldReturnNonCompleted()
    {
        var active = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        var completed = new CallSession("s2", "l2", "srv1", CallDirection.Inbound);
        completed.TryTransition(CallSessionState.Failed);

        await _sut.SaveAsync(active, CancellationToken.None);
        await _sut.SaveAsync(completed, CancellationToken.None);

        var result = await _sut.GetActiveAsync(CancellationToken.None);
        result.Should().HaveCount(1);
    }
}
```

- [ ] **Step 2: Create abstract base classes**

```csharp
// src/Asterisk.Sdk.Sessions/Extensions/CallRouterBase.cs
namespace Asterisk.Sdk.Sessions.Extensions;

public abstract class CallRouterBase
{
    public abstract ValueTask<string> SelectNodeAsync(CallSession session, CancellationToken ct);
    public virtual ValueTask<bool> CanRouteAsync(CallSession session, CancellationToken ct)
        => ValueTask.FromResult(true);
}
```

```csharp
// src/Asterisk.Sdk.Sessions/Extensions/AgentSelectorBase.cs
using Asterisk.Sdk.Live.Agents;
using Asterisk.Sdk.Live.Queues;

namespace Asterisk.Sdk.Sessions.Extensions;

public abstract class AgentSelectorBase
{
    public abstract ValueTask<AsteriskAgent?> SelectAgentAsync(AsteriskQueue queue, CancellationToken ct);
    public virtual ValueTask<IReadOnlyList<AsteriskAgent>> RankAgentsAsync(
        AsteriskQueue queue, IReadOnlyList<AsteriskAgent> candidates, CancellationToken ct)
        => ValueTask.FromResult(candidates);
}
```

```csharp
// src/Asterisk.Sdk.Sessions/Extensions/SessionStoreBase.cs
namespace Asterisk.Sdk.Sessions.Extensions;

public abstract class SessionStoreBase
{
    public abstract ValueTask SaveAsync(CallSession session, CancellationToken ct);
    public abstract ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct);
    public virtual ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct)
        => ValueTask.FromResult(Enumerable.Empty<CallSession>());
    public virtual ValueTask DeleteAsync(string sessionId, CancellationToken ct)
        => ValueTask.CompletedTask;
}
```

- [ ] **Step 3: Create default implementations**

```csharp
// src/Asterisk.Sdk.Sessions/Internal/PassthroughCallRouter.cs
using Asterisk.Sdk.Sessions.Extensions;

namespace Asterisk.Sdk.Sessions.Internal;

internal sealed class PassthroughCallRouter : CallRouterBase
{
    public override ValueTask<string> SelectNodeAsync(CallSession session, CancellationToken ct)
        => ValueTask.FromResult(session.ServerId);
}
```

```csharp
// src/Asterisk.Sdk.Sessions/Internal/NativeAgentSelector.cs
using Asterisk.Sdk.Live.Agents;
using Asterisk.Sdk.Live.Queues;
using Asterisk.Sdk.Sessions.Extensions;

namespace Asterisk.Sdk.Sessions.Internal;

internal sealed class NativeAgentSelector : AgentSelectorBase
{
    public override ValueTask<AsteriskAgent?> SelectAgentAsync(AsteriskQueue queue, CancellationToken ct)
        => ValueTask.FromResult<AsteriskAgent?>(null); // Let Asterisk decide
}
```

```csharp
// src/Asterisk.Sdk.Sessions/Internal/InMemorySessionStore.cs
using System.Collections.Concurrent;
using Asterisk.Sdk.Sessions.Extensions;

namespace Asterisk.Sdk.Sessions.Internal;

internal sealed class InMemorySessionStore : SessionStoreBase
{
    private readonly ConcurrentDictionary<string, CallSession> _store = new();

    public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
    {
        _store[session.SessionId] = session;
        return ValueTask.CompletedTask;
    }

    public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct)
        => ValueTask.FromResult(_store.GetValueOrDefault(sessionId));

    public override ValueTask<IEnumerable<CallSession>> GetActiveAsync(CancellationToken ct)
        => ValueTask.FromResult(_store.Values.Where(s =>
            s.State is not CallSessionState.Completed
            and not CallSessionState.Failed
            and not CallSessionState.TimedOut));

    public override ValueTask DeleteAsync(string sessionId, CancellationToken ct)
    {
        _store.TryRemove(sessionId, out _);
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/Asterisk.Sdk.Sessions.Tests/ --filter "FullyQualifiedName~InMemorySessionStoreTests" -v n`
Expected: All PASS

- [ ] **Step 5: Commit**

```
feat(sessions): add extension points and default implementations
```

---

## Task 9: ICallSessionManager + CallSessionManager

**Files:**
- Create: `src/Asterisk.Sdk.Sessions/Manager/ICallSessionManager.cs`
- Create: `src/Asterisk.Sdk.Sessions/Manager/CallSessionManager.cs`
- Create: `Tests/Asterisk.Sdk.Sessions.Tests/CallSessionManagerTests.cs`

- [ ] **Step 1: Write manager tests**

```csharp
// Tests/Asterisk.Sdk.Sessions.Tests/CallSessionManagerTests.cs
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Live.Agents;
using Asterisk.Sdk.Live.Bridges;
using Asterisk.Sdk.Live.Channels;
using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Sdk.Sessions.Tests;

public sealed class CallSessionManagerTests : IAsyncDisposable
{
    private readonly CallSessionManager _sut;
    private readonly AsteriskServer _server;
    private readonly IAmiConnection _connection;

    public CallSessionManagerTests()
    {
        _connection = Substitute.For<IAmiConnection>();
        _connection.AsteriskVersion.Returns("20.0.0");
        _server = new AsteriskServer(_connection, NullLogger<AsteriskServer>.Instance);
        var options = Options.Create(new SessionOptions());
        _sut = new CallSessionManager(options, NullLogger<CallSessionManager>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public void AttachToServer_ShouldAcceptServer()
    {
        _sut.AttachToServer(_server, "srv-1");
        // No throw = success
    }

    [Fact]
    public void NewChannel_ShouldCreateSession()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1", context: "from-trunk");

        var session = _sut.GetByLinkedId("linked-1");
        session.Should().NotBeNull();
        session!.Direction.Should().Be(CallDirection.Inbound);
        session.Participants.Should().HaveCount(1);
        session.Participants[0].Role.Should().Be(ParticipantRole.Caller);
    }

    [Fact]
    public void SecondChannel_ShouldAddAsDestination()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");
        _server.Channels.OnNewChannel("uid-2", "PJSIP/200-001", ChannelState.Ring,
            linkedId: "linked-1");

        var session = _sut.GetByLinkedId("linked-1")!;
        session.Participants.Should().HaveCount(2);
        session.Participants[1].Role.Should().Be(ParticipantRole.Destination);
    }

    [Fact]
    public void LocalChannel_ShouldBeMarkedInternal()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");
        _server.Channels.OnNewChannel("uid-2", "Local/100@default-001;1", ChannelState.Ring,
            linkedId: "linked-1");

        var session = _sut.GetByLinkedId("linked-1")!;
        session.Participants[1].Role.Should().Be(ParticipantRole.Internal);
    }

    [Fact]
    public void GetByChannelId_ShouldFindSession()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");

        _sut.GetByChannelId("uid-1").Should().NotBeNull();
    }

    [Fact]
    public void ChannelHangup_ShouldCompleteSession_WhenAllParticipantsLeft()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Up,
            linkedId: "linked-1");
        _server.Channels.OnHangup("uid-1");

        var session = _sut.GetByLinkedId("linked-1")!;
        session.State.Should().BeOneOf(CallSessionState.Completed, CallSessionState.Failed);
    }

    [Fact]
    public void ActiveSessions_ShouldReturnNonCompleted()
    {
        _sut.AttachToServer(_server, "srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");

        _sut.ActiveSessions.Should().HaveCount(1);
    }

    [Fact]
    public void DetachFromServer_ShouldUnsubscribe()
    {
        _sut.AttachToServer(_server, "srv-1");
        _sut.DetachFromServer("srv-1");

        _server.Channels.OnNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1");

        _sut.GetByLinkedId("linked-1").Should().BeNull();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Asterisk.Sdk.Sessions.Tests/ --filter "FullyQualifiedName~CallSessionManagerTests" -v n`
Expected: FAIL

- [ ] **Step 3: Create ICallSessionManager interface**

```csharp
// src/Asterisk.Sdk.Sessions/Manager/ICallSessionManager.cs
using Asterisk.Sdk.Live.Server;

namespace Asterisk.Sdk.Sessions.Manager;

public interface ICallSessionManager : IAsyncDisposable
{
    CallSession? GetById(string sessionId);
    CallSession? GetByLinkedId(string linkedId);
    CallSession? GetByChannelId(string uniqueId);
    CallSession? GetByBridgeId(string bridgeId);
    IEnumerable<CallSession> ActiveSessions { get; }
    IEnumerable<CallSession> GetRecentCompleted(int count = 100);

    IObservable<SessionDomainEvent> Events { get; }

    void AttachToServer(AsteriskServer server, string serverId);
    void DetachFromServer(string serverId);
}
```

- [ ] **Step 4: Create CallSessionManager**

```csharp
// src/Asterisk.Sdk.Sessions/Manager/CallSessionManager.cs
using System.Collections.Concurrent;
using System.Reactive.Subjects;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Live.Agents;
using Asterisk.Sdk.Live.Bridges;
using Asterisk.Sdk.Live.Channels;
using Asterisk.Sdk.Live.Queues;
using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.Sessions.Diagnostics;
using Asterisk.Sdk.Sessions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Sessions.Manager;

public sealed class CallSessionManager : ICallSessionManager
{
    private readonly ConcurrentDictionary<string, CallSession> _sessions = new();
    private readonly ConcurrentDictionary<string, CallSession> _byLinkedId = new();
    private readonly ConcurrentDictionary<string, CallSession> _byChannelId = new();
    private readonly ConcurrentDictionary<string, string> _bridgeToSession = new();
    private readonly ConcurrentQueue<string> _completedOrder = new();
    private readonly ConcurrentDictionary<string, ServerSubscriptions> _serverSubs = new();
    private readonly Subject<SessionDomainEvent> _events = new();
    private readonly SessionCorrelator _correlator;
    private readonly SessionOptions _options;
    private readonly ILogger<CallSessionManager> _logger;

    public CallSessionManager(IOptions<SessionOptions> options, ILogger<CallSessionManager> logger)
    {
        _options = options.Value;
        _logger = logger;
        _correlator = new SessionCorrelator(_options);
    }

    public IObservable<SessionDomainEvent> Events => _events;

    public IEnumerable<CallSession> ActiveSessions => _sessions.Values
        .Where(s => s.State is not CallSessionState.Completed
            and not CallSessionState.Failed
            and not CallSessionState.TimedOut);

    public CallSession? GetById(string sessionId) => _sessions.GetValueOrDefault(sessionId);
    public CallSession? GetByLinkedId(string linkedId) => _byLinkedId.GetValueOrDefault(linkedId);
    public CallSession? GetByChannelId(string uniqueId) => _byChannelId.GetValueOrDefault(uniqueId);

    public CallSession? GetByBridgeId(string bridgeId) =>
        _bridgeToSession.TryGetValue(bridgeId, out var sessionId)
            ? _sessions.GetValueOrDefault(sessionId)
            : null;

    public IEnumerable<CallSession> GetRecentCompleted(int count = 100) =>
        _sessions.Values
            .Where(s => s.State is CallSessionState.Completed or CallSessionState.Failed or CallSessionState.TimedOut)
            .OrderByDescending(s => s.CompletedAt)
            .Take(count);

    public void AttachToServer(AsteriskServer server, string serverId)
    {
        // Create typed delegates so we can -= unsubscribe later
        Action<AsteriskChannel> onAdded = ch => OnChannelAdded(ch, serverId);
        Action<AsteriskChannel> onRemoved = OnChannelRemoved;
        Action<AsteriskChannel> onStateChanged = OnChannelStateChanged;
        Action<AsteriskChannel> onDialBegin = OnChannelDialBegin;
        Action<AsteriskChannel> onDialEnd = OnChannelDialEnd;
        Action<AsteriskChannel> onHeld = OnChannelHeld;
        Action<AsteriskChannel> onUnheld = OnChannelUnheld;
        Action<AsteriskBridge, string> onBridgeEntered = OnBridgeChannelEntered;
        Action<AsteriskBridge> onBridgeDestroyed = OnBridgeDestroyed;
        Action<BridgeTransferInfo> onTransfer = OnTransfer;
        Action<string, AsteriskQueueEntry> onCallerJoined = OnQueueCallerJoined;

        server.Channels.ChannelAdded += onAdded;
        server.Channels.ChannelRemoved += onRemoved;
        server.Channels.ChannelStateChanged += onStateChanged;
        server.Channels.ChannelDialBegin += onDialBegin;
        server.Channels.ChannelDialEnd += onDialEnd;
        server.Channels.ChannelHeld += onHeld;
        server.Channels.ChannelUnheld += onUnheld;
        server.Bridges.ChannelEntered += onBridgeEntered;
        server.Bridges.BridgeDestroyed += onBridgeDestroyed;
        server.Bridges.TransferOccurred += onTransfer;
        server.Queues.CallerJoined += onCallerJoined;

        _serverSubs[serverId] = new ServerSubscriptions(server,
            onAdded, onRemoved, onStateChanged, onDialBegin, onDialEnd,
            onHeld, onUnheld, onBridgeEntered, onBridgeDestroyed, onTransfer, onCallerJoined);
    }

    public void DetachFromServer(string serverId)
    {
        if (_serverSubs.TryRemove(serverId, out var subs))
            subs.Detach();
    }

    // --- Event Handlers ---

    private void OnChannelAdded(AsteriskChannel channel, string serverId)
    {
        var linkedId = channel.LinkedId;
        if (string.IsNullOrEmpty(linkedId)) linkedId = channel.UniqueId;

        if (_byLinkedId.TryGetValue(linkedId, out var existing))
        {
            // Add participant to existing session
            lock (existing.SyncRoot)
            {
                var role = _correlator.InferRole(channel.Name, existing.Participants.Count);
                existing.AddParticipant(new SessionParticipant
                {
                    UniqueId = channel.UniqueId,
                    Channel = channel.Name,
                    Technology = SessionCorrelator.ExtractTechnology(channel.Name),
                    Role = role,
                    CallerIdNum = channel.CallerIdNum,
                    CallerIdName = channel.CallerIdName,
                    JoinedAt = DateTimeOffset.UtcNow
                });
                existing.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.ParticipantJoined, channel.Name, null, role.ToString()));
            }
            _byChannelId[channel.UniqueId] = existing;
            return;
        }

        // Create new session
        var direction = _correlator.InferDirection(channel.Context, channel.Extension);
        var session = new CallSession(Guid.NewGuid().ToString("N"), linkedId, serverId, direction);

        var callerRole = _correlator.InferRole(channel.Name, 0);
        session.AddParticipant(new SessionParticipant
        {
            UniqueId = channel.UniqueId,
            Channel = channel.Name,
            Technology = SessionCorrelator.ExtractTechnology(channel.Name),
            Role = callerRole,
            CallerIdNum = channel.CallerIdNum,
            CallerIdName = channel.CallerIdName,
            JoinedAt = DateTimeOffset.UtcNow
        });
        session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
            CallSessionEventType.Created, channel.Name, null, null));

        _sessions[session.SessionId] = session;
        _byLinkedId[linkedId] = session;
        _byChannelId[channel.UniqueId] = session;
        SessionMetrics.SessionsCreated.Add(1);

        _events.OnNext(new CallStartedEvent(session.SessionId, serverId,
            DateTimeOffset.UtcNow, direction, channel.CallerIdNum));
    }

    private void OnChannelRemoved(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            var participant = session.Participants.FirstOrDefault(p => p.UniqueId == channel.UniqueId);
            if (participant is not null)
            {
                participant.LeftAt = DateTimeOffset.UtcNow;
                participant.HangupCause = channel.HangupCause;
            }

            session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                CallSessionEventType.ParticipantLeft, channel.Name, null, channel.HangupCause.ToString()));

            // Check if all participants have left
            if (session.Participants.All(p => p.LeftAt.HasValue))
            {
                session.HangupCause = channel.HangupCause;
                var targetState = channel.HangupCause == HangupCause.NormalClearing
                    ? CallSessionState.Completed
                    : CallSessionState.Failed;

                // Try the natural progression if needed
                if (session.State == CallSessionState.Created)
                    session.TryTransition(CallSessionState.Failed);
                else
                    session.TryTransition(targetState) || session.TryTransition(CallSessionState.Failed);

                OnSessionCompleted(session);
            }
        }

        _byChannelId.TryRemove(channel.UniqueId, out _);
    }

    private void OnChannelStateChanged(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            switch (channel.State)
            {
                case ChannelState.Ringing or ChannelState.Ring:
                    if (session.TryTransition(CallSessionState.Ringing))
                        session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                            CallSessionEventType.Ringing, channel.Name, null, null));
                    break;

                case ChannelState.Up:
                    if (session.TryTransition(CallSessionState.Connected))
                        session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                            CallSessionEventType.Connected, channel.Name, null, null));
                    break;
            }
        }
    }

    private void OnChannelHeld(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            if (session.TryTransition(CallSessionState.OnHold))
            {
                session.StartHold();
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Hold, channel.Name, null, channel.HoldMusicClass));
                _events.OnNext(new CallHeldEvent(session.SessionId, session.ServerId, DateTimeOffset.UtcNow));
            }
        }
    }

    private void OnChannelUnheld(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            if (session.TryTransition(CallSessionState.Connected))
            {
                session.EndHold();
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Unhold, channel.Name, null, null));
                _events.OnNext(new CallResumedEvent(session.SessionId, session.ServerId, DateTimeOffset.UtcNow));
            }
        }
    }

    private void OnBridgeChannelEntered(AsteriskBridge bridge, string uniqueId)
    {
        if (!_byChannelId.TryGetValue(uniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            session.BridgeId = bridge.BridgeUniqueid;
            _bridgeToSession[bridge.BridgeUniqueid] = session.SessionId;

            if (session.TryTransition(CallSessionState.Connected))
            {
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Connected, null, null, $"bridge:{bridge.BridgeUniqueid}"));

                var waitTime = session.WaitTime ?? TimeSpan.Zero;
                _events.OnNext(new CallConnectedEvent(session.SessionId, session.ServerId,
                    DateTimeOffset.UtcNow, session.AgentId, session.QueueName, waitTime));

                if (waitTime > TimeSpan.Zero)
                    SessionMetrics.WaitTimeMs.Record(waitTime.TotalMilliseconds);
            }
        }
    }

    private void OnTransfer(BridgeTransferInfo info)
    {
        var session = GetByBridgeId(info.BridgeId);
        if (session is null) return;

        lock (session.SyncRoot)
        {
            if (session.TryTransition(CallSessionState.Transferring))
            {
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Transfer, null, info.TargetChannel, info.TransferType));
                _events.OnNext(new CallTransferredEvent(session.SessionId, session.ServerId,
                    DateTimeOffset.UtcNow, info.TransferType, info.TargetChannel));
            }
        }
    }

    private void OnQueueCallerJoined(string queueName, AsteriskQueueEntry entry)
    {
        // Find session by channel name matching
        var session = _byChannelId.Values.FirstOrDefault(s =>
            s.Participants.Any(p => p.Channel == entry.Channel));
        if (session is null) return;

        lock (session.SyncRoot)
        {
            session.QueueName = queueName;
            session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                CallSessionEventType.QueueJoined, entry.Channel, null, queueName));
        }
    }

    private void OnSessionCompleted(CallSession session)
    {
        _completedOrder.Enqueue(session.SessionId);

        // Record metrics
        SessionMetrics.DurationMs.Record(session.Duration.TotalMilliseconds);
        if (session.TalkTime.HasValue)
            SessionMetrics.TalkTimeMs.Record(session.TalkTime.Value.TotalMilliseconds);
        if (session.HoldTime > TimeSpan.Zero)
            SessionMetrics.HoldTimeMs.Record(session.HoldTime.TotalMilliseconds);

        switch (session.State)
        {
            case CallSessionState.Completed:
                SessionMetrics.SessionsCompleted.Add(1);
                break;
            case CallSessionState.Failed:
                SessionMetrics.SessionsFailed.Add(1);
                break;
            case CallSessionState.TimedOut:
                SessionMetrics.SessionsTimedOut.Add(1);
                break;
        }

        _events.OnNext(new CallEndedEvent(session.SessionId, session.ServerId,
            DateTimeOffset.UtcNow, session.HangupCause, session.Duration, session.TalkTime));

        EvictStaleCompleted();
    }

    private void EvictStaleCompleted()
    {
        var cutoff = DateTimeOffset.UtcNow - _options.CompletedRetention;
        while (_completedOrder.TryPeek(out var oldId) &&
               _sessions.TryGetValue(oldId, out var old) &&
               old.CompletedAt < cutoff)
        {
            _completedOrder.TryDequeue(out _);
            _sessions.TryRemove(oldId, out _);
            _byLinkedId.TryRemove(old.LinkedId, out _);
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var serverId in _serverSubs.Keys.ToArray())
            DetachFromServer(serverId);
        _events.OnCompleted();
        _events.Dispose();
        return ValueTask.CompletedTask;
    }

    // --- Dial event handlers (S1 fix) ---

    private void OnChannelDialBegin(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            if (session.TryTransition(CallSessionState.Dialing))
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Dialing, channel.Name, channel.DialedChannel, null));
        }
    }

    private void OnChannelDialEnd(AsteriskChannel channel)
    {
        if (!_byChannelId.TryGetValue(channel.UniqueId, out var session)) return;

        lock (session.SyncRoot)
        {
            session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                CallSessionEventType.Connected, channel.Name, null, $"dial:{channel.DialStatus}"));

            if (channel.DialStatus == "ANSWER")
                session.TryTransition(CallSessionState.Connected);
        }
    }

    // --- Bridge destroyed handler (S3 fix) ---

    private void OnBridgeDestroyed(AsteriskBridge bridge)
    {
        if (_bridgeToSession.TryRemove(bridge.BridgeUniqueid, out var sessionId) &&
            _sessions.TryGetValue(sessionId, out var session))
        {
            lock (session.SyncRoot)
            {
                if (session.BridgeId == bridge.BridgeUniqueid)
                    session.BridgeId = null;
            }
        }
    }

    // Subscription management: stores typed delegates for proper -= unsubscribe
    private sealed class ServerSubscriptions(
        AsteriskServer server,
        Action<AsteriskChannel> onAdded,
        Action<AsteriskChannel> onRemoved,
        Action<AsteriskChannel> onStateChanged,
        Action<AsteriskChannel> onDialBegin,
        Action<AsteriskChannel> onDialEnd,
        Action<AsteriskChannel> onHeld,
        Action<AsteriskChannel> onUnheld,
        Action<AsteriskBridge, string> onBridgeEntered,
        Action<AsteriskBridge> onBridgeDestroyed,
        Action<BridgeTransferInfo> onTransfer,
        Action<string, AsteriskQueueEntry> onCallerJoined)
    {
        public void Detach()
        {
            server.Channels.ChannelAdded -= onAdded;
            server.Channels.ChannelRemoved -= onRemoved;
            server.Channels.ChannelStateChanged -= onStateChanged;
            server.Channels.ChannelDialBegin -= onDialBegin;
            server.Channels.ChannelDialEnd -= onDialEnd;
            server.Channels.ChannelHeld -= onHeld;
            server.Channels.ChannelUnheld -= onUnheld;
            server.Bridges.ChannelEntered -= onBridgeEntered;
            server.Bridges.BridgeDestroyed -= onBridgeDestroyed;
            server.Bridges.TransferOccurred -= onTransfer;
            server.Queues.CallerJoined -= onCallerJoined;
        }
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test Tests/Asterisk.Sdk.Sessions.Tests/ --filter "FullyQualifiedName~CallSessionManagerTests" -v n`
Expected: All PASS

- [ ] **Step 6: Run full solution build**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 7: Commit**

```
feat(sessions): add CallSessionManager with LinkedId correlation and domain events
```

---

## Task 10: SessionReconciler

**Files:**
- Create: `src/Asterisk.Sdk.Sessions/Manager/SessionReconciler.cs`
- Create: `Tests/Asterisk.Sdk.Sessions.Tests/SessionReconcilerTests.cs`

- [ ] **Step 1: Write reconciler tests**

```csharp
// Tests/Asterisk.Sdk.Sessions.Tests/SessionReconcilerTests.cs
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Manager;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.Tests;

public sealed class SessionReconcilerTests
{
    [Fact]
    public void MarkOrphaned_ShouldTransitionToFailed()
    {
        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        session.TryTransition(CallSessionState.Dialing);

        SessionReconciler.TryMarkOrphaned(session);

        session.State.Should().Be(CallSessionState.Failed);
        session.Metadata.Should().ContainKey("cause");
        session.Metadata["cause"].Should().Be("orphaned");
    }

    [Fact]
    public void MarkTimedOut_ShouldTransitionToTimedOut_WhenDialing()
    {
        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        session.TryTransition(CallSessionState.Dialing);

        SessionReconciler.TryMarkTimedOut(session);

        session.State.Should().Be(CallSessionState.TimedOut);
    }

    [Fact]
    public void MarkTimedOut_ShouldNotAffectConnectedSession()
    {
        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        session.TryTransition(CallSessionState.Dialing);
        session.TryTransition(CallSessionState.Connected);

        SessionReconciler.TryMarkTimedOut(session);

        session.State.Should().Be(CallSessionState.Connected);
    }
}
```

- [ ] **Step 2: Create SessionReconciler**

```csharp
// src/Asterisk.Sdk.Sessions/Manager/SessionReconciler.cs
using Asterisk.Sdk.Sessions.Diagnostics;

namespace Asterisk.Sdk.Sessions.Manager;

internal sealed class SessionReconciler
{
    public static bool TryMarkOrphaned(CallSession session)
    {
        lock (session.SyncRoot)
        {
            if (session.TryTransition(CallSessionState.Failed))
            {
                session.SetMetadata("cause", "orphaned");
                session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                    CallSessionEventType.Failed, null, null, "orphaned"));
                SessionMetrics.SessionsOrphaned.Add(1);
                return true;
            }
        }
        return false;
    }

    public static bool TryMarkTimedOut(CallSession session)
    {
        lock (session.SyncRoot)
        {
            if (session.State is CallSessionState.Dialing or CallSessionState.Ringing)
            {
                if (session.TryTransition(CallSessionState.TimedOut))
                {
                    session.AddEvent(new CallSessionEvent(DateTimeOffset.UtcNow,
                        CallSessionEventType.TimedOut, null, null, "timeout"));
                    SessionMetrics.SessionsTimedOut.Add(1);
                    return true;
                }
            }
        }
        return false;
    }
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test Tests/Asterisk.Sdk.Sessions.Tests/ --filter "FullyQualifiedName~SessionReconcilerTests" -v n`
Expected: All PASS

- [ ] **Step 4: Commit**

```
feat(sessions): add SessionReconciler for orphan detection and timeout handling
```

---

## Task 11: SessionOptions Validator + Hosting Registration

**Files:**
- Create: `src/Asterisk.Sdk.Sessions/Internal/SessionOptionsValidator.cs`
- Create: `src/Asterisk.Sdk.Hosting/SessionManagerHostedService.cs`
- Modify: `src/Asterisk.Sdk.Hosting/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Create AOT-safe validator**

```csharp
// src/Asterisk.Sdk.Sessions/Internal/SessionOptionsValidator.cs
using Asterisk.Sdk.Sessions.Manager;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Sessions.Internal;

[OptionsValidator]
internal partial class SessionOptionsValidator : IValidateOptions<SessionOptions>
{
}
```

- [ ] **Step 2: Create SessionManagerHostedService**

```csharp
// src/Asterisk.Sdk.Hosting/SessionManagerHostedService.cs
using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.Sessions.Manager;
using Microsoft.Extensions.Hosting;

namespace Asterisk.Sdk.Hosting;

internal sealed class SessionManagerHostedService(
    ICallSessionManager sessionManager,
    AsteriskServer server) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (sessionManager is CallSessionManager csm)
            csm.AttachToServer(server, "default");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (sessionManager is CallSessionManager csm)
            csm.DetachFromServer("default");
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Add AddAsteriskSessions() to ServiceCollectionExtensions**

Add to `src/Asterisk.Sdk.Hosting/ServiceCollectionExtensions.cs` after existing methods:

```csharp
using Asterisk.Sdk.Sessions.Extensions;
using Asterisk.Sdk.Sessions.Internal;
using Asterisk.Sdk.Sessions.Manager;

// ... add this method:

    public static IServiceCollection AddAsteriskSessions(
        this IServiceCollection services,
        Action<SessionOptions>? configure = null)
    {
        services.TryAddSingleton<ICallSessionManager, CallSessionManager>();
        services.TryAddSingleton<CallRouterBase, PassthroughCallRouter>();
        services.TryAddSingleton<AgentSelectorBase, NativeAgentSelector>();
        services.TryAddSingleton<SessionStoreBase, InMemorySessionStore>();

        if (configure is not null)
            services.Configure(configure);

        services.AddSingleton<IValidateOptions<SessionOptions>, SessionOptionsValidator>();
        services.AddOptions<SessionOptions>().ValidateOnStart();
        services.AddSingleton<IHostedService, SessionManagerHostedService>();

        return services;
    }
```

- [ ] **Step 4: Build**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 5: Commit**

```
feat(hosting): add AddAsteriskSessions() DI registration with AOT-safe validation
```

---

## Task 12: Full Test Suite + Final Verification

**Files:**
- Run all tests across entire solution

- [ ] **Step 1: Run all tests**

Run: `dotnet test Asterisk.Sdk.slnx -v n`
Expected: All PASS, 0 failures

- [ ] **Step 2: Verify AOT compatibility**

Run: `dotnet build Asterisk.Sdk.slnx /p:PublishAot=true`
Expected: 0 trim warnings

- [ ] **Step 3: Run existing Live tests to verify no regressions**

Run: `dotnet test Tests/Asterisk.Sdk.Live.Tests/ -v n`
Expected: All existing tests PASS

- [ ] **Step 4: Run all Session tests**

Run: `dotnet test Tests/Asterisk.Sdk.Sessions.Tests/ -v n`
Expected: All PASS

- [ ] **Step 5: Commit final**

```
feat(sessions): complete Sprint 5-6 — Session Engine core with Live layer gap fixes
```

---

## Summary

| Task | Files | Tests | Description |
|------|-------|-------|-------------|
| 1 | 4 | 0 | Project scaffolding |
| 2 | 4 | 10 | BridgeManager + AsteriskBridge |
| 3 | 2 | 8 | ChannelManager new props/methods |
| 4 | 2 | 0 | EventObserver 10 new cases + bridge metrics |
| 5 | 7 | 13 | CallSession domain models |
| 6 | 1 | 0 | SessionDomainEvent hierarchy |
| 7 | 3 | 7 | SessionCorrelator + SessionMetrics |
| 8 | 7 | 4 | Extension points + defaults |
| 9 | 3 | 8 | ICallSessionManager + CallSessionManager |
| 10 | 2 | 3 | SessionReconciler |
| 11 | 3 | 0 | Hosting + DI registration |
| 12 | 0 | — | Full verification |
| **Total** | **38** | **53** | |
