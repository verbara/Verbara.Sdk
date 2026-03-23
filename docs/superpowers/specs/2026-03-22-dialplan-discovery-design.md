# Dialplan Discovery & Editor — Design Specification

**Date:** 2026-03-22
**Status:** Approved
**Scope:** PbxAdmin — generic dialplan viewer/editor for any Asterisk server

---

## 1. Problem Statement

PbxAdmin's extension and trunk creation forms use a free-text field for the PJSIP endpoint `context`. This causes:

- Users type invalid context names (e.g., `from-internal`) that don't exist in the Asterisk dialplan
- No visibility into what contexts exist on the connected Asterisk server
- No way to inspect the dialplan structure (extensions, includes, hierarchy) from PbxAdmin
- No way to edit the dialplan without SSH access to the Asterisk server

**Design principle:** PbxAdmin is a generic admin tool for ANY Asterisk. It must discover what exists, not impose a structure.

## 2. Solution Overview

A **Dialplan Discovery Service** that:

- Discovers all contexts, extensions, and includes from any connected Asterisk via AMI `dialplan show`
- Caches results in memory with 5-minute TTL + manual refresh
- Filters system/internal contexts for UI dropdowns
- Replaces free-text context inputs with validated dropdowns

A **Dialplan Editor Service** that:

- Adds/removes extensions, includes, and contexts via AMI commands
- Persists changes: File mode → `dialplan save`, Realtime mode → SQL `extensions` table
- Refreshes discovery cache after every mutation

A **Dialplan page** (`/dialplan`) that:

- Lists all discovered contexts with extension counts
- Shows context detail: extensions, includes, included-by
- Provides full CRUD for extensions, includes, and contexts
- Visualizes the include hierarchy as an indented tree

## 3. Architecture

### 3.1 Service Layer

```
AsteriskMonitorService (existing AMI connection)
         │
         ▼
DialplanDiscoveryService (new, singleton)
    ├── In-memory cache per server (TTL 5min)
    ├── ParseDialplanOutput() — regex parser for AMI output
    ├── RefreshAsync(serverId) — force refresh
    ├── GetSnapshotAsync(serverId) → DialplanSnapshot
    ├── GetContextsAsync(serverId) → List<DiscoveredContext>
    ├── GetContextAsync(serverId, name) → DiscoveredContext?
    ├── GetUserContextsAsync(serverId) → List<DiscoveredContext> (filtered, no system)
    └── ContextExistsAsync(serverId, name) → bool

DialplanEditorService (new, singleton)
    ├── AddExtensionAsync(serverId, context, exten, priority, app, appData)
    ├── RemoveExtensionAsync(serverId, context, exten, priority)
    ├── AddIncludeAsync(serverId, context, includedContext)
    ├── RemoveIncludeAsync(serverId, context, includedContext)
    ├── CreateContextAsync(serverId, name) — adds placeholder + saves
    ├── RemoveContextAsync(serverId, name)
    ├── SaveDialplanAsync(serverId) — File: dialplan save, Realtime: no-op (already in DB)
    └── Uses IConfigProviderResolver to detect File vs Realtime mode
```

**Logging:** Both services use source-generated `[LoggerMessage]` via static partial log classes (`DialplanDiscoveryLog`, `DialplanEditorLog`), consistent with existing `FileDialplanLog`, `RealtimeDialplanLog`, etc.

### 3.2 Refresh Strategy

- **Initial load:** When `AsteriskMonitorService` connects to a server, `DialplanDiscoveryService.RefreshAsync(serverId)` is called
- **TTL refresh:** Background timer refreshes every 5 minutes per server
- **Manual refresh:** UI "Refresh" button calls `RefreshAsync(serverId)`
- **Post-mutation refresh:** `DialplanEditorService` calls `RefreshAsync` after every successful mutation
- **Post-regeneration:** `DialplanRegenerator` calls `RefreshAsync` after regenerating routes/IVR/time conditions

### 3.3 Dual Persistence

**File mode** (`ConfigMode = File`):
```
Mutation request
  → AMI command (e.g., "dialplan add extension default,_100X,1,Dial,PJSIP/${EXTEN}")
  → AMI "dialplan save" (writes to extensions.conf, Asterisk creates .old backup)
  → RefreshAsync(serverId)
```

**Realtime mode** (`ConfigMode = Realtime`):
```
Mutation request
  → SQL INSERT/UPDATE/DELETE on "extensions" table via Dapper
  → AMI "dialplan reload" (Asterisk reloads from DB)
  → RefreshAsync(serverId)
```

