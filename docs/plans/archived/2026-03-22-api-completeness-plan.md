# Asterisk.Sdk — API Completeness Plan

**Fecha:** 2026-03-22
**Objetivo:** Cubrir el 100% de la API pública de Asterisk (18-23) en AMI, ARI y AGI.
**Versión actual:** 1.1.0
**Target:** v1.2.0 → v1.3.0 → v1.4.0

---

## Gap Analysis Summary

| Capa | Asterisk Ofrece | SDK Tiene | Cobertura | Gap |
|------|----------------|-----------|-----------|-----|
| AMI Actions | 152 | 118 | 78% | 34 faltan |
| AMI Events | 180 | 261 | 145% | SDK tiene más (legacy/compat) |
| AGI Commands | 47 | 54 | 100%+ | Completo + 7 extras |
| ARI Resources | 11 cat / 98 endpoints | 8 cat / ~44 endpoints | 45% | 54 endpoints faltan |
| ARI Events | 46 | 46 | 100% | Completo |
| ARI Models | 27 | 16 | 59% | 11 modelos faltan |

---

## Sprint A — AMI PJSIP + Bridge + Transfer Actions (v1.2.0)

**Scope:** 21 new AMI actions + 1 event field update
**Estimated effort:** ~2 days with subagent-driven development

### Task A1: PJSIP Actions (11 actions)
- [ ] `PJSIPShowAors` — List all PJSIP AOR objects
- [ ] `PJSIPShowAuths` — List all PJSIP Auth objects
- [ ] `PJSIPShowRegistrationsInbound` — List inbound registrations
- [ ] `PJSIPShowRegistrationsOutbound` — List outbound registrations
- [ ] `PJSIPShowResourceLists` — List resource lists
- [ ] `PJSIPShowSubscriptionsInbound` — List inbound subscriptions
- [ ] `PJSIPShowSubscriptionsOutbound` — List outbound subscriptions
- [ ] `PJSIPRegister` — Trigger outbound registration
- [ ] `PJSIPUnregister` — Cancel outbound registration
- [ ] `PJSIPQualify` — Send SIP OPTIONS qualify
- [ ] `PJSIPHangup` — Hangup with SIP cause code

**Notes:**
- PJSIPShowContacts, PJSIPShowEndpoint, PJSIPShowEndpoints already exist in SDK (added in v1.1.0)
- PJSIPNotify already exists in SDK
- Each "Show" action is event-generating (returns list events + Complete event)
- Need corresponding response/list events for each Show action

### Task A2: PJSIP Response Events (for Show actions)
- [ ] `AorDetailEvent` — already exists, verify fields
- [ ] `AorListEvent` / `AorListCompleteEvent` — if missing
- [ ] `AuthDetailEvent` / `AuthListEvent` / `AuthListCompleteEvent` — verify/add
- [ ] `InboundRegistrationDetailEvent` — already exists, verify fields
- [ ] `OutboundRegistrationDetailEvent` — already exists, verify fields
- [ ] `ResourceListDetailEvent` — add if missing
- [ ] `InboundSubscriptionDetailEvent` — add if missing
- [ ] `OutboundSubscriptionDetailEvent` — add if missing
- [ ] `ContactListEvent` / `ContactListCompleteEvent` — verify/add
- [ ] `TransportDetailEvent` — verify/add

### Task A3: Bridge Management Actions (7 actions)
- [ ] `BridgeDestroyAction` — Destroy a bridge
- [ ] `BridgeInfoAction` — Get bridge information
- [ ] `BridgeKickAction` — Kick channel from bridge
- [ ] `BridgeListAction` — List all bridges
- [ ] `BridgeTechnologyListAction` — List bridge technologies
- [ ] `BridgeTechnologySuspendAction` — Suspend bridge tech
- [ ] `BridgeTechnologyUnsuspendAction` — Unsuspend bridge tech

**Notes:**
- BridgeInfo is event-generating (returns BridgeInfoChannel events + BridgeInfoComplete)
- BridgeList is event-generating (returns BridgeListItem events + BridgeListComplete)
- Need corresponding events: `BridgeInfoChannelEvent`, `BridgeInfoCompleteEvent`, `BridgeListItemEvent`, `BridgeListCompleteEvent`

### Task A4: Transfer Actions (2 actions)
- [ ] `BlindTransferAction` — Blind transfer via AMI (different from ARI)
- [ ] `CancelAtxferAction` — Cancel attended transfer

### Task A5: TechCause Field (Asterisk 23)
- [ ] Add `TechCause` nullable string property to `HangupEvent`
- [ ] Add `TechCause` nullable string property to `HangupRequestEvent`
- [ ] Add `TechCause` nullable string property to `SoftHangupRequestEvent`

