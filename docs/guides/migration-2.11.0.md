# Upgrading from 2.10.x to 2.11.0

This is the one migration guide for 2.11.0. It has one section per change a reader may have to act on, and says who
each one reaches and what to do. The [CHANGELOG](../../CHANGELOG.md) has the full entry for each.

| Package | Change | Kind |
|---|---|---|
| `Verbara.Sdk.Ami` | [Six `StatusEvent` members no Asterisk fills are removed](#six-statusevent-members-no-asterisk-fills-are-removed) | removed members, BREAKING |
| `Verbara.Sdk.Ami` | [Seven AMI types whose wire name no Asterisk knows are removed](#seven-ami-types-whose-wire-name-no-asterisk-knows-are-removed) | removed types, BREAKING |

## Before you start: move every package at once

Move every `Verbara.Sdk.*` package to 2.11.0 together, then recompile. The packages are built and tested as one
release; a 2.10.x package loaded next to a 2.11.0 one is not a combination anything verifies.

## Deprecated surface removed

2.10.0 printed "Removed in Verbara.Sdk 2.11.0." in the warning of each symbol below, and linked the warning to its id's
heading in the [2.10.0 guide](migration-2.10.0.md#deprecated-surface). Those symbols are gone. The ids are retired:
nothing in the SDK carries them any more, so a `NoWarn` or `#pragma warning disable` you kept for one of them
suppresses nothing and can be deleted.

A binary built against 2.10.x or earlier that names a removed symbol fails when it reaches it, with
`MissingMethodException` for a removed member and `TypeLoadException` for a removed type, until it is recompiled
against 2.11.0 and the use is moved as below.

### Six `StatusEvent` members no Asterisk fills are removed

**What changed.** `StatusEvent.State`, `CallerId`, `Account`, `BridgedChannel`, `Link` and `BridgedUniqueId` are
removed (marked with `VSDK0002` since 2.9.0). Each named a header that no supported Asterisk sends on a `Status`
frame, so each was always null. What a frame carries is read from the member that binds it:

| Removed member | Read instead |
|---|---|
| `State` | `ChannelStateDesc` (text, for example `Up`) or `ChannelState` (numeric, for example `6`) |
| `CallerId` | `CallerIdNum` (number) or `CallerIdName` (name) |
| `Account` | `AccountCode` |
| `BridgedChannel` | `BridgeId`: a `Status` frame names the bridge, never the peer channel |
| `Link` | `BridgeId` |
| `BridgedUniqueId` | `BridgeId` |

Nothing changes at run time for any frame a supported Asterisk sends. A `Status` frame that did carry one of these
headers would keep it in `RawFields`, under the name it arrived with.

**Who is affected.** Code that reads or sets one of the six members. A read reports `CS1061`
(`'StatusEvent' does not contain a definition for 'State'`); a member set in an object initializer reports `CS0117`.

**What to do.** Move each read to the member in the table. Before (2.10.x: warned `VSDK0002`, always null; does
not compile against 2.11.0):

<!-- skip-doc-snippet -->
```csharp
var state = status.State;
var caller = status.CallerId;
```

After:

```csharp
using Verbara.Sdk.Ami.Events;

static (string? State, string? Caller) Read(StatusEvent status) =>
    (status.ChannelStateDesc, status.CallerIdNum);
```

### Seven AMI types whose wire name no Asterisk knows are removed

**What changed.** Seven types of `Verbara.Sdk.Ami` are removed (marked with `VSDK0007` since 2.10.0). Each named an
AMI action that no Asterisk registers, or an event that no supported Asterisk (22, 23, 24) emits:

| Removed type | Wire name | What to use |
|---|---|---|
| `DbPrefixGetAction` | `DBPrefixGet`, never registered | `DbGetTreeAction` (`DBGetTree`): the entries of a family, optionally under a key |
| `ExecAction` | `exec`, never registered | `CommandAction` to run a CLI command; `RawAction` to send an action by its name |
| `JitterBufStatsEvent` | `JitterBufStats`, last emitted by chan_iax2 in Asterisk 11 | nothing: delete the handler |
| `MasqueradeEvent` | `Masquerade`, last emitted by the core in Asterisk 11 | nothing: delete the handler |
| `PriEventEvent` | `PRIEvent`, emitted by chan_dahdi in Asterisk 1.6, gone by 11 | nothing: delete the handler |
| `RtpReceiverStatEvent` | `RTPReceiverStat`, emitted by the core in Asterisk 1.6, gone by 11 | nothing: delete the handler |
| `RtpSenderStatEvent` | `RTPSenderStat`, emitted by the core in Asterisk 1.6, gone by 11 | nothing: delete the handler |

**An event of a removed type's name still arrives.** If a server did send one, it reaches every subscriber as a base
`ManagerEvent`: its name, spelled as sent, is in `EventType`, and every header is in `RawFields`. Nothing is dropped.
This is how the event names removed in 2.9.0 and 2.10.0 arrive too.

**Who is affected.** Code that names one of the seven types. It reports `CS0246`
(`The type or namespace name 'MasqueradeEvent' could not be found`), or `CS0234` when written fully qualified.

**What to do.** Replace the two actions as the table says. Before (2.10.x: warned `VSDK0007`, and Asterisk answered
with an error; does not compile against 2.11.0):

<!-- skip-doc-snippet -->
```csharp
await connection.SendActionAsync(new DbPrefixGetAction { Family = "cidname", Key = "123" });
```

After:

```csharp
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;

static async Task ReadFamilyAsync(IAmiConnection connection)
{
    await foreach (var entry in connection.SendEventGeneratingActionAsync(new DbGetTreeAction { Family = "cidname", Key = "123" }))
    {
        // one event per entry of the family under the key; its headers are in entry.RawFields
    }
}
```

Delete a handler or a type check for one of the five events: it never ran against a supported Asterisk. To read one
by name on a server that sends it anyway, match `EventType` and read `RawFields`:

```csharp
using Verbara.Sdk;

static (string? Clone, string? Original) ReadMasquerade(ManagerEvent evt) =>
    evt.EventType == "Masquerade" && evt.RawFields is { } fields
        ? (fields.GetValueOrDefault("Clone"), fields.GetValueOrDefault("Original"))
        : (null, null);
```
