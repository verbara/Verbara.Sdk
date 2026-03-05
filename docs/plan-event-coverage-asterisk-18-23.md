# Plan: Cobertura Completa de Eventos Asterisk 18-23

> Resultado de la auditoria de cobertura de eventos del SDK contra Asterisk 18 hasta 23.
> Fecha: 2026-03-05 | Branch: `feature/rename-asterisk-sdk`

---

## Contexto

La auditoria identifico:

- **42 eventos AMI** sin tipo especifico (caen a `ManagerEvent` generico)
- **23 eventos ARI** sin tipo especifico (caen a `AriEvent` generico)
- **3 campos nuevos** (`TechCause`) faltantes en eventos existentes
- **~28 eventos legacy** en el SDK que ya no existen en Asterisk 21+
- **0 datos perdidos** — todo se preserva via `RawFields` / `RawJson`

El plan se organiza en **5 sprints** de trabajo incremental, priorizando impacto operacional.

---

## Sprint 1 — Eventos ARI Criticos (Transferencias y Grabacion) ✅ COMPLETADO

**Commit:** `6fd4677` feat(ari): add transfer, bridge merge and recording failed events

### Entregado

- 6 eventos ARI nuevos tipados:
  - `BridgeAttendedTransferEvent` — transferencia atendida completada
  - `BridgeBlindTransferEvent` — transferencia ciega completada
  - `ChannelTransferEvent` — transferencia iniciada (Ast 21+)
  - `BridgeMergedEvent` — bridges fusionados
  - `BridgeVideoSourceChangedEvent` — cambio de fuente de video
  - `RecordingFailedEvent` — fallo de grabacion
- Registrados en `AriJsonContext.cs` con `[JsonSerializable]`
- Registrados en `s_eventParsers` en `AriClient.cs`
- 6 tests en `AriClientParseEventTests.cs`
- Build: 0 warnings

---

## Sprint 2 — Eventos AMI Criticos (Presencia, UserEvent, Parking, ConfBridge) ✅ COMPLETADO

**Commit:** `6d1d460` feat(ami): add presence, UserEvent, parking, ConfBridge and monitor events

### Entregado

- 13 eventos AMI nuevos:
  - `PresenceStateChangeEvent`, `PresenceStatusEvent`, `PresenceStateListCompleteEvent`
  - `DeviceStateListCompleteEvent`, `ExtensionStateListCompleteEvent`
  - `UserEventEvent`
  - `ParkedCallSwapEvent`
  - `ConfbridgeMuteEvent`, `ConfbridgeUnmuteEvent`, `ConfbridgeRecordEvent`, `ConfbridgeStopRecordEvent`
  - `HangupHandlerPopEvent`
  - `MixMonitorMuteEvent`
- Todos con `[AsteriskMapping]` — source generator los incluye automaticamente
- 16 tests en `Sprint2EventTests.cs`
- Build: 0 warnings

---

## Sprint 3 — Campos Nuevos de Asterisk 20.17+/22.7+/23+ y Eventos ARI Complementarios ✅ COMPLETADO

**Commit:** `9fa6f27` feat: add TechCause fields and complementary ARI events

### Entregado

**Campos AMI nuevos:**
- `TechCause` en `HangupEvent`, `HangupRequestEvent`, `SoftHangupRequestEvent`
- `LoginTime` en `QueueMemberStatusEvent`

**8 eventos ARI nuevos:**
- `ChannelCallerIdEvent`, `ChannelDialplanEvent`, `ChannelUsereventEvent`
- `DeviceStateChangedEvent`, `PlaybackContinuingEvent`
- `ContactStatusChangeEvent`, `PeerStatusChangeEvent`, `TextMessageReceivedEvent`

**Campos ARI nuevos:**
- `TechCause` en `ChannelHangupRequestEvent` y `ChannelDestroyedEvent`

**4 modelos ARI auxiliares:**
- `AriDeviceState`, `AriContactInfo`, `AriPeer`, `AriTextMessage`

- Todos registrados en `AriJsonContext.cs` y `s_eventParsers`
- 10 tests ARI + 5 tests AMI campos en `Sprint2EventTests.cs`
- Build: 0 warnings

---

## Sprint 4 — Eventos AMI de Baja Prioridad (PJSIP Detail, FAX, Voicemail, Sistema) ✅ COMPLETADO

**Commit:** `8a5b725` feat(ami): add PJSIP detail, FAX, bridge info, voicemail, system and AOC events

### Entregado

**32 eventos AMI nuevos:**