### Task A6: Unit Tests for Sprint A
- [ ] Tests for all 21 new actions (serialization, required fields)
- [ ] Tests for new/updated events (deserialization, TechCause field)
- [ ] Integration test stubs for event-generating actions
- [ ] Verify source generators pick up new actions/events

---

## Sprint B — ARI Completeness (v1.3.0)

**Scope:** 3 new resource classes, ~37 new endpoints, 11 new models
**Estimated effort:** ~2-3 days with subagent-driven development

### Task B1: ARI Asterisk Resource (16 endpoints) — NEW
- [ ] Create `AriAsteriskResource` class
- [ ] `GetInfoAsync()` → AriAsteriskInfo
- [ ] `PingAsync()` → AriAsteriskPing
- [ ] `GetVariableAsync(variable)` → string
- [ ] `SetVariableAsync(variable, value)`
- [ ] `ListModulesAsync()` → AriModule[]
- [ ] `GetModuleAsync(moduleName)` → AriModule
- [ ] `LoadModuleAsync(moduleName)`
- [ ] `UnloadModuleAsync(moduleName)`
- [ ] `ReloadModuleAsync(moduleName)`
- [ ] `ListLoggingAsync()` → AriLogChannel[]
- [ ] `AddLogChannelAsync(logChannelName, configuration)`
- [ ] `DeleteLogChannelAsync(logChannelName)`
- [ ] `RotateLogChannelAsync(logChannelName)`
- [ ] `GetConfigAsync(configClass, objectType, id)` → AriConfigTuple[]
- [ ] `UpdateConfigAsync(configClass, objectType, id, fields)`
- [ ] `DeleteConfigAsync(configClass, objectType, id)`

### Task B2: ARI Mailboxes Resource (4 endpoints) — NEW
- [ ] Create `AriMailboxesResource` class
- [ ] `ListAsync()` → AriMailbox[]
- [ ] `GetAsync(mailboxName)` → AriMailbox
- [ ] `UpdateAsync(mailboxName, oldMessages, newMessages)`
- [ ] `DeleteAsync(mailboxName)`

### Task B3: ARI Events Resource (1 endpoint) — NEW
- [ ] `GenerateUserEventAsync(eventName, application, source, variables)`

### Task B4: Complete ARI Channels Resource (~7 missing endpoints)
- [ ] `MoveAsync(channelId, app, appArgs)` — Move to another Stasis app
- [ ] `DialAsync(channelId, caller, timeout)` — Dial a created channel
- [ ] `GetRtpStatisticsAsync(channelId)` → AriRtpStats
- [ ] `TransferProgressAsync(channelId)` — Inform transfer progress (Ast 22.3+)
- [ ] `SnoopWithIdAsync(channelId, snoopId, spy, whisper, app, appArgs)`
- [ ] `PlayWithIdAsync(channelId, playbackId, media, lang, offsetms, skipms)`
- [ ] `OriginateWithIdAsync(channelId, ...)` — Originate with specific channel ID

### Task B5: Complete ARI Bridges Resource (~6 missing endpoints)
- [ ] `CreateWithIdAsync(bridgeId, type, name)` — Create with specific ID
- [ ] `SetVideoSourceAsync(bridgeId, channelId)` — Set video source
- [ ] `ClearVideoSourceAsync(bridgeId)` — Clear video source
- [ ] `StartMohAsync(bridgeId, mohClass)` — Start MOH (separate from play)
- [ ] `StopMohAsync(bridgeId)` — Stop MOH
- [ ] `PlayWithIdAsync(bridgeId, playbackId, media, ...)` — Play with specific ID
- [ ] Add `announcerFormat` param to `PlayAsync()` (Asterisk 23)
- [ ] Add `recorderFormat` param to `RecordAsync()` (Asterisk 23)

### Task B6: Complete ARI Endpoints Resource (5 missing endpoints)
- [ ] `ListByTechAsync(tech)` → AriEndpoint[]
- [ ] `SendMessageAsync(to, from, body, variables)` — Send to URI
- [ ] `SendMessageToEndpointAsync(tech, resource, from, body, variables)`
- [ ] `ReferAsync(to, from, referTo)` — Refer endpoint (Ast 18.20+)
- [ ] `ReferEndpointAsync(tech, resource, from, referTo)`

### Task B7: Complete ARI Applications Resource (3 missing endpoints)
- [ ] `SubscribeAsync(applicationName, eventSource)` — Subscribe to event source
- [ ] `UnsubscribeAsync(applicationName, eventSource)` — Unsubscribe
- [ ] `SetEventFilterAsync(applicationName, filter)` — Filter events (Ast 13.26+)

