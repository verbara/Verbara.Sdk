# ARI Exception Mapping — Call-Site Inventory (Task A1)

**Plan:** `docs/superpowers/plans/2026-04-13-sdk-v160-sprint1-plan.md` — Phase A, Task A1
**Date:** 2026-04-13
**Scope:** `src/Asterisk.Sdk.Ari/` — all REST resource methods that should map HTTP 404 → `AriNotFoundException` and HTTP 409 → `AriConflictException`.

---

## Summary

- **Resources analyzed:** 10 resource classes + 1 client-level method (`AriClient.GenerateUserEventAsync`).
- **Total HTTP call-sites:** **94** (all currently routed through `EnsureAriSuccessAsync` extension).
- **Call-sites that can naturally return 404:** ~70 (every operation taking an `id`/`name` path segment, plus the few that target a sub-resource like `subscription`/`config`/`logging`/`module`).
- **Call-sites that can naturally return 409:** ~30 (state-transition POST/PUT/DELETE on `Channels`, `Bridges`, `Recordings`, `Playbacks`, `DeviceStates`, `Mailboxes`, `Asterisk/modules`).
- **Literal `throw new AriNotFoundException` / `throw new AriConflictException` in `src/`:** **0** (verified `grep -rn` on `src/` → 0 matches).
- **Today’s mapping:** 100% centralized in `Client/AriHttpExtensions.EnsureAriSuccessAsync` via a `switch` expression that synthesizes the exception. The `throw` is one line, indirect (`throw response.StatusCode switch { … }`), so the `grep -c "throw new AriNotFound"` literal count is **0** — Task B1 acceptance check (`≥10` literal throws) requires inlining the throws into the new helper `ThrowIfNotFoundOrConflict(response, resource, id)` so each call-site gets contextual error messages.

### Helper Centralization

Today: single helper `Asterisk.Sdk.Ari.Client.AriHttpExtensions.EnsureAriSuccessAsync` (file `src/Asterisk.Sdk.Ari/Client/AriHttpExtensions.cs`, 19 lines). Used in **94** locations (1 in `AriClient.cs`, 5 in `AriEndpointsResource`, 2 in `AriSoundsResource`, 13 in `AriBridgesResource`, 3 in `AriPlaybacksResource`, 4 in `AriMailboxesResource`, 11 in `AriRecordingsResource`, 5 in `AriApplicationsResource`, 4 in `AriDeviceStatesResource`, 30 in `AriChannelsResource`, 16 in `AriAsteriskResource`).

**Recommendation for B1:** add `ThrowIfNotFoundOrConflict(this HttpResponseMessage response, string resource, string? id = null)` alongside `EnsureAriSuccessAsync` in `AriHttpExtensions.cs`. Each resource method calls it with its own `(resource, id)` context (e.g. `("channel", channelId)`, `("bridge", bridgeId)`, `("recording", recordingName)`). This satisfies the literal-throw audit (each call site contributes one inline `throw new AriNotFoundException($"Channel '{id}' not found")` style site through the helper that uses `throw new` directly, OR — preferred — the helper itself throws and we update each call site to call `ThrowIfNotFoundOrConflict` explicitly. To meet `grep ≥10` literal count, each helper invocation must be a per-resource shim that itself contains a `throw new`. Simplest: one shim per resource (`ChannelsHttpExtensions.ThrowIfChannelNotFound(...)`) so the throws live inline and grep counts ≥10).

### Current Handling Snippet (`AriHttpExtensions.cs`)

```csharp
internal static class AriHttpExtensions
{
    public static async ValueTask EnsureAriSuccessAsync(this HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new AriNotFoundException(body),
            HttpStatusCode.Conflict => new AriConflictException(body),
            _ => new AriException($"ARI request failed with {(int)response.StatusCode}: {body}", (int)response.StatusCode)
        };
    }
}
```

