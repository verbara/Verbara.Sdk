# Asterisk.Sdk.Config

Parsers for Asterisk `.conf` configuration files — reads sections, variables, comments, includes, and template inheritance.

## Installation

```bash
dotnet add package Asterisk.Sdk.Config
```

## Quick Start

```csharp
// Parse a .conf file from disk
ConfigFile config = ConfigFileReader.Parse("/etc/asterisk/sip.conf");

// Read a section
ConfigCategory? general = config.GetCategory("general");
if (general is not null)
{
    string? port = general.Variables.GetValueOrDefault("port");
    Console.WriteLine($"SIP port: {port}");
}

// Parse extensions.conf with dialplan context support
ExtensionsConfig dialplan = ExtensionsConfigFileReader.Parse("/etc/asterisk/extensions.conf");
foreach (var context in dialplan.Contexts)
    Console.WriteLine($"Context: {context.Name}, {context.Extensions.Count} extensions");
```

## Features

- `ConfigFileReader` — general-purpose `.conf` parser: sections `[name]`, `key = value`, `#include`, `#exec`, template inheritance `[section](template)`
- `ExtensionsConfigFileReader` — specialized parser for `extensions.conf` dialplan files
- `ConfigFile` / `ConfigCategory` / `ConfigVariable` — strongly-typed model with ordered variable access
- Inline comment stripping (`;` outside quoted values)
- Native AOT compatible; no reflection

## Documentation

See the [main README](../../README.md) for full documentation.