The `extensions` table is standard Asterisk Realtime:
```sql
-- Standard Asterisk Realtime table, already exists if Realtime dialplan is configured
-- If PbxAdmin detects Realtime mode but table doesn't exist, it shows a setup warning
CREATE TABLE IF NOT EXISTS extensions (
    id       SERIAL PRIMARY KEY,
    context  VARCHAR(40) NOT NULL,
    exten    VARCHAR(40) NOT NULL,
    priority INT NOT NULL,
    app      VARCHAR(40) NOT NULL,
    appdata  VARCHAR(256) DEFAULT '',
    UNIQUE(context, exten, priority)
);
```

### 3.4 Error Handling

| Scenario | Behavior |
|----------|----------|
| AMI `dialplan add` fails | Return error message to UI, no save attempted |
| AMI `dialplan save` fails | Revert with `dialplan remove`, notify user |
| SQL INSERT fails | Rollback transaction, notify user |
| AMI `dialplan reload` fails | Data is in DB but not loaded — show warning: "Saved but reload failed, try manual reload" |
| AMI disconnected | Discovery returns stale cache with warning badge "Last refreshed X min ago" |
| `ShowDialplan` returns empty | Show "No dialplan loaded" message |
| Circular include detected | Validate at write time: `AddIncludeAsync` checks if adding would create a cycle, rejects with error |
| Empty context creation | `CreateContextAsync` adds a `NoOp` placeholder extension (Asterisk does not persist empty contexts in `extensions.conf`) |
| Delete extension | `RemoveExtensionAsync` removes ALL priorities for a pattern (not individual priorities) |

## 4. Data Model

All in-memory, no PbxAdmin database tables. Asterisk is the source of truth.

```csharp
/// <summary>Point-in-time snapshot of a server's dialplan.</summary>
public sealed class DialplanSnapshot
{
    public string ServerId { get; set; } = "";
    public DateTime RefreshedAt { get; set; }
    public List<DiscoveredContext> Contexts { get; set; } = [];
}

/// <summary>A dialplan context discovered from Asterisk.</summary>
public sealed class DiscoveredContext
{
    public string Name { get; set; } = "";
    public string CreatedBy { get; set; } = "";   // "pbx_config", "res_parking", "pbx_realtime"
    public bool IsSystem { get; set; }             // true when CreatedBy is NOT pbx_config/pbx_realtime
    public List<DialplanExtension> Extensions { get; set; } = [];
    public List<string> Includes { get; set; } = [];
}

/// <summary>An extension pattern within a context.</summary>
public sealed class DialplanExtension
{
    public string Pattern { get; set; } = "";      // "_2XXX", "100", "*78"
    public List<DialplanPriority> Priorities { get; set; } = [];
}

/// <summary>A single priority line within an extension.</summary>
public sealed class DialplanPriority
{
    public int Number { get; set; }                // 1, 2, 3...
    public string? Label { get; set; }             // "nodata", "allow", null
    public string Application { get; set; } = "";  // "Dial", "Set", "Goto"
    public string ApplicationData { get; set; } = ""; // "PJSIP/${EXTEN},30"
    public string? Source { get; set; }            // "[extensions.conf:25]"
}
```

### 4.1 Discovery Strategy — SDK `ShowDialplanAction`

**The SDK already has typed AMI support for dialplan discovery.** We use `ShowDialplanAction` (not CLI `dialplan show`) which returns structured `ListDialplanEvent` events.

**SDK types (in `Asterisk.Sdk.Ami`):**
- `ShowDialplanAction` — sends AMI `ShowDialplan` action
- `ListDialplanEvent` — one event per priority line, with typed fields
- `ShowDialplanCompleteEvent` — completion event with counts

**`ListDialplanEvent` fields from AMI (verified against Asterisk 22):**
```
Event: ListDialplan
Context: default              ← context name
Extension: _2XXX              ← extension pattern
Priority: 1                   ← priority number
Application: Dial             ← application name
AppData: PJSIP/${EXTEN},30   ← application data
Registrar: pbx_config         ← who created it (module name)
IncludeContext: parkedcalls    ← present only for include entries
ExtensionLabel: nodata         ← priority label (optional)
```