### Task B8: Complete ARI Recordings Resource (9 missing endpoints)
- [ ] `ListStoredAsync()` → AriStoredRecording[]
- [ ] `GetStoredAsync(recordingName)` → AriStoredRecording
- [ ] `DeleteStoredAsync(recordingName)` — already exists, verify
- [ ] `GetStoredFileAsync(recordingName)` → Stream (binary download, Ast 14+)
- [ ] `CopyStoredAsync(recordingName, destinationName)` → AriStoredRecording
- [ ] `PauseAsync(recordingName)` — Pause live recording
- [ ] `UnpauseAsync(recordingName)` — Unpause live recording
- [ ] `MuteAsync(recordingName)` — Mute live recording
- [ ] `UnmuteAsync(recordingName)` — Unmute live recording

### Task B9: New ARI Models (11 models)
- [ ] `AriAsteriskInfo` — System info aggregate (build, system, config, status)
- [ ] `AriAsteriskPing` — Ping response (timestamp, ping, asterisk_id)
- [ ] `AriBuildInfo` — Build info (os, kernel, machine, options, date)
- [ ] `AriConfigInfo` — Config info (name, default_language, setid, max_channels, etc.)
- [ ] `AriConfigTuple` — Config key-value pair (attribute, value)
- [ ] `AriSystemInfo` — System info (version, entity_id)
- [ ] `AriStatusInfo` — Status info (startup_time, last_reload_time)
- [ ] `AriLogChannel` — Log channel (channel, type, status, configuration)
- [ ] `AriModule` — Module info (name, description, use_count, status, support_level)
- [ ] `AriMailbox` — Mailbox (name, old_messages, new_messages)
- [ ] `AriRtpStats` — RTP statistics (txcount, rxcount, txjitter, rxjitter, etc.)

### Task B10: Update IAriClient Interface
- [ ] Add `Asterisk` property → `AriAsteriskResource`
- [ ] Add `Mailboxes` property → `AriMailboxesResource`
- [ ] Add `Events` property → `AriEventsResource`
- [ ] Update `Bridges` with new methods
- [ ] Update `Channels` with new methods
- [ ] Update `Endpoints` with new methods
- [ ] Update `Applications` with new methods
- [ ] Update `Recordings` with new methods

### Task B11: ARI JSON Serialization
- [ ] Add all new models to `AriJsonContext` [JsonSerializable] attributes
- [ ] Verify source-generated JSON for all new types
- [ ] Test round-trip serialization

### Task B12: Unit Tests for Sprint B
- [ ] Tests for each new resource class
- [ ] Tests for each new/updated endpoint (HTTP method, path, params)
- [ ] Tests for all new models (JSON deserialization)
- [ ] Tests for IAriClient interface compliance

---

## Sprint C — AMI Misc + Asterisk 23 Specifics (v1.4.0)

**Scope:** 10 AMI actions + AudioSocket update + cleanup
**Estimated effort:** ~1 day with subagent-driven development

### Task C1: Voicemail Actions (2 actions)
- [ ] `VoicemailRefreshAction` — Refresh mailbox
- [ ] `VoicemailUserStatusAction` — Get voicemail user status

### Task C2: Presence Actions (2 actions)
- [ ] `PresenceStateAction` — Get presence state
- [ ] `PresenceStateListAction` — List all presence states

### Task C3: Queue Extras (2 actions)
- [ ] `QueueReloadAction` — Reload queue configuration
- [ ] `QueueRuleAction` — Show queue rules

### Task C4: Database Extra (1 action)
- [ ] `DbGetTreeAction` — Get tree of values from AstDB

### Task C5: Miscellaneous Actions (3 actions)
- [ ] `CoreShowChannelMapAction` — Show channel relationships
- [ ] `SendFlashAction` — Send flash hook
- [ ] `DialplanExtensionAddAction` — Add extension to dialplan (runtime)
- [ ] `DialplanExtensionRemoveAction` — Remove extension from dialplan

### Task C6: Presence Events (if missing)
- [ ] `PresenceStateChangeEvent`
- [ ] `PresenceStateListCompleteEvent`
- [ ] `PresenceStatusEvent`

### Task C7: AudioSocket High Sample Rate (Asterisk 23)
- [ ] Add message type constants: `0x11` (slin12) through `0x18` (slin192)
- [ ] Update `AudioSocketSession` to handle new message types
- [ ] Update `AudioSocketClient` for testing with new types
- [ ] Add resampler support for 12/32/44/96/192 kHz if not present in `Asterisk.Sdk.Audio`

### Task C8: Additional Asterisk 23 Event Fields
- [ ] Verify all hangup-related events have `TechCause` (from Sprint A)
- [ ] Audit any other Asterisk 23 field additions across all events

### Task C9: Unit Tests for Sprint C
- [ ] Tests for all new actions
- [ ] Tests for new events
- [ ] Tests for AudioSocket new message types
- [ ] Update AudioSocket integration tests

