# Migrating to `CreateExternalMediaAsync` with a `channelId`

Required by ADR-0028: a minor that carries a breaking change ships a migration guide.

## What happened

`CreateExternalMediaAsync` gained an optional `string? channelId` parameter, on
`Verbara.Sdk.IAriChannelsResource` and on `Verbara.Sdk.Ari.Resources.AriChannelsResource`. It sits
between `data` and the cancellation token:

```csharp
// before
ValueTask<AriChannel> CreateExternalMediaAsync(string app, string externalHost, string format,
    string? encapsulation = null, string? transport = null, string? connectionType = null,
    string? direction = null, string? data = null, CancellationToken cancellationToken = default);

// after
ValueTask<AriChannel> CreateExternalMediaAsync(string app, string externalHost, string format,
    string? encapsulation = null, string? transport = null, string? connectionType = null,
    string? direction = null, string? data = null, string? channelId = null,
    CancellationToken cancellationToken = default);
```

Optional parameters are compile-time sugar. The nine-parameter member is gone from both assemblies,
which is why this is a break and not an addition: `CP0002` in both packages, plus `CP0006` on the
interface. Those three are declared in each package's `CompatibilitySuppressions.xml`.

## Why a parameter is worth a break

Asterisk's `externalMedia` takes two identifiers that are easy to mistake for one.

- `data` is what AudioSocket sends back on the audio connection, in its identification frame. It is
  the value an `AudioSocketServer` keys its stream table by, and Asterisk requires it to parse as a
  UUID.
- `channelId` is the id Asterisk assigns the ARI channel it creates. It comes back as `Channel.Id`.

Supply only `data` and Asterisk mints the channel id itself — something like `1790244226.1`, which
appears in no stream table. A consumer holding only an ARI channel id, which is what a `StasisStart`
or `ChannelHangupRequest` handler holds, then has nothing it can look the stream up with. Passing one
identifier as **both** makes `Channel.Id` the key, and that is the capability this parameter buys.

The smaller fix that breaks nothing — mint the UUID, pass it only as `data`, and look the stream up by
the value you minted — works, and was rejected deliberately: it fixes the caller that minted the UUID
and leaves everyone downstream of it unable to find the stream.

## What you have to do

**Recompile.** For almost every caller that is the whole of it, because the parameter is optional and
appended rather than inserted.

Three populations need an edit.

### If you passed the cancellation token positionally

```csharp
// stops compiling: the ninth positional argument now binds to string? channelId
await channels.CreateExternalMediaAsync(app, host, "slin16", "audiosocket", "tcp", null, null,
    uuid, cancellationToken);
```

The compiler reports `CS1503` — `cannot convert from 'System.Threading.CancellationToken' to
'string?'`. Name the token:

```csharp
await channels.CreateExternalMediaAsync(app, host, "slin16", "audiosocket", "tcp", null, null,
    uuid, cancellationToken: cancellationToken);
```

The parameter was appended rather than placed first among the optionals — where
`CreateWithoutDialAsync` puts its own `channelId` — precisely so this break would be a compile error.
Inserting ahead of `encapsulation` would have rebound every existing positional argument to a
different `string?` parameter and gone on compiling.

### If you implement `IAriChannelsResource`

Fakes, decorators and proxies stop compiling with `CS0535` until they adopt the parameter. A decorator
forwards it; a fake that ignores it should still accept it, so it keeps matching the interface.

### If you have a mocking framework set up on this method

A setup that enumerates positional matchers ending in a `CancellationToken` hits the `CS1503` above.
Move it to named arguments — and name `channelId` explicitly:

```csharp
channels.CreateExternalMediaAsync(
    app: Arg.Any<string>(), externalHost: Arg.Any<string>(), format: Arg.Any<string>(),
    encapsulation: Arg.Any<string?>(), transport: Arg.Any<string?>(),
    connectionType: Arg.Any<string?>(), direction: Arg.Any<string?>(),
    data: Arg.Any<string?>(), channelId: Arg.Any<string?>(),
    cancellationToken: Arg.Any<CancellationToken>())
```

