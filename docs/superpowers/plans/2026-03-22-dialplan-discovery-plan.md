# Dialplan Discovery & Editor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add dialplan discovery, visualization, and editing to PbxAdmin so admins can see and manage Asterisk contexts without SSH access, and extension/trunk forms use validated dropdowns instead of free text.

**Architecture:** `DialplanDiscoveryService` uses the SDK's `ShowDialplanAction` to collect structured `ListDialplanEvent` events, builds an in-memory `DialplanSnapshot` per server with 5min TTL cache. `DialplanEditorService` mutates via AMI commands (File mode) or SQL (Realtime mode) and refreshes the cache after each change. A new `/dialplan` page provides a two-panel viewer/editor, and existing Extension/Trunk edit pages get context dropdowns.

**Tech Stack:** .NET 10, Blazor Server, Asterisk.Sdk.Ami (ShowDialplanAction), Dapper (Realtime mode), xUnit, FluentAssertions, NSubstitute. AOT-safe. Source-gen logging (`[LoggerMessage]`).

**Spec:** `docs/superpowers/specs/2026-03-22-dialplan-discovery-design.md`

---

## File Map

### New Files

| File | Responsibility |
|------|---------------|
| `Examples/PbxAdmin/Models/DialplanSnapshot.cs` | `DialplanSnapshot`, `DiscoveredContext`, `DialplanExtension`, `DialplanPriority` records |
| `Examples/PbxAdmin/Services/Dialplan/DialplanDiscoveryService.cs` | AMI-based discovery + snapshot-swap cache + TTL timer |
| `Examples/PbxAdmin/Services/Dialplan/DialplanEditorService.cs` | Mutations via AMI (File) or SQL (Realtime) + refresh |
| `Examples/PbxAdmin/Components/Pages/Dialplan.razor` | Two-panel dialplan page (context list + detail) |
| `Tests/PbxAdmin.Tests/Services/Dialplan/DialplanDiscoveryServiceTests.cs` | Discovery + cache + filtering tests |
| `Tests/PbxAdmin.Tests/Services/Dialplan/DialplanEditorServiceTests.cs` | Mutation + dual-persistence tests |

### Modified Files

| File | Change |
|------|--------|
| `src/Asterisk.Sdk.Ami/Events/ListDialplanEvent.cs` | Add `Context` and `Priority` properties |
| `src/Asterisk.Sdk.Ami/Actions/ShowDialplanAction.cs` | Add optional `Context` filter property |
| `Examples/PbxAdmin/Components/Pages/ExtensionEdit.razor` | Context text input → dropdown |
| `Examples/PbxAdmin/Components/Pages/TrunkEdit.razor` | Context text input → dropdown |
| `Examples/PbxAdmin/Components/Layout/MainLayout.razor` | Add "Dialplan" nav link |
| `Examples/PbxAdmin/Services/Dialplan/DialplanRegenerator.cs` | Add nullable `DialplanDiscoveryService?` dependency, call RefreshAsync after regeneration |
| `Examples/PbxAdmin/Program.cs` | Register `DialplanDiscoveryService` and `DialplanEditorService` |
| `Examples/PbxAdmin/Resources/SharedStrings.resx` | Add DP_* localization keys (EN) |
| `Examples/PbxAdmin/Resources/SharedStrings.es.resx` | Add DP_* localization keys (ES) |

---

## Task 1: SDK Changes — ListDialplanEvent + ShowDialplanAction

**Files:**
- Modify: `src/Asterisk.Sdk.Ami/Events/ListDialplanEvent.cs`
- Modify: `src/Asterisk.Sdk.Ami/Actions/ShowDialplanAction.cs`

- [ ] **Step 1: Add Context and Priority to ListDialplanEvent**

Add the two missing properties that Asterisk sends but the SDK doesn't map:

```csharp
public string? Context { get; set; }
public int? Priority { get; set; }
```

- [ ] **Step 2: Add optional Context filter to ShowDialplanAction**

```csharp
public string? Context { get; set; }
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Sdk.Ami/Events/ListDialplanEvent.cs \
  src/Asterisk.Sdk.Ami/Actions/ShowDialplanAction.cs
git commit -m "feat(ami): add Context and Priority to ListDialplanEvent, Context filter to ShowDialplanAction"
```

---

## Task 2: Data Model — DialplanSnapshot

**Files:**
- Create: `Examples/PbxAdmin/Models/DialplanSnapshot.cs`

- [ ] **Step 1: Create model file**