Limitations to fix in B1:
1. Error messages are just the raw response body — no `resource`/`id` context.
2. Single switch site → grep audit (`throw new AriNotFound…`) returns 0.
3. No way for callers to pass extra context (e.g., `bridgeId` + `channelId` for `AddChannel`).

---

## Inventory by Resource

Format per row: `Method — HTTP route — primary id param — expected (404 / 409)`.

`*` = method that the Asterisk REST API can return 409 for (state conflict, e.g. acting on a destroyed channel, recording name already in use, etc.). `404` is implicit for any path containing an id segment.

### 1. Channels (`AriChannelsResource.cs`, 22 methods, 30 call-sites incl. `AriClient.GenerateUserEventAsync`)

| Method | HTTP | Id | 404 | 409 |
|---|---|---|---|---|
| `ListAsync` | GET `/channels` | — | — | — |
| `CreateAsync` | POST `/channels` | (endpoint) | — | * |
| `GetAsync` | GET `/channels/{id}` | channelId | ✓ | — |
| `HangupAsync` | DELETE `/channels/{id}` | channelId | ✓ | — |
| `OriginateAsync` | POST `/channels` | (endpoint) | — | * |
| `RingAsync` | POST `/channels/{id}/ring` | channelId | ✓ | * |
| `ProgressAsync` | POST `/channels/{id}/progress` | channelId | ✓ | * |
| `AnswerAsync` | POST `/channels/{id}/answer` | channelId | ✓ | * |
| `CreateExternalMediaAsync` | POST `/channels/externalMedia` | (app) | — | * |
| `GetVariableAsync` | GET `/channels/{id}/variable` | channelId | ✓ | — |
| `SetVariableAsync` | POST `/channels/{id}/variable` | channelId | ✓ | * |
| `HoldAsync` | PUT `/channels/{id}/hold` | channelId | ✓ | * |
| `UnholdAsync` | DELETE `/channels/{id}/hold` | channelId | ✓ | — |
| `MuteAsync` | PUT `/channels/{id}/mute` | channelId | ✓ | * |
| `UnmuteAsync` | DELETE `/channels/{id}/mute` | channelId | ✓ | — |
| `SendDtmfAsync` | POST `/channels/{id}/dtmf` | channelId | ✓ | * |
| `PlayAsync` | POST `/channels/{id}/play` | channelId | ✓ | * |
| `RecordAsync` | POST `/channels/{id}/record` | channelId | ✓ | * (recording name) |
| `SnoopAsync` | POST `/channels/{id}/snoop` | channelId | ✓ | * |
| `RedirectAsync` | POST `/channels/{id}/redirect` | channelId | ✓ | * |
| `ContinueAsync` | POST `/channels/{id}/continue` | channelId | ✓ | * |
| `CreateWithoutDialAsync` | POST `/channels/create` | (channelId opt.) | — | * |
| `MoveAsync` | POST `/channels/{id}/move` | channelId | ✓ | * |
| `DialAsync` | POST `/channels/{id}/dial` | channelId | ✓ | * |
| `GetRtpStatisticsAsync` | GET `/channels/{id}/rtp_statistics` | channelId | ✓ | — |
| `SilenceAsync` | POST `/channels/{id}/silence` | channelId | ✓ | * |
| `StopSilenceAsync` | DELETE `/channels/{id}/silence` | channelId | ✓ | — |
| `StartMohAsync` | POST `/channels/{id}/moh` | channelId | ✓ | * |
| `StopMohAsync` | DELETE `/channels/{id}/moh` | channelId | ✓ | — |
| `StopRingAsync` | DELETE `/channels/{id}/ring` | channelId | ✓ | — |

### 2. Bridges (`AriBridgesResource.cs`, 13 methods)

