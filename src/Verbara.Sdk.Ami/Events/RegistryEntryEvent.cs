using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("RegistryEntry")]
public sealed class RegistryEntryEvent : ResponseEvent
{
    public long? RegistrationTime { get; set; }
    public int? Port { get; set; }
    public string? Username { get; set; }
    public string? Host { get; set; }
    public string? State { get; set; }
    public int? Refresh { get; set; }
}

