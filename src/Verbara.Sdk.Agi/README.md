# Asterisk.Sdk.Agi

FastAGI and AsyncAGI server for .NET 10 with Native AOT support.

## Features

- FastAGI TCP server with concurrent connection handling
- 54 AGI commands (Answer, Hangup, Playback, Record, SayDigits, etc.)
- Script mapping strategies (Simple, Resource-based)
- Zero-copy I/O via `System.IO.Pipelines`

## Quick Start

```csharp
var server = new FastAgiServer(new FastAgiServerOptions
{
    BindAddress = "0.0.0.0",
    Port = 4573
}, new SimpleMappingStrategy(new Dictionary<string, Func<AgiChannel, Task>>
{
    ["hello.agi"] = async channel =>
    {
        await channel.AnswerAsync();
        await channel.StreamFileAsync("hello-world");
        await channel.HangupAsync();
    }
}), logger);

await server.StartAsync(cancellationToken);
```

## Documentation

- [Asterisk Version Compatibility](../../docs/asterisk-version-compatibility.md)