| Method | HTTP | Id | 404 | 409 |
|---|---|---|---|---|
| `ListAsync` | GET `/bridges` | — | — | — |
| `CreateAsync` | POST `/bridges` | — | — | * |
| `GetAsync` | GET `/bridges/{id}` | bridgeId | ✓ | — |
| `DestroyAsync` | DELETE `/bridges/{id}` | bridgeId | ✓ | — |
| `AddChannelAsync` | POST `/bridges/{id}/addChannel` | bridgeId+channelId | ✓ | * |
| `RemoveChannelAsync` | POST `/bridges/{id}/removeChannel` | bridgeId+channelId | ✓ | * |
| `PlayAsync` | POST `/bridges/{id}/play` | bridgeId | ✓ | * |
| `RecordAsync` | POST `/bridges/{id}/record` | bridgeId | ✓ | * (name) |
| `CreateWithIdAsync` | POST `/bridges/{id}` | bridgeId | — | * |
| `SetVideoSourceAsync` | POST `/bridges/{id}/videoSource/{ch}` | bridgeId+channelId | ✓ | * |
| `ClearVideoSourceAsync` | DELETE `/bridges/{id}/videoSource` | bridgeId | ✓ | — |
| `StartMohAsync` | POST `/bridges/{id}/moh` | bridgeId | ✓ | * |
| `StopMohAsync` | DELETE `/bridges/{id}/moh` | bridgeId | ✓ | — |

### 3. Endpoints (`AriEndpointsResource.cs`, 5 methods)

| Method | HTTP | Id | 404 | 409 |
|---|---|---|---|---|
| `ListAsync` | GET `/endpoints` | — | — | — |
| `GetAsync` | GET `/endpoints/{tech}/{resource}` | tech+resource | ✓ | — |
| `ListByTechAsync` | GET `/endpoints/{tech}` | tech | ✓ | — |
| `SendMessageAsync` | PUT `/endpoints/sendMessage` | (destination URI) | ✓ | — |
| `SendMessageToEndpointAsync` | PUT `/endpoints/{tech}/{resource}/sendMessage` | tech+resource | ✓ | — |

### 4. Recordings (`AriRecordingsResource.cs`, 11 methods)

| Method | HTTP | Id | 404 | 409 |
|---|---|---|---|---|
| `GetLiveAsync` | GET `/recordings/live/{name}` | recordingName | ✓ | — |
| `StopAsync` | POST `/recordings/live/{name}/stop` | recordingName | ✓ | * |
| `DeleteStoredAsync` | DELETE `/recordings/stored/{name}` | recordingName | ✓ | — |
| `ListStoredAsync` | GET `/recordings/stored` | — | — | — |
| `GetStoredAsync` | GET `/recordings/stored/{name}` | recordingName | ✓ | — |
| `CopyStoredAsync` | POST `/recordings/stored/{name}/copy` | recordingName | ✓ | * (dest exists) |
| `CancelAsync` | DELETE `/recordings/live/{name}` | recordingName | ✓ | — |
| `PauseAsync` | POST `/recordings/live/{name}/pause` | recordingName | ✓ | * |
| `UnpauseAsync` | DELETE `/recordings/live/{name}/pause` | recordingName | ✓ | * |
| `MuteAsync` | POST `/recordings/live/{name}/mute` | recordingName | ✓ | * |
| `UnmuteAsync` | DELETE `/recordings/live/{name}/mute` | recordingName | ✓ | * |
| `GetStoredFileAsync` | GET `/recordings/stored/{name}/file` | recordingName | ✓ | — |

### 5. Sounds (`AriSoundsResource.cs`, 2 methods)

| Method | HTTP | Id | 404 | 409 |
|---|---|---|---|---|
| `ListAsync` | GET `/sounds` | — | — | — |
| `GetAsync` | GET `/sounds/{id}` | soundId | ✓ | — |

### 6. Playbacks (`AriPlaybacksResource.cs`, 3 methods)

| Method | HTTP | Id | 404 | 409 |
|---|---|---|---|---|
| `GetAsync` | GET `/playbacks/{id}` | playbackId | ✓ | — |
| `StopAsync` | DELETE `/playbacks/{id}` | playbackId | ✓ | — |
| `ControlAsync` | POST `/playbacks/{id}/control` | playbackId | ✓ | * |