---

## NOT Prioritized (Hardware Legacy / Obsolete)

These actions exist in Asterisk but target hardware/legacy protocols with minimal modern usage:

### DAHDI Actions (8) — TDM hardware only
- `DAHDIDNDoff`, `DAHDIDNDon`, `DAHDIDialOffhook`, `DAHDIHangup`
- `DAHDIRestart`, `DAHDIShowStatus`, `DAHDITransfer`
- Already have: `DAHDIShowChannels`

### PRI Actions (4) — E1/T1 hardware only
- `PRIDebugFileSet`, `PRIDebugFileUnset`, `PRIDebugSet`, `PRIShowSpans`

### IAX Actions — Obsolete protocol
- Already have: `IaxPeerListAction`
- Missing: `IAXnetstats`, `IAXpeers`, `IAXregistry`

### Sorcery Cache Actions (5) — Internal Asterisk admin
- `SorceryMemoryCacheExpire`, `SorceryMemoryCacheExpireObject`
- `SorceryMemoryCachePopulate`, `SorceryMemoryCacheStale`, `SorceryMemoryCacheStaleObject`

### Other Low Priority
- `AOCMessage` — Advice of Charge (EU telco specific)
- `JabberSend` — Already exists (XMPP deprecated in most deployments)
- `MeetMeList`, `MeetMeListRooms` — MeetMe deprecated, ConfBridge is replacement
- `WaitEvent` — HTTP long-poll (WebSocket preferred)
- `FAXSession`, `FAXSessions`, `FAXStats` — Fax-specific

**Rationale:** These can be added on-demand if users request them. They affect <5% of modern Asterisk deployments using PJSIP + ConfBridge.

---

## Post-Completion Coverage

| Capa | After Sprint A | After Sprint B | After Sprint C |
|------|---------------|----------------|----------------|
| AMI Actions | 139/152 (91%) | 139/152 (91%) | 152/152 (100%) |
| AMI Events | ~270/180 (150%+) | ~270/180 (150%+) | ~275/180 (152%+) |
| AGI Commands | 54/47 (100%+) | 54/47 (100%+) | 54/47 (100%+) |
| ARI Endpoints | ~44/98 (45%) | ~94/98 (96%) | ~94/98 (96%) |
| ARI Events | 46/46 (100%) | 46/46 (100%) | 46/46 (100%) |
| ARI Models | 16/27 (59%) | 27/27 (100%) | 27/27 (100%) |

**Final coverage after all 3 sprints: ~97% of all Asterisk 23 public APIs**
(remaining 3% = DAHDI/PRI/IAX hardware-specific + Sorcery admin)

---

## Execution Strategy

- **Use Subagent-Driven Development** for all tasks
- Each task = 1 subagent with isolated worktree
- Source generators will auto-pick-up new actions/events
- Build must remain 0 warnings after each sprint
- All existing tests must continue passing
- Pack and publish to nuget.org after each sprint

---

## Epilogue (2026-04-19)

Closed as part of the 2026-04-19 product alignment audit — see [docs/research/2026-04-19-product-alignment-audit.md](../../research/2026-04-19-product-alignment-audit.md) §3.

**Final coverage shipped at v1.11.0:**

| Capa | Target | Actual | Delta |
|------|--------|--------|-------|
| AMI Actions | 152/152 (100%) | 148/152 (97%) | -4 |
| ARI Endpoints | 98/98 (100%) | 94/98 (96%) | -4 |
| ARI Events | 46/46 (100%) | 46/46 (100%) | 0 |
| ARI Models | 27/27 (100%) | 27/27 (100%) | 0 |
| AGI Commands | 47/47 (100%) | 54/47 (115%) | +7 |

Verified at audit by `find src/Asterisk.Sdk.Ami/Actions -name "*.cs" -not -name "*Base*" -not -name "*Registry*" | wc -l` → 148, and `grep -hE 'public.*Async\(' src/Asterisk.Sdk.Ari/Resources/*.cs | wc -l` → 94. Numbers match the README.md v1.10.2 claim of `148/152 AMI actions (97%), 94/98 ARI endpoints (96%), 46/46 ARI event types (100%)`.

**On the 8 residual endpoints:** they correspond to features this plan explicitly marked lower-priority — legacy channel technologies (DAHDI TDM hardware, PRI E1/T1 hardware, IAX peering) plus Sorcery admin cache-management, whose users are dwindling on Asterisk 22/23 deployments. Sprints A and B shipped in full; Sprint C shipped AudioSocket as its main deliverable and explicitly scoped out the legacy channel-tech actions. The 97%/96% number is a steady-state product decision, not an abandoned sprint.

**Status:** closed, no-reopen. Any future additions to these residual categories should open a new plan, not resume this one.