**Required SDK changes:** Add `Context` and `Priority` properties to `ListDialplanEvent` (currently missing — the AMI sends them but the SDK doesn't map them). These are simple property additions to the existing event class.

**Processing flow:**
```
1. SendActionAsync<ShowDialplanCompleteEvent>(new ShowDialplanAction())
   → collects List<ListDialplanEvent> via ResponseEventCollector
2. Group events by Context
3. For each context:
   - Set CreatedBy from Registrar (first event in context)
   - Events with IncludeContext → add to Includes list
   - Events with Extension → group by Extension, build priorities
4. Build DialplanSnapshot

No regex parsing needed.
```

**Realtime mode supplement:** In Realtime mode, `ShowDialplan` only returns contexts loaded in Asterisk's memory. To get the complete picture, also query the `extensions` table directly:
```sql
SELECT DISTINCT context FROM extensions ORDER BY context
```
Merge with AMI results — SQL provides contexts that exist in DB but may not be loaded yet.

### 4.2 Thread Safety & Cache Design

The `DialplanDiscoveryService` is a singleton shared across Blazor circuits. The cache uses a **snapshot-swap pattern**:

```csharp
private volatile IReadOnlyDictionary<string, DialplanSnapshot> _snapshots =
    new Dictionary<string, DialplanSnapshot>();

public async Task RefreshAsync(string serverId, CancellationToken ct = default)
{
    var snapshot = await BuildSnapshotAsync(serverId, ct);
    var dict = new Dictionary<string, DialplanSnapshot>(_snapshots) { [serverId] = snapshot };
    _snapshots = dict; // atomic reference swap, no lock needed for readers
}
```

All public methods take `CancellationToken ct = default` following the project convention. The service implements `IDisposable` to clean up background refresh timers.

### 4.3 SDK Changes Required

Add missing properties to `ListDialplanEvent`:
```csharp
// src/Asterisk.Sdk.Ami/Events/ListDialplanEvent.cs
public string? Context { get; set; }     // AMI sends this but SDK doesn't map it yet
public int? Priority { get; set; }        // AMI sends this but SDK doesn't map it yet
```

Add optional `Context` filter to `ShowDialplanAction`:
```csharp
// src/Asterisk.Sdk.Ami/Actions/ShowDialplanAction.cs
public string? Context { get; set; }     // Filter by context name (optional)
```

These are backward-compatible additions. No existing code breaks.

### 4.4 System Context Filtering

A context is considered **system/internal** when `CreatedBy` is NOT one of:
- `pbx_config` (loaded from extensions.conf)
- `pbx_realtime` (loaded from Realtime DB)
- `pbx_lua`, `pbx_ael` (alternative dialplan languages — user-created)

Known system modules to filter:
- `func_periodic_hook` — internal hook context
- `res_parking` — parking lot contexts (shown separately in PbxAdmin Parking page)
- `app_queue` — queue contexts

The UI dropdown for Extension/Trunk edit shows only non-system contexts. The Dialplan page shows ALL contexts but badges system ones.

## 5. UI Pages

### 5.1 New Page: Dialplan (`/dialplan`)

**Route:** `@page "/dialplan"`
**Nav:** Under "PBX Management" section, after existing entries

**Layout:** Two-panel (consistent with existing PbxAdmin pattern)

**Left panel — Context list:**
- KPI row: Total contexts, User contexts, Total extensions
- Search/filter input
- Card per context:
  - Name (bold)
  - Badge: extension count
  - Badge: "System" (muted) if IsSystem
  - Click → loads detail in right panel
- Buttons at top: "New Context" (opens modal), "Refresh" (force cache refresh)
- Refresh shows `RefreshedAt` timestamp

**Right panel — Context detail (when context selected):**
- Header: context name, CreatedBy badge, System/User badge
- **Includes section:**
  - List of included contexts with "Remove" button each
  - "Add Include" button → dropdown of other contexts
- **Included By section:**
  - Read-only list of contexts that include this one (reverse lookup from snapshot)
- **Extensions section:**
  - Table: Pattern | Priority Count | First App | Actions
  - Click pattern → expands to show all priorities (Number, Label, App, AppData, Source)
  - Actions: "Edit" (opens modal), "Delete" (confirmation)
  - "Add Extension" button → opens modal
- **Footer:**
  - "Delete Context" button (red, confirmation dialog)
  - Disabled if context has "Included By" entries or is System
  - Warning: "This will remove the context and all its extensions"

**Modal: Add/Edit Extension:**
- Context (read-only, inherited from selected context)
- Pattern (text input, e.g., `_100X`, `1001`, `*78`)
- Priorities: ordered list
  - Each row: Priority # (auto), Label (optional), Application (dropdown), AppData (text)
  - Common apps in dropdown: `Answer`, `Dial`, `Goto`, `GotoIf`, `Set`, `Playback`, `Queue`, `VoiceMail`, `VoiceMailMain`, `Hangup`, `NoOp`, `Park`, `Pickup`, `MeetMe`, `ConfBridge`, `AgentLogin`, `AgentLogoff`, `Background`, `WaitExten`, `Wait`, `Busy`, `Congestion`, `Progress`, `Ringing`, `Echo`, `SendDTMF`
  - Free text also allowed for apps not in the list
  - "Add Priority" button
  - Drag/reorder or up/down buttons

**Modal: View Include Tree:**
- Triggered by "View Tree" button on any context
- Shows indented tree starting from selected context:
  ```
  default
  ├── parkedcalls (3 extensions) [System]
  ├── outbound-routes (5 extensions)
  └── from-trunk (2 extensions)
      └── internal (0 extensions)
  ```
- Tree is built by recursively following Includes from the snapshot
- Cycle detection: if a context appears twice in the chain, show "↻ cycle" and stop

### 5.2 Modified Pages

**ExtensionEdit.razor:**
- Replace `<input class="input" @bind="_config.Context">` with:
  ```razor
  <select class="input" @bind="_config.Context">
      @foreach (var ctx in _userContexts)
      {
          <option value="@ctx.Name">@ctx.Name (@ctx.Extensions.Count ext)</option>
      }
  </select>
  ```
- Load contexts in `OnInitializedAsync`: `_userContexts = await DiscoverySvc.GetUserContextsAsync(serverId)`
- Keep the current value even if not in the list (existing extensions may have custom contexts)

**TrunkEdit.razor:**
- Same pattern: replace text input with dropdown
- Default remains `from-trunk` but shows all discovered contexts

### 5.3 Localization Keys

New keys for EN and ES:

```
Nav_Dialplan = "Dialplan" / "Dialplan"
DP_Title = "Dialplan" / "Dialplan"
DP_Heading = "Dialplan Contexts" / "Contextos del Dialplan"
DP_Refresh = "Refresh" / "Actualizar"
DP_RefreshedAt = "Last refreshed: {0}" / "Última actualización: {0}"
DP_NewContext = "New Context" / "Nuevo Contexto"
DP_ContextName = "Context Name" / "Nombre del Contexto"
DP_CreatedBy = "Created By" / "Creado Por"
DP_System = "System" / "Sistema"
DP_User = "User" / "Usuario"
DP_Extensions = "Extensions" / "Extensiones"
DP_Includes = "Includes" / "Incluye"
DP_IncludedBy = "Included By" / "Incluido Por"
DP_AddInclude = "Add Include" / "Agregar Include"
DP_AddExtension = "Add Extension" / "Agregar Extensión"
DP_EditExtension = "Edit Extension" / "Editar Extensión"
DP_Pattern = "Pattern" / "Patrón"
DP_Priority = "Priority" / "Prioridad"
DP_Application = "Application" / "Aplicación"
DP_AppData = "App Data" / "Datos de la App"
DP_Label = "Label" / "Etiqueta"
DP_Source = "Source" / "Fuente"
DP_DeleteContext = "Delete Context" / "Eliminar Contexto"
DP_DeleteContextWarn = "This will remove the context and all its extensions." / "Esto eliminará el contexto y todas sus extensiones."
DP_ViewTree = "View Tree" / "Ver Árbol"
DP_NoContexts = "No contexts found." / "No se encontraron contextos."
DP_SaveSuccess = "Dialplan saved." / "Dialplan guardado."
DP_SaveFailed = "Save failed: {0}" / "Error al guardar: {0}"
DP_ReloadFailed = "Saved but dialplan reload failed. Try manual reload." / "Guardado pero la recarga del dialplan falló. Intente recargar manualmente."
DP_StaleWarning = "Data may be outdated." / "Los datos pueden estar desactualizados."
DP_Total = "Total" / "Total"
DP_UserContexts = "User" / "Usuario"
DP_TotalExtensions = "Extensions" / "Extensiones"
Lbl_System = "System" / "Sistema"
```

## 6. Integration Points

### 6.1 Service Dependencies

```
DialplanDiscoveryService
  ├── IConfigProviderResolver (to call ExecuteCommandAsync for AMI)
  └── ILogger<DialplanDiscoveryService>

DialplanEditorService
  ├── IConfigProviderResolver (AMI commands for File mode)
  ├── IConfiguration (to get RealtimeConnectionString for Realtime mode)
  ├── DialplanDiscoveryService (to refresh cache after mutations)
  └── ILogger<DialplanEditorService>
```

### 6.2 Existing Services Modified

**ExtensionService:**
- `CreateExtensionAsync` / `UpdateExtensionAsync`: validate context exists via `DialplanDiscoveryService.ContextExistsAsync()`. If context doesn't exist, return validation error (don't block — warn the user but allow saving for advanced use cases where context will be created later).