Leaving `channelId` out of a named list is the quiet failure. The compiler supplies its default
`null`, the mock equality-matches that `null`, and the setup stops matching the moment real code
supplies an id — so the test fails somewhere else entirely, on whatever the unmatched call returned.

## How to use it

Mint one identifier and pass it as both, in canonical lowercase hyphenated form:

```csharp
var id = Guid.NewGuid().ToString();   // "e1a2..." — lowercase, hyphenated
var channel = await ariClient.Channels.CreateExternalMediaAsync(
    app: "myapp", externalHost: "10.0.0.5:9092", format: "slin16",
    encapsulation: "audiosocket", transport: "tcp",
    data: id, channelId: id);

var stream = audioSocketServer.GetStream(channel.Id);   // now finds it
```

Three things that will cost you time if you take them on trust instead of from here:

- **`transport = "tcp"` is mandatory with `encapsulation = "audiosocket"`.** Without it the create
  returns `HTTP 400 — transport must be 'tcp' for audiosocket encapsulation`, and with it but without
  `data`, `HTTP 400 — data can not be empty`.
- **The spelling is `channelId`, in camelCase**, alone among the snake_case siblings
  (`external_host`, `connection_type`). Asterisk ignores a query parameter it does not recognise
  rather than rejecting it, so a misspelling is an `HTTP 200` with an Asterisk-minted id and no error
  anywhere — the SDK's own test asserts the literal bytes for that reason.
- **Canonical lowercase is not cosmetic.** Asterisk accepts an uppercase UUID and echoes `Channel.Id`
  back in the spelling you sent, but the identification frame is sixteen raw bytes that the server
  renders with `Guid.ToString()` — lowercase — into an ordinal dictionary. An uppercase id therefore
  creates a channel whose stream can never be found, with no error on any hop.

`channelId` itself is free-form as far as Asterisk is concerned; it is `data` that must parse as a
UUID. Using the same value for both is what makes the lookup work, so in practice both are a UUID.

## If you use `ExternalMediaActivity`

The activity now mints the identifier and supplies it to both parameters itself, in the same release
as this break — making that true is what the break was bought for. It also derives the rest of the
request, so three behaviours changed. Only the third can break a caller that works today.

**1. `Encapsulation` left null with an `AudioSocketServer` supplied now means `audiosocket`.** It
used to mean rtp/udp, which is how the default configuration created a `UnicastRTP` channel that
nothing connected to and then threw `TimeoutException` after `ConnectionTimeout`. Nothing to do: the
configuration that was broken is the one that changed.

**2. `Transport` left null under AudioSocket encapsulation now means `"tcp"`.** It used to be sent as
absent, which is `HTTP 400 — transport must be 'tcp' for audiosocket encapsulation`. An explicit
`Transport` is still sent exactly as given, so a wrong one still fails at the create call with
Asterisk's own message rather than being silently rewritten.

**3. An `AudioSocketServer` with a non-AudioSocket `Encapsulation` is now refused.**

```csharp
new ExternalMediaActivity(ariClient, audioSocketServer)
{
    App = "myapp", ExternalHost = "10.0.0.5:9092", Encapsulation = "rtp"
}
```

`StartAsync` throws `InvalidOperationException` naming the contradiction, before any channel is
created. Previously this created an RTP channel, waited out the whole `ConnectionTimeout` — 30
seconds by default — and threw `TimeoutException: Asterisk did not connect to audio server`, which
reports the symptom one layer away from the cause. If you hit this, either set `Encapsulation` to
`"audiosocket"` (or leave it null, which derives it) or stop passing the `AudioSocketServer`.

Callers with **no** audio server, or with a WebSocket server only, take an unchanged path.

## What this does not cover

The WebSocket transport. `WebSocketAudioServer` keys its table on the last segment of the request
path, not on an identification frame, so `channelId` does not make its lookups work. What
`externalMedia` with `transport=websocket` actually puts in the request path has not been measured,
and is tracked separately rather than designed from a reading of the code.