- PJSIP: `IdentifyDetailEvent`, `InboundRegistrationDetailEvent`, `OutboundRegistrationDetailEvent`, `InboundSubscriptionDetailEvent`, `OutboundSubscriptionDetailEvent`, `AorListEvent`, `AorListCompleteEvent`, `AuthListEvent`, `AuthListCompleteEvent`, `ResourceListDetailEvent`
- FAX: `FAXSessionEvent`, `FAXSessionsEntryEvent`, `FAXSessionsCompleteEvent`, `FAXStatsEvent`
- Bridge: `BridgeInfoChannelEvent`, `BridgeInfoCompleteEvent`
- MCID: `MCIDEvent`
- Voicemail: `MWIGetEvent`, `MWIGetCompleteEvent`, `MiniVoiceMailEvent`, `VoicemailPasswordChangeEvent`
- Sistema: `LoadEvent`, `UnloadEvent`
- DAHDI/Signal: `FlashEvent`, `WinkEvent` (Ast 20+), `SpanAlarmEvent`, `SpanAlarmClearEvent`
- Debug: `DeadlockStartEvent` (Ast 20+), `CoreShowChannelMapCompleteEvent` (Ast 20+)
- AOC: `AocDEvent` (AOC-D), `AocEEvent` (AOC-E), `AocSEvent` (AOC-S)

- Todos con `[AsteriskMapping]` — source generator los incluye automaticamente
- 35 tests en `Sprint4EventTests.cs`
- Build: 0 warnings

---

## Sprint 5 — Eventos ARI de Asterisk 22+, Limpieza Legacy y Documentacion ✅ COMPLETADO

**Commit:** `2ebc0a6` feat: add ARI 16-22+ events, mark 26 legacy events [Obsolete], add version compatibility docs

### Entregado

**7 eventos ARI nuevos:**
- `ApplicationMoveFailedEvent` (Ast 16+)
- `ApplicationRegisteredEvent` (Ast 21+)
- `ApplicationUnregisteredEvent` (Ast 21+)
- `MissingParamsEvent` (Ast 12+)
- `ReferToEvent` (Ast 22+)
- `ReferredByEvent` (Ast 22+)
- `RequiredDestinationEvent` (Ast 22+)

**26 eventos legacy marcados `[Obsolete]`:**
- MeetMe (7): MeetMeJoin, MeetMeLeave, MeetMeEnd, MeetMeTalking, MeetMeStopTalking, MeetMeTalkingRequest, MeetMeMute
- Monitor (2): MonitorStart, MonitorStop
- Legacy Bridge/Dial (6): Link, Unlink, Bridge, Dial, Join, Leave
- Queue (2): Paused, Unpaused
- Skype (7): SkypeAccountStatus, SkypeBuddyEntry, SkypeBuddyListComplete, SkypeBuddyStatus, SkypeChatMessage, SkypeLicense, SkypeLicenseListComplete
- Zaptel (2): ZapShowChannels, ZapShowChannelsComplete

**Infraestructura:**
- Source generators emiten `#pragma warning disable CS0618` en codigo generado
- `LegacyEventAdapter`, `AsteriskServer`, `CallFlowTracker` y tests con pragma suppress

**Documentacion:**
- `docs/asterisk-version-compatibility.md` — matriz completa de compatibilidad Asterisk 18-23

- 7 tests ARI en `AriClientParseEventTests.cs`
- Build: 0 warnings, suite completa verde

---

## Resumen de Entregables por Sprint

| Sprint | Estado | Eventos AMI | Eventos ARI | Campos | Tests | Commit |
|--------|--------|------------|------------|--------|-------|--------|
| **1** | ✅ | 0 | 6 | 0 | 6 | `6fd4677` |
| **2** | ✅ | 13 | 0 | 0 | 16 | `6d1d460` |
| **3** | ✅ | 0 | 8 | 4 | 15 | `9fa6f27` |
| **4** | ✅ | 32 | 0 | 0 | 35 | `8a5b725` |
| **5** | ✅ | 0 | 7 | 0 | 7 | `2ebc0a6` |
| **Total** | **5/5** | **45** | **21** | **4** | **79** | — |

## Resultado Final

| Metrica | Antes | Despues |
|---------|-------|---------|
| Eventos AMI tipados | 216 | **261** |
| Eventos ARI tipados | 24 | **45** |
| Cobertura AMI tipada | 93% | **100%** |
| Cobertura ARI tipada | 48% | **90%** |
| Campos faltantes | 4 | **0** |
| Eventos legacy marcados | 0 | **26** |
| Datos perdidos | 0 | **0** (sin cambios) |
| Versiones soportadas | 18-23 | **18-23** (documentado) |