```csharp
namespace PbxAdmin.Models;

public sealed class DialplanSnapshot
{
    public string ServerId { get; init; } = "";
    public DateTime RefreshedAt { get; init; }
    public IReadOnlyList<DiscoveredContext> Contexts { get; init; } = [];
}

public sealed class DiscoveredContext
{
    public string Name { get; init; } = "";
    public string CreatedBy { get; init; } = "";
    public bool IsSystem { get; init; }
    public IReadOnlyList<DialplanExtension> Extensions { get; init; } = [];
    public IReadOnlyList<string> Includes { get; init; } = [];
}

public sealed class DialplanExtension
{
    public string Pattern { get; init; } = "";
    public IReadOnlyList<DialplanPriority> Priorities { get; init; } = [];
}

public sealed class DialplanPriority
{
    public int Number { get; init; }
    public string? Label { get; init; }
    public string Application { get; init; } = "";
    public string ApplicationData { get; init; } = "";
    public string? Source { get; init; }
}
```

Use `init` setters and `IReadOnlyList` since these are immutable cache objects shared across Blazor circuits.

- [ ] **Step 2: Build and verify**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Commit**

```bash
git add Examples/PbxAdmin/Models/DialplanSnapshot.cs
git commit -m "feat(dialplan): add DialplanSnapshot model types"
```

---

## Task 3: DialplanDiscoveryService — Core + Cache + Tests

**Files:**
- Create: `Examples/PbxAdmin/Services/Dialplan/DialplanDiscoveryService.cs`
- Create: `Tests/PbxAdmin.Tests/Services/Dialplan/DialplanDiscoveryServiceTests.cs`

- [ ] **Step 1: Write discovery service tests**

Test scenarios:
- `BuildSnapshot_ShouldGroupEventsByContext` — given a list of `ListDialplanEvent`, builds correct `DialplanSnapshot` with contexts, extensions, priorities
- `BuildSnapshot_ShouldDetectSystemContexts` — contexts from `func_periodic_hook`, `res_parking` are marked `IsSystem = true`
- `BuildSnapshot_ShouldParseIncludes` — events with `IncludeContext` populate the `Includes` list
- `BuildSnapshot_ShouldParseLabels` — events with `ExtensionLabel` populate `Label` on priorities
- `GetUserContextsAsync_ShouldFilterSystemContexts` — only returns contexts where `IsSystem == false`
- `ContextExistsAsync_ShouldReturnCorrectResult` — true for known context, false for unknown
- `Cache_ShouldReturnStaleData_WhenWithinTtl` — second call within TTL doesn't re-query AMI

**Test helper:** Create a mock `IConfigProviderResolver` + `IConfigProvider` that returns a predefined set of `ListDialplanEvent` objects. Since `ShowDialplanAction` uses `SendActionAsync<ShowDialplanCompleteEvent>` with event collection, the test needs to mock `AsteriskMonitorService.GetServer(serverId).ConfigConnection.SendActionAsync(...)`. This is complex to mock directly — instead, extract the event-to-snapshot logic into a `internal static` method `BuildSnapshot(string serverId, List<ListDialplanEvent> events)` that tests can call directly.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/PbxAdmin.Tests/ --filter "DialplanDiscoveryService"`
Expected: FAIL

- [ ] **Step 3: Implement DialplanDiscoveryService**

```csharp
using PbxAdmin.Models;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;

namespace PbxAdmin.Services.Dialplan;

internal static partial class DialplanDiscoveryLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[DP_DISCOVERY] Refreshed: server={ServerId} contexts={ContextCount} extensions={ExtensionCount}")]
    public static partial void Refreshed(ILogger logger, string serverId, int contextCount, int extensionCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[DP_DISCOVERY] Refresh failed: server={ServerId}")]
    public static partial void RefreshFailed(ILogger logger, Exception exception, string serverId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[DP_DISCOVERY] Cache hit: server={ServerId} age={AgeSeconds}s")]
    public static partial void CacheHit(ILogger logger, string serverId, int ageSeconds);
}

public sealed class DialplanDiscoveryService : IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> UserRegistrars = new(StringComparer.OrdinalIgnoreCase)
        { "pbx_config", "pbx_realtime", "pbx_lua", "pbx_ael" };

    private volatile IReadOnlyDictionary<string, DialplanSnapshot> _snapshots =
        new Dictionary<string, DialplanSnapshot>();
    private readonly AsteriskMonitorService _monitor;
    private readonly IConfiguration _config;
    private readonly ILogger<DialplanDiscoveryService> _logger;
    private Timer? _refreshTimer;

    // Constructor, public methods, BuildSnapshot static method, timer logic, IDisposable
}
```

Key methods:
- `RefreshAsync(serverId, ct)` — sends `ShowDialplanAction` via `_monitor.GetServer(serverId).ConfigConnection`, collects events, calls `BuildSnapshot`, swaps cache reference
- `GetSnapshotAsync(serverId, ct)` — checks TTL, refreshes if stale, returns cached snapshot
- `GetContextsAsync(serverId, ct)` — returns all contexts from snapshot
- `GetUserContextsAsync(serverId, ct)` — filters `IsSystem == false`
- `GetContextAsync(serverId, name, ct)` — single context by name
- `ContextExistsAsync(serverId, name, ct)` — bool check
- `internal static BuildSnapshot(string serverId, IReadOnlyList<ListDialplanEvent> events)` — pure function, testable

For Realtime mode supplement: after AMI discovery, also query `SELECT DISTINCT context FROM extensions` and merge any contexts not already in the snapshot.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/PbxAdmin.Tests/ --filter "DialplanDiscoveryService"`
Expected: ALL PASS