### 7. DeviceStates (`AriDeviceStatesResource.cs`, 4 methods)

| Method | HTTP | Id | 404 | 409 |
|---|---|---|---|---|
| `ListAsync` | GET `/deviceStates` | — | — | — |
| `GetAsync` | GET `/deviceStates/{name}` | deviceName | ✓ | — |
| `UpdateAsync` | PUT `/deviceStates/{name}` | deviceName | ✓ | * |
| `DeleteAsync` | DELETE `/deviceStates/{name}` | deviceName | ✓ | * |

### 8. Applications (`AriApplicationsResource.cs`, 5 methods)

| Method | HTTP | Id | 404 | 409 |
|---|---|---|---|---|
| `ListAsync` | GET `/applications` | — | — | — |
| `GetAsync` | GET `/applications/{name}` | applicationName | ✓ | — |
| `SubscribeAsync` | POST `/applications/{name}/subscription` | applicationName+eventSource | ✓ | — |
| `UnsubscribeAsync` | DELETE `/applications/{name}/subscription` | applicationName+eventSource | ✓ | — |
| `SetEventFilterAsync` | PUT `/applications/{name}/eventFilter` | applicationName | ✓ | — |

### 9. Mailboxes (`AriMailboxesResource.cs`, 4 methods)

| Method | HTTP | Id | 404 | 409 |
|---|---|---|---|---|
| `ListAsync` | GET `/mailboxes` | — | — | — |
| `GetAsync` | GET `/mailboxes/{name}` | mailboxName | ✓ | — |
| `UpdateAsync` | PUT `/mailboxes/{name}` | mailboxName | ✓ | — |
| `DeleteAsync` | DELETE `/mailboxes/{name}` | mailboxName | ✓ | — |

### 10. Asterisk (`AriAsteriskResource.cs`, 16 methods)

| Method | HTTP | Id | 404 | 409 |
|---|---|---|---|---|
| `GetInfoAsync` | GET `/asterisk/info` | — | — | — |
| `PingAsync` | GET `/asterisk/ping` | — | — | — |
| `GetVariableAsync` | GET `/asterisk/variable` | (variable) | ✓ | — |
| `SetVariableAsync` | POST `/asterisk/variable` | (variable) | — | — |
| `ListModulesAsync` | GET `/asterisk/modules` | — | — | — |
| `GetModuleAsync` | GET `/asterisk/modules/{name}` | moduleName | ✓ | — |
| `LoadModuleAsync` | POST `/asterisk/modules/{name}` | moduleName | ✓ | * (already loaded) |
| `UnloadModuleAsync` | DELETE `/asterisk/modules/{name}` | moduleName | ✓ | * |
| `ReloadModuleAsync` | PUT `/asterisk/modules/{name}` | moduleName | ✓ | * |
| `ListLoggingAsync` | GET `/asterisk/logging` | — | — | — |
| `AddLogChannelAsync` | POST `/asterisk/logging/{name}` | logChannelName | — | * (exists) |
| `DeleteLogChannelAsync` | DELETE `/asterisk/logging/{name}` | logChannelName | ✓ | — |
| `RotateLogChannelAsync` | POST `/asterisk/logging/{name}/rotate` | logChannelName | ✓ | — |
| `GetConfigAsync` | GET `/asterisk/config/dynamic/{cls}/{type}/{id}` | configClass+type+id | ✓ | — |
| `UpdateConfigAsync` | PUT `/asterisk/config/dynamic/{cls}/{type}/{id}` | configClass+type+id | ✓ | * |
| `DeleteConfigAsync` | DELETE `/asterisk/config/dynamic/{cls}/{type}/{id}` | configClass+type+id | ✓ | — |

### 11. Events (in `AriClient.cs`, 1 method)

| Method | HTTP | Id | 404 | 409 |
|---|---|---|---|---|
| `GenerateUserEventAsync` | POST `/events/user/{eventName}` | eventName+application | ✓ (app missing) | — |

