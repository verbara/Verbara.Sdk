# Verbara.Sdk.Ami

Asterisk Manager Interface (AMI) client for .NET 10 with Native AOT support.

## Features

- Full AMI protocol implementation (120 actions, 228 events, 14 response types)
- Zero-copy TCP parsing via `System.IO.Pipelines`
- Async event pump with configurable backpressure (`EventPumpCapacity`)
- MD5 challenge-response authentication
- Automatic reconnection with exponential backoff
- Source-generated serialization (no runtime reflection)
- `System.Diagnostics.Metrics` for observability

## Quick Start

```csharp
services.AddVerbara(options =>
{
    options.Ami.Hostname = "pbx.example.com";
    options.Ami.Username = "admin";
    options.Ami.Password = "secret";
});
```

```csharp
// Send an action
var response = await connection.SendActionAsync(new StatusAction());

// Subscribe to events
connection.Subscribe(Observer.Create<ManagerEvent>(evt =>
{
    Console.WriteLine($"Event: {evt.EventType}");
}));
```

## Sending an action the SDK does not type

`RawAction` sends any AMI action by its name and fields through the same connection, for example a `DAHDI*` action
or `LoggerRotate`:

```csharp
// Written as: Action: DAHDIDNDon, the connection's ActionID, DAHDIChannel: 1
var response = await connection.SendActionAsync(new RawAction("DAHDIDNDon").Add("DAHDIChannel", "1"));

// A listing action: the sequence ends at its ...Complete event, which it does not yield
await foreach (var peer in connection.SendEventGeneratingActionAsync(new RawAction("IAXpeers")))
    Console.WriteLine(peer.RawFields?["ObjectName"]);
```

Fields are written in the order added, and a repeated key is written each time. The name must be ASCII letters and
digits; a key ASCII letters, digits, `-`, `_`, `(` or `)`, and not `Action` or `ActionID`; a value holds no CR or LF.
Each is checked where the action is built and throws `ArgumentException`, which never includes the value. The answer
is read by the action's name: the response type of the typed action of that name when there is one, otherwise a
`ManagerResponse` whose `RawFields` hold every header. A name Asterisk does not register is answered by Asterisk with
`Response: Error`.

The connection refuses a `ManagerAction` subclass of your own with `ArgumentException` naming its type, before any
byte is written; send it as a `RawAction` instead. The connection's `Trace` log writes every sent field with its
value, raw actions included, so keep `Trace` off where a field carries a secret.

## Documentation

- [High-Load Tuning Guide](../../docs/guides/high-load-tuning.md)
- [Asterisk Version Compatibility](../../docs/guides/asterisk-version-compatibility.md)