- [ ] **Step 5: Commit**

```bash
git add Examples/PbxAdmin/Services/Dialplan/DialplanDiscoveryService.cs \
  Tests/PbxAdmin.Tests/Services/Dialplan/DialplanDiscoveryServiceTests.cs
git commit -m "feat(dialplan): add DialplanDiscoveryService with AMI-based discovery and TTL cache"
```

---

## Task 4: DialplanEditorService — Mutations + Dual Persistence + Tests

**Files:**
- Create: `Examples/PbxAdmin/Services/Dialplan/DialplanEditorService.cs`
- Create: `Tests/PbxAdmin.Tests/Services/Dialplan/DialplanEditorServiceTests.cs`

- [ ] **Step 1: Write editor service tests**

Test scenarios:
- `AddExtensionAsync_FileMode_ShouldCallAmiCommand` — verifies AMI `dialplan add extension` is sent
- `AddExtensionAsync_FileMode_ShouldCallDialplanSave` — verifies `dialplan save` follows
- `AddExtensionAsync_RealtimeMode_ShouldInsertIntoDb` — verifies SQL INSERT on `extensions` table
- `AddExtensionAsync_RealtimeMode_ShouldReloadDialplan` — verifies `dialplan reload` AMI command
- `RemoveExtensionAsync_ShouldRemoveAllPriorities` — removes all priorities for a pattern
- `AddIncludeAsync_ShouldDetectCircularInclude` — rejects if it would create a cycle
- `CreateContextAsync_ShouldAddNoOpPlaceholder` — creates a `NoOp(placeholder)` extension
- `RemoveContextAsync_ShouldCallAmiRemoveContext` — verifies AMI `dialplan remove context`
- `AllMutations_ShouldRefreshCache` — verifies `DialplanDiscoveryService.RefreshAsync` is called after each mutation

Mock pattern: mock `IConfigProviderResolver` to return a mock `IConfigProvider` with `ExecuteCommandAsync` that records calls. Mock `DialplanDiscoveryService.RefreshAsync`. For Realtime tests, use NSubstitute for the connection string from `IConfiguration`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/PbxAdmin.Tests/ --filter "DialplanEditorService"`
Expected: FAIL

- [ ] **Step 3: Implement DialplanEditorService**

```csharp
namespace PbxAdmin.Services.Dialplan;

internal static partial class DialplanEditorLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[DP_EDITOR] Extension added: server={ServerId} ctx={Context} exten={Exten}")]
    public static partial void ExtensionAdded(ILogger logger, string serverId, string context, string exten);

    [LoggerMessage(Level = LogLevel.Information, Message = "[DP_EDITOR] Extension removed: server={ServerId} ctx={Context} exten={Exten}")]
    public static partial void ExtensionRemoved(ILogger logger, string serverId, string context, string exten);

    [LoggerMessage(Level = LogLevel.Information, Message = "[DP_EDITOR] Include added: server={ServerId} ctx={Context} include={Include}")]
    public static partial void IncludeAdded(ILogger logger, string serverId, string context, string include);

    [LoggerMessage(Level = LogLevel.Information, Message = "[DP_EDITOR] Context created: server={ServerId} ctx={Context}")]
    public static partial void ContextCreated(ILogger logger, string serverId, string context);

    [LoggerMessage(Level = LogLevel.Error, Message = "[DP_EDITOR] Operation failed: server={ServerId} operation={Operation}")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string serverId, string operation);
}

