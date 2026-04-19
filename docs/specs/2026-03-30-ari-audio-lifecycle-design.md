# ARI Audio Server Lifecycle Fix

**Date:** 2026-03-30
**Status:** Approved
**Version target:** 1.5.3

## Problem

When a user configures `options.Ari.ConfigureAudioServer` in `AddAsterisk()`, the ARI audio servers (`AudioSocketServer`, `WebSocketAudioServer`) are registered as DI singletons but **never started**. Nobody calls their `StartAsync()`/`StopAsync()`. This means:

- Asterisk ExternalMedia channels cannot connect (TCP listeners never open)
- `ExternalMediaActivity` times out waiting for audio streams
- Same class of bug fixed in v1.5.2 (AGI/ARI hosted services missing)

## Design

### Approach: Dedicated `AriAudioHostedService`

Create a new hosted service that starts/stops the concrete ARI audio servers, following the existing SDK pattern where each lifecycle resource has its own hosted service.

### Changes

**New file:** `src/Asterisk.Sdk.Hosting/AriAudioHostedService.cs`

```csharp
public sealed class AriAudioHostedService(
    AudioSocketServer audioSocketServer,
    WebSocketAudioServer? webSocketAudioServer = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await audioSocketServer.StartAsync(cancellationToken);
        if (webSocketAudioServer is not null)
            await webSocketAudioServer.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (webSocketAudioServer is not null)
            await webSocketAudioServer.StopAsync(cancellationToken);
        await audioSocketServer.StopAsync(cancellationToken);
    }
}
```

**Modified file:** `src/Asterisk.Sdk.Hosting/ServiceCollectionExtensions.cs`

Register `AriAudioHostedService` inside the `if (options.Ari.ConfigureAudioServer is not null)` block, after audio server singletons are registered.

**Modified file:** `src/Asterisk.Sdk.Hosting/PublicAPI.Unshipped.txt`

Add public API entries for the new class.

### Why not the alternatives

| Alternative | Why rejected |
|-------------|-------------|
| Extend `AriConnectionHostedService` | Mixes WebSocket connection with TCP listeners — different concerns |
| Start in `AriClient.ConnectAsync()` | AriClient receives `IAudioServer` (no lifecycle methods), would need casting. Also couples unrelated concerns |
| Add Start/Stop to `IAudioServer` | Breaking change to shipped public API |
| Make audio servers implement `IHostedService` | Couples domain classes to hosting framework |

### Pattern consistency

| Resource | HostedService |
|----------|--------------|
| AMI Connection | `AmiConnectionHostedService` |
| AGI Server | `AgiHostedService` |
| ARI Client | `AriConnectionHostedService` |
| Asterisk Server | `AsteriskServerHostedService` |
| Sessions | `SessionManagerHostedService` |
| **ARI Audio** | **`AriAudioHostedService`** (new) |

## Test plan

- Verify build: 0 warnings
- Verify existing tests pass
- Verify `AriAudioHostedService` is registered when `ConfigureAudioServer` is provided
- Verify it is NOT registered when audio is not configured