**DialplanRegenerator:**
- After `RegenerateAsync` completes, call `DialplanDiscoveryService.RefreshAsync(serverId)` to update cache with newly generated routes/IVR/TC contexts.

**Program.cs:**
- Register `DialplanDiscoveryService` and `DialplanEditorService` as singletons.

### 6.3 Startup Flow

```
App starts
  → AsteriskMonitorService connects to each server
  → For each connected server:
    → DialplanDiscoveryService.RefreshAsync(serverId) — initial load
    → Timer started: RefreshAsync every 5 minutes
  → UI pages can now call GetContextsAsync/GetUserContextsAsync
```

## 7. Testing Strategy

### 7.1 Unit Tests

- `DialplanParserTests` — parse various `dialplan show` outputs:
  - Normal output with contexts, extensions, includes
  - Empty output
  - Malformed lines (skipped gracefully)
  - Context with labels on priorities
  - Multiple contexts with includes
  - System context detection (res_parking, func_periodic_hook)
- `DialplanDiscoveryServiceTests` — cache behavior:
  - Returns cached data within TTL
  - Refreshes after TTL expires
  - RefreshAsync forces immediate reload
  - GetUserContextsAsync filters system contexts
  - ContextExistsAsync returns correct results
- `DialplanEditorServiceTests` — mutation + persistence:
  - File mode: calls correct AMI commands + dialplan save
  - Realtime mode: executes correct SQL + dialplan reload
  - Refresh called after each mutation
  - Error handling: AMI failure, SQL failure