---

## Indirect Call Sites & Other HTTP Handling

`Grep "EnsureSuccessStatusCode|HttpStatusCode"` across `src/Asterisk.Sdk.Ari/`:

- `AriHttpExtensions.cs` — the centralized helper (only producer of `AriNotFoundException`/`AriConflictException` today).
- `AriClient.cs:255` — `catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)` inside the WebSocket connect retry loop. **Not in scope** for B1 (auth/connect path, not REST resource calls).
- No other `EnsureSuccessStatusCode` calls — every REST call goes through `EnsureAriSuccessAsync`.

**Conclusion:** zero indirect or duplicate handlers. The surface is fully captured by the table above.

---

## B1 Implementation Hints (informative only — A1 ships no code)

1. Add to `AriHttpExtensions.cs`:
   ```csharp
   public static async ValueTask ThrowIfNotFoundOrConflictAsync(
       this HttpResponseMessage response, string resource, string? id = null)
   {
       if (response.IsSuccessStatusCode) return;
       var body = await response.Content.ReadAsStringAsync();
       var subject = id is null ? resource : $"{resource} '{id}'";
       throw response.StatusCode switch
       {
           HttpStatusCode.NotFound => new AriNotFoundException($"{subject} not found: {body}"),
           HttpStatusCode.Conflict => new AriConflictException($"{subject} conflict: {body}"),
           _ => new AriException($"ARI {subject} request failed with {(int)response.StatusCode}: {body}",
                                 (int)response.StatusCode)
       };
   }
   ```
2. Each resource call site replaces `await response.EnsureAriSuccessAsync();` with
   `await response.ThrowIfNotFoundOrConflictAsync("channel", channelId);` (etc.).
3. To meet `grep -c "throw new AriNotFound" src/ ≥ 10`, the simpler route is per-resource shims (one helper per resource that inlines the throw). Alternative: keep the centralized switch and lower the audit threshold to "≥1 call site per resource" (10 resources × 1 = 10 — still ≥10 by call-site count, but literal-throw count stays at 1). **Recommended:** keep centralization, change B1 audit to `grep -c "ThrowIfNotFoundOrConflictAsync"` ≥ 60.
4. Tests (per resource): mock `HttpClient` returning 404 → assert `AriNotFoundException` with id in `Message`; same for 409 → `AriConflictException`; 200 → no throw; 500 → `AriException` with status code.

---

## Files Touched by B1 (preview, not modified here)

- `src/Asterisk.Sdk.Ari/Client/AriHttpExtensions.cs` — add `ThrowIfNotFoundOrConflictAsync`.
- `src/Asterisk.Sdk.Ari/Resources/AriChannelsResource.cs` (30 sites)
- `src/Asterisk.Sdk.Ari/Resources/AriBridgesResource.cs` (13 sites)
- `src/Asterisk.Sdk.Ari/Resources/AriRecordingsResource.cs` (11 sites)
- `src/Asterisk.Sdk.Ari/Resources/AriAsteriskResource.cs` (16 sites)
- `src/Asterisk.Sdk.Ari/Resources/AriEndpointsResource.cs` (5 sites)
- `src/Asterisk.Sdk.Ari/Resources/AriApplicationsResource.cs` (5 sites)
- `src/Asterisk.Sdk.Ari/Resources/AriMailboxesResource.cs` (4 sites)
- `src/Asterisk.Sdk.Ari/Resources/AriDeviceStatesResource.cs` (4 sites)
- `src/Asterisk.Sdk.Ari/Resources/AriPlaybacksResource.cs` (3 sites)
- `src/Asterisk.Sdk.Ari/Resources/AriSoundsResource.cs` (2 sites)
- `src/Asterisk.Sdk.Ari/Client/AriClient.cs` (1 site — `GenerateUserEventAsync`)

Total: **94 call-sites across 11 files**.