public sealed class DialplanEditorService
{
    private readonly IConfigProviderResolver _configResolver;
    private readonly IConfiguration _config;
    private readonly DialplanDiscoveryService _discovery;
    private readonly ILogger<DialplanEditorService> _logger;
    // Constructor
}
```

Key methods — each returns `(bool Success, string? Error)`:
- `AddExtensionAsync` — File: AMI `dialplan add extension {ctx},{exten},{prio},{app},{appData}` + `dialplan save`. Realtime: `INSERT INTO extensions` + `dialplan reload`
- `RemoveExtensionAsync` — File: AMI `dialplan remove extension {exten}@{ctx}` for each priority + `dialplan save`. Realtime: `DELETE FROM extensions WHERE context=@ctx AND exten=@exten` + `dialplan reload`
- `AddIncludeAsync` — Check circular includes via discovery snapshot, then File: AMI `dialplan add include {ctx} into {parent}` + save. Realtime: INSERT with special `app='include'`
- `RemoveIncludeAsync` — File: AMI `dialplan remove include {ctx} from {parent}` + save. Realtime: DELETE
- `CreateContextAsync` — `AddExtensionAsync(serverId, name, "s", 1, "NoOp", "placeholder")` — empty contexts need at least one extension to persist
- `RemoveContextAsync` — File: AMI `dialplan remove context {name}` + save. Realtime: `DELETE FROM extensions WHERE context=@name` + reload

All methods call `_discovery.RefreshAsync(serverId)` at the end.

Detect File vs Realtime: read `IConfiguration` section `Asterisk:Servers` for the matching server's `ConfigMode`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/PbxAdmin.Tests/ --filter "DialplanEditorService"`
Expected: ALL PASS

- [ ] **Step 5: Commit**

```bash
git add Examples/PbxAdmin/Services/Dialplan/DialplanEditorService.cs \
  Tests/PbxAdmin.Tests/Services/Dialplan/DialplanEditorServiceTests.cs
git commit -m "feat(dialplan): add DialplanEditorService with dual File/Realtime persistence"
```

---

## Task 5: DI Registration + DialplanRegenerator Integration

**Files:**
- Modify: `Examples/PbxAdmin/Program.cs`
- Modify: `Examples/PbxAdmin/Services/Dialplan/DialplanRegenerator.cs`

- [ ] **Step 1: Register services in Program.cs**

After existing dialplan service registrations (around line 69-71), add:
```csharp
builder.Services.AddSingleton<DialplanDiscoveryService>();
builder.Services.AddSingleton<DialplanEditorService>();
```

- [ ] **Step 2: Add DialplanDiscoveryService to DialplanRegenerator**

Add nullable parameter with default (backward compatible):

```csharp
public sealed class DialplanRegenerator(
    IRouteRepositoryResolver repoResolver,
    IDialplanProviderResolver dialplanResolver,
    IIvrMenuRepository ivrRepo,
    DialplanDiscoveryService? discoveryService = null)
```

At the end of `RegenerateAsync`, after `provider.ReloadAsync`:
```csharp
if (discoveryService is not null)
    await discoveryService.RefreshAsync(serverId, ct);
```

- [ ] **Step 3: Build and run all tests**

Run: `dotnet build Asterisk.Sdk.slnx && dotnet test Tests/PbxAdmin.Tests/`
Expected: 0 errors, 0 warnings, all tests pass

- [ ] **Step 4: Commit**

```bash
git add Examples/PbxAdmin/Program.cs \
  Examples/PbxAdmin/Services/Dialplan/DialplanRegenerator.cs
git commit -m "feat(dialplan): register discovery/editor services and integrate with regenerator"
```

---

## Task 6: UI — Dialplan Page (Context List + Detail + Editing)

**Files:**
- Create: `Examples/PbxAdmin/Components/Pages/Dialplan.razor`
- Modify: `Examples/PbxAdmin/Components/Layout/MainLayout.razor`
- Modify: `Examples/PbxAdmin/Resources/SharedStrings.resx`
- Modify: `Examples/PbxAdmin/Resources/SharedStrings.es.resx`

- [ ] **Step 1: Add nav link in MainLayout.razor**

After the Time Conditions nav link (around line 34), add:
```razor
<NavLink href="/dialplan" class="nav-item">@L["Nav_Dialplan"]</NavLink>
```

- [ ] **Step 2: Add localization keys**

Add all `DP_*` keys from spec section 5.3 to both `SharedStrings.resx` (EN) and `SharedStrings.es.resx` (ES). Also add `Lbl_System` if not present.

- [ ] **Step 3: Create Dialplan.razor**

Single-file Razor component with two-panel layout. Left panel: KPI cards + context list with search/filter. Right panel: context detail (includes, included-by, extensions table with expand). Full CRUD modals.

