# Asterisk.Sdk.Hosting

Microsoft.Extensions.DependencyInjection integration for Asterisk.Sdk. Provides the `AddAsterisk()` extension method for easy service registration and options validation.

## Quick Start

```csharp
services.AddAsterisk(options =>
{
    options.AmiConnection.Hostname = "pbx.example.com";
    options.AmiConnection.Username = "admin";
    options.AmiConnection.Password = "secret";
});
```

## Features

- `AddAsterisk()` DI registration for all SDK services
- AOT-safe options validation via `[OptionsValidator]` source generator
- `AsteriskOptions` configuration model