### 7.2 Existing Test Impact

- `ExtensionConfigTests`: context default changed from `from-internal` to `default` (already done on this branch)
- `DialplanGeneratorTests`: destination contexts changed to `default` (already done)
- `ExtensionEditTests`: may need mock `DialplanDiscoveryService` registered in DI

## 8. File Map

### New Files

| File | Responsibility |
|------|---------------|
| `Examples/PbxAdmin/Models/DialplanSnapshot.cs` | `DialplanSnapshot`, `DiscoveredContext`, `DialplanExtension`, `DialplanPriority` |
| `Examples/PbxAdmin/Services/Dialplan/DialplanDiscoveryService.cs` | AMI-based discovery + in-memory cache |
| `Examples/PbxAdmin/Services/Dialplan/DialplanEditorService.cs` | Mutations via AMI (File) or SQL (Realtime) |
| `src/Asterisk.Sdk.Ami/Events/ListDialplanEvent.cs` | Add `Context` and `Priority` properties |
| `src/Asterisk.Sdk.Ami/Actions/ShowDialplanAction.cs` | Add optional `Context` filter property |
| `Examples/PbxAdmin/Components/Pages/Dialplan.razor` | Main dialplan page (two-panel) |
| `Examples/PbxAdmin/Components/Pages/DialplanContextDetail.razor` | Context detail component (right panel) |
| `Tests/PbxAdmin.Tests/Services/DialplanParserTests.cs` | Parser unit tests |
| `Tests/PbxAdmin.Tests/Services/DialplanDiscoveryServiceTests.cs` | Cache + filtering tests |
| `Tests/PbxAdmin.Tests/Services/DialplanEditorServiceTests.cs` | Mutation + persistence tests |

### Modified Files

| File | Change |
|------|--------|
| `Examples/PbxAdmin/Components/Pages/ExtensionEdit.razor` | Context text input → dropdown |
| `Examples/PbxAdmin/Components/Pages/TrunkEdit.razor` | Context text input → dropdown |
| `Examples/PbxAdmin/Components/Layout/MainLayout.razor` | Add "Dialplan" nav link |
| `Examples/PbxAdmin/Services/Dialplan/DialplanRegenerator.cs` | Call RefreshAsync after regeneration |
| `Examples/PbxAdmin/Program.cs` | Register new services in DI |
| `Examples/PbxAdmin/Resources/SharedStrings.resx` | Add DP_* localization keys (EN) |
| `Examples/PbxAdmin/Resources/SharedStrings.es.resx` | Add DP_* localization keys (ES) |