Key sections:
- `@page "/dialplan"`
- `@inject DialplanDiscoveryService DiscoverySvc`
- `@inject DialplanEditorService EditorSvc`
- `@inject ISelectedServerService ServerSvc`
- `@inject IStringLocalizer<SharedStrings> L`
- KPI row: total contexts, user contexts, total extensions
- Context cards with Name, extension count badge, System badge
- Context detail: includes section (add/remove), extensions table (add/edit/delete), included-by reverse lookup
- "New Context" modal: name input → calls `EditorSvc.CreateContextAsync`
- "Add Extension" modal: pattern, priorities list (app dropdown + appData), add/remove rows
- "View Tree" modal: recursive include tree with cycle detection, indented with `├── └──` characters
- "Refresh" button with `RefreshedAt` timestamp
- "Delete Context" button (disabled if System or has IncludedBy)

Follow existing PbxAdmin Razor patterns: `_loading`, `_error`, `OnInitializedAsync`, `StateHasChanged()`.

- [ ] **Step 4: Build and verify**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 5: Commit**

```bash
git add Examples/PbxAdmin/Components/Pages/Dialplan.razor \
  Examples/PbxAdmin/Components/Layout/MainLayout.razor \
  Examples/PbxAdmin/Resources/SharedStrings.resx \
  Examples/PbxAdmin/Resources/SharedStrings.es.resx
git commit -m "feat(dialplan): add Dialplan page with context viewer, editor, and include tree"
```

---

## Task 7: UI — Context Dropdown in ExtensionEdit + TrunkEdit

**Files:**
- Modify: `Examples/PbxAdmin/Components/Pages/ExtensionEdit.razor`
- Modify: `Examples/PbxAdmin/Components/Pages/TrunkEdit.razor`

- [ ] **Step 1: Modify ExtensionEdit.razor**

1. Add `@inject DialplanDiscoveryService DiscoverySvc` at the top
2. Add field: `private List<DiscoveredContext> _userContexts = [];`
3. In `OnInitializedAsync`, after loading server data:
   ```csharp
   var serverId = ServerSvc.SelectedServerId;
   if (serverId is not null)
       _userContexts = (await DiscoverySvc.GetUserContextsAsync(serverId)).ToList();
   ```
4. Replace the context `<input>` (currently `<input class="input" @bind="_config.Context" placeholder="default" />`) with:
   ```razor
   <select class="input" @bind="_config.Context">
       @if (_userContexts.Count == 0)
       {
           <option value="@_config.Context">@_config.Context</option>
       }
       else
       {
           @foreach (var ctx in _userContexts)
           {
               <option value="@ctx.Name">@ctx.Name (@ctx.Extensions.Count ext)</option>
           }
           @if (!_userContexts.Any(c => c.Name == _config.Context))
           {
               <option value="@_config.Context">@_config.Context (custom)</option>
           }
       }
   </select>
   ```

The fallback ensures: if contexts can't be loaded (AMI down), the current value is preserved. If the current value isn't in the discovered list, it shows with "(custom)" suffix.

- [ ] **Step 2: Modify TrunkEdit.razor**

Same pattern as ExtensionEdit. Replace context text input with dropdown. Default value `from-trunk` shows all discovered contexts.

- [ ] **Step 3: Build and run all tests**

Run: `dotnet build Asterisk.Sdk.slnx && dotnet test Tests/PbxAdmin.Tests/`
Expected: ALL PASS

> **Note:** If `ExtensionEditTests` fail due to missing `DialplanDiscoveryService` in DI, register a mock in the test setup.

- [ ] **Step 4: Commit**

```bash
git add Examples/PbxAdmin/Components/Pages/ExtensionEdit.razor \
  Examples/PbxAdmin/Components/Pages/TrunkEdit.razor
git commit -m "feat(dialplan): replace context text inputs with discovered context dropdowns"
```

---

## Task 8: Full Test Run + Fix Regressions

**Files:** Any test files that need fixing

- [ ] **Step 1: Run full PbxAdmin test suite**

Run: `dotnet test Tests/PbxAdmin.Tests/`
Expected: ALL PASS

- [ ] **Step 2: Run full solution build**

Run: `dotnet build Asterisk.Sdk.slnx`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Fix any regressions**

If tests fail due to `DialplanDiscoveryService` DI registration in bUnit tests, add mock registrations:
```csharp
var discoverySvc = Substitute.For<DialplanDiscoveryService>();
// register in bUnit service collection
```

If tests fail due to `DialplanRegenerator` constructor change (new nullable parameter), existing code should still compile since the parameter has a default value.

- [ ] **Step 4: Commit fixes (if any)**

```bash
git add -A
git commit -m "test: fix regressions from dialplan discovery integration"
```
